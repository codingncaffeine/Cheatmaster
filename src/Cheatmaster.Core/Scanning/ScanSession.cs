using System.Buffers;
using System.Diagnostics;
using Cheatmaster.Core.Memory;

namespace Cheatmaster.Core.Scanning;

public sealed class ScanException : Exception
{
    public ScanException(string message) : base(message) { }
}

/// <summary>
/// Drives a scan from first pass to final address list, keeping the surviving results and the
/// history needed to undo a step.
/// </summary>
public sealed partial class ScanSession
{
    private const int WindowSize = 64 * 1024;

    private readonly List<ScanResults> _history = [];

    public ScanSession(TargetProcess process, ScanSettings settings)
    {
        Process = process;
        Settings = settings;
    }

    public TargetProcess Process { get; }
    public ScanSettings Settings { get; }
    public ScanResults? Current { get; private set; }

    public bool HasResults => Current is { Count: > 0 };
    public bool CanUndo => _history.Count > 0;

    public void Reset()
    {
        Current = null;
        _history.Clear();
    }

    public void Undo()
    {
        if (_history.Count == 0) return;
        Current = _history[^1];
        _history.RemoveAt(_history.Count - 1);
    }

    /// <summary>Installs a derived result set, such as the output of a filter, as the current one.</summary>
    public void Replace(ScanResults results) => Commit(results);

    private int WorkerCount => Settings.WorkerCount > 0 ? Settings.WorkerCount : Environment.ProcessorCount;

    // ---------------------------------------------------------------- first scan

    public ScanResults FirstScan(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (request.Compare == CompareKind.UnknownInitialValue)
            throw new ScanException("Unknown-value scans run through SnapshotScan, not FirstScan.");
        if (!request.Compare.IsRangeComparison())
            throw new ScanException($"'{request.Compare.Label()}' needs a previous scan to compare against.");
        if (request.Compare == CompareKind.NotEqualTo)
            throw new ScanException("'Not equal to' matches almost everything on a first scan. Start with a value you can see.");
        if (!request.Value.IsValid)
            throw new ScanException("Enter a value to search for.");
        if (request.Compare == CompareKind.Between && !request.Value2.IsValid)
            throw new ScanException("A between search needs both ends of the range.");

        var interpretations = InterpretationSets.Build(request.Profile, request.ForcedType);
        var plan = ScanPlan.Build(interpretations, request.Compare, request.Value, request.Value2, request.Rounding);
        if (plan.Items.Length == 0)
            throw new ScanException("No storage theory can hold that value. Check the number, or widen the scan profile.");

        var filter = Settings.Regions.ToFilter();
        var regions = Process.EnumerateRegions(filter);
        if (regions.Count == 0)
            throw new ScanException("No readable memory matched the region filter.");

        var chunks = BuildChunks(regions, Settings.ChunkSize, plan.MaxWidth - 1);
        long totalBytes = 0;
        foreach (var c in chunks) totalBytes += c.CoreLength;

        var sw = Stopwatch.StartNew();
        var results = RunChunks(plan, chunks, totalBytes, progress, ct);
        sw.Stop();

        var final = BuildResults(plan.Interpretations, results.Buffers, results.InterpCounts, results.Truncated, totalBytes, sw.Elapsed);
        Commit(final);
        return final;
    }

    private readonly record struct ChunkSpec(ulong Base, int CoreLength, int ReadLength);

    private static List<ChunkSpec> BuildChunks(List<MemoryRegion> regions, int chunkSize, int overlap)
    {
        var chunks = new List<ChunkSpec>();
        foreach (var region in regions)
        {
            // Round the start up so candidate offsets stay aligned to absolute addresses.
            ulong start = (region.Base + 7UL) & ~7UL;
            ulong end = region.End;
            if (end <= start) continue;

            for (ulong at = start; at < end; at += (ulong)chunkSize)
            {
                int core = (int)Math.Min((ulong)chunkSize, end - at);
                int extra = (int)Math.Min((ulong)overlap, end - at - (ulong)core);
                chunks.Add(new ChunkSpec(at, core, core + extra));
            }
        }
        return chunks;
    }

    private sealed class WorkerState
    {
        public byte[] Buffer = [];
        public List<ValidRun> Runs = [];
    }

    private readonly record struct ChunkRunOutcome(HitBuffer?[] Buffers, long[] InterpCounts, bool Truncated);

    private ChunkRunOutcome RunChunks(ScanPlan plan, List<ChunkSpec> chunks, long totalBytes,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var buffers = new HitBuffer?[chunks.Count];
        long[] interpCounts = new long[plan.Interpretations.Length];
        long bytesDone = 0;
        long found = 0;
        int truncated = 0;
        int chunkSize = Settings.ChunkSize;
        int alignment = Settings.Alignment;
        long perInterpCap = Math.Max(1, Settings.MaxResultsPerInterpretation);
        long globalCap = Settings.MaxResults;
        var items = plan.Items;

        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = WorkerCount };

        Parallel.For(0, chunks.Count, options,
            () => new WorkerState { Buffer = ArrayPool<byte>.Shared.Rent(chunkSize + 16), Runs = [] },
            (index, _, state) =>
            {
                if (Volatile.Read(ref truncated) != 0) return state;

                var spec = chunks[index];
                var span = state.Buffer.AsSpan(0, spec.ReadLength);
                int read = Process.ReadRuns(spec.Base, span, state.Runs);
                Interlocked.Add(ref bytesDone, spec.CoreLength);

                if (read > 0)
                {
                    HitBuffer? sink = null;
                    foreach (var item in items)
                    {
                        if (Volatile.Read(ref interpCounts[item.InterpId]) >= perInterpCap) continue;

                        sink ??= new HitBuffer(64);
                        int before = sink.Count;

                        foreach (var run in state.Runs)
                        {
                            int core = Math.Min(run.Length, spec.CoreLength - run.Offset);
                            if (core <= 0) continue;
                            ScanKernel.Scan(item, state.Buffer.AsSpan(run.Offset, run.Length),
                                spec.Base + (ulong)run.Offset, core, alignment, sink);
                        }

                        int added = sink.Count - before;
                        if (added > 0) Interlocked.Add(ref interpCounts[item.InterpId], added);
                    }

                    if (sink is { Count: > 0 })
                    {
                        sink.Sort();
                        buffers[index] = sink;
                        if (Interlocked.Add(ref found, sink.Count) > globalCap)
                            Interlocked.Exchange(ref truncated, 1);
                    }
                }

                if (progress is not null && (index & 31) == 0)
                    progress.Report(new ScanProgress("Scanning", Volatile.Read(ref bytesDone), totalBytes, Volatile.Read(ref found)));

                return state;
            },
            state => ArrayPool<byte>.Shared.Return(state.Buffer));

        progress?.Report(new ScanProgress("Scanning", totalBytes, totalBytes, Volatile.Read(ref found)));
        return new ChunkRunOutcome(buffers, interpCounts, Volatile.Read(ref truncated) != 0);
    }

    private ScanResults BuildResults(Interpretation[] interpretations, HitBuffer?[] buffers, long[] interpCounts,
        bool truncated, long bytesScanned, TimeSpan duration)
    {
        int total = 0;
        foreach (var b in buffers) total += b?.Count ?? 0;

        var addresses = new ulong[total];
        var ids = new int[total];
        var values = new ulong[total];

        int pos = 0;
        foreach (var b in buffers)
        {
            if (b is null || b.Count == 0) continue;
            Array.Copy(b.Addresses, 0, addresses, pos, b.Count);
            Array.Copy(b.Interpretations, 0, ids, pos, b.Count);
            Array.Copy(b.Values, 0, values, pos, b.Count);
            pos += b.Count;
        }

        var counts = new int[interpretations.Length];
        foreach (int id in ids) counts[id]++;

        var groups = new List<InterpretationGroup>();
        for (int i = 0; i < interpretations.Length; i++)
        {
            if (counts[i] == 0) continue;
            bool capped = interpCounts.Length > i && interpCounts[i] >= Settings.MaxResultsPerInterpretation;
            groups.Add(new InterpretationGroup(i, interpretations[i], counts[i], capped));
        }

        return new ScanResults
        {
            Interpretations = interpretations,
            Addresses = addresses,
            InterpIds = ids,
            Values = values,
            Count = total,
            Groups = groups,
            Truncated = truncated,
            BytesScanned = bytesScanned,
            Duration = duration
        };
    }

    private void Commit(ScanResults results)
    {
        if (Current is not null) _history.Add(Current);
        if (_history.Count > 16) _history.RemoveAt(0);
        Current = results;
    }

    // ---------------------------------------------------------------- next scan

    public ScanResults NextScan(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var previous = Current ?? throw new ScanException("There is nothing to narrow yet. Run a first scan.");
        if (previous.Count == 0) throw new ScanException("The previous scan found nothing to narrow.");

        if (request.Compare.NeedsValue() && !request.Value.IsValid)
            throw new ScanException("Enter a value for this comparison.");
        if (request.Compare.NeedsSecondValue() && !request.Value2.IsValid)
            throw new ScanException("This comparison needs two values.");

        var interpretations = previous.Interpretations;
        var ranges = new (bool Ok, ulong Lo, ulong Hi)[interpretations.Length];
        if (request.Compare.IsRangeComparison())
        {
            bool any = false;
            for (int i = 0; i < interpretations.Length; i++)
            {
                bool ok = ScanPlan.TryBuildRange(interpretations[i], request.Compare, request.Value, request.Value2,
                    request.Rounding, out ulong lo, out ulong hi);
                ranges[i] = (ok, lo, hi);
                any |= ok;
            }
            if (!any) throw new ScanException("No surviving theory can hold that value.");
        }

        (double Lo, double Hi) deltaWindow = default;
        if (request.Compare is CompareKind.IncreasedBy or CompareKind.DecreasedBy)
            deltaWindow = request.Value.Window(request.Rounding);

        (bool Ok, ulong Lo, ulong Hi)[] fromRanges = [];
        (bool Ok, ulong Lo, ulong Hi)[] toRanges = [];
        if (request.Compare == CompareKind.ChangedFromTo)
        {
            fromRanges = new (bool, ulong, ulong)[interpretations.Length];
            toRanges = new (bool, ulong, ulong)[interpretations.Length];
            for (int i = 0; i < interpretations.Length; i++)
            {
                fromRanges[i] = (interpretations[i].TryEncodeRange(request.Value, request.Rounding, out ulong flo, out ulong fhi), flo, fhi);
                toRanges[i] = (interpretations[i].TryEncodeRange(request.Value2, request.Rounding, out ulong tlo, out ulong thi), tlo, thi);
            }
        }

        HashSet<int>? allowed = request.RestrictToInterpretations is { Length: > 0 } r ? [.. r] : null;

        int workers = Math.Max(1, Math.Min(WorkerCount, Math.Max(1, previous.Count / 4096)));
        var partitions = new HitBuffer[workers];
        int perPartition = (previous.Count + workers - 1) / workers;
        long processed = 0;
        var sw = Stopwatch.StartNew();

        Parallel.For(0, workers, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers }, p =>
        {
            int start = p * perPartition;
            int end = Math.Min(previous.Count, start + perPartition);
            var sink = new HitBuffer(Math.Max(64, (end - start) / 8));
            partitions[p] = sink;
            if (start >= end) return;

            byte[] window = ArrayPool<byte>.Shared.Rent(WindowSize);
            var runs = new List<ValidRun>();
            ulong windowBase = 0;
            int windowLen = 0;

            try
            {
                for (int i = start; i < end; i++)
                {
                    if ((i & 8191) == 0) ct.ThrowIfCancellationRequested();

                    ulong address = previous.Addresses[i];
                    int interpId = previous.InterpIds[i];
                    if (allowed is not null && !allowed.Contains(interpId)) continue;

                    var interp = interpretations[interpId];
                    int width = interp.Width;

                    if (address < windowBase || address + (ulong)width > windowBase + (ulong)windowLen)
                    {
                        windowBase = address;
                        windowLen = 0;
                        int want = WindowSize;
                        Process.ReadRuns(address, window.AsSpan(0, want), runs);
                        if (runs.Count > 0 && runs[0].Offset == 0) windowLen = runs[0].Length;
                        if (windowLen < width) continue;
                    }

                    int offset = (int)(address - windowBase);
                    ulong current = Raw.ReadBits(interp.Type, window.AsSpan(offset, width));
                    ulong before = previous.Values[i];

                    if (Keep(request, interp, interpId, current, before, ranges, fromRanges, toRanges, deltaWindow))
                        sink.Add(address, interpId, current);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(window);
                Interlocked.Add(ref processed, end - start);
                progress?.Report(new ScanProgress("Narrowing", Volatile.Read(ref processed), previous.Count, sink.Count));
            }
        });

        sw.Stop();

        int total = 0;
        foreach (var part in partitions) total += part?.Count ?? 0;

        var addresses = new ulong[total];
        var ids = new int[total];
        var values = new ulong[total];
        int pos = 0;
        foreach (var part in partitions)
        {
            if (part is null || part.Count == 0) continue;
            Array.Copy(part.Addresses, 0, addresses, pos, part.Count);
            Array.Copy(part.Interpretations, 0, ids, pos, part.Count);
            Array.Copy(part.Values, 0, values, pos, part.Count);
            pos += part.Count;
        }

        var counts = new int[interpretations.Length];
        foreach (int id in ids) counts[id]++;
        var groups = new List<InterpretationGroup>();
        for (int i = 0; i < interpretations.Length; i++)
        {
            if (counts[i] == 0) continue;
            groups.Add(new InterpretationGroup(i, interpretations[i], counts[i], false));
        }

        var results = new ScanResults
        {
            Interpretations = interpretations,
            Addresses = addresses,
            InterpIds = ids,
            Values = values,
            Count = total,
            Groups = groups,
            Truncated = previous.Truncated,
            BytesScanned = (long)previous.Count * 8,
            Duration = sw.Elapsed
        };

        Commit(results);
        return results;
    }

    private static bool Keep(ScanRequest request, in Interpretation interp, int interpId, ulong current, ulong before,
        (bool Ok, ulong Lo, ulong Hi)[] ranges,
        (bool Ok, ulong Lo, ulong Hi)[] fromRanges,
        (bool Ok, ulong Lo, ulong Hi)[] toRanges,
        (double Lo, double Hi) deltaWindow)
    {
        switch (request.Compare)
        {
            case CompareKind.EqualTo:
            case CompareKind.GreaterThan:
            case CompareKind.LessThan:
            case CompareKind.Between:
            {
                var r = ranges[interpId];
                return r.Ok && InRange(interp.Type, current, r.Lo, r.Hi);
            }
            case CompareKind.NotEqualTo:
            {
                var r = ranges[interpId];
                return r.Ok && !InRange(interp.Type, current, r.Lo, r.Hi);
            }
            case CompareKind.Changed:
                return current != before;
            case CompareKind.Unchanged:
                return current == before;
            case CompareKind.Increased:
                return CompareDisplay(interp, current, before) > 0;
            case CompareKind.Decreased:
                return CompareDisplay(interp, current, before) < 0;
            case CompareKind.IncreasedBy:
            {
                double delta = interp.Decode(current) - interp.Decode(before);
                return delta >= deltaWindow.Lo && delta <= deltaWindow.Hi;
            }
            case CompareKind.DecreasedBy:
            {
                double delta = interp.Decode(before) - interp.Decode(current);
                return delta >= deltaWindow.Lo && delta <= deltaWindow.Hi;
            }
            case CompareKind.ChangedFromTo:
            {
                var f = fromRanges[interpId];
                var t = toRanges[interpId];
                return f.Ok && t.Ok
                    && InRange(interp.Type, before, f.Lo, f.Hi)
                    && InRange(interp.Type, current, t.Lo, t.Hi);
            }
            default:
                return false;
        }
    }

    private static bool InRange(ScanType type, ulong value, ulong lo, ulong hi) =>
        Raw.Compare(type, value, lo) >= 0 && Raw.Compare(type, value, hi) <= 0;

    /// <summary>
    /// Orders two stored patterns the way the player would see them. Byte-swapped and XORed
    /// storage does not order the same way as its raw bytes, so that layer is undone first —
    /// exactly, rather than by decoding to double, which would collapse 64-bit values above
    /// 2^53 and make a genuine increase look like no change at all.
    /// </summary>
    private static int CompareDisplay(in Interpretation interp, ulong a, ulong b) =>
        interp.PointOnly
            ? Raw.Compare(interp.Type, interp.Unscramble(a), interp.Unscramble(b))
            : Raw.Compare(interp.Type, a, b);

    /// <summary>Re-reads the current stored value at one result index.</summary>
    public bool TryReadCurrent(int index, out ulong bits)
    {
        bits = 0;
        var current = Current;
        if (current is null || index < 0 || index >= current.Count) return false;

        var interp = current.InterpretationAt(index);
        Span<byte> buffer = stackalloc byte[8];
        if (!Process.ReadExact(current.Addresses[index], buffer[..interp.Width])) return false;
        bits = Raw.ReadBits(interp.Type, buffer);
        return true;
    }
}

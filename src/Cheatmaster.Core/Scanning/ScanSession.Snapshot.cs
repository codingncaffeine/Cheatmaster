using System.Buffers;
using System.Diagnostics;

namespace Cheatmaster.Core.Scanning;

/// <summary>
/// Searching without knowing the value, and searching for values whose storage is scrambled by
/// a key the game picked at random.
///
/// Both work the same way: keep a copy of memory from one moment, then compare it against
/// memory now. What the user knows is not the number in memory but how it *moved*, and a
/// relationship between two readings survives encodings that hide the number itself.
/// </summary>
public sealed partial class ScanSession
{
    private MemorySnapshot? _snapshot;
    private UserValue _snapshotValue = UserValue.Invalid;

    public bool HasSnapshot => _snapshot is not null;
    public MemorySnapshot? Snapshot => _snapshot;

    /// <summary>The value the user said was on screen when the snapshot was taken, if they said.</summary>
    public UserValue SnapshotValue => _snapshotValue;

    public void ClearSnapshot()
    {
        _snapshot = null;
        _snapshotValue = UserValue.Invalid;
    }

    public MemorySnapshot CaptureSnapshot(UserValue valueAtCapture, IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        _snapshot = MemorySnapshot.Capture(Process, Settings.Regions.ToFilter(), Settings.SnapshotBudget, progress, ct);
        _snapshotValue = valueAtCapture;
        return _snapshot;
    }

    // ---------------------------------------------------------------- unknown initial value

    /// <summary>
    /// Narrows against the snapshot using only how the value moved: changed, unchanged, went up,
    /// went down, or moved by a known amount. This is the fallback when the number on screen is
    /// not a number you can type.
    /// </summary>
    public ScanResults SnapshotScan(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var snapshot = _snapshot ?? throw new ScanException("Capture memory first, then change the value and scan.");

        DiffOp op = request.Compare switch
        {
            CompareKind.Changed => DiffOp.Changed,
            CompareKind.Unchanged => DiffOp.Unchanged,
            CompareKind.Increased => DiffOp.Increased,
            CompareKind.Decreased => DiffOp.Decreased,
            CompareKind.IncreasedBy or CompareKind.DecreasedBy => DiffOp.DeltaEquals,
            _ => throw new ScanException($"'{request.Compare.Label()}' cannot be compared against a snapshot.")
        };

        var types = PlainTypes(request);
        var passes = new List<DiffPass>(types.Count);

        foreach (var type in types)
        {
            ulong operand = 0;
            if (op == DiffOp.DeltaEquals)
            {
                // before - after is positive when the value went down.
                if (!TryDelta(type, request.Value, request.Compare == CompareKind.DecreasedBy, out operand))
                    continue;
            }
            passes.Add(new DiffPass(type, op, operand));
        }

        if (passes.Count == 0)
            throw new ScanException("That amount cannot be represented in any of the selected types.");

        var clock = Stopwatch.StartNew();
        var hits = CompareWithSnapshot(snapshot, passes, Settings.MaxResults, progress, ct);
        clock.Stop();

        var results = BuildPlainResults(hits, snapshot.TotalBytes, clock.Elapsed);
        Commit(results);
        return results;
    }

    private static bool TryDelta(ScanType type, in UserValue amount, bool decreased, out ulong operandBits)
    {
        operandBits = 0;
        double magnitude = amount.FitsDecimal ? (double)amount.Dec : amount.Dbl;
        double delta = decreased ? magnitude : -magnitude;
        return Raw.TryFromDouble(type, delta, out operandBits);
    }

    // ---------------------------------------------------------------- obfuscated values

    /// <summary>
    /// "It was A, now it is B." Matches plain storage, storage with a constant offset, and
    /// storage XORed with a key chosen at run time — and recovers that key, so the value can be
    /// written afterwards as well as read.
    /// </summary>
    public ScanResults DifferentialScan(in UserValue before, in UserValue after, ScanRequest request,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var snapshot = _snapshot ?? throw new ScanException("Capture memory first, then change the value and scan.");
        if (!before.IsValid || !after.IsValid)
            throw new ScanException("Enter the value before the change and the value now.");

        double beforeValue = before.FitsDecimal ? (double)before.Dec : before.Dbl;
        double afterValue = after.FitsDecimal ? (double)after.Dec : after.Dbl;
        if (Math.Abs(beforeValue - afterValue) < double.Epsilon)
            throw new ScanException("The two values are the same. Change the value in the game first, then scan.");

        var types = PlainTypes(request);
        var passes = new List<DiffPass>(types.Count * 2);
        var encoded = new Dictionary<ScanType, (ulong Before, ulong After)>();

        foreach (var type in types)
        {
            if (!Raw.TryFromDouble(type, beforeValue, out ulong encBefore)) continue;
            if (!Raw.TryFromDouble(type, afterValue, out ulong encAfter)) continue;
            encoded[type] = (encBefore, encAfter);

            ulong mask = Raw.Mask(type);
            passes.Add(new DiffPass(type, DiffOp.XorEquals, (encBefore ^ encAfter) & mask));

            // An additive offset only makes sense where the offset itself is a whole number.
            if (!type.IsFloat())
            {
                ulong delta = unchecked(encBefore - encAfter) & mask;
                passes.Add(new DiffPass(type, DiffOp.DeltaEquals, delta));
            }
        }

        if (passes.Count == 0)
            throw new ScanException("Neither value fits any of the selected types.");

        var clock = Stopwatch.StartNew();
        var hits = CompareWithSnapshot(snapshot, passes, Settings.MaxResults, progress, ct);
        clock.Stop();

        var results = BuildRecoveredResults(hits, encoded, snapshot.TotalBytes, clock.Elapsed);
        Commit(results);
        return results;
    }

    // ---------------------------------------------------------------- shared machinery

    private readonly record struct DiffPass(ScanType Type, DiffOp Op, ulong Operand);

    private readonly record struct SnapshotChunk(int RegionIndex, int Offset, int CoreLength, int ReadLength);

    private static List<ScanType> PlainTypes(ScanRequest request)
    {
        if (request.ForcedType is ScanType only) return [only];

        return request.Profile switch
        {
            ScanProfile.Fast => [ScanType.Int32, ScanType.Float, ScanType.Int64, ScanType.Double],
            _ => [.. ScanTypes.All]
        };
    }

    private List<DiffHit> CompareWithSnapshot(MemorySnapshot snapshot, List<DiffPass> passes, int limit,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        int maxWidth = 1;
        foreach (var pass in passes) maxWidth = Math.Max(maxWidth, pass.Type.Width());

        var chunks = new List<SnapshotChunk>();
        int chunkSize = Settings.ChunkSize;
        for (int i = 0; i < snapshot.Regions.Count; i++)
        {
            int size = (int)Math.Min(snapshot.Regions[i].Size, int.MaxValue);
            for (int offset = 0; offset < size; offset += chunkSize)
            {
                int core = Math.Min(chunkSize, size - offset);
                int extra = Math.Min(maxWidth - 1, size - offset - core);
                chunks.Add(new SnapshotChunk(i, offset, core, core + extra));
            }
        }

        long totalBytes = 0;
        foreach (var c in chunks) totalBytes += c.CoreLength;

        var perChunk = new DiffSink?[chunks.Count];
        long bytesDone = 0;
        long found = 0;
        int alignment = Settings.Alignment;
        int perChunkLimit = Math.Max(1024, limit / Math.Max(1, chunks.Count / 4));

        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = WorkerCount };

        Parallel.For(0, chunks.Count, options,
            () => new WorkerState { Buffer = ArrayPool<byte>.Shared.Rent(chunkSize + 16), Runs = [] },
            (index, _, state) =>
            {
                var chunk = chunks[index];
                var region = snapshot.Regions[chunk.RegionIndex];
                ulong chunkBase = region.Base + (ulong)chunk.Offset;

                var live = state.Buffer.AsSpan(0, chunk.ReadLength);
                int read = Process.ReadRuns(chunkBase, live, state.Runs);
                Interlocked.Add(ref bytesDone, chunk.CoreLength);

                if (read > 0)
                {
                    var stored = snapshot.DataFor(chunk.RegionIndex);
                    DiffSink? sink = null;

                    foreach (var run in state.Runs)
                    {
                        int core = Math.Min(run.Length, chunk.CoreLength - run.Offset);
                        if (core <= 0) continue;

                        int storedOffset = chunk.Offset + run.Offset;
                        if (storedOffset >= stored.Length) continue;
                        int available = Math.Min(run.Length, stored.Length - storedOffset);
                        if (available <= 0) continue;

                        var beforeSpan = stored.Slice(storedOffset, available);
                        var afterSpan = state.Buffer.AsSpan(run.Offset, available);
                        ulong runBase = chunkBase + (ulong)run.Offset;

                        foreach (var pass in passes)
                        {
                            sink ??= new DiffSink { Limit = perChunkLimit };
                            DiffKernel.Compare(pass.Type, pass.Op, pass.Operand, beforeSpan, afterSpan,
                                runBase, Math.Min(core, available), alignment, sink);
                            if (sink.Full) break;
                        }
                    }

                    if (sink is { Hits.Count: > 0 })
                    {
                        perChunk[index] = sink;
                        Interlocked.Add(ref found, sink.Hits.Count);
                    }
                }

                if (progress is not null && (index & 15) == 0)
                    progress.Report(new ScanProgress("Comparing", Volatile.Read(ref bytesDone), totalBytes, Volatile.Read(ref found)));

                return state;
            },
            state => ArrayPool<byte>.Shared.Return(state.Buffer));

        progress?.Report(new ScanProgress("Comparing", totalBytes, totalBytes, Volatile.Read(ref found)));

        var all = new List<DiffHit>((int)Math.Min(found, limit));
        foreach (var sink in perChunk)
        {
            if (sink is null) continue;
            foreach (var hit in sink.Hits)
            {
                if (all.Count >= limit) break;
                all.Add(hit);
            }
        }

        all.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        return all;
    }

    private ScanResults BuildPlainResults(List<DiffHit> hits, long bytesScanned, TimeSpan duration)
    {
        var interpretations = new Interpretation[ScanTypes.All.Length];
        for (int i = 0; i < ScanTypes.All.Length; i++)
            interpretations[i] = Interpretation.Plain(ScanTypes.All[i]);

        var addresses = new ulong[hits.Count];
        var ids = new int[hits.Count];
        var values = new ulong[hits.Count];
        var counts = new int[interpretations.Length];

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            int id = Array.IndexOf(ScanTypes.All, hit.Type);
            addresses[i] = hit.Address;
            ids[i] = id;
            values[i] = hit.After;
            counts[id]++;
        }

        return Assemble(interpretations, addresses, ids, values, counts, bytesScanned, duration);
    }

    private ScanResults BuildRecoveredResults(List<DiffHit> hits,
        Dictionary<ScanType, (ulong Before, ulong After)> encoded, long bytesScanned, TimeSpan duration)
    {
        // One address can match under more than one theory. Keep the least exotic explanation.
        var best = new Dictionary<(ulong Address, ScanType Type), (Interpretation Interp, int Rank, ulong Value)>();

        foreach (var hit in hits)
        {
            if (!encoded.TryGetValue(hit.Type, out var enc)) continue;
            if (!TryRecover(hit, enc.Before, out Interpretation interp, out int rank)) continue;

            var key = (hit.Address, hit.Type);
            if (best.TryGetValue(key, out var existing) && existing.Rank <= rank) continue;
            best[key] = (interp, rank, hit.After);
        }

        var interpIds = new Dictionary<Interpretation, int>();
        var interpretations = new List<Interpretation>();
        var ordered = best.OrderBy(static kv => kv.Key.Address).ToList();

        var addresses = new ulong[ordered.Count];
        var ids = new int[ordered.Count];
        var values = new ulong[ordered.Count];

        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            if (!interpIds.TryGetValue(entry.Value.Interp, out int id))
            {
                id = interpretations.Count;
                interpIds[entry.Value.Interp] = id;
                interpretations.Add(entry.Value.Interp);
            }

            addresses[i] = entry.Key.Address;
            ids[i] = id;
            values[i] = entry.Value.Value;
        }

        var counts = new int[interpretations.Count];
        foreach (int id in ids) counts[id]++;

        return Assemble([.. interpretations], addresses, ids, values, counts, bytesScanned, duration);
    }

    /// <summary>
    /// Works out which encoding explains a matching pair, and recovers its key or offset.
    /// Rank orders the explanations so a plain value is never reported as an exotic one.
    /// </summary>
    private static bool TryRecover(in DiffHit hit, ulong encodedBefore, out Interpretation interpretation, out int rank)
    {
        interpretation = Interpretation.Plain(hit.Type);
        rank = 0;
        ulong mask = Raw.Mask(hit.Type);

        if (hit.Before == (encodedBefore & mask))
            return true;   // plain storage: the snapshot already held the typed value

        if (hit.Relation == DiffOp.XorEquals)
        {
            ulong key = (hit.Before ^ encodedBefore) & mask;
            if (key == 0) return true;
            interpretation = Interpretation.Plain(hit.Type).WithXor(key);
            rank = 2;
            return true;
        }

        // A constant offset, which only makes sense for whole numbers.
        if (hit.Type.IsFloat()) return false;

        double stored = Raw.ToDouble(hit.Type, hit.Before);
        double wanted = Raw.ToDouble(hit.Type, encodedBefore);
        double bias = stored - wanted;
        if (Math.Abs(bias) > long.MaxValue || bias != Math.Truncate(bias)) return false;

        interpretation = Interpretation.Plain(hit.Type).WithBias((long)bias);
        rank = 1;
        return true;
    }

    private ScanResults Assemble(Interpretation[] interpretations, ulong[] addresses, int[] ids, ulong[] values,
        int[] counts, long bytesScanned, TimeSpan duration)
    {
        var groups = new List<InterpretationGroup>();
        for (int i = 0; i < interpretations.Length; i++)
        {
            if (i < counts.Length && counts[i] > 0)
                groups.Add(new InterpretationGroup(i, interpretations[i], counts[i], counts[i] >= Settings.MaxResultsPerInterpretation));
        }

        return new ScanResults
        {
            Interpretations = interpretations,
            Addresses = addresses,
            InterpIds = ids,
            Values = values,
            Count = addresses.Length,
            Groups = groups,
            BytesScanned = bytesScanned,
            Duration = duration
        };
    }
}

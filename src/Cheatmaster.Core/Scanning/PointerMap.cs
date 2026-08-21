using System.Buffers;
using System.Runtime.InteropServices;
using Cheatmaster.Core.Memory;

namespace Cheatmaster.Core.Scanning;

/// <summary>
/// Every pointer-looking value in the target, indexed by what it points at.
///
/// This is the raw material for finding a route to a value that lands somewhere new each time
/// the game starts. A heap address is worthless tomorrow, but the chain of pointers that leads
/// to it from a fixed place in the executable is not, and that chain can only be found by
/// asking, repeatedly, "what points here?".
/// </summary>
public sealed class PointerMap
{
    private readonly ulong[] _targets;   // what the pointer points at, ascending
    private readonly ulong[] _sources;   // where that pointer was found

    private PointerMap(ulong[] targets, ulong[] sources, int pointerSize, long bytesScanned)
    {
        _targets = targets;
        _sources = sources;
        PointerSize = pointerSize;
        BytesScanned = bytesScanned;
    }

    public int Count => _targets.Length;
    public int PointerSize { get; }
    public long BytesScanned { get; }

    /// <summary>
    /// Builds the index. Only values that land inside committed memory are kept, which throws
    /// away the overwhelming majority of words and keeps this affordable.
    /// </summary>
    public static PointerMap Build(TargetProcess process, RegionFilter filter, int maxPointers = 40_000_000,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        int pointerSize = process.Is64Bit ? 8 : 4;

        // Pointers can live anywhere readable, not only in writable pages.
        var searched = process.EnumerateRegions(filter);
        if (searched.Count == 0) throw new ScanException("No readable memory matched the region filter.");

        // A candidate only counts if it points into memory that exists.
        var addressable = process.EnumerateRegions(RegionFilter.Everything);
        if (addressable.Count == 0) throw new ScanException("The target has no readable memory.");

        ulong lowest = addressable[0].Base;
        ulong highest = addressable[^1].End;

        long total = 0;
        foreach (var region in searched) total += (long)region.Size;

        var targets = new List<ulong>(1 << 20);
        var sources = new List<ulong>(1 << 20);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        var runs = new List<ValidRun>();
        long done = 0;

        try
        {
            foreach (var region in searched)
            {
                for (ulong at = region.Base; at < region.End; at += (ulong)buffer.Length)
                {
                    ct.ThrowIfCancellationRequested();

                    int length = (int)Math.Min((ulong)buffer.Length, region.End - at);
                    process.ReadRuns(at, buffer.AsSpan(0, length), runs);
                    done += length;

                    foreach (var run in runs)
                    {
                        var span = buffer.AsSpan(run.Offset, run.Length);
                        ulong runBase = at + (ulong)run.Offset;

                        if (pointerSize == 8) Collect64(span, runBase, lowest, highest, addressable, targets, sources);
                        else Collect32(span, runBase, lowest, highest, addressable, targets, sources);
                    }

                    if (targets.Count >= maxPointers) goto finished;
                    progress?.Report(new ScanProgress("Indexing pointers", done, total, targets.Count));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

    finished:
        var targetArray = targets.ToArray();
        var sourceArray = sources.ToArray();
        Array.Sort(targetArray, sourceArray);

        progress?.Report(new ScanProgress("Indexing pointers", total, total, targetArray.Length));
        return new PointerMap(targetArray, sourceArray, pointerSize, done);
    }

    private static void Collect64(ReadOnlySpan<byte> span, ulong runBase, ulong lowest, ulong highest,
        List<MemoryRegion> addressable, List<ulong> targets, List<ulong> sources)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(span);
        for (int i = 0; i < words.Length; i++)
        {
            ulong value = words[i];
            if (value < lowest || value >= highest) continue;
            if ((value & 3) != 0) continue;               // real pointers are aligned
            if (!IsCommitted(addressable, value)) continue;

            targets.Add(value);
            sources.Add(runBase + (ulong)(i * 8));
        }
    }

    private static void Collect32(ReadOnlySpan<byte> span, ulong runBase, ulong lowest, ulong highest,
        List<MemoryRegion> addressable, List<ulong> targets, List<ulong> sources)
    {
        var words = MemoryMarshal.Cast<byte, uint>(span);
        for (int i = 0; i < words.Length; i++)
        {
            ulong value = words[i];
            if (value < lowest || value >= highest) continue;
            if ((value & 3) != 0) continue;
            if (!IsCommitted(addressable, value)) continue;

            targets.Add(value);
            sources.Add(runBase + (ulong)(i * 4));
        }
    }

    private static bool IsCommitted(List<MemoryRegion> regions, ulong address)
    {
        int lo = 0, hi = regions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var region = regions[mid];
            if (address < region.Base) hi = mid - 1;
            else if (address >= region.End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>
    /// Every place holding a pointer that lands within <paramref name="maxOffset"/> bytes before
    /// the address — that is, every candidate for "this address is a field of that object".
    /// </summary>
    public void FindPointersTo(ulong address, int maxOffset, List<(ulong Source, int Offset)> results)
    {
        results.Clear();
        ulong lowest = address >= (ulong)maxOffset ? address - (ulong)maxOffset : 0;

        int start = LowerBound(lowest);
        for (int i = start; i < _targets.Length && _targets[i] <= address; i++)
            results.Add((_sources[i], (int)(address - _targets[i])));
    }

    private int LowerBound(ulong value)
    {
        int lo = 0, hi = _targets.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_targets[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

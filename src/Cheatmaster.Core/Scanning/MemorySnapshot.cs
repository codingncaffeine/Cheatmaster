using Cheatmaster.Core.Memory;

namespace Cheatmaster.Core.Scanning;

/// <summary>
/// A copy of the searched area taken at one moment. Holding the raw bytes rather than a record
/// per candidate is what makes unknown-value searching affordable: the cost is the size of the
/// area, not the number of addresses in it.
/// </summary>
public sealed class MemorySnapshot
{
    private readonly MemoryRegion[] _regions;
    private readonly byte[][] _data;

    private MemorySnapshot(MemoryRegion[] regions, byte[][] data, long totalBytes, TimeSpan duration)
    {
        _regions = regions;
        _data = data;
        TotalBytes = totalBytes;
        Duration = duration;
        TakenAt = DateTimeOffset.Now;
    }

    public long TotalBytes { get; }
    public TimeSpan Duration { get; }
    public DateTimeOffset TakenAt { get; }
    public int RegionCount => _regions.Length;

    public IReadOnlyList<MemoryRegion> Regions => _regions;

    public ReadOnlySpan<byte> DataFor(int regionIndex) => _data[regionIndex];

    public static MemorySnapshot Capture(TargetProcess process, RegionFilter filter, long budgetBytes,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var regions = process.EnumerateRegions(filter);
        if (regions.Count == 0)
            throw new ScanException("No readable memory matched the region filter.");

        long total = 0;
        foreach (var r in regions) total += (long)r.Size;

        if (budgetBytes > 0 && total > budgetBytes)
        {
            throw new ScanException(
                $"Capturing {total / (1024 * 1024)} MB would exceed the snapshot budget of {budgetBytes / (1024 * 1024)} MB. " +
                "Narrow the region filter, or raise the budget in scan options.");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var data = new byte[regions.Count][];
        long captured = 0;
        var runs = new List<ValidRun>();

        for (int i = 0; i < regions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var region = regions[i];
            var buffer = new byte[region.Size];
            int read = process.ReadRuns(region.Base, buffer, runs);

            // Pages that could not be read stay zero. They are compared like any other bytes,
            // and a page that fails on one pass and succeeds on the next simply looks changed.
            data[i] = buffer;
            captured += read;

            if ((i & 15) == 0)
                progress?.Report(new ScanProgress("Capturing", captured, total, 0));
        }

        clock.Stop();
        progress?.Report(new ScanProgress("Capturing", total, total, 0));
        return new MemorySnapshot([.. regions], data, captured, clock.Elapsed);
    }

    /// <summary>Finds the region holding an address, or -1.</summary>
    public int RegionIndexOf(ulong address)
    {
        int lo = 0, hi = _regions.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var r = _regions[mid];
            if (address < r.Base) hi = mid - 1;
            else if (address >= r.End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    public bool TryReadBits(ScanType type, ulong address, out ulong bits)
    {
        bits = 0;
        int index = RegionIndexOf(address);
        if (index < 0) return false;

        var region = _regions[index];
        int width = type.Width();
        ulong offset = address - region.Base;
        if (offset + (ulong)width > region.Size) return false;

        bits = Raw.ReadBits(type, _data[index].AsSpan((int)offset, width));
        return true;
    }
}

using System.Runtime.InteropServices;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// A small number in a big game matches millions of places, more than one theory is allowed to
/// keep. Whatever the cap left out must not be lost for good: the next scan with a value has to
/// find the real address even when the first scan never kept it.
/// </summary>
public class CappedScanTests
{
    [Fact]
    public void A_value_the_first_scan_capped_away_is_found_by_the_next_scan()
    {
        // Every slot holds 7, the target sits last, so a cap of 100 keeps the first hundred and not it.
        int[] memory = new int[64 * 1024];
        Array.Fill(memory, 7);
        int targetIndex = memory.Length - 1;

        var pin = GCHandle.Alloc(memory, GCHandleType.Pinned);
        try
        {
            ulong start = (ulong)pin.AddrOfPinnedObject();
            ulong target = start + (ulong)targetIndex * 4;
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var settings = new ScanSettings
            {
                Alignment = 4,
                // The cap is checked between chunks: small chunks on one worker make it bite early and in order.
                ChunkSize = 4096,
                WorkerCount = 1,
                MaxResultsPerInterpretation = 100,
                Regions = new RegionFilterSettings { Start = start, End = start + (ulong)memory.Length * 4 }
            };
            var session = new ScanSession(process!, settings);

            var first = session.FirstScan(new ScanRequest { Value = UserValue.Parse("7"), ForcedType = ScanType.Int32, Rounding = RoundingMode.Exact });
            Assert.Contains(first.Groups, g => g.Capped);
            Assert.DoesNotContain(target, first.Addresses.Take(first.Count));

            // The player earns ten more; everything else stays at 7.
            memory[targetIndex] = 17;
            var next = session.NextScan(new ScanRequest { Value = UserValue.Parse("17"), ForcedType = ScanType.Int32, Rounding = RoundingMode.Exact });

            Assert.Contains(target, next.Addresses.Take(next.Count));
        }
        finally
        {
            pin.Free();
        }
    }
}

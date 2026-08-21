using System.Runtime.InteropServices;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

public class ScanKernelTests
{
    private const ulong Base = 0x1000_0000;

    private static void Plant(ScanType type, byte[] buffer, int offset, double value)
    {
        Assert.True(Raw.TryFromDouble(type, value, out ulong bits));
        Raw.WriteBytes(type, bits, buffer.AsSpan(offset));
    }

    private static List<ulong> Run(ScanType type, byte[] buffer, int alignment, UserValue value, RoundingMode mode,
        Interpretation? custom = null)
    {
        var interp = custom ?? Interpretation.Plain(type);
        Assert.True(interp.TryEncodeRange(value, mode, out ulong lo, out ulong hi));

        var item = new ScanPlanItem(0, interp.Type, lo, hi);
        var sink = new HitBuffer();
        ScanKernel.Scan(item, buffer, Base, buffer.Length, alignment, sink);

        var hits = new List<ulong>();
        for (int i = 0; i < sink.Count; i++) hits.Add(sink.Addresses[i] - Base);
        hits.Sort();
        return hits;
    }

    public static TheoryData<ScanType> AllTypes()
    {
        var data = new TheoryData<ScanType>();
        foreach (var t in ScanTypes.All) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Finds_every_planted_value_at_byte_alignment(ScanType type)
    {
        var buffer = new byte[64 * 1024];
        int[] offsets = [0, 64, 4096, 20000, 4202];
        foreach (int o in offsets) Plant(type, buffer, o, 77);

        var hits = Run(type, buffer, alignment: 1, UserValue.Parse("77"), RoundingMode.Exact);

        foreach (int o in offsets)
            Assert.Contains((ulong)o, hits);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Alignment_limits_candidates_to_multiples(ScanType type)
    {
        var buffer = new byte[64 * 1024];
        int[] aligned = [0, 64, 4096, 20000];
        foreach (int o in aligned) Plant(type, buffer, o, 77);
        Plant(type, buffer, 4202, 77);

        var hits = Run(type, buffer, alignment: 4, UserValue.Parse("77"), RoundingMode.Exact);

        foreach (int o in aligned) Assert.Contains((ulong)o, hits);

        // The scan step is never coarser than the type, so narrow types still see the unaligned plant.
        if (type.Width() >= 4)
            Assert.DoesNotContain(4202UL, hits);
    }

    [Fact]
    public void Vector_path_and_scalar_tail_agree()
    {
        var buffer = new byte[4096 + 12];
        var expected = new List<ulong>();
        for (int o = 0; o + 4 <= buffer.Length; o += 4)
        {
            if (o % 128 == 0)
            {
                Plant(ScanType.Int32, buffer, o, 4242);
                expected.Add((ulong)o);
            }
        }

        var hits = Run(ScanType.Int32, buffer, alignment: 4, UserValue.Parse("4242"), RoundingMode.Exact);
        Assert.Equal(expected, hits);
    }

    [Fact]
    public void Unsigned_ranges_compare_without_sign_confusion()
    {
        var buffer = new byte[8192];
        Plant(ScanType.UInt32, buffer, 0, 4_000_000_000);
        Plant(ScanType.UInt32, buffer, 64, 10);

        var interp = Interpretation.Plain(ScanType.UInt32);
        Assert.True(ScanPlan.TryBuildRange(interp, CompareKind.GreaterThan, UserValue.Parse("1000000"),
            UserValue.Invalid, RoundingMode.Exact, out ulong lo, out ulong hi));

        var item = new ScanPlanItem(0, ScanType.UInt32, lo, hi);
        var sink = new HitBuffer();
        ScanKernel.Scan(item, buffer, Base, buffer.Length, 4, sink);

        var hits = new List<ulong>();
        for (int i = 0; i < sink.Count; i++) hits.Add(sink.Addresses[i] - Base);

        Assert.Contains(0UL, hits);
        Assert.DoesNotContain(64UL, hits);
    }

    [Fact]
    public void Signed_ranges_include_negative_values()
    {
        var buffer = new byte[8192];
        Plant(ScanType.Int32, buffer, 0, -50);
        Plant(ScanType.Int32, buffer, 64, 50);

        var interp = Interpretation.Plain(ScanType.Int32);
        Assert.True(ScanPlan.TryBuildRange(interp, CompareKind.LessThan, UserValue.Parse("0"),
            UserValue.Invalid, RoundingMode.Exact, out ulong lo, out ulong hi));

        var item = new ScanPlanItem(0, ScanType.Int32, lo, hi);
        var sink = new HitBuffer();
        ScanKernel.Scan(item, buffer, Base, buffer.Length, 4, sink);

        var hits = new List<ulong>();
        for (int i = 0; i < sink.Count; i++) hits.Add(sink.Addresses[i] - Base);

        Assert.Contains(0UL, hits);
        Assert.DoesNotContain(64UL, hits);
    }

    [Fact]
    public void Searching_a_whole_number_finds_a_truncated_float()
    {
        var buffer = new byte[8192];
        Plant(ScanType.Float, buffer, 128, 82.67);

        var found = Run(ScanType.Float, buffer, 4, UserValue.Parse("83"), RoundingMode.Display);
        Assert.Contains(128UL, found);

        var exact = Run(ScanType.Float, buffer, 4, UserValue.Parse("83"), RoundingMode.Exact);
        Assert.DoesNotContain(128UL, exact);
    }

    [Fact]
    public void Searching_a_whole_number_finds_percentage_storage()
    {
        var buffer = new byte[8192];
        Plant(ScanType.Float, buffer, 256, 0.75);

        var interp = Interpretation.Plain(ScanType.Float).WithScale(1, 100);
        var found = Run(ScanType.Float, buffer, 4, UserValue.Parse("75"), RoundingMode.Display, interp);
        Assert.Contains(256UL, found);
    }

    [Fact]
    public void Searching_a_whole_number_finds_multiplied_storage()
    {
        var buffer = new byte[8192];
        Plant(ScanType.Int32, buffer, 512, 8300);

        var interp = Interpretation.Plain(ScanType.Int32).WithScale(100, 1);
        var found = Run(ScanType.Int32, buffer, 4, UserValue.Parse("83"), RoundingMode.Display, interp);
        Assert.Contains(512UL, found);
    }

    [Fact]
    public void Searching_finds_xor_obfuscated_storage_when_the_key_is_known()
    {
        var buffer = new byte[8192];
        const uint key = 0xDEADBEEF;
        Plant(ScanType.UInt32, buffer, 640, 77u ^ key);

        var interp = Interpretation.Plain(ScanType.Int32).WithXor(key);
        var found = Run(ScanType.Int32, buffer, 4, UserValue.Parse("77"), RoundingMode.Exact, interp);
        Assert.Contains(640UL, found);
    }

    [Fact]
    public void Searching_finds_byte_swapped_storage()
    {
        var buffer = new byte[8192];
        buffer[704] = 0x00; buffer[705] = 0x00; buffer[706] = 0x00; buffer[707] = 0x4D;

        var interp = Interpretation.Plain(ScanType.Int32) with { BigEndian = true };
        var found = Run(ScanType.Int32, buffer, 4, UserValue.Parse("77"), RoundingMode.Exact, interp);
        Assert.Contains(704UL, found);
    }

    [Fact]
    public void Overlap_region_is_reported_by_the_owning_chunk_only()
    {
        var buffer = new byte[128];
        Plant(ScanType.Int32, buffer, 60, 999);  // inside the core
        Plant(ScanType.Int32, buffer, 68, 999);  // past the core, belongs to the next chunk

        var interp = Interpretation.Plain(ScanType.Int32);
        Assert.True(interp.TryEncodeRange(UserValue.Parse("999"), RoundingMode.Exact, out ulong lo, out ulong hi));

        var sink = new HitBuffer();
        ScanKernel.Scan(new ScanPlanItem(0, ScanType.Int32, lo, hi), buffer, Base, coreLength: 64, alignment: 4, sink);

        Assert.Equal(1, sink.Count);
        Assert.Equal(Base + 60, sink.Addresses[0]);
    }
}

public class LiveScanTests
{
    private const int Magic = 1234567891;

    [Fact]
    public void First_scan_finds_a_known_value_in_this_process()
    {
        int[] payload = new int[256];
        for (int i = 0; i < payload.Length; i++) payload[i] = Magic;

        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var session = new ScanSession(process!, new ScanSettings { Alignment = 4 });
            var results = session.FirstScan(new ScanRequest
            {
                Compare = CompareKind.EqualTo,
                Value = UserValue.Parse(Magic.ToString()),
                Profile = ScanProfile.Fast,
                Rounding = RoundingMode.Exact
            });

            ulong target = (ulong)pin.AddrOfPinnedObject();
            Assert.Contains(target, results.Addresses);
            Assert.True(results.Count > 0);
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Next_scan_narrows_to_a_value_that_changed()
    {
        int[] payload = new int[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = Magic;

        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var session = new ScanSession(process!, new ScanSettings { Alignment = 4 });
            session.FirstScan(new ScanRequest
            {
                Compare = CompareKind.EqualTo,
                Value = UserValue.Parse(Magic.ToString()),
                Profile = ScanProfile.Fast,
                Rounding = RoundingMode.Exact
            });

            const int changed = Magic + 5;
            payload[0] = changed;

            var narrowed = session.NextScan(new ScanRequest
            {
                Compare = CompareKind.EqualTo,
                Value = UserValue.Parse(changed.ToString()),
                Profile = ScanProfile.Fast,
                Rounding = RoundingMode.Exact
            });

            ulong target = (ulong)pin.AddrOfPinnedObject();
            Assert.Contains(target, narrowed.Addresses);
            Assert.True(narrowed.Count < 100000);
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Writing_a_value_changes_the_target()
    {
        int[] payload = [42];
        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            ulong address = (ulong)pin.AddrOfPinnedObject();
            Assert.True(process!.WriteValue(address, 4242));
            Assert.Equal(4242, payload[0]);
            Assert.Equal(4242, process.ReadValue<int>(address));
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Region_enumeration_returns_ordered_readable_regions()
    {
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        var regions = process!.EnumerateRegions(RegionFilter.Default);
        Assert.NotEmpty(regions);

        ulong last = 0;
        foreach (var r in regions)
        {
            Assert.True(r.Base >= last, "regions must be in ascending address order");
            Assert.True(r.Size > 0);
            last = r.Base;
        }
    }
}

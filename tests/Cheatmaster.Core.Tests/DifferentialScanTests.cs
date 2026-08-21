using System.Runtime.InteropServices;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// Exercises the snapshot-based searches against this process, including the case an exact
/// search cannot solve at all: a value stored XORed with a key chosen at run time.
/// </summary>
public class DifferentialScanTests
{
    /// <summary>Restricts the scan to a window around the payload so the test stays quick.</summary>
    private static ScanSettings SettingsAround(ulong address)
    {
        ulong start = address & ~0xFFFFFUL;
        return new ScanSettings
        {
            Alignment = 4,
            Regions = new RegionFilterSettings { Start = start, End = start + (2UL << 20) }
        };
    }

    [Fact]
    public void Recovers_a_value_hidden_behind_a_random_xor_key()
    {
        const uint key = 0x5A17C3E9;
        const int before = 100;
        const int after = 75;

        int[] payload = new int[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = unchecked((int)((uint)before ^ key));

        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            ulong target = (ulong)pin.AddrOfPinnedObject();
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var session = new ScanSession(process!, SettingsAround(target));
            session.CaptureSnapshot(UserValue.Parse(before.ToString()));

            // The game "spends" the value. Nothing that looks like 75 is ever written to memory.
            for (int i = 0; i < payload.Length; i++) payload[i] = unchecked((int)((uint)after ^ key));

            var results = session.DifferentialScan(
                UserValue.Parse(before.ToString()),
                UserValue.Parse(after.ToString()),
                new ScanRequest { Profile = ScanProfile.Fast });

            int index = Array.IndexOf(results.Addresses, target);
            Assert.True(index >= 0, $"the obfuscated address was not found among {results.Count} results");

            var interpretation = results.InterpretationAt(index);
            Assert.Equal(key, (uint)interpretation.XorKey);
            Assert.Equal(after, interpretation.Decode(results.Values[index]), 6);

            // The recovered key is what makes the value writable, not just findable.
            Assert.True(interpretation.TryEncodeExact(999, out ulong encoded));
            Assert.True(process!.Write(target, BitConverter.GetBytes((uint)encoded)));
            Assert.Equal(999, unchecked((int)((uint)payload[0] ^ key)));
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void An_exact_search_cannot_find_the_same_obfuscated_value()
    {
        const uint key = 0x5A17C3E9;
        int[] payload = new int[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = unchecked((int)(100u ^ key));

        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            ulong target = (ulong)pin.AddrOfPinnedObject();
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var session = new ScanSession(process!, SettingsAround(target));
            var results = session.FirstScan(new ScanRequest
            {
                Compare = CompareKind.EqualTo,
                Value = UserValue.Parse("100"),
                Profile = ScanProfile.Thorough,
                Rounding = RoundingMode.Display
            });

            Assert.DoesNotContain(target, results.Addresses);
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Recovers_a_value_stored_with_a_constant_offset()
    {
        const int bias = 4096;
        int[] payload = new int[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = 250 + bias;

        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            ulong target = (ulong)pin.AddrOfPinnedObject();
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var session = new ScanSession(process!, SettingsAround(target));
            session.CaptureSnapshot(UserValue.Parse("250"));

            for (int i = 0; i < payload.Length; i++) payload[i] = 90 + bias;

            var results = session.DifferentialScan(
                UserValue.Parse("250"), UserValue.Parse("90"),
                new ScanRequest { Profile = ScanProfile.Fast });

            int index = Array.IndexOf(results.Addresses, target);
            Assert.True(index >= 0, $"the offset address was not found among {results.Count} results");
            Assert.Equal(90, results.InterpretationAt(index).Decode(results.Values[index]), 6);
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Plain_storage_is_reported_as_plain_not_as_an_exotic_encoding()
    {
        int[] payload = new int[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = 500;

        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            ulong target = (ulong)pin.AddrOfPinnedObject();
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var session = new ScanSession(process!, SettingsAround(target));
            session.CaptureSnapshot(UserValue.Parse("500"));

            for (int i = 0; i < payload.Length; i++) payload[i] = 300;

            var results = session.DifferentialScan(
                UserValue.Parse("500"), UserValue.Parse("300"),
                new ScanRequest { Profile = ScanProfile.Fast });

            int index = Array.IndexOf(results.Addresses, target);
            Assert.True(index >= 0, "the plain address was not found");
            Assert.True(results.InterpretationAt(index).IsPlain, "plain storage was reported as something exotic");
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Unknown_initial_value_narrows_by_how_the_value_moved()
    {
        int[] payload = new int[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = 10_000 + i;

        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            ulong target = (ulong)pin.AddrOfPinnedObject();
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            var session = new ScanSession(process!, SettingsAround(target));
            session.CaptureSnapshot(UserValue.Invalid);

            for (int i = 0; i < payload.Length; i++) payload[i] -= 7;

            var results = session.SnapshotScan(new ScanRequest
            {
                Compare = CompareKind.DecreasedBy,
                Value = UserValue.Parse("7"),
                Profile = ScanProfile.Fast
            });

            Assert.Contains(target, results.Addresses);
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Capturing_more_than_the_budget_is_refused_with_an_explanation()
    {
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        var session = new ScanSession(process!, new ScanSettings { SnapshotBudget = 1024 });
        var thrown = Assert.Throws<ScanException>(() => session.CaptureSnapshot(UserValue.Invalid));
        Assert.Contains("budget", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }
}

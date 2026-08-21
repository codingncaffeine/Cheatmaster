using System.Runtime.InteropServices;
using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// A heap address is different every launch, so a saved cheat is only worth anything if it
/// records the route to the value rather than the value's current address. These build a real
/// two-level pointer chain in this process and check that what the scanner discovers walking
/// backwards is exactly what the resolver produces walking forwards.
/// </summary>
public class PointerScanTests
{
    private sealed class Chain : IDisposable
    {
        private readonly GCHandle _leaf;
        private readonly GCHandle _mid;
        private readonly GCHandle _root;

        public Chain()
        {
            var leaf = new int[64];
            var mid = new nint[8];
            var root = new nint[8];

            _leaf = GCHandle.Alloc(leaf, GCHandleType.Pinned);
            _mid = GCHandle.Alloc(mid, GCHandleType.Pinned);
            _root = GCHandle.Alloc(root, GCHandleType.Pinned);

            LeafBase = (ulong)_leaf.AddrOfPinnedObject();
            MidBase = (ulong)_mid.AddrOfPinnedObject();
            RootBase = (ulong)_root.AddrOfPinnedObject();

            // root+8  ->  mid          then +16
            // mid+16  ->  leaf         then +8
            // so the value sits at leaf+8, reached as [[root+8]+16]+8
            mid[2] = (nint)LeafBase;
            root[1] = (nint)MidBase;
            leaf[2] = 1234567;
        }

        public ulong LeafBase { get; }
        public ulong MidBase { get; }
        public ulong RootBase { get; }

        public ulong Target => LeafBase + 8;
        public ulong RootSlot => RootBase + 8;
        public ulong MidSlot => MidBase + 16;

        /// <summary>A window covering all three allocations, to keep the pointer index small.</summary>
        public RegionFilter Window()
        {
            ulong low = Math.Min(LeafBase, Math.Min(MidBase, RootBase));
            ulong high = Math.Max(LeafBase, Math.Max(MidBase, RootBase));
            return new RegionFilter
            {
                WritableOnly = true,
                Start = (low - (2UL << 20)) & ~0xFFFUL,
                End = high + (2UL << 20)
            };
        }

        public void Dispose()
        {
            _leaf.Free();
            _mid.Free();
            _root.Free();
        }
    }

    [Fact]
    public void Finds_what_points_at_an_address_and_at_what_offset()
    {
        using var chain = new Chain();
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        var map = PointerMap.Build(process!, chain.Window());
        Assert.True(map.Count > 0, "the pointer index came back empty");

        var hits = new List<(ulong Source, int Offset)>();

        // One level up: something holds a pointer that lands 8 bytes before the value.
        map.FindPointersTo(chain.Target, 0x800, hits);
        Assert.Contains(hits, h => h.Source == chain.MidSlot && h.Offset == 8);

        // Two levels up: something holds a pointer that lands 16 bytes before that slot.
        map.FindPointersTo(chain.MidSlot, 0x800, hits);
        Assert.Contains(hits, h => h.Source == chain.RootSlot && h.Offset == 16);
    }

    /// <summary>
    /// The offsets discovered walking backwards must be the offsets the resolver needs walking
    /// forwards. If these two disagree, every saved pointer cheat silently reads the wrong place.
    /// </summary>
    [Fact]
    public void A_discovered_chain_resolves_back_to_the_same_address()
    {
        using var chain = new Chain();
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        var map = PointerMap.Build(process!, chain.Window());
        var hits = new List<(ulong Source, int Offset)>();

        map.FindPointersTo(chain.Target, 0x800, hits);
        var first = hits.First(h => h.Source == chain.MidSlot);

        map.FindPointersTo(first.Source, 0x800, hits);
        var second = hits.First(h => h.Source == chain.RootSlot);

        var spec = new AddressSpec
        {
            Offset = second.Source,
            Pointers = [second.Offset, first.Offset]
        };

        Assert.Equal(chain.Target, spec.Resolve(process!));
        Assert.Equal(1234567, process!.ReadValue<int>(spec.Resolve(process)));
    }

    [Fact]
    public void A_path_that_no_longer_resolves_is_rejected()
    {
        using var chain = new Chain();
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        var broken = new PointerPath("does-not-exist.dll", 0x1000, [0x10, 0x20]);
        var survivors = PointerScanner.Verify(process!, [broken], ScanType.Int32, 1234567);

        Assert.Empty(survivors);
    }

    [Fact]
    public void Every_route_it_reports_starts_from_a_module_that_exists()
    {
        using var chain = new Chain();
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        var map = PointerMap.Build(process!, chain.Window());
        var paths = PointerScanner.Find(process!, map, chain.Target,
            new PointerScanOptions { MaxDepth = 3, MaxOffset = 0x800, MaxResults = 20 });

        // The chain here is entirely on the heap, so anything reported must still be anchored
        // to a real module rather than invented.
        foreach (var path in paths)
            Assert.True(process!.FindModule(path.Module) is not null, $"unknown module {path.Module}");
    }
}

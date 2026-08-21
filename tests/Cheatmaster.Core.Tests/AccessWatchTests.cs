using System.Diagnostics;
using Cheatmaster.Core.Debugging;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// The watchpoint against a real process: attach, catch the game's own code touching an address,
/// and get out again without hurting it.
///
/// The last part is not a formality. A debuggee is killed with its debugger unless told
/// otherwise, and watchpoints left armed in the debug registers make the target trap into a
/// debugger that is no longer there — which kills it on the very next access. Both faults look
/// like "the game crashed for some reason" long after the fact, so both are checked here.
/// </summary>
public class AccessWatchTests(ITestOutputHelper output)
{
    [Fact]
    public void It_finds_the_code_that_touches_an_address_and_the_object_it_belongs_to()
    {
        using var target = WatchTargetProcess.Start();
        using var process = TargetProcess.Open(target.Pid, out string error);
        Assert.True(process is not null, error);

        var watch = AccessWatch.Start(process!, target.Field, 4, new AccessWatchOptions { MaxHits = 40 }, out string why);
        Assert.True(watch is not null, why);

        using (watch)
        {
            Assert.True(WaitFor(() => watch!.HitCount >= 8), $"nothing tripped the watchpoint: {watch!.Status}");
            watch.Stop();

            var sites = watch.Snapshot();
            Report(target, watch, sites);

            // The stand-in writes the value and reads it back from two different places, so both
            // kinds of access should be accounted for and told apart.
            Assert.Contains(sites, site => site.Writes > 0);
            Assert.Contains(sites, site => site.Reads > 0);

            var writer = sites.First(site => site.Writes > 0);
            Assert.NotEqual(0UL, writer.InstructionPointer);

            // The point of the whole exercise: the object pointer, read out of the register file
            // rather than decoded out of the instruction.
            Assert.NotEmpty(writer.Bases);
            Assert.Contains(writer.Bases,
                guess => guess.Value == target.Block && guess.Offset == WatchTargetProcess.FieldOffset);
        }

        Assert.False(target.HasExited, "the target did not survive being watched");

        // Still running is not enough — a target left with armed debug registers dies at its next
        // access, so what matters is that it is still getting work done.
        int before = process!.ReadValue<int>(target.Field);
        Assert.True(WaitFor(() => process.ReadValue<int>(target.Field) != before),
            "the target stopped making progress after the watch was removed");
    }

    /// <summary>
    /// There is no read-only watchpoint in the hardware, but there is a write-only one. Asking for
    /// writes has to actually mean writes, or the busiest addresses are unwatchable.
    /// </summary>
    [Fact]
    public void Watching_for_writes_leaves_the_reader_alone()
    {
        using var target = WatchTargetProcess.Start();
        using var process = TargetProcess.Open(target.Pid, out string error);
        Assert.True(process is not null, error);

        var options = new AccessWatchOptions { MaxHits = 20, On = WatchOn.Write };
        var watch = AccessWatch.Start(process!, target.Field, 4, options, out string why);
        Assert.True(watch is not null, why);

        using (watch)
        {
            Assert.True(WaitFor(() => watch!.HitCount >= 8), $"nothing tripped the watchpoint: {watch!.Status}");
            watch.Stop();

            var sites = watch.Snapshot();
            Report(target, watch, sites);

            // The same run watched for reads and writes reports the reading instruction too;
            // asking for writes must leave it out entirely.
            Assert.All(sites, site => Assert.Equal(0, site.Reads));
            Assert.Single(sites);
        }
    }

    /// <summary>
    /// What the watchpoint is for in the end: it hands over the object and the offset, and a
    /// pointer scan that already knows the offset can throw away every route that reaches the
    /// value some other way.
    /// </summary>
    [Fact]
    public void A_known_field_offset_keeps_only_the_routes_that_use_it()
    {
        using var target = WatchTargetProcess.Start();
        using var process = TargetProcess.Open(target.Pid, out string error);
        Assert.True(process is not null, error);

        var module = process!.MainModule;
        Assert.NotNull(module);

        // Plant a pointer to the object inside the target's own image, which is the fixed ground
        // a route has to reach. It goes in the DOS stub — the "cannot be run in DOS mode" text
        // that nothing reads once the image is loaded.
        ulong slot = module!.Base + 0x50;
        Assert.True(process.ReadValue<int>(module.Base + 0x3C) >= 0x58,
            "the PE header starts inside the stub, so there is no dead space to plant a pointer in");

        Assert.True(process.WriteValue(slot, target.Block), "could not plant a pointer in the image");
        Assert.Equal(target.Block, process.ReadValue<ulong>(slot));

        var map = PointerMap.Build(process, RegionFilter.Everything);

        List<PointerPath> Search(int? finalOffset) => PointerScanner.Find(process, map, target.Field,
            new PointerScanOptions { MaxDepth = 2, MaxOffset = 0x800, MaxResults = 200, FinalOffset = finalOffset });

        bool IsPlanted(PointerPath path) =>
            string.Equals(path.Module, module.Name, StringComparison.OrdinalIgnoreCase) && path.ModuleOffset == 0x50;

        // Control: the route is there to be found without any help.
        var blind = Search(null);
        Assert.Contains(blind, IsPlanted);

        var guided = Search(WatchTargetProcess.FieldOffset);
        output.WriteLine($"{map.Count:N0} pointers indexed over {map.BytesScanned:N0} bytes");
        output.WriteLine($"{blind.Count} route(s) blind, {guided.Count} knowing the field is at +{WatchTargetProcess.FieldOffset:X}");

        Assert.Contains(guided, IsPlanted);
        Assert.True(guided.Count <= blind.Count, "filtering by a known offset returned more routes, not fewer");

        // Every surviving route has to still land on the value. This is where getting the ends of
        // the chain the wrong way round would show up.
        foreach (var path in guided)
            Assert.Equal(target.Field, path.ToAddressSpec().Resolve(process));

        // And the wrong offset must throw the route away rather than quietly keep it.
        Assert.DoesNotContain(Search(WatchTargetProcess.FieldOffset + 8), IsPlanted);
    }

    /// <summary>
    /// What was actually caught. A watch that quietly caught nothing and a watch that worked look
    /// identical from a green tick, so the numbers get written down.
    /// </summary>
    private void Report(WatchTargetProcess target, AccessWatch watch, List<AccessSite> sites)
    {
        output.WriteLine($"pid {target.Pid}, object {target.Block:X}, value {target.Field:X}");
        output.WriteLine($"{watch.HitCount} hit(s) across {sites.Count} site(s) — {watch.Status}");

        foreach (var site in sites)
        {
            string bases = site.Bases.Count == 0
                ? "no base register"
                : string.Join(", ", site.Bases.Take(3).Select(b => $"{b.Display} = {b.Value:X} ({b.Hits} hits)"));
            output.WriteLine($"  {site.Location}: {site.Reads} read(s), {site.Writes} write(s) — {bases}");
        }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 20_000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}

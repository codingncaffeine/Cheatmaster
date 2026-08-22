using System.IO.Compression;
using System.Runtime.InteropServices;
using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// Reading a Cheat Engine table.
///
/// The dangerous part is the pointer chain. Cheat Engine resolves one from the last offset in the
/// file backwards to the first, and writes them out in the opposite order, so a table imported
/// with the offsets left as they were found reads a plausible wrong address while looking
/// perfectly correct. These build a real chain in this process and check that an imported entry
/// lands on the value, which is the only check that can tell those two apart.
/// </summary>
public class CheatEngineImportTests
{
    private static string Table(string entries) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <CheatTable CheatEngineTableVersion="42">
           <CheatEntries>
         {entries}
           </CheatEntries>
         </CheatTable>
         """;

    /// <summary>
    /// root+8 points at mid, mid+16 points at leaf, and the value sits at leaf+8 — so the value is
    /// reached as [[root+8]+16]+8, exactly the shape a Cheat Engine pointer describes.
    /// </summary>
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

            mid[2] = _leaf.AddrOfPinnedObject();
            root[1] = _mid.AddrOfPinnedObject();
            leaf[2] = 1234567;
        }

        public ulong RootSlot => (ulong)_root.AddrOfPinnedObject() + 8;
        public ulong Target => (ulong)_leaf.AddrOfPinnedObject() + 8;

        public void Dispose()
        {
            _leaf.Free();
            _mid.Free();
            _root.Free();
        }
    }

    [Fact]
    public void An_imported_pointer_chain_lands_on_the_value_it_described()
    {
        using var chain = new Chain();
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        // Written the way Cheat Engine writes it: the final offset first, the one applied to the
        // base last, both in hex.
        var report = CheatEngineTable.Parse(Table($"""
            <CheatEntry>
              <Description>"Health"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>{chain.RootSlot:X}</Address>
              <Offsets>
                <Offset>8</Offset>
                <Offset>10</Offset>
              </Offsets>
            </CheatEntry>
        """));

        Assert.False(report.Failed, report.Error);
        var entry = Assert.Single(report.Entries);

        Assert.Equal("Health", entry.Description);
        Assert.Equal(ScanType.Int32, entry.Type);

        // Reversed: 0x10 is applied to the base, 8 is the field inside what that points at.
        Assert.Equal([0x10, 0x8], entry.Address.Pointers);

        Assert.Equal(chain.Target, entry.Address.Resolve(process!));
        Assert.True(entry.TryReadValue(process!, out ulong bits));
        Assert.Equal(1234567UL, bits);
    }

    /// <summary>
    /// The control for the test above: keeping the offsets in the order the file lists them
    /// resolves somewhere else entirely. Without this, a passing import proves nothing.
    /// </summary>
    [Fact]
    public void Keeping_the_file_order_would_read_the_wrong_address()
    {
        using var chain = new Chain();
        using var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);

        var asListed = new AddressSpec { Offset = chain.RootSlot, Pointers = [0x8, 0x10] };

        Assert.NotEqual(chain.Target, asListed.Resolve(process!));
    }

    [Fact]
    public void A_module_relative_address_keeps_its_module()
    {
        var report = CheatEngineTable.Parse(Table("""
            <CheatEntry>
              <Description>"Plain"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>game.exe+1A2B3C</Address>
            </CheatEntry>
            <CheatEntry>
              <Description>"Quoted"</Description>
              <VariableType>Float</VariableType>
              <Address>"the game.exe"+40</Address>
            </CheatEntry>
        """));

        Assert.Equal(2, report.Entries.Count);

        Assert.Equal("game.exe", report.Entries[0].Address.Module);
        Assert.Equal(0x1A2B3CUL, report.Entries[0].Address.Offset);
        Assert.True(report.Entries[0].Address.IsModuleRelative);

        Assert.Equal("the game.exe", report.Entries[1].Address.Module);
        Assert.Equal(0x40UL, report.Entries[1].Address.Offset);
        Assert.Equal(ScanType.Float, report.Entries[1].Type);
    }

    [Fact]
    public void A_bare_address_comes_across_as_session_only()
    {
        var report = CheatEngineTable.Parse(Table("""
            <CheatEntry>
              <Description>"Loose"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>7FF612340000</Address>
            </CheatEntry>
        """));

        var entry = Assert.Single(report.Entries);
        Assert.Equal(0x7FF612340000UL, entry.Address.Offset);
        Assert.True(entry.Address.IsSessionOnly);
        Assert.Equal(1, report.SessionOnly);
    }

    /// <summary>
    /// Everything refused has to be named. A table that looks imported and silently does nothing
    /// is worse than one that says what it could not bring across.
    /// </summary>
    [Fact]
    public void What_cannot_come_across_is_reported_rather_than_dropped()
    {
        var report = CheatEngineTable.Parse(Table("""
            <CheatEntry>
              <Description>"Infinite ammo"</Description>
              <VariableType>Auto Assembler Script</VariableType>
              <AssemblerScript>[ENABLE]
            aobscanmodule(x,game.exe,89 41 18)
            [DISABLE]</AssemblerScript>
            </CheatEntry>
            <CheatEntry>
              <Description>"Player name"</Description>
              <VariableType>String</VariableType>
              <Address>game.exe+10</Address>
            </CheatEntry>
            <CheatEntry>
              <Description>"Stack value"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>THREADSTACK0-00000994</Address>
            </CheatEntry>
            <CheatEntry>
              <Description>"Computed offset"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>game.exe+10</Address>
              <Offsets>
                <Offset>rax+8</Offset>
              </Offsets>
            </CheatEntry>
            <CheatEntry>
              <Description>"Works"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>game.exe+20</Address>
            </CheatEntry>
        """));

        Assert.Single(report.Entries);
        Assert.Equal(4, report.Skipped.Count);

        Assert.Contains(report.Skipped, s => s.Description == "Infinite ammo" && s.Reason.Contains("assembler"));
        Assert.Contains(report.Skipped, s => s.Description == "Player name" && s.Reason.Contains("String"));
        Assert.Contains(report.Skipped, s => s.Description == "Stack value" && s.Reason.Contains("stack"));
        Assert.Contains(report.Skipped, s => s.Description == "Computed offset");
    }

    [Fact]
    public void A_group_header_names_everything_underneath_it()
    {
        var report = CheatEngineTable.Parse(Table("""
            <CheatEntry>
              <Description>"Player"</Description>
              <GroupHeader>1</GroupHeader>
              <CheatEntries>
                <CheatEntry>
                  <Description>"Health"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>game.exe+10</Address>
                </CheatEntry>
                <CheatEntry>
                  <Description>"Stamina"</Description>
                  <VariableType>Float</VariableType>
                  <Address>game.exe+14</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatEntry>
        """));

        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, entry => Assert.Equal("Player", entry.Group));

        // The header itself holds no value, so it must not become an entry of its own.
        Assert.DoesNotContain(report.Entries, entry => entry.Description == "Player");
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public void Every_type_Cheat_Engine_names_maps_to_the_same_width()
    {
        var report = CheatEngineTable.Parse(Table("""
            <CheatEntry><Description>"a"</Description><VariableType>Byte</VariableType><Address>g.exe+0</Address></CheatEntry>
            <CheatEntry><Description>"b"</Description><VariableType>2 Bytes</VariableType><Address>g.exe+0</Address></CheatEntry>
            <CheatEntry><Description>"c"</Description><VariableType>4 Bytes</VariableType><Address>g.exe+0</Address></CheatEntry>
            <CheatEntry><Description>"d"</Description><VariableType>8 Bytes</VariableType><Address>g.exe+0</Address></CheatEntry>
            <CheatEntry><Description>"e"</Description><VariableType>Float</VariableType><Address>g.exe+0</Address></CheatEntry>
            <CheatEntry><Description>"f"</Description><VariableType>Double</VariableType><Address>g.exe+0</Address></CheatEntry>
        """));

        Assert.Equal(6, report.Entries.Count);
        Assert.Equal([1, 2, 4, 8, 4, 8], report.Entries.Select(e => e.Type.Width()).ToArray());
        Assert.Equal(ScanType.Float, report.Entries[4].Type);
        Assert.Equal(ScanType.Double, report.Entries[5].Type);
    }

    [Fact]
    public void An_entry_marked_unsigned_comes_across_unsigned()
    {
        var report = CheatEngineTable.Parse(Table("""
            <CheatEntry>
              <Description>"Signed by default"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>g.exe+0</Address>
            </CheatEntry>
            <CheatEntry>
              <Description>"Told to be unsigned"</Description>
              <VariableType>4 Bytes</VariableType>
              <ShowAsSigned>0</ShowAsSigned>
              <Address>g.exe+0</Address>
            </CheatEntry>
        """));

        Assert.Equal(ScanType.Int32, report.Entries[0].Type);
        Assert.Equal(ScanType.UInt32, report.Entries[1].Type);
    }

    /// <summary>Importing a table must never start writing into a game by itself.</summary>
    [Fact]
    public void An_entry_that_was_active_keeps_its_value_but_arrives_thawed()
    {
        var report = CheatEngineTable.Parse(Table("""
            <CheatEntry>
              <Description>"Held"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>g.exe+0</Address>
              <LastState Value="999" Activated="1" RealAddress="1A2B3C40"/>
            </CheatEntry>
        """));

        var entry = Assert.Single(report.Entries);
        Assert.Equal("999", entry.FreezeValue);
        Assert.False(entry.Frozen);
    }

    [Fact]
    public void A_compressed_table_reads_the_same_as_a_plain_one()
    {
        string xml = Table("""
            <CheatEntry>
              <Description>"Health"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>game.exe+1A2B3C</Address>
            </CheatEntry>
        """);

        string path = Path.Combine(Path.GetTempPath(), $"cheatmaster-{Guid.NewGuid():N}.CT");
        try
        {
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using var stream = archive.CreateEntry("table.xml").Open();
                using var writer = new StreamWriter(stream);
                writer.Write(xml);
            }

            var report = CheatEngineTable.Load(path);

            Assert.False(report.Failed, report.Error);
            var entry = Assert.Single(report.Entries);
            Assert.Equal("game.exe", entry.Address.Module);
            Assert.Equal(0x1A2B3CUL, entry.Address.Offset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Something_that_is_not_a_cheat_table_is_refused_with_a_reason()
    {
        var report = CheatEngineTable.Parse("<html><body>not a table</body></html>");

        Assert.True(report.Failed);
        Assert.Empty(report.Entries);
        Assert.Contains("Cheat Engine", report.Error!);
    }
}

using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// A saved table is the whole point of the library, so what it carries has to survive the round
/// trip. The route and the last time it was confirmed matter most: both are what make a cheat
/// found today still worth something tomorrow.
/// </summary>
public class CheatTablePersistenceTests
{
    [Fact]
    public void A_route_and_when_it_was_last_confirmed_survive_a_save_and_load()
    {
        var confirmed = new DateTimeOffset(2026, 8, 21, 14, 30, 0, TimeSpan.FromHours(1));

        var table = new CheatTable { GameName = "Test" };
        table.Entries.Add(new CheatEntry
        {
            Description = "Health",
            Address = new AddressSpec { Module = "game.exe", Offset = 0x1A2B3C, Pointers = [0x10, 0x8] },
            Type = ScanType.Int32,
            LastVerified = confirmed
        });

        string path = Path.Combine(Path.GetTempPath(), $"cheatmaster-{Guid.NewGuid():N}.cmt");
        try
        {
            table.Save(path);
            var loaded = CheatTable.Load(path);

            Assert.NotNull(loaded);
            var entry = Assert.Single(loaded!.Entries);

            Assert.Equal("game.exe", entry.Address.Module);
            Assert.Equal(0x1A2B3CUL, entry.Address.Offset);
            Assert.Equal([0x10, 0x8], entry.Address.Pointers);
            Assert.True(entry.Address.IsPointerChain);
            Assert.Equal(confirmed, entry.LastVerified);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An entry that has never been confirmed must not come back looking as if it had.</summary>
    [Fact]
    public void An_unconfirmed_route_stays_unconfirmed()
    {
        var table = new CheatTable { GameName = "Test" };
        table.Entries.Add(new CheatEntry
        {
            Description = "Ammo",
            Address = new AddressSpec { Module = "game.exe", Offset = 0x20, Pointers = [0x4] }
        });

        string path = Path.Combine(Path.GetTempPath(), $"cheatmaster-{Guid.NewGuid():N}.cmt");
        try
        {
            table.Save(path);
            var loaded = CheatTable.Load(path);

            Assert.Null(Assert.Single(loaded!.Entries).LastVerified);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

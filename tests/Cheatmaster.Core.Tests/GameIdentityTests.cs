using Cheatmaster.Core.Cheats;
using Xunit;

namespace Cheatmaster.Core.Tests;

public class GameIdentityTests
{
    [Theory]
    [InlineData("ShooterGame-Win64-Shipping", "Shooter Game")]
    [InlineData("MyGame_Win64_Shipping", "My Game")]
    [InlineData("The_Long_Title", "The Long Title")]
    [InlineData("Some Title™", "Some Title")]
    [InlineData("Another.Title", "Another Title")]
    [InlineData("SpaceEngine", "Space Engine")]
    public void Cleans_install_derived_names_into_something_searchable(string raw, string expected) =>
        Assert.Equal(expected, GameIdentity.Clean(raw));

    [Theory]
    [InlineData("Portal")]
    [InlineData("DOOM")]
    [InlineData("Half Life")]
    public void Leaves_names_that_are_already_clean_alone(string name) =>
        Assert.Equal(name, GameIdentity.Clean(name));

    [Fact]
    public void Falls_back_to_the_product_name_and_then_the_executable()
    {
        var fingerprint = new GameFingerprint("Runtime-Win64-Shipping.exe", "Studio Title", "1.0", "abcd1234", string.Empty);
        var hints = GameIdentity.Resolve(fingerprint);

        Assert.Contains(hints, h => h.Source == IdentitySource.ProductName && h.Name == "Studio Title");
        Assert.Contains(hints, h => h.Source == IdentitySource.ExecutableName && h.Name == "Runtime");
    }

    /// <summary>
    /// A store install folder names the game far better than the executable does, so it has to
    /// outrank the fallbacks.
    /// </summary>
    [Fact]
    public void Prefers_the_install_folder_over_the_executable_name()
    {
        var fingerprint = new GameFingerprint(
            "Binary-Win64-Shipping.exe", string.Empty, string.Empty, "abcd1234",
            @"D:\Games\The Long Title\Binaries\Win64\Binary-Win64-Shipping.exe");

        var hints = GameIdentity.Resolve(fingerprint);

        int folder = hints.FindIndex(h => h.Source == IdentitySource.StoreFolder);
        int executable = hints.FindIndex(h => h.Source == IdentitySource.ExecutableName);

        Assert.True(folder >= 0, "no install-folder hint was produced");
        Assert.True(executable < 0 || folder < executable, "the executable name outranked the install folder");
    }

    [Fact]
    public void Reads_the_title_out_of_a_gog_descriptor()
    {
        string root = Path.Combine(Path.GetTempPath(), "cheatmaster-gog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "goggame-1207658930.info"),
                """{"gameId":"1207658930","name":"The Long Title","playTasks":[]}""");

            string exe = Path.Combine(root, "launcher.exe");
            File.WriteAllBytes(exe, new byte[64]);

            var hints = GameIdentity.Resolve(new GameFingerprint("launcher.exe", string.Empty, string.Empty, "abcd1234", exe));

            var gog = hints.Find(h => h.Source == IdentitySource.GogInfoFile);
            Assert.NotNull(gog);
            Assert.Equal("The Long Title", gog!.Name);
            Assert.Equal(1207658930L, gog.GogProductId);
            Assert.Equal(IdentitySource.GogInfoFile, hints[0].Source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reads_the_app_id_out_of_a_steam_marker()
    {
        string root = Path.Combine(Path.GetTempPath(), "cheatmaster-steam-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "steam_appid.txt"), "440\n");

            string exe = Path.Combine(root, "game.exe");
            File.WriteAllBytes(exe, new byte[64]);

            var hints = GameIdentity.Resolve(new GameFingerprint("game.exe", string.Empty, string.Empty, "abcd1234", exe));

            Assert.Equal(IdentitySource.SteamAppId, hints[0].Source);
            Assert.Equal(440, hints[0].SteamAppId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cheatmaster.Core.Cheats;

/// <summary>Where a game's identity came from, best first.</summary>
public enum IdentitySource
{
    SteamAppId,
    GogInfoFile,
    EpicManifest,
    StoreFolder,
    ProductName,
    ExecutableName
}

public sealed record GameIdentityHint(IdentitySource Source, string Name, int SteamAppId = 0, long GogProductId = 0);

/// <summary>
/// Works out what a game is actually called by reading the install, not by guessing from the
/// executable.
///
/// Storefronts each leave something behind that names the game exactly: Steam writes
/// <c>steam_appid.txt</c>, GOG writes <c>goggame-{id}.info</c>, Epic records the title in its
/// launcher manifests. Reading those beats any amount of cleverness applied to
/// <c>ShooterGame-Win64-Shipping.exe</c>, and it is what makes lookups work for games that were
/// never on Steam at all.
/// </summary>
public static partial class GameIdentity
{
    /// <summary>How far up from the executable to look for a storefront marker.</summary>
    private const int MaxParentWalk = 5;

    /// <summary>Every plausible identity for this install, most trustworthy first.</summary>
    public static List<GameIdentityHint> Resolve(GameFingerprint fingerprint)
    {
        var hints = new List<GameIdentityHint>();
        string path = fingerprint.Path;

        if (!string.IsNullOrEmpty(path))
        {
            // Each probe is isolated. A missing folder or a permission error on one storefront
            // must not take the others down with it — losing every hint because one directory
            // could not be listed is how a game ends up with no cover for no good reason.
            var directory = new DirectoryInfo(Path.GetDirectoryName(path) ?? path);

            Probe(() => AddSteam(hints, directory));
            Probe(() => AddGog(hints, directory));
            Probe(() => AddEpic(hints, path));
            Probe(() => AddStoreFolder(hints, path));
        }

        if (!string.IsNullOrWhiteSpace(fingerprint.DisplayName))
            Add(hints, new GameIdentityHint(IdentitySource.ProductName, Clean(fingerprint.DisplayName)));

        string stem = Path.GetFileNameWithoutExtension(fingerprint.ExecutableName);
        if (!string.IsNullOrWhiteSpace(stem))
            Add(hints, new GameIdentityHint(IdentitySource.ExecutableName, Clean(stem)));

        return hints;
    }

    private static void Probe(Action probe)
    {
        try
        {
            probe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // This source had nothing to say; the others still might.
        }
    }

    private static void Add(List<GameIdentityHint> hints, GameIdentityHint hint)
    {
        if (string.IsNullOrWhiteSpace(hint.Name) && hint.SteamAppId == 0 && hint.GogProductId == 0) return;

        foreach (var existing in hints)
        {
            if (existing.SteamAppId == hint.SteamAppId && existing.GogProductId == hint.GogProductId &&
                string.Equals(existing.Name, hint.Name, StringComparison.OrdinalIgnoreCase))
                return;
        }
        hints.Add(hint);
    }

    private static void AddSteam(List<GameIdentityHint> hints, DirectoryInfo start)
    {
        for (var (at, depth) = (start, 0); at is not null && depth < MaxParentWalk; at = at.Parent, depth++)
        {
            string marker = Path.Combine(at.FullName, "steam_appid.txt");
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out int id) && id > 0)
            {
                Add(hints, new GameIdentityHint(IdentitySource.SteamAppId, string.Empty, SteamAppId: id));
                return;
            }

            if (IsStoreRoot(at)) return;
        }
    }

    /// <summary>
    /// GOG drops a JSON descriptor next to the game holding its exact store title, which is the
    /// single most useful thing on disk for a non-Steam install.
    /// </summary>
    private static void AddGog(List<GameIdentityHint> hints, DirectoryInfo start)
    {
        for (var (at, depth) = (start, 0); at is not null && depth < MaxParentWalk; at = at.Parent, depth++)
        {
            foreach (string file in Directory.EnumerateFiles(at.FullName, "goggame-*.info"))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    string name = json.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    long id = 0;
                    if (json.RootElement.TryGetProperty("gameId", out var g) && long.TryParse(g.GetString(), out long parsed))
                        id = parsed;

                    if (!string.IsNullOrWhiteSpace(name) || id > 0)
                    {
                        Add(hints, new GameIdentityHint(IdentitySource.GogInfoFile, Clean(name), GogProductId: id));
                        return;
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // Try the next descriptor.
                }
            }

            if (IsStoreRoot(at)) return;
        }
    }

    /// <summary>Epic records the display name in a launcher manifest keyed by install folder.</summary>
    private static void AddEpic(List<GameIdentityHint> hints, string exePath)
    {
        string manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifests)) return;

        foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                if (!json.RootElement.TryGetProperty("InstallLocation", out var location)) continue;

                string install = location.GetString() ?? string.Empty;
                if (install.Length == 0 || !exePath.StartsWith(install, StringComparison.OrdinalIgnoreCase)) continue;

                string name = json.RootElement.TryGetProperty("DisplayName", out var d) ? d.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    Add(hints, new GameIdentityHint(IdentitySource.EpicManifest, Clean(name)));
                    return;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Try the next manifest.
            }
        }
    }

    /// <summary>The folder a store installs into is usually the title, minus punctuation.</summary>
    private static void AddStoreFolder(List<GameIdentityHint> hints, string exePath)
    {
        string[] parts = exePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            bool isSteam = string.Equals(parts[i], "common", StringComparison.OrdinalIgnoreCase) &&
                           i > 0 && string.Equals(parts[i - 1], "steamapps", StringComparison.OrdinalIgnoreCase);
            bool isGeneric = string.Equals(parts[i], "GOG Games", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(parts[i], "GOG Galaxy", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(parts[i], "Games", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(parts[i], "Epic Games", StringComparison.OrdinalIgnoreCase);

            if (isSteam || isGeneric)
            {
                Add(hints, new GameIdentityHint(IdentitySource.StoreFolder, Clean(parts[i + 1])));
                return;
            }
        }

        // Nothing recognisable: the folder holding the executable is still a better guess than
        // the executable, which is often a generic engine name.
        if (parts.Length >= 2)
            Add(hints, new GameIdentityHint(IdentitySource.StoreFolder, Clean(parts[^2])));
    }

    private static bool IsStoreRoot(DirectoryInfo directory) =>
        string.Equals(directory.Name, "steamapps", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(directory.Name, "common", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(directory.Name, "Program Files", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(directory.Name, "Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
        directory.Parent is null;

    /// <summary>
    /// Turns an install-derived string into something a store search will match: engine and
    /// build suffixes off, separators normalised, trademark marks removed.
    /// </summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = new StringBuilder(raw.Trim());
        text.Replace("™", string.Empty).Replace("®", string.Empty).Replace("©", string.Empty);

        string value = text.ToString();
        value = SuffixPattern().Replace(value, string.Empty);
        value = SeparatorPattern().Replace(value, " ");
        value = CamelPattern().Replace(value, "$1 $2");
        value = WhitespacePattern().Replace(value, " ").Trim();

        return value;
    }

    // Engine and build decorations that never appear in a store listing.
    [GeneratedRegex(@"[\s_-]*(Win64|Win32|x64|x86)?[\s_-]*(Shipping|Client|Launcher|Game|Retail|Final|Release)?\s*(\(64-bit\)|\(32-bit\))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SuffixPattern();

    [GeneratedRegex(@"[._\-]+")]
    private static partial Regex SeparatorPattern();

    // ShooterGame -> Shooter Game, but only where a lower case letter meets an upper case one.
    [GeneratedRegex(@"(\p{Ll})(\p{Lu})")]
    private static partial Regex CamelPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespacePattern();
}

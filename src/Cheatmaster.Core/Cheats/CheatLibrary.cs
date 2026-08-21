namespace Cheatmaster.Core.Cheats;

/// <summary>A game in the library, with just enough detail to list it without loading every entry.</summary>
public sealed record LibraryEntry(string Key, string GameName, string ExecutableName, string GameVersion,
    int CheatCount, DateTimeOffset Modified, string Path, string Description, string Developer,
    string ReleaseDate, string ArtPath, string Notes)
{
    public bool HasArt => !string.IsNullOrEmpty(ArtPath) && File.Exists(ArtPath);

    public string CheatSummary => CheatCount == 1 ? "1 cheat" : $"{CheatCount} cheats";

    public string Subtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Developer)) return Developer;
            if (!string.IsNullOrWhiteSpace(GameVersion)) return GameVersion;
            return ExecutableName;
        }
    }
}

/// <summary>
/// The per-game store of saved cheats. Tables are individual files rather than rows in one
/// database so a single game's cheats can be handed to someone else as one file, and so a
/// corrupt entry can never take the whole collection with it.
/// </summary>
public sealed class CheatLibrary
{
    public CheatLibrary(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? DefaultRoot;
        Directory.CreateDirectory(RootDirectory);
    }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cheatmaster", "Library");

    public string RootDirectory { get; }

    public string PathFor(GameFingerprint fingerprint) =>
        Path.Combine(RootDirectory, fingerprint.Key + CheatTable.FileExtension);

    public bool Has(GameFingerprint fingerprint) => File.Exists(PathFor(fingerprint));

    /// <summary>Loads this game's table, or an empty one ready to fill.</summary>
    public CheatTable LoadOrCreate(GameFingerprint fingerprint)
    {
        string path = PathFor(fingerprint);
        var table = File.Exists(path) ? CheatTable.Load(path) : null;
        if (table is not null) return table;

        var fresh = CheatTable.ForGame(fingerprint);
        fresh.SourcePath = path;
        return fresh;
    }

    public void Save(CheatTable table, GameFingerprint fingerprint)
    {
        table.GameName = string.IsNullOrWhiteSpace(table.GameName) ? fingerprint.DisplayName : table.GameName;
        table.ExecutableName = fingerprint.ExecutableName;
        table.ExecutableHash = fingerprint.Hash;
        if (!string.IsNullOrEmpty(fingerprint.Path)) table.ExecutablePath = fingerprint.Path;
        if (string.IsNullOrEmpty(table.GameVersion)) table.GameVersion = fingerprint.Version;
        table.Save(PathFor(fingerprint));
    }

    public List<LibraryEntry> List()
    {
        var entries = new List<LibraryEntry>();
        if (!Directory.Exists(RootDirectory)) return entries;

        foreach (string path in Directory.EnumerateFiles(RootDirectory, "*" + CheatTable.FileExtension))
        {
            var table = CheatTable.Load(path);
            if (table is null) continue;

            entries.Add(new LibraryEntry(
                Path.GetFileNameWithoutExtension(path),
                string.IsNullOrWhiteSpace(table.GameName) ? Path.GetFileNameWithoutExtension(path) : table.GameName,
                table.ExecutableName,
                table.GameVersion,
                table.Entries.Count,
                table.Modified,
                path,
                table.Description,
                table.Developer,
                table.ReleaseDate,
                GameMetadataService.ArtFileFor(Path.GetFileNameWithoutExtension(path)),
                table.Notes));
        }

        entries.Sort(static (a, b) => b.Modified.CompareTo(a.Modified));
        return entries;
    }

    public bool Delete(string key)
    {
        string path = Path.Combine(RootDirectory, key + CheatTable.FileExtension);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Finds tables saved for the same executable under a different build hash, so a game update
    /// does not silently hide the cheats the user already made.
    /// </summary>
    public List<LibraryEntry> FindOtherVersions(GameFingerprint fingerprint)
    {
        var matches = new List<LibraryEntry>();
        foreach (var entry in List())
        {
            if (entry.Key == fingerprint.Key) continue;
            if (string.Equals(entry.ExecutableName, fingerprint.ExecutableName, StringComparison.OrdinalIgnoreCase))
                matches.Add(entry);
        }
        return matches;
    }
}

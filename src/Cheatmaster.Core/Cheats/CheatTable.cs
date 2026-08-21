using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cheatmaster.Core.Cheats;

/// <summary>Identifies a game well enough to find its saved table again after an update or reinstall.</summary>
public sealed record GameFingerprint(string ExecutableName, string DisplayName, string Version, string Hash, string Path)
{
    /// <summary>File name used in the library, stable across launches.</summary>
    public string Key
    {
        get
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(ExecutableName);
            var safe = new char[stem.Length];
            for (int i = 0; i < stem.Length; i++)
            {
                char c = stem[i];
                safe[i] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
            }
            return new string(safe) + "-" + Hash;
        }
    }

    public static GameFingerprint Unknown(string name) => new(name, name, string.Empty, "00000000", string.Empty);

    /// <summary>
    /// Hashes the head of the executable plus its length. Reading the whole file would stall on
    /// multi-gigabyte builds, and the head plus size already separates versions and installs.
    /// </summary>
    public static GameFingerprint For(string exePath)
    {
        string name = System.IO.Path.GetFileName(exePath);
        if (string.IsNullOrEmpty(name)) return Unknown("unknown.exe");

        string display = System.IO.Path.GetFileNameWithoutExtension(name);
        string version = string.Empty;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.ProductName)) display = info.ProductName!.Trim();
            if (!string.IsNullOrWhiteSpace(info.FileVersion)) version = info.FileVersion!.Trim();
        }
        catch
        {
            // Version info is optional.
        }

        string hash = "00000000";
        try
        {
            using var stream = File.OpenRead(exePath);
            long length = stream.Length;
            byte[] head = new byte[(int)Math.Min(4L * 1024 * 1024, length)];
            stream.ReadExactly(head, 0, head.Length);

            using var sha = SHA256.Create();
            sha.TransformBlock(head, 0, head.Length, null, 0);
            byte[] tail = BitConverter.GetBytes(length);
            sha.TransformFinalBlock(tail, 0, tail.Length);
            hash = Convert.ToHexString(sha.Hash!)[..8];
        }
        catch
        {
            // An unreadable executable still gets a usable table, keyed by name alone.
        }

        return new GameFingerprint(name, display, version, hash, exePath);
    }
}

/// <summary>A saved set of cheats for one game.</summary>
public sealed class CheatTable
{
    public const int CurrentFormatVersion = 1;
    public const string FileExtension = ".cmt";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string GameName { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public string ExecutableHash { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;
    public List<CheatEntry> Entries { get; set; } = [];

    [JsonIgnore]
    public string? SourcePath { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CheatTable ForGame(GameFingerprint fingerprint) => new()
    {
        GameName = fingerprint.DisplayName,
        ExecutableName = fingerprint.ExecutableName,
        ExecutableHash = fingerprint.Hash,
        GameVersion = fingerprint.Version
    };

    public void Save(string path)
    {
        Modified = DateTimeOffset.Now;
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        File.Move(temporary, path, overwrite: true);
        SourcePath = path;
    }

    public static CheatTable? Load(string path)
    {
        try
        {
            var table = JsonSerializer.Deserialize<CheatTable>(File.ReadAllText(path), Options);
            if (table is null) return null;
            table.SourcePath = path;
            return table;
        }
        catch
        {
            return null;
        }
    }
}

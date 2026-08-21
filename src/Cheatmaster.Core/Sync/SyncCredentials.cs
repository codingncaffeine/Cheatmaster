using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cheatmaster.Core.Sync;

/// <summary>
/// Stores the GitHub token for this user.
///
/// The token carries the <c>repo</c> scope, which reaches every repository the account owns, so
/// it is never written in the clear: DPAPI ties the stored bytes to this Windows user on this
/// machine, and a copied file is useless anywhere else.
/// </summary>
public sealed class SyncCredentials
{
    private sealed record Stored(string ProtectedToken, string Username);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("cheatmaster-sync-v1");

    private SyncCredentials(string token, string username)
    {
        Token = token;
        Username = username;
    }

    public string Token { get; }
    public string Username { get; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cheatmaster", "github.json");

    public static SyncCredentials? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            if (stored is null || string.IsNullOrEmpty(stored.ProtectedToken)) return null;

            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(stored.ProtectedToken), Entropy, DataProtectionScope.CurrentUser);

            return new SyncCredentials(Encoding.UTF8.GetString(plain), stored.Username);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or FormatException)
        {
            // A token that cannot be unprotected is a token from another user or machine.
            return null;
        }
    }

    public static SyncCredentials Save(string token, string username)
    {
        byte[] guarded = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(FilePath, JsonSerializer.Serialize(
            new Stored(Convert.ToBase64String(guarded), username),
            new JsonSerializerOptions { WriteIndented = true }));

        return new SyncCredentials(token, username);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (IOException)
        {
            // Nothing useful to do if the file is locked.
        }
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cheatmaster.Core.Sync;

/// <summary>
/// Stores the GitHub token for this user.
///
/// The token carries the <c>repo</c> scope, which reaches every repository the account owns, so
/// it is never written in the clear where the system offers anything better. On Windows, DPAPI
/// ties the stored bytes to this user on this machine. On Linux the token goes to the desktop's
/// keyring through the Secret Service (GNOME Keyring, KWallet, KeePassXC); only when no keyring
/// is running does it fall back to a file readable by this user alone, which is what git's own
/// credential store and the GitHub CLI do in the same situation.
/// </summary>
public sealed class SyncCredentials
{
    private const string SecretServiceStore = "secret-service";
    private const string FileStore = "file";

    private sealed record Stored(string ProtectedToken, string Username, string? Store = null, string? Token = null);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("cheatmaster-sync-v1");

    private SyncCredentials(string token, string username, bool inKeyring)
    {
        Token = token;
        Username = username;
        InKeyring = inKeyring;
    }

    public string Token { get; }
    public string Username { get; }

    /// <summary>False when the token sits in a private file because no keyring was available.</summary>
    public bool InKeyring { get; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "Cheatmaster", "github.json");

    public static SyncCredentials? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            if (stored is null) return null;

            if (OperatingSystem.IsWindows())
            {
                if (string.IsNullOrEmpty(stored.ProtectedToken)) return null;
                byte[] plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored.ProtectedToken), Entropy, DataProtectionScope.CurrentUser);
                return new SyncCredentials(Encoding.UTF8.GetString(plain), stored.Username, inKeyring: true);
            }

            if (stored.Store == SecretServiceStore)
            {
                string? token = SecretTool("lookup", stored.Username, input: null);
                return string.IsNullOrEmpty(token) ? null : new SyncCredentials(token, stored.Username, inKeyring: true);
            }

            return string.IsNullOrEmpty(stored.Token) ? null : new SyncCredentials(stored.Token, stored.Username, inKeyring: false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or FormatException or UnauthorizedAccessException)
        {
            // A token that cannot be unprotected is a token from another user or machine.
            return null;
        }
    }

    public static SyncCredentials Save(string token, string username)
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            byte[] guarded = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            WriteRecord(new Stored(Convert.ToBase64String(guarded), username));
            return new SyncCredentials(token, username, inKeyring: true);
        }

        // Stored, then read back: a keyring that is present but locked or broken accepts nothing.
        if (SecretTool("store", username, token) is not null && SecretTool("lookup", username, null) == token)
        {
            WriteRecord(new Stored(string.Empty, username, SecretServiceStore));
            return new SyncCredentials(token, username, inKeyring: true);
        }

        WriteRecord(new Stored(string.Empty, username, FileStore, token));
        return new SyncCredentials(token, username, inKeyring: false);
    }

    private static void WriteRecord(Stored record)
    {
        string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(FilePath, json);
            return;
        }

        // Created private, never made private afterwards: there is no moment it is readable by others.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        using (var stream = new FileStream(FilePath, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(json);
        File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Clear()
    {
        try
        {
            if (!OperatingSystem.IsWindows() && File.Exists(FilePath))
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
                if (stored?.Store == SecretServiceStore) SecretTool("clear", stored.Username, null);
            }
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Nothing useful to do if the file is locked.
        }
    }

    /// <summary>
    /// Runs the Secret Service's command-line client. Returns its output, or null when it failed
    /// or is not installed. The token travels over stdin, never on a command line other users can read.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static string? SecretTool(string verb, string username, string? input)
    {
        try
        {
            var info = new ProcessStartInfo("secret-tool")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add(verb);
            if (verb == "store")
            {
                info.ArgumentList.Add("--label");
                info.ArgumentList.Add("Cheatmaster GitHub backup");
            }
            info.ArgumentList.Add("application");
            info.ArgumentList.Add("cheatmaster");
            info.ArgumentList.Add("account");
            info.ArgumentList.Add(username);

            using var process = Process.Start(info);
            if (process is null) return null;

            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}

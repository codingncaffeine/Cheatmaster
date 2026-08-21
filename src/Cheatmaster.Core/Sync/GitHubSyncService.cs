using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cheatmaster.Core.Cheats;

namespace Cheatmaster.Core.Sync;

/// <summary>What the user has to do to finish signing in.</summary>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, string DeviceCode, int IntervalSeconds, int ExpiresInSeconds);

public sealed record SyncOutcome(int Uploaded, int Downloaded, int Unchanged, string Message);

public sealed class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
}

/// <summary>
/// Backs the cheat library up to a private GitHub repository the app creates for the user.
///
/// Sign-in uses the OAuth device flow: the user types a short code on github.com and the app
/// polls for the token. That needs no client secret, no callback server and no personal access
/// token pasted by hand, which is the only way this is worth doing for a desktop app.
/// </summary>
public sealed class GitHubSyncService
{
    /// <summary>Public by design — the device flow has no client secret to protect.</summary>
    public const string ClientId = "Ov23cteHWU5op2Q9mAkD";

    public const string RepositoryName = "cheatmaster-cheats";
    private const string ManifestPath = "manifest.json";
    private const int SchemaVersion = 1;

    private static readonly HttpClient Http = CreateClient();

    private SyncCredentials? _credentials;

    public GitHubSyncService() => _credentials = SyncCredentials.Load();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Cheatmaster/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public bool IsSignedIn => _credentials is not null;
    public string Username => _credentials?.Username ?? string.Empty;

    public string RepositoryUrl => IsSignedIn
        ? $"https://github.com/{Username}/{RepositoryName}"
        : string.Empty;

    // ---------------------------------------------------------------- sign in

    public async Task<DeviceCodePrompt> StartSignInAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = "repo"
            })
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(body);

        if (json.RootElement.TryGetProperty("error", out var error))
        {
            string code = error.GetString() ?? "unknown";
            throw new SyncException(code == "device_flow_disabled"
                ? "Device Flow is not enabled on the GitHub OAuth app. Turn it on in the app's settings on github.com."
                : "GitHub refused the sign-in request: " + code);
        }

        return new DeviceCodePrompt(
            json.RootElement.GetProperty("user_code").GetString() ?? string.Empty,
            json.RootElement.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device",
            json.RootElement.GetProperty("device_code").GetString() ?? string.Empty,
            json.RootElement.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            json.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900);
    }

    /// <summary>Polls until the user authorises the app, or the code expires.</summary>
    public async Task<bool> CompleteSignInAsync(DeviceCodePrompt prompt, CancellationToken ct = default)
    {
        int interval = Math.Max(1, prompt.IntervalSeconds);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(prompt.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = prompt.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            if (json.RootElement.TryGetProperty("access_token", out var token))
            {
                string value = token.GetString() ?? string.Empty;
                string login = await FetchLoginAsync(value, ct).ConfigureAwait(false);
                _credentials = SyncCredentials.Save(value, login);
                return true;
            }

            string error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    throw new SyncException("The sign-in code expired. Start again.");
                case "access_denied":
                    throw new SyncException("Sign-in was declined on GitHub.");
                default:
                    throw new SyncException("GitHub returned: " + error);
            }
        }

        throw new SyncException("The sign-in code expired. Start again.");
    }

    public void SignOut()
    {
        SyncCredentials.Clear();
        _credentials = null;
    }

    private static async Task<string> FetchLoginAsync(string token, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, "https://api.github.com/user", token);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return string.Empty;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return json.RootElement.TryGetProperty("login", out var login) ? login.GetString() ?? string.Empty : string.Empty;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var credentials = _credentials ?? throw new SyncException("Sign in to GitHub first.");
        return Authorized(method, url, credentials.Token);
    }

    // ---------------------------------------------------------------- sync

    private readonly record struct RemoteFile(string Hash, DateTimeOffset Modified, long Size);

    /// <summary>
    /// Content hash of a local file. Uploading is decided on content, never on timestamps
    /// alone: git stores every version it is handed and never lets one go, so re-uploading
    /// bytes that did not change grows the repository permanently for nothing.
    /// </summary>
    private static string HashOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))[..32];
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    public async Task<SyncOutcome> SyncAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_credentials is null) throw new SyncException("Sign in to GitHub first.");

        progress?.Report("Checking the backup repository…");
        await EnsureRepositoryAsync(ct).ConfigureAwait(false);

        progress?.Report("Reading what is already backed up…");
        var shas = await FetchShaCacheAsync(ct).ConfigureAwait(false);
        var manifest = await FetchManifestAsync(shas, ct).ConfigureAwait(false);

        var local = EnumerateLocalFiles();
        int uploaded = 0, downloaded = 0, unchanged = 0;
        bool manifestChanged = false;

        // Local files that are new or newer than the backup.
        foreach (var (remotePath, localPath) in local)
        {
            ct.ThrowIfCancellationRequested();
            var localModified = ModifiedOf(remotePath, localPath);
            string localHash = HashOf(localPath);

            if (manifest.TryGetValue(remotePath, out var remote))
            {
                bool sameContent = remote.Hash.Length > 0 && remote.Hash == localHash;
                if (sameContent || remote.Modified >= localModified)
                {
                    unchanged++;
                    continue;
                }
            }

            progress?.Report("Uploading " + Path.GetFileName(localPath));
            string? sha = shas.GetValueOrDefault(remotePath);
            await UploadAsync(remotePath, await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false), sha, ct)
                .ConfigureAwait(false);

            manifest[remotePath] = new RemoteFile(localHash, localModified, new FileInfo(localPath).Length);
            manifestChanged = true;
            uploaded++;
        }

        // Backed-up files this machine has never seen, or that are newer there.
        foreach (var (remotePath, remote) in manifest.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (remotePath == ManifestPath) continue;

            string localPath = LocalPathFor(remotePath);
            if (localPath.Length == 0) continue;

            if (File.Exists(localPath))
            {
                if (remote.Hash.Length > 0 && HashOf(localPath) == remote.Hash) continue;
                if (ModifiedOf(remotePath, localPath) >= remote.Modified) continue;
            }

            progress?.Report("Downloading " + Path.GetFileName(localPath));
            byte[]? bytes = await DownloadAsync(remotePath, ct).ConfigureAwait(false);
            if (bytes is null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, bytes, ct).ConfigureAwait(false);

            // Stamp the recorded time onto the file, or the next sync sees a file that is
            // newer than the backup it just came from and uploads it straight back.
            File.SetLastWriteTimeUtc(localPath, remote.Modified.UtcDateTime);
            downloaded++;
        }

        if (manifestChanged || downloaded > 0)
        {
            progress?.Report("Updating the index…");
            await WriteManifestAsync(manifest, shas.GetValueOrDefault(ManifestPath), ct).ConfigureAwait(false);
        }

        string message = uploaded == 0 && downloaded == 0
            ? "Already up to date."
            : $"Backed up {uploaded}, restored {downloaded}.";
        return new SyncOutcome(uploaded, downloaded, unchanged, message);
    }

    private static DateTimeOffset ModifiedOf(string remotePath, string localPath)
    {
        // A table carries its own modified time inside the file, which survives copying and
        // is what the other machine will compare against.
        if (remotePath.StartsWith("tables/", StringComparison.Ordinal))
        {
            var table = CheatTable.Load(localPath);
            if (table is not null) return table.Modified.ToUniversalTime();
        }
        return new DateTimeOffset(File.GetLastWriteTimeUtc(localPath), TimeSpan.Zero);
    }

    /// <summary>
    /// What is worth backing up.
    ///
    /// Every cheat table goes, always. Covers are another matter: git keeps every version of
    /// every blob forever, so uploading an automatic cover costs repository space permanently
    /// in exchange for something the other machine can fetch again from the store IDs already
    /// recorded in the table. Only covers the user chose by hand are irreplaceable, so only
    /// those are backed up.
    /// </summary>
    private static List<(string RemotePath, string LocalPath)> EnumerateLocalFiles()
    {
        var files = new List<(string, string)>();

        string tables = CheatLibrary.DefaultRoot;
        if (!Directory.Exists(tables)) return files;

        foreach (string path in Directory.EnumerateFiles(tables, "*" + CheatTable.FileExtension))
        {
            files.Add(("tables/" + Path.GetFileName(path), path));

            var table = CheatTable.Load(path);
            if (table is null || !table.ArtIsCustom) continue;

            string key = Path.GetFileNameWithoutExtension(path);
            string art = GameMetadataService.ArtFileFor(key);
            if (File.Exists(art)) files.Add(("art/" + Path.GetFileName(art), art));
        }

        return files;
    }

    private static string LocalPathFor(string remotePath)
    {
        if (remotePath.StartsWith("tables/", StringComparison.Ordinal))
            return Path.Combine(CheatLibrary.DefaultRoot, remotePath["tables/".Length..]);
        if (remotePath.StartsWith("art/", StringComparison.Ordinal))
            return Path.Combine(GameMetadataService.DefaultArtDirectory, remotePath["art/".Length..]);
        return string.Empty;
    }

    private async Task EnsureRepositoryAsync(CancellationToken ct)
    {
        using var probe = Authorized(HttpMethod.Get, $"https://api.github.com/repos/{Username}/{RepositoryName}");
        using var found = await Http.SendAsync(probe, ct).ConfigureAwait(false);
        if (found.IsSuccessStatusCode) return;

        if (found.StatusCode == HttpStatusCode.Unauthorized)
            throw new SyncException("GitHub rejected the saved sign-in. Sign out and sign in again.");

        using var create = Authorized(HttpMethod.Post, "https://api.github.com/user/repos");
        create.Content = new StringContent(JsonSerializer.Serialize(new
        {
            name = RepositoryName,
            description = "Cheatmaster library backup",
            @private = true,
            auto_init = true
        }), Encoding.UTF8, "application/json");

        using var created = await Http.SendAsync(create, ct).ConfigureAwait(false);

        // 422 means it already exists under a name we could not read, which is fine.
        if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.UnprocessableEntity)
            throw new SyncException($"Could not create the backup repository ({(int)created.StatusCode}).");
    }

    private async Task<Dictionary<string, string>> FetchShaCacheAsync(CancellationToken ct)
    {
        var shas = new Dictionary<string, string>(StringComparer.Ordinal);

        using var request = Authorized(HttpMethod.Get,
            $"https://api.github.com/repos/{Username}/{RepositoryName}/git/trees/HEAD?recursive=1");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

        // A repository with no commits yet has no tree; that is not an error.
        if (!response.IsSuccessStatusCode) return shas;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("tree", out var tree)) return shas;

        foreach (var node in tree.EnumerateArray())
        {
            if (node.TryGetProperty("path", out var path) && node.TryGetProperty("sha", out var sha) &&
                node.TryGetProperty("type", out var type) && type.GetString() == "blob")
                shas[path.GetString() ?? string.Empty] = sha.GetString() ?? string.Empty;
        }

        return shas;
    }

    private async Task<Dictionary<string, RemoteFile>> FetchManifestAsync(Dictionary<string, string> shas, CancellationToken ct)
    {
        var manifest = new Dictionary<string, RemoteFile>(StringComparer.Ordinal);
        if (!shas.ContainsKey(ManifestPath)) return manifest;

        byte[]? bytes = await DownloadAsync(ManifestPath, ct).ConfigureAwait(false);
        if (bytes is null) return manifest;

        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (!json.RootElement.TryGetProperty("files", out var files)) return manifest;

            foreach (var entry in files.EnumerateObject())
            {
                var value = entry.Value;
                var modified = value.TryGetProperty("m", out var m) && m.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
                long size = value.TryGetProperty("n", out var n) && n.TryGetInt64(out long parsedSize) ? parsedSize : 0;
                string hash = value.TryGetProperty("h", out var h) ? h.GetString() ?? string.Empty : string.Empty;
                manifest[entry.Name] = new RemoteFile(hash, modified, size);
            }
        }
        catch (JsonException)
        {
            // A damaged index just means everything looks new.
        }

        return manifest;
    }

    private async Task WriteManifestAsync(Dictionary<string, RemoteFile> manifest, string? sha, CancellationToken ct)
    {
        var files = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (path, file) in manifest)
        {
            if (path == ManifestPath) continue;
            files[path] = new { m = file.Modified.UtcDateTime.ToString("O"), n = file.Size, h = file.Hash };
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { schemaVersion = SchemaVersion, files },
            new JsonSerializerOptions { WriteIndented = true });

        await UploadAsync(ManifestPath, bytes, sha, ct).ConfigureAwait(false);
    }

    private async Task UploadAsync(string remotePath, byte[] content, string? sha, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Put,
            $"https://api.github.com/repos/{Username}/{RepositoryName}/contents/{Uri.EscapeDataString(remotePath).Replace("%2F", "/", StringComparison.Ordinal)}");

        var payload = sha is { Length: > 0 }
            ? new Dictionary<string, object> { ["message"] = "Update " + remotePath, ["content"] = Convert.ToBase64String(content), ["sha"] = sha }
            : new Dictionary<string, object> { ["message"] = "Add " + remotePath, ["content"] = Convert.ToBase64String(content) };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SyncException("GitHub rejected the saved sign-in. Sign out and sign in again.");
        throw new SyncException($"Uploading {remotePath} failed ({(int)response.StatusCode}).");
    }

    private async Task<byte[]?> DownloadAsync(string remotePath, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get,
            $"https://api.github.com/repos/{Username}/{RepositoryName}/contents/{Uri.EscapeDataString(remotePath).Replace("%2F", "/", StringComparison.Ordinal)}");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("content", out var content)) return null;

        // The API wraps base64 at 60 columns; Convert ignores the whitespace.
        return Convert.FromBase64String(content.GetString() ?? string.Empty);
    }
}

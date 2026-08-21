using System.Net.Http;
using System.Text.Json;

namespace Cheatmaster.Core.Cheats;

public sealed record GameMetadata(int AppId, string Name, string Description, string Developer, string ReleaseDate, string ArtPath);

/// <summary>
/// Fills in a cover image and a description for a saved game.
///
/// Uses the public Steam store endpoints, which need no key and no registration. The identity
/// is resolved from the install itself wherever possible — a <c>steam_appid.txt</c> beside the
/// executable, or the folder name under <c>steamapps\common</c> — and only falls back to
/// searching by name when the install gives nothing away. Everything fetched is cached on
/// disk, so a game is looked up once and never again.
/// </summary>
public sealed class GameMetadataService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly string _artDirectory;

    public GameMetadataService(string? artDirectory = null)
    {
        _artDirectory = artDirectory ?? Path.Combine(CheatLibrary.DefaultRoot, "art");
        Directory.CreateDirectory(_artDirectory);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "Cheatmaster/0.1 (+https://github.com/codingncaffeine/Cheatmaster)");
        return client;
    }

    public string ArtDirectory => _artDirectory;

    public string ArtPathFor(string key) => Path.Combine(_artDirectory, key + ".jpg");

    public bool HasArt(string key) => File.Exists(ArtPathFor(key));

    /// <summary>
    /// Looks up a game and caches its cover. Returns null when nothing matched or the network is
    /// unavailable; a missing cover is never worth an error.
    /// </summary>
    public async Task<GameMetadata?> FetchAsync(GameFingerprint fingerprint, CancellationToken ct = default)
    {
        try
        {
            int appId = ResolveAppId(fingerprint);
            if (appId == 0)
            {
                appId = await SearchAsync(SearchTermFor(fingerprint), ct).ConfigureAwait(false);
                if (appId == 0) return null;
            }

            var details = await DetailsAsync(appId, ct).ConfigureAwait(false);
            if (details is null) return null;

            string artPath = ArtPathFor(fingerprint.Key);
            if (!File.Exists(artPath))
                await DownloadArtAsync(appId, details.Value.HeaderImage, artPath, ct).ConfigureAwait(false);

            return new GameMetadata(appId, details.Value.Name, details.Value.Description,
                details.Value.Developer, details.Value.ReleaseDate, File.Exists(artPath) ? artPath : string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads the app id straight out of the install, which beats any name search.</summary>
    public static int ResolveAppId(GameFingerprint fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint.Path)) return 0;

        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(fingerprint.Path)!);

            // Many builds ship the id beside the executable, or one level up.
            for (var at = directory; at is not null; at = at.Parent)
            {
                string marker = Path.Combine(at.FullName, "steam_appid.txt");
                if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out int id) && id > 0)
                    return id;

                if (string.Equals(at.Name, "common", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(at.Parent?.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable install just means we fall back to searching by name.
        }

        return 0;
    }

    /// <summary>
    /// The folder a game is installed into is usually its real title, which searches far better
    /// than an executable name.
    /// </summary>
    public static string SearchTermFor(GameFingerprint fingerprint)
    {
        string path = fingerprint.Path;
        if (!string.IsNullOrEmpty(path))
        {
            string[] parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "common", StringComparison.OrdinalIgnoreCase) &&
                    i > 0 && string.Equals(parts[i - 1], "steamapps", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1];
            }
        }

        string name = fingerprint.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fingerprint.ExecutableName) : name;
    }

    private static async Task<int> SearchAsync(string term, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term)) return 0;

        string url = "https://store.steampowered.com/api/storesearch/?cc=us&l=en&term=" + Uri.EscapeDataString(term);
        using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!json.RootElement.TryGetProperty("items", out var items)) return 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.TryGetInt32(out int value)) return value;
        }
        return 0;
    }

    private readonly record struct Details(string Name, string Description, string Developer, string ReleaseDate, string HeaderImage);

    private static async Task<Details?> DetailsAsync(int appId, CancellationToken ct)
    {
        string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
        using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!json.RootElement.TryGetProperty(appId.ToString(), out var entry)) return null;
        if (!entry.TryGetProperty("success", out var success) || !success.GetBoolean()) return null;
        if (!entry.TryGetProperty("data", out var data)) return null;

        string name = Text(data, "name");
        string description = Text(data, "short_description");
        string header = Text(data, "header_image");

        string developer = string.Empty;
        if (data.TryGetProperty("developers", out var developers) && developers.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in developers.EnumerateArray())
            {
                developer = d.GetString() ?? string.Empty;
                break;
            }
        }

        string released = string.Empty;
        if (data.TryGetProperty("release_date", out var release) && release.TryGetProperty("date", out var date))
            released = date.GetString() ?? string.Empty;

        return new Details(name, description, developer, released, header);

        static string Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static async Task DownloadArtAsync(int appId, string headerImage, string destination, CancellationToken ct)
    {
        // The tall library capsule looks far better in a grid than the wide header, but not
        // every title has one.
        string[] candidates =
        [
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
            headerImage
        ];

        foreach (string url in candidates)
        {
            if (string.IsNullOrEmpty(url)) continue;
            try
            {
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length < 512) continue;

                string temporary = destination + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // Try the next candidate.
            }
        }
    }
}

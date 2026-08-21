using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cheatmaster.Core.Cheats;

public sealed record GameMetadata(string Provider, int SteamAppId, long GogProductId, string Name,
    string Description, string Developer, string ReleaseDate, string Genres, string ArtPath);

/// <summary>
/// Finds a cover image and a description for a saved game, without an API key, an account, or a
/// quota to run out of.
///
/// Identity comes from the install first (see <see cref="GameIdentity"/>), then two public store
/// catalogues are tried in turn — Steam, then GOG — because plenty of games only exist on one of
/// them. Every call is cancellable and time-boxed: artwork is decoration, and nothing about it
/// is ever allowed to make someone wait to use the app.
/// </summary>
public sealed partial class GameMetadataService
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>A whole lookup, across every candidate and both catalogues, gives up after this.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(25);

    private readonly string _artDirectory;
    private readonly Func<byte[], byte[]>? _optimize;

    /// <param name="optimizeArt">
    /// Optional re-encoder applied to a downloaded cover before it is cached. Supplied by the UI
    /// layer, which has an imaging stack; the engine keeps none of its own.
    /// </param>
    public GameMetadataService(string? artDirectory = null, Func<byte[], byte[]>? optimizeArt = null)
    {
        _artDirectory = artDirectory ?? DefaultArtDirectory;
        _optimize = optimizeArt;
        Directory.CreateDirectory(_artDirectory);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent", "Cheatmaster/0.1 (+https://github.com/codingncaffeine/Cheatmaster)");
        return client;
    }

    public string ArtDirectory => _artDirectory;

    /// <summary>
    /// Covers live at a path derived from the library key, never at a path recorded in the
    /// table. An absolute path saved on one machine means nothing on the next one, and these
    /// tables are meant to travel.
    /// </summary>
    public static string DefaultArtDirectory => Path.Combine(CheatLibrary.DefaultRoot, "art");

    public static string ArtFileFor(string key) => Path.Combine(DefaultArtDirectory, key + ".jpg");

    public string ArtPathFor(string key) => Path.Combine(_artDirectory, key + ".jpg");

    public bool HasArt(string key) => File.Exists(ArtPathFor(key));

    /// <summary>
    /// Looks a game up and caches its cover. Returns null when nothing matched or the network is
    /// unavailable — a missing cover is never worth an error, and never worth a delay.
    /// </summary>
    public async Task<GameMetadata?> FetchAsync(GameFingerprint fingerprint, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(LookupBudget);
        var token = budget.Token;

        try
        {
            // Reading the install touches the disk, so it happens off whatever thread asked.
            var hints = await Task.Run(() => GameIdentity.Resolve(fingerprint), token).ConfigureAwait(false);

            foreach (var hint in hints)
            {
                token.ThrowIfCancellationRequested();

                var found = await LookUpAsync(hint, token).ConfigureAwait(false);
                if (found is null) continue;

                string artPath = ArtPathFor(fingerprint.Key);
                if (!File.Exists(artPath))
                    await DownloadArtAsync(found.Value.ArtUrls, artPath, _optimize, token).ConfigureAwait(false);

                return new GameMetadata(found.Value.Provider, found.Value.SteamAppId, found.Value.GogProductId,
                    found.Value.Name, found.Value.Description, found.Value.Developer, found.Value.ReleaseDate,
                    found.Value.Genres, File.Exists(artPath) ? artPath : string.Empty);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            // Offline, slow, or nothing matched. Either way the app carries on.
        }

        return null;
    }

    private readonly record struct Listing(string Provider, int SteamAppId, long GogProductId, string Name,
        string Description, string Developer, string ReleaseDate, string Genres, string[] ArtUrls);

    private static async Task<Listing?> LookUpAsync(GameIdentityHint hint, CancellationToken ct)
    {
        if (hint.SteamAppId > 0)
        {
            var exact = await SteamDetailsAsync(hint.SteamAppId, ct).ConfigureAwait(false);
            if (exact is not null) return exact;
        }

        if (hint.GogProductId > 0)
        {
            var exact = await GogDetailsAsync(hint.GogProductId, ct).ConfigureAwait(false);
            if (exact is not null) return exact;
        }

        if (string.IsNullOrWhiteSpace(hint.Name)) return null;

        int steamId = await SteamSearchAsync(hint.Name, ct).ConfigureAwait(false);
        if (steamId > 0)
        {
            var listing = await SteamDetailsAsync(steamId, ct).ConfigureAwait(false);
            if (listing is not null) return listing;
        }

        return await GogSearchAsync(hint.Name, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Steam

    private static async Task<int> SteamSearchAsync(string term, CancellationToken ct)
    {
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

    private static async Task<Listing?> SteamDetailsAsync(int appId, CancellationToken ct)
    {
        string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
        using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!json.RootElement.TryGetProperty(appId.ToString(), out var entry)) return null;
        if (!entry.TryGetProperty("success", out var success) || !success.GetBoolean()) return null;
        if (!entry.TryGetProperty("data", out var data)) return null;

        string developer = FirstOf(data, "developers");
        string released = data.TryGetProperty("release_date", out var release) && release.TryGetProperty("date", out var date)
            ? date.GetString() ?? string.Empty
            : string.Empty;

        // The tall library capsule suits a grid far better than the wide header image.
        string[] art =
        [
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
            Text(data, "header_image")
        ];

        return new Listing("Steam", appId, 0, Text(data, "name"), StripHtml(Text(data, "short_description")),
            developer, released, JoinNames(data, "genres", "description"), art);
    }

    // ---------------------------------------------------------------- GOG

    /// <summary>
    /// Searches the GOG catalogue.
    ///
    /// The older embed.gog.com endpoint only searches an account's own library and returns
    /// nothing for a general query, so this uses the catalogue service and then reads the full
    /// listing by id, which keeps one path for images and descriptions.
    /// </summary>
    private static async Task<Listing?> GogSearchAsync(string term, CancellationToken ct)
    {
        string url = "https://catalog.gog.com/v1/catalog?limit=5&query=" +
                     Uri.EscapeDataString("like:" + term);

        using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!json.RootElement.TryGetProperty("products", out var products)) return null;

        foreach (var product in products.EnumerateArray())
        {
            // The catalogue reports ids as strings.
            if (!product.TryGetProperty("id", out var idElement)) continue;
            if (!long.TryParse(idElement.GetString(), out long id) || id <= 0) continue;

            var listing = await GogDetailsAsync(id, ct).ConfigureAwait(false);
            if (listing is null) continue;
            return Enrich(listing.Value, product);
        }

        return null;
    }

    /// <summary>
    /// Folds in what only the catalogue carries: the genres, the developer, and a ready-made
    /// portrait cover. The product endpoint has none of the three.
    /// </summary>
    private static Listing Enrich(Listing listing, JsonElement product)
    {
        var art = new List<string>();
        string cover = Text(product, "coverVertical");
        if (cover.Length > 0) art.Add(Normalize(cover));
        art.AddRange(listing.ArtUrls);

        string developer = FirstOf(product, "developers");
        string genres = JoinNames(product, "genres", "name");

        return listing with
        {
            ArtUrls = [.. art],
            Developer = developer.Length > 0 ? developer : listing.Developer,
            Genres = genres.Length > 0 ? genres : listing.Genres
        };
    }

    /// <summary>
    /// Looks a known product up in the catalogue by its title, to pick up the fields the product
    /// endpoint does not return. Nothing here is worth failing a lookup over.
    /// </summary>
    private static async Task<Listing> EnrichFromCatalogAsync(Listing listing, CancellationToken ct)
    {
        if (listing.Name.Length == 0) return listing;

        try
        {
            string url = "https://catalog.gog.com/v1/catalog?limit=5&query=" +
                         Uri.EscapeDataString("like:" + listing.Name);

            using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("products", out var products)) return listing;

            foreach (var product in products.EnumerateArray())
            {
                if (!product.TryGetProperty("id", out var idElement)) continue;
                if (!long.TryParse(idElement.GetString(), out long id) || id != listing.GogProductId) continue;
                return Enrich(listing, product);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            // Keep what the product endpoint gave us.
        }

        return listing;
    }

    private static async Task<Listing?> GogDetailsAsync(long productId, CancellationToken ct)
    {
        string url = $"https://api.gog.com/products/{productId}?expand=description";
        using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var root = json.RootElement;
        string title = Text(root, "title");
        if (title.Length == 0) return null;

        string description = string.Empty;
        if (root.TryGetProperty("description", out var descriptionElement))
            description = StripHtml(Text(descriptionElement, "lead"));

        string released = Text(root, "release_date");
        if (released.Length >= 10) released = released[..10];

        var art = new List<string>();
        if (root.TryGetProperty("images", out var images))
        {
            string logo = Text(images, "logo2x");
            if (logo.Length == 0) logo = Text(images, "logo");
            art.AddRange(BuildGogCovers(logo));

            // Landscape, and the last thing worth falling back to.
            string background = Text(images, "background");
            if (background.Length > 0) art.Add(Normalize(background));
        }

        var listing = new Listing("GOG", 0, productId, title, description, string.Empty, released, string.Empty, [.. art]);
        return await EnrichFromCatalogAsync(listing, ct).ConfigureAwait(false);
    }

    /// <summary>

    /// <summary>
    /// GOG image URLs are a content hash plus a size suffix, so the portrait covers can be built
    /// from any other image for the same product. The host varies — images-1..4.gog-statics.com
    /// and the older images.gog.com all appear — so only the hash is taken from the URL.
    /// </summary>
    internal static List<string> BuildGogCovers(string imageUrl)
    {
        var covers = new List<string>();
        var match = GogHashPattern().Match(imageUrl);
        if (!match.Success) return covers;

        string hash = match.Groups[1].Value;
        covers.Add($"https://images.gog-statics.com/{hash}_glx_vertical_cover.jpg");
        covers.Add($"https://images.gog-statics.com/{hash}_product_card_v2_mobile_slider_639.jpg");
        return covers;
    }

    /// <summary>
    /// Makes a GOG image URL fetchable: some are protocol-relative, and some already carry an
    /// extension while others do not.
    /// </summary>
    internal static string Normalize(string url)
    {
        if (url.Length == 0) return string.Empty;
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;

        int lastSlash = url.LastIndexOf('/');
        bool hasExtension = lastSlash >= 0 && url.IndexOf('.', lastSlash) > lastSlash;
        return hasExtension ? url : url + ".jpg";
    }

    // ---------------------------------------------------------------- shared

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Flattens an array of objects into "A, B, C" using one named field from each.</summary>
    private static string JoinNames(JsonElement element, string property, string field, int max = 3)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var names = new List<string>(max);
        foreach (var item in array.EnumerateArray())
        {
            string name = Text(item, field);
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
            if (names.Count >= max) break;
        }
        return string.Join(", ", names);
    }

    private static string FirstOf(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var item in array.EnumerateArray())
            return item.GetString() ?? string.Empty;
        return string.Empty;
    }

    /// <summary>
    /// Turns a store blurb into plain prose. Tags are only half the job: these descriptions are
    /// full of entities like &amp;nbsp; and layout whitespace, which show up verbatim as
    /// gibberish if only the angle brackets are removed.
    /// </summary>
    internal static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        string text = HtmlTagPattern().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = text.Replace(' ', ' ');            // decoded &nbsp;
        text = WhitespaceRunPattern().Replace(text, " ");
        return text.Trim();
    }

    /// <summary>Downloads the first candidate that works, re-encoded to the size the app draws.</summary>
    public static async Task<bool> DownloadArtAsync(string[] candidates, string destination,
        Func<byte[], byte[]>? optimize, CancellationToken ct)
    {
        foreach (string url in candidates)
        {
            if (string.IsNullOrEmpty(url)) continue;
            ct.ThrowIfCancellationRequested();

            try
            {
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length < 512) continue;

                if (optimize is not null) bytes = optimize(bytes);

                string? directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string temporary = destination + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // Try the next candidate.
            }
        }

        return false;
    }

    /// <summary>
    /// Re-downloads just the cover for a game whose store identity is already known. This is how
    /// a second machine gets its artwork back without any of it having to travel through the
    /// backup repository.
    /// </summary>
    public async Task<bool> RestoreArtAsync(string key, int steamAppId, long gogProductId, CancellationToken ct = default)
    {
        string destination = ArtPathFor(key);
        if (File.Exists(destination)) return true;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(LookupBudget);

        try
        {
            Listing? listing = steamAppId > 0
                ? await SteamDetailsAsync(steamAppId, budget.Token).ConfigureAwait(false)
                : gogProductId > 0
                    ? await GogDetailsAsync(gogProductId, budget.Token).ConfigureAwait(false)
                    : null;

            if (listing is null) return false;
            return await DownloadArtAsync(listing.Value.ArtUrls, destination, _optimize, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return false;
        }
    }

    // Host-agnostic on purpose: GOG serves from images.gog.com, images.gog-statics.com and
    // images-1..4.gog-statics.com, and only the content hash is stable across them.
    [GeneratedRegex(@"([0-9a-f]{32,})")]
    private static partial Regex GogHashPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRunPattern();
}

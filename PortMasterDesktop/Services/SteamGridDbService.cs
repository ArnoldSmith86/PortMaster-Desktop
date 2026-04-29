using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using PortMasterDesktop.Models;

namespace PortMasterDesktop.Services;

public class SteamGridDbService
{
    private static readonly HttpClient Http = new();
    private readonly CacheService _cache;
    private readonly string _apiKey;
    // Shared with DiskCachedWebImageLoader — raw image files keyed by MD5(url)
    private readonly string _imageCacheDir;

    public SteamGridDbService(CacheService cache)
    {
        _cache = cache;
        _apiKey = Secrets.SteamGridDbApiKey;
        _imageCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortMasterDesktop", "ImageCache");
    }

    /// <summary>
    /// Enriches SgdbCoverUrl for matches whose covers are not already a ~2:3 portrait.
    /// Detects aspect ratio by reading image headers — images are cached in the shared image
    /// cache directory so AsyncImageLoader never re-downloads them.
    /// Pass <paramref name="setUrl"/> to dispatch updates to the UI thread from a ViewModel.
    /// </summary>
    public async Task EnrichMatchesAsync(
        IEnumerable<GameMatch> matches,
        Action<GameMatch, string>? setUrl = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return;

        setUrl ??= static (m, u) => m.SgdbCoverUrl = u;

        // Sync pre-filter: skip matches where every owned game is a known portrait format
        var candidates = matches
            .Where(m => m.OwnedGames.Any(g => !IsKnownPortrait(g)))
            .ToList();
        if (candidates.Count == 0) return;

        var sem = new SemaphoreSlim(3);
        await Task.WhenAll(candidates.Select(async match =>
        {
            await sem.WaitAsync(ct);
            try
            {
                foreach (var game in match.OwnedGames.Where(g => !IsKnownPortrait(g)))
                {
                    // Skip if actual image dimensions confirm ~2:3 portrait
                    if (await IsPortrait23Async(game, ct)) continue;

                    var url = await TryGetGridCoverAsync(game.Store, game.Id, game.Title, ct);
                    if (!string.IsNullOrEmpty(url))
                    {
                        setUrl(match, url);
                        break;
                    }
                }
            }
            finally { sem.Release(); }
        }));
    }

    // Sync: does this game have a cover we can confirm is portrait without a network round-trip?
    private static bool IsKnownPortrait(StoreGame game) =>
        game.CoverUrl.Contains("BoxTall"); // Epic tall key art is always portrait

    // Hash a URL for use as a cache key
    private static string HashUrl(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    // Async: does the actual image have approximately 2:3 portrait dimensions?
    private async Task<bool> IsPortrait23Async(StoreGame game, CancellationToken ct)
    {
        var url = game.CoverUrl;
        if (string.IsNullOrEmpty(url)) return false;

        // Cache by URL, not store+ID, so cover URL changes are detected
        var cacheKey = $"coverdim_{HashUrl(url)}";
        var cached = await _cache.LoadJsonAsync<string>(cacheKey);
        if (cached != null) return IsApprox23(cached);

        var (w, h) = await FetchAndCacheDimensionsAsync(url, ct);
        var dim = $"{w}x{h}";
        await _cache.SaveJsonAsync(cacheKey, dim);
        return IsApprox23(dim);
    }

    // True if "WxH" represents approximately a 2:3 portrait (width/height ≈ 0.667 ± 6%)
    private static bool IsApprox23(string dim)
    {
        var x = dim.IndexOf('x');
        if (x <= 0) return false;
        if (!int.TryParse(dim[..x], out int w) || !int.TryParse(dim[(x + 1)..], out int h))
            return false;
        if (w <= 0 || h <= 0) return false;
        var ratio = (double)w / h;
        return ratio is >= 0.63 and <= 0.71;
    }

    /// <summary>
    /// Returns image dimensions, ensuring the full image is stored in the shared image cache
    /// (keyed by MD5 of the URL) so that AsyncImageLoader can serve it from disk without a
    /// separate download.
    /// </summary>
    private async Task<(int w, int h)> FetchAndCacheDimensionsAsync(string url, CancellationToken ct)
    {
        const int PeekBytes = 1024;
        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var path = new Uri(url).LocalPath;
                if (!File.Exists(path)) return (0, 0);
                var buf = new byte[PeekBytes];
                await using var fs = File.OpenRead(path);
                var n = await fs.ReadAsync(buf.AsMemory(0, PeekBytes), ct);
                return ParseDimensions(buf.AsSpan(0, n));
            }

            // Check if already in the shared image cache (written by us or by AsyncImageLoader)
            var cacheFile = ImageCachePath(url);
            if (File.Exists(cacheFile))
            {
                var buf = new byte[PeekBytes];
                await using var fs = File.OpenRead(cacheFile);
                var n = await fs.ReadAsync(buf.AsMemory(0, PeekBytes), ct);
                return ParseDimensions(buf.AsSpan(0, n));
            }

            // Download full image once and save to the shared cache so AsyncImageLoader won't
            // need to download it again when displaying this cover URL.
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return (0, 0);

            var imageData = await resp.Content.ReadAsByteArrayAsync(ct);
            Directory.CreateDirectory(_imageCacheDir);
            await File.WriteAllBytesAsync(cacheFile, imageData, ct);
            var dims = ParseDimensions(imageData.AsSpan(0, Math.Min(imageData.Length, PeekBytes)));
            System.Diagnostics.Debug.WriteLine($"[SteamGridDb] Fetched {url} → {dims.w}x{dims.h}");
            return dims;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamGridDb] Error fetching {url}: {ex.Message}");
            return (0, 0);
        }
    }

    // Returns the path where this URL's image is (or should be) in the shared image cache.
    private string ImageCachePath(string url) =>
        Path.Combine(_imageCacheDir,
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url))));

    private static (int w, int h) ParseDimensions(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4) return (0, 0);

        // PNG: 89 50 4E 47 magic — width and height at bytes 16–23 in the IHDR chunk
        if (buf.Length >= 24 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47)
        {
            int w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
            int h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
            return (w, h);
        }

        // JPEG: FF D8 — walk markers to find an SOF segment with height/width
        if (buf.Length >= 4 && buf[0] == 0xFF && buf[1] == 0xD8)
        {
            int i = 2;
            while (i + 3 < buf.Length)
            {
                if (buf[i] != 0xFF) break;
                byte m = buf[i + 1];
                if (m is 0xD8 or 0xD9) break;
                if (m is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB)
                {
                    if (i + 8 < buf.Length)
                    {
                        int h = (buf[i + 5] << 8) | buf[i + 6];
                        int w = (buf[i + 7] << 8) | buf[i + 8];
                        if (w > 0 && h > 0 && w < 20000 && h < 20000)
                            return (w, h);
                    }
                    break;
                }
                int segLen = (buf[i + 2] << 8) | buf[i + 3];
                if (segLen < 2) break;
                i += 2 + segLen;
            }
        }

        return (0, 0);
    }

    // Platform-ID lookup for stores SteamGridDB supports directly.
    // itch.io, Amazon, Humble have no supported platform ID → fall back to name search.
    private static string? ToPlatform(StoreId store) => store switch
    {
        StoreId.Steam => "steam",
        StoreId.Gog   => "gog",
        StoreId.Epic  => "egs",
        _             => null,
    };

    private async Task<string?> TryGetGridCoverAsync(
        StoreId store, string gameId, string title, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(gameId)) return null;

        var platform = ToPlatform(store);

        if (platform != null)
        {
            // Platform/ID lookup (Steam, GOG, Epic) — cached under sgdb_{store}_{gameId}
            var cacheKey = $"sgdb_{store}_{gameId}";
            var cached = await _cache.LoadJsonAsync<string>(cacheKey);
            if (cached != null) return cached.Length == 0 ? null : cached;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://www.steamgriddb.com/api/v2/grids/{platform}/{gameId}?dimensions=600x900");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) { await _cache.SaveJsonAsync(cacheKey, ""); return null; }
                var json = await resp.Content.ReadFromJsonAsync<SgdbResponse>(cancellationToken: ct);
                var url = json?.Data?.FirstOrDefault()?.Url;
                await _cache.SaveJsonAsync(cacheKey, url ?? "");
                return url;
            }
            catch { return null; }
        }
        else if (!string.IsNullOrEmpty(title))
        {
            // Name search fallback (itch.io, Amazon, Humble) — separate cache key so stale
            // platform-ID entries don't block this path.
            var cacheKey = $"sgdb_ns_{store}_{gameId}";
            var cached = await _cache.LoadJsonAsync<string>(cacheKey);
            if (cached != null) return cached.Length == 0 ? null : cached;

            var url = await SearchByTitleAsync(title, ct);
            await _cache.SaveJsonAsync(cacheKey, url ?? "");
            return url;
        }

        return null;
    }

    private async Task<string?> SearchByTitleAsync(string title, CancellationToken ct)
    {
        try
        {
            using var searchReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(title)}");
            searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var searchResp = await Http.SendAsync(searchReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!searchResp.IsSuccessStatusCode) return null;

            var searchJson = await searchResp.Content.ReadFromJsonAsync<SgdbSearchResponse>(cancellationToken: ct);
            var sgdbId = searchJson?.Data?.FirstOrDefault()?.Id;
            if (sgdbId == null) return null;

            using var gridReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.steamgriddb.com/api/v2/grids/game/{sgdbId}?dimensions=600x900");
            gridReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var gridResp = await Http.SendAsync(gridReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!gridResp.IsSuccessStatusCode) return null;

            var gridJson = await gridResp.Content.ReadFromJsonAsync<SgdbResponse>(cancellationToken: ct);
            return gridJson?.Data?.FirstOrDefault()?.Url;
        }
        catch { return null; }
    }

    private record SgdbResponse(
        [property: JsonPropertyName("data")] List<SgdbGrid>? Data);

    private record SgdbGrid(
        [property: JsonPropertyName("url")] string? Url);

    private record SgdbSearchResponse(
        [property: JsonPropertyName("data")] List<SgdbGameResult>? Data);

    private record SgdbGameResult(
        [property: JsonPropertyName("id")] int? Id);
}

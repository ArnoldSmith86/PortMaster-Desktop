using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PortMasterDesktop.Models;

namespace PortMasterDesktop.Services;

public class SteamGridDbService
{
    private static readonly HttpClient Http = new();
    private readonly CacheService _cache;
    private readonly string _apiKey;

    public SteamGridDbService(CacheService cache)
    {
        _cache = cache;
        _apiKey = Secrets.SteamGridDbApiKey;
    }

    /// <summary>
    /// Enriches SgdbCoverUrl for matches whose owned games don't already have a local portrait.
    /// Runs up to 3 requests concurrently.
    /// <para>Pass <paramref name="setUrl"/> to control how the result is applied — e.g. dispatch
    /// to the UI thread when calling from a ViewModel. Defaults to a direct assignment.</para>
    /// </summary>
    public async Task EnrichMatchesAsync(
        IEnumerable<GameMatch> matches,
        Action<GameMatch, string>? setUrl = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return;

        setUrl ??= static (m, u) => m.SgdbCoverUrl = u;

        var toEnrich = matches
            .Where(m => m.OwnedGames.Any(NeedsPortraitCover))
            .ToList();
        if (toEnrich.Count == 0) return;

        var sem = new SemaphoreSlim(3);
        await Task.WhenAll(toEnrich.Select(async match =>
        {
            await sem.WaitAsync(ct);
            try
            {
                foreach (var game in match.OwnedGames.Where(NeedsPortraitCover))
                {
                    var url = await TryGetGridCoverAsync(game.Store, game.Id, ct);
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

    private async Task<string?> TryGetGridCoverAsync(
        StoreId store, string gameId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(gameId)) return null;

        var platform = ToPlatform(store);
        if (platform == null) return null;

        var cacheKey = $"sgdb_{store}_{gameId}";
        var cached = await _cache.LoadJsonAsync<string>(cacheKey);
        if (cached != null) return cached.Length == 0 ? null : cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.steamgriddb.com/api/v2/grids/{platform}/{gameId}?dimensions=600x900");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                await _cache.SaveJsonAsync(cacheKey, "");
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<SgdbResponse>(cancellationToken: ct);
            var url = json?.Data?.FirstOrDefault()?.Url;
            await _cache.SaveJsonAsync(cacheKey, url ?? "");
            return url;
        }
        catch { return null; }
    }

    private static bool NeedsPortraitCover(StoreGame game)
    {
        var url = game.CoverUrl;
        if (string.IsNullOrEmpty(url)) return true;
        // A local file:// path means Steam has cached the portrait on disk — we know it exists.
        // CDN URLs (even library_600x900_2x) can 404 for older games, so we still try SteamGridDB.
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("BoxTall")) return false; // Epic tall key art
        return true;
    }

    private static string? ToPlatform(StoreId store) => store switch
    {
        StoreId.Steam  => "steam",
        StoreId.Gog    => "gog",
        StoreId.Epic   => "egs",
        StoreId.Itch   => "itchio",
        _ => null,
    };

    private record SgdbResponse(
        [property: JsonPropertyName("data")] List<SgdbGrid>? Data);

    private record SgdbGrid(
        [property: JsonPropertyName("url")] string? Url);
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PortMasterDesktop.Models;

namespace PortMasterDesktop.Services;

/// <summary>
/// Fetches 600×900 portrait grid images from SteamGridDB for games that don't
/// already have one. Results are cached to disk so subsequent loads are free.
/// Does nothing when the API key is not configured.
/// </summary>
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
    /// Updates CoverUrl in-place for every game in the list that needs a portrait cover,
    /// using at most 3 concurrent SteamGridDB requests.
    /// </summary>
    public async Task EnrichCoversAsync(IEnumerable<StoreGame> games, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return;

        var sem = new SemaphoreSlim(3);
        await Task.WhenAll(games.Select(async game =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var url = await TryGetGridCoverAsync(game.Store, game.Id, ct);
                if (!string.IsNullOrEmpty(url)) game.CoverUrl = url;
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

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Epic Games Store integration via OAuth2 authorization code flow.
/// Public EGS launcher credentials (same as Heroic, Legendary, etc.).
/// </summary>
public class EpicStore : BaseGameStore
{
    public override StoreId StoreId => StoreId.Epic;
    public override string DisplayName => "Epic Games";

    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string ClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string LibraryCacheKey = "epic_library";

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private List<StoreGame>? _libraryCache;

    public EpicStore(CacheService cache) : base(cache) { }

    public override async Task<bool> IsAuthenticatedAsync()
        => await LoadCredentialAsync("refresh_token") != null;

    // Legendary/Heroic login pattern: /id/login → /id/api/redirect returns exchange code JSON.
    private static readonly string ApiRedirectUrl =
        "https://www.epicgames.com/id/api/redirect" +
        $"?clientId=34a02cf8f4414e29b15921876da36f9a&responseType=code";

    public override async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        var authUrl = "https://www.epicgames.com/id/login" +
            $"?redirectUrl={HttpUtility.UrlEncode(ApiRedirectUrl)}";

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch { }

        var pasted = await PromptAsync("Epic Games Login — Paste Code",
            "Log in to Epic Games in your browser. After logging in you will see a page showing " +
            "a JSON object with an \"authorizationCode\" field — paste the code value (or the full page text) here.");

        if (string.IsNullOrWhiteSpace(pasted)) return false;

        // Accept either the raw code or a JSON blob containing authorizationCode
        var code = ExtractExchangeCode(pasted.Trim());
        if (string.IsNullOrEmpty(code)) return false;
        return await ExchangeCodeAsync(code, ct);
    }

    private static string? ExtractExchangeCode(string input)
    {
        // Try JSON field "authorizationCode":"..."
        var m = Regex.Match(input, @"""authorizationCode""\s*:\s*""([^""]+)""");
        if (m.Success) return m.Groups[1].Value;
        // Bare code (alphanumeric, 32 chars typical)
        if (Regex.IsMatch(input, @"^[A-Za-z0-9_\-]{20,}$")) return input;
        return null;
    }

    private async Task<bool> ExchangeCodeAsync(string exchangeCode, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
            req.Headers.Add("User-Agent",
                "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = exchangeCode,
                ["token_type"] = "eg1",
            });
            var resp = await GetJsonAsync<EpicTokenResponse>(req, ct);
            if (resp?.AccessToken == null || resp.RefreshToken == null) return false;
            return await StoreTokensAsync(resp);
        }
        catch { return false; }
    }

    private async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var rt = await LoadCredentialAsync("refresh_token");
        if (rt == null) return false;
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = rt,
            ["token_type"] = "eg1",
        });
        try
        {
            var resp = await GetJsonAsync<EpicTokenResponse>(req, ct);
            if (resp == null) return false;
            return await StoreTokensAsync(resp);
        }
        catch { return false; }
    }

    private async Task<bool> StoreTokensAsync(EpicTokenResponse resp)
    {
        await SaveCredentialAsync("access_token", resp.AccessToken);
        await SaveCredentialAsync("refresh_token", resp.RefreshToken);
        if (resp.AccountId != null) await SaveCredentialAsync("account_id", resp.AccountId);
        if (resp.DisplayName != null) await SaveCredentialAsync("display_name", resp.DisplayName);
        _accessToken = resp.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(resp.ExpiresIn - 300);
        return true;
    }

    private async Task<string?> GetValidTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return _accessToken;
        _accessToken = await LoadCredentialAsync("access_token");
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return _accessToken;
        return await RefreshAsync(ct) ? _accessToken : null;
    }

    public override Task LogoutAsync()
    {
        foreach (var k in new[] { "access_token", "refresh_token", "account_id", "display_name" })
            DeleteCredential(k);
        _accessToken = null; _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override async Task<string?> GetAccountNameAsync()
        => await LoadCredentialAsync("display_name");

    public override async Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (_libraryCache != null) return _libraryCache;
        var cached = await Cache.LoadJsonAsync<List<StoreGame>>(LibraryCacheKey);
        if (cached != null) { _libraryCache = cached; return _libraryCache; }

        var token = await GetValidTokenAsync(ct);
        if (token == null) return [];

        // Step 1: collect all library records
        var records = new List<EpicRecord>();
        string? cursor = null;
        do
        {
            var url = "https://library-service.live.use1a.on.epicgames.com/library/api/public/items" +
                      "?includeMetadata=true" + (cursor != null ? $"&cursor={cursor}" : "");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await GetJsonAsync<EpicLibraryResponse>(req, ct);
            if (resp == null) break;
            records.AddRange(resp.Records.Where(r =>
                r.SandboxType != "PRIVATE" && r.AppName != null && r.SandboxName != null));
            cursor = resp.ResponseMetadata?.NextCursor;
        } while (cursor != null);

        // Step 2: deduplicate — same namespace+sandboxName = same game (base + DLC entries)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = records
            .Where(r => seen.Add($"{r.Namespace}:{r.SandboxName}"))
            .ToList();

        // Step 3: fetch title + cover images from catalog API (one pass, grouped by namespace)
        var catalogData = new Dictionary<string, EpicCatalogItem>(StringComparer.OrdinalIgnoreCase); // catalogItemId → item
        foreach (var group in deduped.GroupBy(r => r.Namespace))
        {
            var ids = group.Select(r => r.CatalogItemId).OfType<string>().ToList();
            if (ids.Count == 0) continue;
            try
            {
                var idParams = string.Join("&", ids.Select(id => $"id={Uri.EscapeDataString(id)}"));
                var catalogUrl = "https://catalog-public-service-prod06.ol.epicgames.com" +
                    $"/catalog/api/shared/namespace/{group.Key}/bulk/items" +
                    $"?{idParams}&includeDLCDetails=false&includeMainGameDetails=true&country=US&locale=en-US";
                using var catalogReq = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
                catalogReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var items = await GetJsonAsync<Dictionary<string, EpicCatalogItem>>(catalogReq, ct);
                if (items == null) continue;
                foreach (var (itemId, item) in items)
                    catalogData[itemId] = item;
            }
            catch { /* catalog data is enrichment only */ }
        }

        // Step 4: build game list — prefer catalog title over sandboxName (sandboxName can be "Live" etc.)
        var games = new List<StoreGame>();
        foreach (var r in deduped)
        {
            var catalog = r.CatalogItemId != null && catalogData.TryGetValue(r.CatalogItemId, out var ci) ? ci : null;
            var title = catalog?.Title ?? r.SandboxName!;
            var cover = catalog?.KeyImages?
                .FirstOrDefault(i => i.Type == "DieselGameBoxTall" || i.Type == "OfferImageTall")?.Url ?? "";
            // appName is either a real slug (like "Sugar") or an opaque UUID hash
            var appName = r.AppName!;
            var storeUrl = IsHexId(appName)
                ? $"https://store.epicgames.com/en-US/browse?q={Uri.EscapeDataString(title)}"
                : $"https://store.epicgames.com/en-US/p/{appName.ToLowerInvariant()}";
            games.Add(new StoreGame
            {
                Store = StoreId.Epic, Id = appName,
                Title = title,
                StoreUrl = storeUrl,
                CoverUrl = cover,
            });
        }

        await Cache.SaveJsonAsync(LibraryCacheKey, games);
        _libraryCache = games;
        return _libraryCache;
    }

    // appName is an opaque hex id (not a usable slug) for most EGS games
    private static bool IsHexId(string s) =>
        s.Length >= 24 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    public override async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        var m = Regex.Match(storeUrl, @"/p/([^/?&#]+)");
        if (!m.Success) return null;
        var slug = m.Groups[1].Value.ToLowerInvariant();
        var lib = await GetLibraryAsync(ct);

        // Try direct slug match against Id or StoreUrl first
        var direct = lib.FirstOrDefault(g =>
            g.Id.Equals(slug, StringComparison.OrdinalIgnoreCase) ||
            g.StoreUrl.Contains($"/p/{slug}", StringComparison.OrdinalIgnoreCase));
        if (direct != null) return direct;

        // Fuzzy: strip non-alphanumeric from both slug and title and compare
        var slugNorm = NormalizeSlug(slug);
        return lib.FirstOrDefault(g => NormalizeSlug(g.Title) == slugNorm);
    }

    private static string NormalizeSlug(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

    private record EpicTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("account_id")] string? AccountId,
        [property: JsonPropertyName("displayName")] string? DisplayName);
    private record EpicLibraryResponse(
        [property: JsonPropertyName("records")] List<EpicRecord> Records,
        [property: JsonPropertyName("responseMetadata")] EpicMetadata? ResponseMetadata);
    private record EpicRecord(
        [property: JsonPropertyName("appName")]       string? AppName,
        [property: JsonPropertyName("sandboxType")]   string? SandboxType,
        [property: JsonPropertyName("sandboxName")]   string? SandboxName,
        [property: JsonPropertyName("namespace")]     string? Namespace,
        [property: JsonPropertyName("catalogItemId")] string? CatalogItemId);
    private record EpicMetadata(
        [property: JsonPropertyName("nextCursor")] string? NextCursor);
    private record EpicCatalogItem(
        [property: JsonPropertyName("title")]     string? Title,
        [property: JsonPropertyName("keyImages")] List<EpicKeyImage>? KeyImages);
    private record EpicKeyImage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")]  string Url);
}

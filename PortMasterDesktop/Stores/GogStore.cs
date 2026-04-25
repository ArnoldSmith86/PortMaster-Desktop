using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.Stores;

/// <summary>
/// GOG integration via OAuth2.
/// Public Galaxy client credentials (same as Heroic, Lutris, etc. — not secret).
/// </summary>
public class GogStore : BaseGameStore
{
    public override StoreId StoreId => StoreId.Gog;
    public override string DisplayName => "GOG";

    private const string ClientId = "46899977096215655";
    private const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    private const string LibraryCacheKey = "gog_library";

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private List<StoreGame>? _libraryCache;

    public GogStore(CacheService cache) : base(cache) { }

    public override async Task<bool> IsAuthenticatedAsync()
        => await LoadCredentialAsync("refresh_token") != null;

    // GOG only accepts this exact redirect URI for the Galaxy client_id.
    private const string RedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    public override async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        var authUrl = "https://auth.gog.com/auth" +
            $"?client_id={ClientId}" +
            $"&redirect_uri={HttpUtility.UrlEncode(RedirectUri)}" +
            "&response_type=code&layout=client2";

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch { }

        var pasted = await PromptAsync("GOG Login — Paste URL",
            "Log in to GOG in your browser. After logging in, GOG will redirect to a page — " +
            "copy the full URL from the address bar and paste it here.");

        if (string.IsNullOrWhiteSpace(pasted)) return false;

        string? code;
        try
        {
            code = HttpUtility.ParseQueryString(new Uri(pasted.Trim()).Query)["code"];
        }
        catch
        {
            code = pasted.Trim(); // user may have pasted just the code
        }

        if (string.IsNullOrEmpty(code)) return false;
        return await ExchangeCodeAsync(code, RedirectUri, ct);
    }

    private async Task<bool> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var url = "https://auth.gog.com/token?" +
            $"client_id={ClientId}&client_secret={ClientSecret}" +
            "&grant_type=authorization_code" +
            $"&code={HttpUtility.UrlEncode(code)}" +
            $"&redirect_uri={HttpUtility.UrlEncode(redirectUri)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var resp = await GetJsonAsync<GogTokenResponse>(req, ct);
        if (resp == null) return false;
        return await StoreTokensAsync(resp);
    }

    private async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var rt = await LoadCredentialAsync("refresh_token");
        if (rt == null) return false;
        var url = "https://auth.gog.com/token?" +
            $"client_id={ClientId}&client_secret={ClientSecret}" +
            $"&grant_type=refresh_token&refresh_token={HttpUtility.UrlEncode(rt)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await GetJsonAsync<GogTokenResponse>(req, ct);
            if (resp == null) return false;
            return await StoreTokensAsync(resp);
        }
        catch { return false; }
    }

    private async Task<bool> StoreTokensAsync(GogTokenResponse resp)
    {
        await SaveCredentialAsync("access_token", resp.AccessToken);
        await SaveCredentialAsync("refresh_token", resp.RefreshToken);
        if (resp.UserId != null) await SaveCredentialAsync("user_id", resp.UserId);
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
        foreach (var k in new[] { "access_token", "refresh_token", "user_id" })
            DeleteCredential(k);
        _accessToken = null; _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override async Task<string?> GetAccountNameAsync()
    {
        var token = await GetValidTokenAsync();
        if (token == null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://embed.gog.com/userData.json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await GetJsonAsync<GogUserData>(req);
            return resp?.Username;
        }
        catch { return null; }
    }

    public override async Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (_libraryCache != null) return _libraryCache;
        var cached = await Cache.LoadJsonAsync<List<StoreGame>>(LibraryCacheKey);
        if (cached != null) { _libraryCache = cached; return _libraryCache; }

        var token = await GetValidTokenAsync(ct);
        if (token == null) return [];

        var games = new List<StoreGame>();
        int page = 1, totalPages = 1;
        do
        {
            var url = $"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await GetJsonAsync<GogLibraryResponse>(req, ct);
            if (resp == null) break;
            totalPages = resp.TotalPages;
            foreach (var p in resp.Products)
                games.Add(new StoreGame
                {
                    Store = StoreId.Gog, Id = p.Id.ToString(), Title = p.Title ?? "",
                    CoverUrl = p.Image != null ? $"https:{p.Image}_392.jpg" : "",
                    StoreUrl = p.Url ?? "",
                });
            page++;
        } while (page <= totalPages);

        await Cache.SaveJsonAsync(LibraryCacheKey, games);
        _libraryCache = games;
        return _libraryCache;
    }

    public override async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(storeUrl)) return null;
        var target = Regex.Replace(storeUrl, @"/[a-z]{2}(?:-[A-Z]{2})?/game/", "/game/")
                          .TrimEnd('/').ToLowerInvariant();
        var lib = await GetLibraryAsync(ct);
        return lib.FirstOrDefault(g =>
            Regex.Replace(g.StoreUrl, @"/[a-z]{2}(?:-[A-Z]{2})?/game/", "/game/")
                 .TrimEnd('/').ToLowerInvariant() == target);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the Linux installer for a GOG product.
    /// Returns null on success, error string on failure.
    /// </summary>
    public async Task<string?> DownloadInstallerAsync(
        string productId,
        string destDir,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(("Authenticating with GOG…", 0));
        var token = await GetValidTokenAsync(ct);
        if (token == null) return "Not authenticated with GOG. Log in via Settings.";

        // Fetch product downloads
        progress?.Report(("Fetching installer info…", 0.02));
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.gog.com/products/{productId}?expand=downloads");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var product = await GetJsonAsync<GogProductDownloads>(req, ct);

        var installer = product?.Downloads?.Installers?
            .FirstOrDefault(i => i.Os?.Equals("linux", StringComparison.OrdinalIgnoreCase) == true
                              && i.Language?.Equals("en", StringComparison.OrdinalIgnoreCase) == true)
            ?? product?.Downloads?.Installers?.FirstOrDefault(i =>
                              i.Os?.Equals("linux", StringComparison.OrdinalIgnoreCase) == true);

        if (installer == null)
            return "No Linux installer found for this GOG product.";
        if (installer.Files == null || installer.Files.Count == 0)
            return "Installer has no files listed.";

        progress?.Report(($"Found Linux installer v{installer.Version} ({installer.TotalSize / 1024 / 1024} MB)", 0.03));

        Directory.CreateDirectory(destDir);

        foreach (var file in installer.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(file.Downlink)) continue;

            // Resolve the downlink to the actual CDN URL
            progress?.Report(("Resolving CDN URL…", 0.05));
            using var dlReq = new HttpRequestMessage(HttpMethod.Get, file.Downlink);
            dlReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resolved = await GetJsonAsync<GogDownlinkResponse>(dlReq, ct);
            if (string.IsNullOrEmpty(resolved?.Downlink))
                return $"Could not resolve download URL for {file.Downlink}";

            var fileName = resolved.Downlink.Split('?')[0].Split('/').Last();
            if (string.IsNullOrEmpty(fileName)) fileName = $"gog_{productId}_linux.sh";
            var destPath = Path.Combine(destDir, fileName);

            if (File.Exists(destPath))
            {
                progress?.Report(($"Already cached: {fileName}", 1.0));
                return null;
            }

            progress?.Report(($"Downloading {fileName}…", 0.06));
            var dlErr = await DownloadFileAsync(resolved.Downlink, destPath, installer.TotalSize, progress, ct);
            if (dlErr != null) return dlErr;
        }

        return null;
    }

    private static async Task<string?> DownloadFileAsync(
        string url, string destPath, long totalSize,
        IProgress<(string message, double fraction)>? progress, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PortMasterDesktop/1.0");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return $"HTTP {(int)resp.StatusCode} from CDN.";

        var total = resp.Content.Headers.ContentLength ?? totalSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[131072];
        long done = 0; int read;
        var fileName = Path.GetFileName(destPath);
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            var frac = total > 0 ? (double)done / total : 0;
            progress?.Report(($"{fileName}  {done / 1048576.0:F1} / {total / 1048576.0:F1} MB", frac));
        }
        return null;
    }

    private record GogProductDownloads(
        [property: JsonPropertyName("downloads")] GogDownloads? Downloads);
    private record GogDownloads(
        [property: JsonPropertyName("installers")] List<GogInstaller>? Installers);
    private record GogInstaller(
        [property: JsonPropertyName("os")]       string? Os,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("version")]  string? Version,
        [property: JsonPropertyName("total_size")] long TotalSize,
        [property: JsonPropertyName("files")]    List<GogInstallerFile>? Files);
    private record GogInstallerFile(
        [property: JsonPropertyName("downlink")] string? Downlink);
    private record GogDownlinkResponse(
        [property: JsonPropertyName("downlink")] string? Downlink);

    private record GogTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("user_id")] string? UserId);
    private record GogUserData([property: JsonPropertyName("username")] string? Username);
    private record GogLibraryResponse(
        [property: JsonPropertyName("products")] List<GogProduct> Products,
        [property: JsonPropertyName("totalPages")] int TotalPages);
    private record GogProduct(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("url")] string? Url);
}

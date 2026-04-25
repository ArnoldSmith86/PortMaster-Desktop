using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.Stores;

/// <summary>
/// itch.io integration using a user-supplied API key.
///
/// itch.io's OAuth client uses "itch://oauth-callback" as the redirect URI,
/// which only the official itch.io app can intercept. Instead we ask the user
/// to create an API key at https://itch.io/user/settings/api-keys.
/// </summary>
public class ItchStore : BaseGameStore
{
    public override StoreId StoreId => StoreId.Itch;
    public override string DisplayName => "itch.io";

    private const string LibraryCacheKey = "itch_library";
    private string? _apiKey;
    private List<StoreGame>? _libraryCache;

    public ItchStore(CacheService cache) : base(cache) { }

    public override async Task<bool> IsAuthenticatedAsync()
    {
        _apiKey ??= await LoadCredentialAsync("api_key");
        return _apiKey != null;
    }

    public override async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        // Direct the user to create an API key — itch.io OAuth callback is itch:// only
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://itch.io/user/settings/api-keys") { UseShellExecute = true });
        }
        catch { }

        var key = await PromptAsync("itch.io API Key",
            "Create an API key at itch.io → Settings → API keys, then paste it here.");

        if (string.IsNullOrWhiteSpace(key)) return false;

        // Validate the key by calling /me
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://itch.io/api/1/{key.Trim()}/me");
            var resp = await GetJsonAsync<ItchMeResponse>(req, ct);
            if (resp?.User == null) return false;

            _apiKey = key.Trim();
            await SaveCredentialAsync("api_key", _apiKey);
            return true;
        }
        catch { return false; }
    }

    public override Task LogoutAsync()
    {
        DeleteCredential("api_key");
        _apiKey = null;
        _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override async Task<string?> GetAccountNameAsync()
    {
        _apiKey ??= await LoadCredentialAsync("api_key");
        if (_apiKey == null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://itch.io/api/1/{_apiKey}/me");
            var resp = await GetJsonAsync<ItchMeResponse>(req);
            return resp?.User?.Username;
        }
        catch { return null; }
    }

    public override async Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (_libraryCache != null) return _libraryCache;
        var cached = await Cache.LoadJsonAsync<List<StoreGame>>(LibraryCacheKey);
        if (cached != null) { _libraryCache = cached; return _libraryCache; }

        _apiKey ??= await LoadCredentialAsync("api_key");
        if (_apiKey == null) return [];

        var games = new List<StoreGame>();
        for (int page = 1; ; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://itch.io/api/1/{_apiKey}/my-owned-keys?page={page}");
            var resp = await GetJsonAsync<ItchOwnedKeysResponse>(req, ct);
            if (resp?.OwnedKeys == null || resp.OwnedKeys.Count == 0) break;
            foreach (var key in resp.OwnedKeys)
            {
                if (key.Game == null) continue;
                games.Add(new StoreGame
                {
                    Store = StoreId.Itch,
                    Id = key.Game.Id.ToString(),
                    Title = key.Game.Title ?? "",
                    CoverUrl = key.Game.CoverUrl ?? "",
                    StoreUrl = key.Game.Url ?? "",
                });
            }
            if (resp.OwnedKeys.Count < 50) break;
        }

        await Cache.SaveJsonAsync(LibraryCacheKey, games);
        _libraryCache = games;
        return _libraryCache;
    }

    public override async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(storeUrl)) return null;
        var target = storeUrl.TrimEnd('/').ToLowerInvariant();
        var lib = await GetLibraryAsync(ct);
        return lib.FirstOrDefault(g => g.StoreUrl.TrimEnd('/').ToLowerInvariant() == target);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists uploads for a game (requires download_key_id for owned content).
    /// </summary>
    public async Task<(List<ItchUpload> uploads, string? error)> GetUploadsAsync(
        long gameId, long downloadKeyId, CancellationToken ct = default)
    {
        _apiKey ??= await LoadCredentialAsync("api_key");
        if (_apiKey == null) return ([], "Not authenticated with itch.io.");

        // Must use itch.io/api/1/{key} format; api.itch.io uses a different auth scheme
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://itch.io/api/1/{_apiKey}/game/{gameId}/uploads?download_key_id={downloadKeyId}");
        try
        {
            var resp = await GetJsonAsync<ItchUploadsResponse>(req, ct);
            return (resp?.Uploads ?? [], null);
        }
        catch (Exception ex) { return ([], ex.Message); }
    }

    /// <summary>
    /// Finds the download key ID for a game from the user's owned keys.
    /// Returns (downloadKeyId, error).
    /// </summary>
    public async Task<(long keyId, string? error)> FindDownloadKeyAsync(long gameId, CancellationToken ct = default)
    {
        _apiKey ??= await LoadCredentialAsync("api_key");
        if (_apiKey == null) return (0, "Not authenticated with itch.io.");

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.itch.io/game/{gameId}/download-keys?api_key={_apiKey}");
        try
        {
            var resp = await GetJsonAsync<ItchDownloadKeysResponse>(req, ct);
            var key = resp?.DownloadKeys?.FirstOrDefault();
            if (key == null) return (0, "No download key found for this game.");
            return (key.Id, null);
        }
        catch (Exception ex) { return (0, ex.Message); }
    }

    /// <summary>
    /// Downloads a specific itch.io upload to a file.
    /// Returns null on success, error string on failure.
    /// itch.io uses a GET-with-body pattern: api_key + download_key_id in the request body.
    /// </summary>
    public async Task<string?> DownloadUploadAsync(
        long uploadId, long downloadKeyId,
        string destPath,
        long expectedSize = 0,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        _apiKey ??= await LoadCredentialAsync("api_key");
        if (_apiKey == null) return "Not authenticated with itch.io.";

        if (File.Exists(destPath))
        {
            progress?.Report(($"Already cached: {Path.GetFileName(destPath)}", 1.0));
            return null;
        }

        // itch.io returns a 302 to the CDN URL; resolve it without following the redirect
        var resolveUrl = $"https://api.itch.io/uploads/{uploadId}/download";
        var formBody = $"api_key={Uri.EscapeDataString(_apiKey)}&download_key_id={downloadKeyId}";

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PortMasterDesktop/1.0");

        using var resolveReq = new HttpRequestMessage(HttpMethod.Get, resolveUrl)
        {
            Content = new StringContent(formBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        using var resolveResp = await http.SendAsync(resolveReq, ct);

        string cdnUrl;
        if (resolveResp.StatusCode == System.Net.HttpStatusCode.Redirect
            || resolveResp.StatusCode == System.Net.HttpStatusCode.Found)
        {
            var loc = resolveResp.Headers.Location?.ToString();
            if (loc == null) return "No redirect location returned from itch.io.";
            cdnUrl = loc;
        }
        else if (resolveResp.IsSuccessStatusCode)
        {
            // Some setups may return the URL in a JSON body
            var body = await resolveResp.Content.ReadAsStringAsync(ct);
            cdnUrl = body.Trim().Trim('"');
        }
        else
        {
            return $"itch.io returned HTTP {(int)resolveResp.StatusCode} when resolving download URL.";
        }

        // Now download from the CDN URL (signed, expires quickly — stream immediately)
        progress?.Report(($"Downloading {Path.GetFileName(destPath)}…", 0.01));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        using var dlResp = await http.GetAsync(cdnUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!dlResp.IsSuccessStatusCode)
            return $"CDN download failed: HTTP {(int)dlResp.StatusCode}";

        var total = dlResp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await dlResp.Content.ReadAsStreamAsync(ct);
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

    public record ItchUpload(
        [property: JsonPropertyName("id")]       long Id,
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("size")]     long Size,
        [property: JsonPropertyName("p_linux")]  bool Linux,
        [property: JsonPropertyName("p_windows")]bool Windows,
        [property: JsonPropertyName("p_osx")]    bool Mac,
        [property: JsonPropertyName("demo")]     bool Demo,
        [property: JsonPropertyName("md5_hash")] string? Md5);

    private record ItchUploadsResponse(
        [property: JsonPropertyName("uploads")] List<ItchUpload>? Uploads);
    private record ItchDownloadKeysResponse(
        [property: JsonPropertyName("download_keys")] List<ItchDownloadKey>? DownloadKeys);
    private record ItchDownloadKey([property: JsonPropertyName("id")] long Id);

    private record ItchMeResponse([property: JsonPropertyName("user")] ItchUser? User);
    private record ItchUser([property: JsonPropertyName("username")] string? Username);
    private record ItchOwnedKeysResponse(
        [property: JsonPropertyName("owned_keys")] List<ItchOwnedKey>? OwnedKeys);
    private record ItchOwnedKey([property: JsonPropertyName("game")] ItchGame? Game);
    private record ItchGame(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("cover_url")] string? CoverUrl,
        [property: JsonPropertyName("url")] string? Url);
}

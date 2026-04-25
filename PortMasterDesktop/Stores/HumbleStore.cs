using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Humble Bundle / Humble Store integration.
///
/// Humble has no public OAuth — auth is a session cookie.
/// We ask the user to paste the _simpleauth_sess cookie value from their browser.
/// All API requests require the X-Requested-By: hb_android_app header.
/// </summary>
public class HumbleStore : BaseGameStore
{
    public override StoreId StoreId => StoreId.Humble;
    public override string DisplayName => "Humble Bundle";

    private const string LibraryCacheKey = "humble_library";
    private const string ApiBase = "https://www.humblebundle.com";
    private string? _sessionCookie;
    private List<StoreGame>? _libraryCache;

    public HumbleStore(CacheService cache) : base(cache) { }

    public override async Task<bool> IsAuthenticatedAsync()
    {
        _sessionCookie ??= await LoadCredentialAsync("session_cookie");
        return _sessionCookie != null;
    }

    public override async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://www.humblebundle.com/login") { UseShellExecute = true });
        }
        catch { }

        var cookie = await PromptAsync("Humble Bundle — Paste Session Cookie",
            "Log in to humblebundle.com in your browser, then open DevTools (F12) → " +
            "Application → Cookies → humblebundle.com → find \"_simpleauth_sess\" and paste its value here.");

        if (string.IsNullOrWhiteSpace(cookie)) return false;

        // Validate by calling /api/v1/user/order
        try
        {
            var keys = await FetchGameKeysAsync(cookie.Trim(), ct);
            if (keys == null) return false;
            _sessionCookie = cookie.Trim();
            await SaveCredentialAsync("session_cookie", _sessionCookie);
            return true;
        }
        catch { return false; }
    }

    public override Task LogoutAsync()
    {
        DeleteCredential("session_cookie");
        _sessionCookie = null;
        _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override async Task<string?> GetAccountNameAsync()
    {
        _sessionCookie ??= await LoadCredentialAsync("session_cookie");
        if (_sessionCookie == null) return null;
        try
        {
            using var req = MakeRequest(HttpMethod.Get, "/api/v1/user/home");
            var resp = await GetJsonAsync<HumbleUserHome>(req);
            return resp?.Username;
        }
        catch { return "Humble Bundle"; }
    }

    public override async Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (_libraryCache != null) return _libraryCache;
        var cached = await Cache.LoadJsonAsync<List<StoreGame>>(LibraryCacheKey);
        if (cached != null) { _libraryCache = cached; return _libraryCache; }

        _sessionCookie ??= await LoadCredentialAsync("session_cookie");
        if (_sessionCookie == null) return [];

        var gameKeys = await FetchGameKeysAsync(_sessionCookie, ct);
        if (gameKeys == null) return [];

        var games = new List<StoreGame>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in gameKeys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = MakeRequest(HttpMethod.Get, $"/api/v1/order/{key}");
                var order = await GetJsonAsync<HumbleOrder>(req, ct);
                if (order?.Subproducts == null) continue;

                foreach (var sub in order.Subproducts)
                {
                    if (string.IsNullOrEmpty(sub.MachineName) || !seenIds.Add(sub.MachineName))
                        continue;
                    if (string.IsNullOrEmpty(sub.HumanName)) continue;

                    games.Add(new StoreGame
                    {
                        Store = StoreId.Humble,
                        Id = sub.MachineName,
                        Title = sub.HumanName,
                        CoverUrl = sub.Icon ?? "",
                        StoreUrl = $"https://www.humblebundle.com/store/{sub.MachineName}",
                    });
                }
            }
            catch { /* skip inaccessible orders */ }
        }

        await Cache.SaveJsonAsync(LibraryCacheKey, games);
        _libraryCache = games;
        return _libraryCache;
    }

    public override async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        var lib = await GetLibraryAsync(ct);
        var m = Regex.Match(storeUrl, @"humblebundle\.com/(?:store|game)/([^/?&#]+)");
        if (m.Success)
        {
            var slug = m.Groups[1].Value;
            var direct = lib.FirstOrDefault(g =>
                g.Id.Equals(slug, StringComparison.OrdinalIgnoreCase) ||
                Regex.Match(g.StoreUrl, @"humblebundle\.com/(?:store|game)/([^/?&#]+)").Groups[1].Value
                    .Equals(slug, StringComparison.OrdinalIgnoreCase));
            if (direct != null) return direct;
        }
        // Fuzzy title match
        var titleSlug = Regex.Replace(storeUrl.ToLowerInvariant(), @"[^a-z0-9]", "");
        return lib.FirstOrDefault(g =>
            Regex.Replace(g.Title.ToLowerInvariant(), @"[^a-z0-9]", "") == titleSlug ||
            Regex.Replace(g.Id.ToLowerInvariant(), @"[^a-z0-9]", "") == titleSlug);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists available downloads for a game (identified by machine_name).
    /// Returns the subproduct and its platform download entries.
    /// </summary>
    public async Task<(List<HumbleDownloadEntry> downloads, string? error)> GetDownloadsAsync(
        string machineName, CancellationToken ct = default)
    {
        _sessionCookie ??= await LoadCredentialAsync("session_cookie");
        if (_sessionCookie == null) return ([], "Not authenticated with Humble Bundle.");

        var gameKeys = await FetchGameKeysAsync(_sessionCookie, ct);
        if (gameKeys == null) return ([], "Failed to fetch orders.");

        foreach (var key in gameKeys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = MakeRequest(HttpMethod.Get, $"/api/v1/order/{key}");
                var order = await GetJsonAsync<HumbleOrder>(req, ct);
                var sub = order?.Subproducts?.FirstOrDefault(s =>
                    s.MachineName?.Equals(machineName, StringComparison.OrdinalIgnoreCase) == true);
                if (sub?.Downloads == null) continue;

                var entries = sub.Downloads
                    .SelectMany(d => d.DownloadStructs ?? [],
                        (d, s) => new HumbleDownloadEntry(
                            Platform: d.Platform ?? "",
                            HumanName: s.HumanName ?? "",
                            Url: s.Url?.Web ?? "",
                            FileSize: s.FileSize,
                            Md5: s.Md5 ?? ""))
                    .Where(e => !string.IsNullOrEmpty(e.Url))
                    .ToList();
                return (entries, null);
            }
            catch { /* try next order */ }
        }
        return ([], $"Game '{machineName}' not found in any order.");
    }

    /// <summary>Downloads a Humble Bundle game file. Returns null on success, error on failure.</summary>
    public async Task<string?> DownloadGameFileAsync(
        string url, string destPath,
        long expectedSize = 0,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        if (File.Exists(destPath))
        {
            progress?.Report(($"Already cached: {Path.GetFileName(destPath)}", 1.0));
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        progress?.Report(($"Downloading {Path.GetFileName(destPath)}…", 0.01));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PortMasterDesktop/1.0");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return $"HTTP {(int)resp.StatusCode} from Humble CDN.";

        var total = resp.Content.Headers.ContentLength ?? expectedSize;
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<string>?> FetchGameKeysAsync(string cookie, CancellationToken ct = default)
    {
        using var req = MakeRequestWithCookie(HttpMethod.Get, "/api/v1/user/order", cookie);
        var resp = await GetJsonAsync<List<HumbleOrderKeyEntry>>(req, ct);
        return resp?.Select(e => e.GameKey).OfType<string>().ToList();
    }

    private HttpRequestMessage MakeRequest(HttpMethod method, string path)
        => MakeRequestWithCookie(method, path, _sessionCookie ?? "");

    private static HttpRequestMessage MakeRequestWithCookie(HttpMethod method, string path, string cookie)
    {
        var req = new HttpRequestMessage(method, ApiBase + path);
        req.Headers.Add("X-Requested-By", "hb_android_app");
        req.Headers.Add("Cookie", $"_simpleauth_sess={cookie}");
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    // ── JSON models ───────────────────────────────────────────────────────────

    private record HumbleUserHome([property: JsonPropertyName("username")] string? Username);

    private record HumbleOrderKeyEntry(
        [property: JsonPropertyName("gamekey")] string? GameKey);

    private record HumbleOrder(
        [property: JsonPropertyName("subproducts")] List<HumbleSubproduct>? Subproducts);

    private record HumbleSubproduct(
        [property: JsonPropertyName("machine_name")] string? MachineName,
        [property: JsonPropertyName("human_name")]   string? HumanName,
        [property: JsonPropertyName("icon")]         string? Icon,
        [property: JsonPropertyName("downloads")]    List<HumbleDownload>? Downloads);

    private record HumbleDownload(
        [property: JsonPropertyName("platform")]          string? Platform,
        [property: JsonPropertyName("download_struct")]   List<HumbleDownloadStruct>? DownloadStructs);

    private record HumbleDownloadStruct(
        [property: JsonPropertyName("human_name")]  string? HumanName,
        [property: JsonPropertyName("url")]         HumbleDownloadUrl? Url,
        [property: JsonPropertyName("file_size")]   long FileSize,
        [property: JsonPropertyName("md5")]         string? Md5);

    private record HumbleDownloadUrl(
        [property: JsonPropertyName("web")]         string? Web,
        [property: JsonPropertyName("bittorrent")]  string? Bittorrent);

    public record HumbleDownloadEntry(
        string Platform, string HumanName, string Url, long FileSize, string Md5);
}

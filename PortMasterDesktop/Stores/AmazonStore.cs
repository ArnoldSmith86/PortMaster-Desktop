using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Amazon Games integration via OpenID Connect + PKCE.
///
/// Uses the same device auth flow as the GameNative reference implementation.
/// A unique device serial and client ID are generated per installation.
/// </summary>
public class AmazonStore : BaseGameStore
{
    public override StoreId StoreId => StoreId.Amazon;
    public override string DisplayName => "Amazon Games";

    private const string LibraryCacheKey = "amazon_library";

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private List<StoreGame>? _libraryCache;

    public AmazonStore(CacheService cache) : base(cache) { }

    public override async Task<bool> IsAuthenticatedAsync()
        => await LoadCredentialAsync("refresh_token") != null;

    public override async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        var (verifier, challenge) = GeneratePkce();
        var deviceSerial = await GetOrCreateDeviceSerial();
        var clientId = await GetOrCreateClientId(deviceSerial);

        var authUrl = "https://www.amazon.com/ap/signin?" +
            "openid.ns=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0&" +
            "openid.oa2.scope=device_auth_access&" +
            "openid.oa2.response_type=code&" +
            "openid.oa2.code_challenge_method=S256&" +
            $"openid.oa2.client_id=device%3A{HttpUtility.UrlEncode(clientId)}&" +
            $"openid.oa2.code_challenge={challenge}&" +
            "openid.claimed_id=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0%2Fidentifier_select&" +
            "openid.identity=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0%2Fidentifier_select&" +
            "openid.mode=checkid_setup&" +
            $"openid.return_to={HttpUtility.UrlEncode("portmasterdesktop://amazon-callback")}";

        string? capturedRedirect = null;

        var result = await OAuthHelper.AuthenticateAsync(redirectUri =>
        {
            capturedRedirect = redirectUri;
            return "https://www.amazon.com/ap/signin?" +
                "openid.ns=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0&" +
                "openid.oa2.scope=device_auth_access&" +
                "openid.oa2.response_type=code&" +
                "openid.oa2.code_challenge_method=S256&" +
                $"openid.oa2.client_id=device%3A{HttpUtility.UrlEncode(clientId)}&" +
                $"openid.oa2.code_challenge={challenge}&" +
                "openid.claimed_id=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0%2Fidentifier_select&" +
                "openid.identity=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0%2Fidentifier_select&" +
                "openid.mode=checkid_setup&" +
                $"openid.return_to={HttpUtility.UrlEncode(redirectUri)}";
        }, ct: ct);

        var code2 = result?.GetValueOrDefault("openid.oa2.authorization_code")
                 ?? result?.GetValueOrDefault("code");
        if (string.IsNullOrEmpty(code2)) return false;
        return await RegisterDeviceAsync(code2, verifier, clientId, deviceSerial, ct);
    }

    private async Task<bool> RegisterDeviceAsync(
        string code, string verifier, string clientId, string deviceSerial, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.amazon.com/auth/register");
        req.Headers.Add("x-amzn-identity-auth-domain", "api.amazon.com");

        var body = new
        {
            auth_data = new
            {
                authorization_code = code,
                client_id = $"device:{clientId}",
                code_algorithm = "SHA-256",
                code_verifier = verifier,
                domain = "Device",
            },
            registration_data = new
            {
                device_serial = deviceSerial,
                device_type = "A2CZJZGLK2JJVM",
                domain = "Device",
                os_version = "10.0.19041.1",
                software_version = "35602678",
            },
            requested_extensions = new[] { "device_info", "customer_info" },
            requested_token_type = new[] { "bearer", "mac_dms", "store_authentication_cookie", "website_cookies" },
            user_context_map = new { frc = "" },
        };

        req.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body),
            Encoding.UTF8, "application/json");

        try
        {
            var resp = await GetJsonAsync<AmazonRegisterResponse>(req, ct);
            var tokens = resp?.Response?.Success?.Tokens?.Bearer;
            if (tokens == null) return false;

            await SaveCredentialAsync("access_token", tokens.AccessToken);
            await SaveCredentialAsync("refresh_token", tokens.RefreshToken);
            _accessToken = tokens.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn - 300);
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var refreshToken = await LoadCredentialAsync("refresh_token");
        if (refreshToken == null) return false;

        var deviceSerial = await GetOrCreateDeviceSerial();
        var clientId = await GetOrCreateClientId(deviceSerial);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.amazon.com/auth/token");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = $"device:{clientId}",
        });

        try
        {
            var resp = await GetJsonAsync<AmazonTokenResponse>(req, ct);
            if (resp == null) return false;
            await SaveCredentialAsync("access_token", resp.AccessToken);
            _accessToken = resp.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(resp.ExpiresIn - 300);
            return true;
        }
        catch { return false; }
    }

    private async Task<string?> GetValidTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return _accessToken;
        _accessToken = await LoadCredentialAsync("access_token");
        if (!await RefreshAsync(ct)) return null;
        return _accessToken;
    }

    public override Task LogoutAsync()
    {
        foreach (var k in new[] { "access_token", "refresh_token", "device_serial", "client_id" })
            DeleteCredential(k);
        _accessToken = null;
        _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override Task<string?> GetAccountNameAsync()
        => Task.FromResult<string?>("Amazon Games Account");

    public override async Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (_libraryCache != null) return _libraryCache;
        var cached = await Cache.LoadJsonAsync<List<StoreGame>>(LibraryCacheKey);
        if (cached != null) { _libraryCache = cached; return _libraryCache; }

        var token = await GetValidTokenAsync(ct);
        if (token == null) return [];

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://gaming.amazon.com/api/distribution/entitlements");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("User-Agent", "com.amazon.agp/3.0.0");

        var resp = await GetJsonAsync<AmazonEntitlementsResponse>(req, ct);
        if (resp?.Entitlements == null) return [];

        var games = resp.Entitlements.Select(e => new StoreGame
        {
            Store = StoreId.Amazon,
            Id = e.FulfillmentId ?? e.Id ?? "",
            Title = e.Product?.Title ?? "",
            CoverUrl = e.Product?.CoverUrl ?? "",
            StoreUrl = $"https://gaming.amazon.com/",
        }).ToList();

        await Cache.SaveJsonAsync(LibraryCacheKey, games);
        _libraryCache = games;
        return _libraryCache;
    }

    public override async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        // Amazon URLs don't encode well as slugs; match by title heuristic
        var library = await GetLibraryAsync(ct);
        var m = Regex.Match(storeUrl, @"/dp/([A-Z0-9]+)");
        if (m.Success)
        {
            var asin = m.Groups[1].Value;
            return library.FirstOrDefault(g => g.Id == asin);
        }
        return null;
    }

    // --- Device identity helpers ---

    private async Task<string> GetOrCreateDeviceSerial()
    {
        var serial = await LoadCredentialAsync("device_serial");
        if (serial != null) return serial;
        serial = Guid.NewGuid().ToString("N").ToUpperInvariant();
        await SaveCredentialAsync("device_serial", serial);
        return serial;
    }

    private async Task<string> GetOrCreateClientId(string deviceSerial)
    {
        var clientId = await LoadCredentialAsync("client_id");
        if (clientId != null) return clientId;

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(deviceSerial + "amazon_games_desktop"));
        clientId = Convert.ToHexString(hash).ToLowerInvariant();
        await SaveCredentialAsync("client_id", clientId);
        return clientId;
    }

    private static (string verifier, string challenge) GeneratePkce()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var verifier = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
        var challenge = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    // --- JSON models ---

    private record AmazonRegisterResponse(AmazonRegisterResult? Response);
    private record AmazonRegisterResult(AmazonRegisterSuccess? Success);
    private record AmazonRegisterSuccess(AmazonRegisterTokens? Tokens);
    private record AmazonRegisterTokens(AmazonBearerToken? Bearer);
    private record AmazonBearerToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private record AmazonTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private record AmazonEntitlementsResponse(
        [property: JsonPropertyName("entitlements")] List<AmazonEntitlement>? Entitlements);

    private record AmazonEntitlement(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("fulfillmentId")] string? FulfillmentId,
        [property: JsonPropertyName("product")] AmazonProduct? Product);

    private record AmazonProduct(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("coverUrl")] string? CoverUrl);
}

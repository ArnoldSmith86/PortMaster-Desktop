using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;
using SteamKit2;
using SteamKit2.Authentication;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Steam adapter for machines WITHOUT a local Steam client.
///
/// Authenticates via SteamKit2 (same CM protocol as the Steam client) using
/// username + password + optional 2FA code. After auth, fetches the full owned
/// library via Steam Web API using the session access token.
/// </summary>
public class RemoteSteamStore : BaseGameStore
{
    public override StoreId StoreId => StoreId.Steam;
    public override string DisplayName => "Steam (remote)";

    private const string LibraryCacheKey = "steam_remote_library";
    private string? _steamId64;
    private string? _username;
    private List<StoreGame>? _libraryCache;

    public RemoteSteamStore(CacheService cache) : base(cache) { }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public override async Task<bool> IsAuthenticatedAsync()
    {
        _steamId64 ??= await LoadCredentialAsync("remote_steam_id64");
        var rt = await LoadCredentialAsync("remote_refresh_token");
        return rt != null && _steamId64 != null;
    }

    public override async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        var username = await PromptAsync("Steam Username", "Enter your Steam account username:");
        if (string.IsNullOrWhiteSpace(username)) return false;

        var password = await PromptAsync("Steam Password", "Enter your Steam password:");
        if (string.IsNullOrWhiteSpace(password)) return false;

        try
        {
            var (refreshToken, steamId64) = await LoginViaSteamKitAsync(
                username.Trim(), password.Trim(), ct);
            if (refreshToken == null || steamId64 == null) return false;

            _username = username.Trim();
            _steamId64 = steamId64;
            await SaveCredentialAsync("remote_username", _username);
            await SaveCredentialAsync("remote_steam_id64", steamId64);
            await SaveCredentialAsync("remote_refresh_token", refreshToken);
            return true;
        }
        catch (Exception ex)
        {
            await PromptAsync("Steam Login Error", ex.Message);
            return false;
        }
    }

    public override Task LogoutAsync()
    {
        foreach (var k in new[] { "remote_username", "remote_steam_id64", "remote_refresh_token" })
            DeleteCredential(k);
        _steamId64 = null; _username = null; _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override async Task<string?> GetAccountNameAsync()
    {
        _username ??= await LoadCredentialAsync("remote_username");
        return _username;
    }

    // ── Library ───────────────────────────────────────────────────────────────

    public override async Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (_libraryCache != null) return _libraryCache;
        var cached = await Cache.LoadJsonAsync<List<StoreGame>>(LibraryCacheKey);
        if (cached != null) { _libraryCache = cached; return _libraryCache; }

        _steamId64 ??= await LoadCredentialAsync("remote_steam_id64");
        if (_steamId64 == null) return [];

        var accessToken = await GetAccessTokenAsync(ct);
        if (accessToken == null) return [];

        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                  $"?access_token={accessToken}&steamid={_steamId64}" +
                  $"&include_appinfo=true&include_played_free_games=true";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var result = await GetJsonAsync<SteamOwnedGamesResponse>(req, ct);
            if (result?.Response?.Games == null) return [];

            _libraryCache = result.Response.Games.Select(g => new StoreGame
            {
                Store = StoreId.Steam,
                Id = g.AppId.ToString(),
                Title = g.Name ?? "",
                CoverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.AppId}/library_600x900_2x.jpg",
                StoreUrl = $"https://store.steampowered.com/app/{g.AppId}/",
            }).ToList();

            await Cache.SaveJsonAsync(LibraryCacheKey, _libraryCache);
            return _libraryCache;
        }
        catch { return []; }
    }

    public override async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        var match = Regex.Match(storeUrl, @"/app/(\d+)");
        if (!match.Success) return null;
        var appId = match.Groups[1].Value;
        var library = await GetLibraryAsync(ct);
        return library.FirstOrDefault(g => g.Id == appId);
    }

    // ── SteamKit2 helpers ─────────────────────────────────────────────────────

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var refreshToken = await LoadCredentialAsync("remote_refresh_token");
        _steamId64 ??= await LoadCredentialAsync("remote_steam_id64");
        if (refreshToken == null || _steamId64 == null) return null;

        try
        {
            var steamClient = new SteamClient();
            var manager = new CallbackManager(steamClient);

            var connectedTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult(true));
            manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
                connectedTcs.TrySetException(new Exception("Disconnected")));

            steamClient.Connect();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            _ = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                    manager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
            }, cts.Token);

            await connectedTcs.Task.WaitAsync(cts.Token);

            var result = await steamClient.Authentication.GenerateAccessTokenForAppAsync(
                new SteamID(_steamId64),
                refreshToken,
                allowRenewal: true);
            cts.Cancel();
            return result.AccessToken;
        }
        catch { return null; }
    }

    private async Task<(string? refreshToken, string? steamId64)> LoginViaSteamKitAsync(
        string username, string password, CancellationToken ct)
    {
        var steamClient = new SteamClient();
        var manager = new CallbackManager(steamClient);

        var connectedTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult(true));
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            connectedTcs.TrySetException(new Exception("Steam CM disconnected.")));

        steamClient.Connect();

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loopCts.CancelAfter(TimeSpan.FromSeconds(30));
        _ = Task.Run(() =>
        {
            while (!loopCts.Token.IsCancellationRequested)
                manager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
        }, loopCts.Token);

        await connectedTcs.Task.WaitAsync(loopCts.Token);

        var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
            new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = new GuiAuthenticator(),
            });

        // Keep pumping while waiting for auth result
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollCts.CancelAfter(TimeSpan.FromMinutes(3));
        _ = Task.Run(() =>
        {
            while (!pollCts.Token.IsCancellationRequested)
                manager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
        }, pollCts.Token);

        var steamId64 = authSession.SteamID.ConvertToUInt64().ToString();
        var pollResult = await authSession.PollingWaitForResultAsync(ct);
        pollCts.Cancel();
        loopCts.Cancel();

        return (pollResult.RefreshToken, steamId64);
    }

    private class GuiAuthenticator : IAuthenticator
    {
        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            => await PromptAsync("Steam Guard Code",
                   previousCodeWasIncorrect
                       ? "Incorrect code. Enter your Steam Guard code again:"
                       : "Enter your Steam Guard authenticator code:") ?? "";

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            => await PromptAsync("Steam Email Code",
                   previousCodeWasIncorrect
                       ? $"Incorrect code. Enter the code sent to {email} again:"
                       : $"Enter the code sent to {email}:") ?? "";

        public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);
    }

    private record SteamOwnedGamesResponse(SteamResponseBody? Response);
    private record SteamResponseBody(List<SteamAppEntry>? Games);
    private record SteamAppEntry(
        [property: JsonPropertyName("appid")] int AppId,
        [property: JsonPropertyName("name")] string? Name);
}

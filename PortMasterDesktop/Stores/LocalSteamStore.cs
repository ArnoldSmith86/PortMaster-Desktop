using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Steam adapter for machines with a local Steam client installed.
///
/// Reads game data entirely from local VDF/ACF files — no network auth required.
/// Optionally augments with the full owned library via Steam Web API
/// (needs user-provided API key + Steam ID64).
///
/// Displayed as "Steam client logged in as {username}" in Settings.
/// </summary>
public class LocalSteamStore : BaseGameStore
{
    public override StoreId StoreId => StoreId.Steam;
    public override string DisplayName => "Steam";

    private const string LibraryCacheKey = "steam_local_library";
    private string? _steamRoot;
    private string? _steamId64;
    private string? _displayName;
    private string? _webApiKey;
    private List<StoreGame>? _libraryCache;

    public LocalSteamStore(CacheService cache) : base(cache) { }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public override Task<bool> IsAuthenticatedAsync()
    {
        _steamRoot ??= FindSteamRoot();
        return Task.FromResult(_steamRoot != null);
    }

    public override async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        // Steam local: no interactive login needed — just read the installation.
        // Optionally let user provide a Web API key for full library fetch.
        _steamRoot ??= FindSteamRoot();
        if (_steamRoot == null) return false;

        _webApiKey ??= await LoadCredentialAsync("api_key");
        _steamId64 ??= await LoadCredentialAsync("steam_id64");

        if (_webApiKey == null)
        {
            var apiKey = await PromptAsync("Steam Web API Key (optional)",
                "Provide a Steam Web API key from https://steamcommunity.com/dev/apikey " +
                "to load your full library (including uninstalled games). Leave blank to use only installed games.");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _webApiKey = apiKey.Trim();
                await SaveCredentialAsync("api_key", _webApiKey);
            }
        }

        if (_steamId64 == null)
        {
            var (detectedId, detectedName) = ReadLocalSteamUser(_steamRoot);
            if (detectedId != null)
            {
                _steamId64 = detectedId;
                _displayName = detectedName;
                await SaveCredentialAsync("steam_id64", detectedId);
                if (detectedName != null) await SaveCredentialAsync("display_name", detectedName);
            }
        }

        return true;
    }

    public override Task LogoutAsync()
    {
        DeleteCredential("api_key");
        DeleteCredential("steam_id64");
        DeleteCredential("display_name");
        _webApiKey = null;
        _steamId64 = null;
        _displayName = null;
        _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override Task InvalidateLibraryCacheAsync()
    {
        _libraryCache = null;
        Cache.Invalidate(LibraryCacheKey);
        return Task.CompletedTask;
    }

    public override async Task<string?> GetAccountNameAsync()
    {
        _steamRoot ??= FindSteamRoot();
        if (_steamRoot == null) return null;

        if (_displayName == null)
        {
            _displayName = await LoadCredentialAsync("display_name");
            if (_displayName == null)
            {
                var (_, name) = ReadLocalSteamUser(_steamRoot);
                _displayName = name;
                if (name != null) await SaveCredentialAsync("display_name", name);
            }
        }
        return _displayName != null ? $"Steam client: {_displayName}" : "Steam (local)";
    }

    // ── Library ───────────────────────────────────────────────────────────────

    public override async Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (_libraryCache != null) return _libraryCache;
        var cached = await Cache.LoadJsonAsync<List<StoreGame>>(LibraryCacheKey);
        if (cached != null) { _libraryCache = cached; return _libraryCache; }

        _steamRoot ??= FindSteamRoot();
        if (_steamRoot == null) return [];

        _steamId64 ??= await LoadCredentialAsync("steam_id64");
        if (_steamId64 == null)
        {
            var (id, _) = ReadLocalSteamUser(_steamRoot);
            _steamId64 = id;
            if (id != null) await SaveCredentialAsync("steam_id64", id);
        }

        // Build installed-games map from ACF files (has install size + path); dedup by id
        var installed = ReadLocalLibrary(_steamRoot)
            .GroupBy(g => g.Id)
            .ToDictionary(g => g.Key, g => g.First());

        // Full owned library from binary VDF files (no network needed)
        var games = new List<StoreGame>();
        if (long.TryParse(_steamId64, out long id64))
        {
            var owned = SteamLocalLibraryReader.ReadOwnedApps(_steamRoot, id64);
            foreach (var (appId, entry) in owned)
            {
                // Only include actual games (type=Game/game), not tools, DLC, videos, etc.
                if (!entry.appType.Equals("game", StringComparison.OrdinalIgnoreCase))
                    continue;

                var appIdStr = appId.ToString();
                if (installed.TryGetValue(appIdStr, out var local))
                {
                    if (entry.name.Length > 0) local.Title = entry.name;
                    games.Add(local);
                }
                else
                {
                    games.Add(new StoreGame
                    {
                        Store = StoreId.Steam,
                        Id = appIdStr,
                        Title = entry.name,
                        IsInstalled = false,
                        StoreUrl = $"https://store.steampowered.com/app/{appId}/",
                        CoverUrl = LocalImagePath(_steamRoot, appIdStr),
                    });
                }
            }

            // Include any installed games not found through licensecache (e.g. family sharing)
            foreach (var g in installed.Values)
                if (!games.Any(x => x.Id == g.Id))
                    games.Add(g);
        }

        // Fall back to just installed games if binary parsing gave nothing
        if (games.Count == 0)
            games.AddRange(installed.Values);

        // Augment with Web API if key is available
        _webApiKey ??= await LoadCredentialAsync("api_key");
        if (_webApiKey != null && _steamId64 != null && games.Count < 50)
        {
            try
            {
                var onlineGames = await FetchOnlineLibraryAsync(_webApiKey, _steamId64, ct);
                var existingIds = games.Select(g => g.Id).ToHashSet();
                foreach (var og in onlineGames)
                    if (!existingIds.Contains(og.Id))
                        games.Add(og);
            }
            catch { /* non-fatal */ }
        }

        // Filter out tool/runtime entries
        games = games
            .Where(g => !IsToolOrRuntime(g.Title))
            .ToList();

        await Cache.SaveJsonAsync(LibraryCacheKey, games);
        _libraryCache = games;
        return _libraryCache;
    }

    // Returns local portrait image path as file:// URI, falls back to CDN URL
    internal static string LocalImagePath(string steamRoot, string appId)
    {
        var local = Path.Combine(steamRoot, "appcache", "librarycache", appId, "library_600x900.jpg");
        if (File.Exists(local))
            return new Uri(local).AbsoluteUri; // file:///...
        return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg";
    }

    private static bool IsToolOrRuntime(string title) =>
        title.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        title.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase) ||
        title.StartsWith("Steam VR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scans all Steam library folders for an installed game with the given appId.
    /// Returns null if not found or not fully installed.
    /// </summary>
    public static StoreGame? FindInstalledGame(string appId)
    {
        var steamRoot = FindSteamRoot();
        if (steamRoot == null) return null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appsDir = new List<string>();

        void TryAdd(string d)
        {
            if (!Directory.Exists(d)) return;
            var real = RealPath(d);
            if (seen.Add(real)) appsDir.Add(d);
        }

        TryAdd(Path.Combine(steamRoot, "steamapps"));

        var libFolders = Path.Combine(steamRoot, "config", "libraryfolders.vdf");
        if (File.Exists(libFolders))
            foreach (Match m in Regex.Matches(File.ReadAllText(libFolders), @"""path""\s*""([^""]+)"""))
                TryAdd(Path.Combine(m.Groups[1].Value.Replace(@"\\", "/"), "steamapps"));

        foreach (var dir in appsDir)
        {
            var acf = Path.Combine(dir, $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;
            var game = ParseAcf(acf, dir);
            if (game?.IsInstalled == true) return game;
        }
        return null;
    }

    /// <summary>
    /// Sends steam://install/{appId} to the running Steam client, prompting it to install the game.
    /// Returns null on success or an error message.
    /// </summary>
    public static string? RequestInstall(string appId)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"steam://install/{appId}") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public override async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        var match = Regex.Match(storeUrl, @"/app/(\d+)");
        if (!match.Success) return null;
        var appId = match.Groups[1].Value;
        var library = await GetLibraryAsync(ct);
        return library.FirstOrDefault(g => g.Id == appId);
    }

    // ── Local Steam reading ───────────────────────────────────────────────────

    public static string? FindSteamRoot()
    {
        string[] candidates;
        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates = [
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".steam", "steam"),
                "/usr/share/steam",
            ];
        }
        else if (OperatingSystem.IsWindows())
        {
            candidates = [
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            ];
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates = [Path.Combine(home, "Library", "Application Support", "Steam")];
        }
        else return null;

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static (string? steamId64, string? displayName) ReadLocalSteamUser(string steamRoot)
    {
        var loginUsersPath = Path.Combine(steamRoot, "config", "loginusers.vdf");
        if (!File.Exists(loginUsersPath)) return (null, null);
        var vdf = File.ReadAllText(loginUsersPath);

        var mostRecent = Regex.Match(vdf,
            @"""(\d{17})""\s*\{[^}]*""MostRecent""\s*""1""", RegexOptions.Singleline);
        if (mostRecent.Success)
        {
            var id = mostRecent.Groups[1].Value;
            var name = Regex.Match(mostRecent.Value, @"""PersonaName""\s*""([^""]+)""");
            return (id, name.Success ? name.Groups[1].Value : null);
        }

        var firstId = Regex.Match(vdf, @"""(\d{17})""");
        if (firstId.Success)
        {
            var name = Regex.Match(vdf, @"""PersonaName""\s*""([^""]+)""");
            return (firstId.Groups[1].Value, name.Success ? name.Groups[1].Value : null);
        }
        return (null, null);
    }

    private static List<StoreGame> ReadLocalLibrary(string steamRoot)
    {
        var games = new List<StoreGame>();
        // Resolve symlinks so we never add the same physical directory twice
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraryFolders = new List<string>();

        void TryAddDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            var real = RealPath(dir);
            if (seen.Add(real)) libraryFolders.Add(dir);
        }

        TryAddDir(Path.Combine(steamRoot, "steamapps"));

        var libFoldersVdf = Path.Combine(steamRoot, "config", "libraryfolders.vdf");
        if (File.Exists(libFoldersVdf))
        {
            var vdf = File.ReadAllText(libFoldersVdf);
            foreach (Match m in Regex.Matches(vdf, @"""path""\s*""([^""]+)"""))
                TryAddDir(Path.Combine(m.Groups[1].Value.Replace(@"\\", "/"), "steamapps"));
        }

        foreach (var appsDir in libraryFolders)
        {
            if (!Directory.Exists(appsDir)) continue;
            foreach (var acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
            {
                var game = ParseAcf(acf, appsDir);
                if (game != null) games.Add(game);
            }
        }
        return games;
    }

    private static StoreGame? ParseAcf(string acfPath, string appsDir)
    {
        try
        {
            var text = File.ReadAllText(acfPath);
            var appId = Regex.Match(text, @"""appid""\s*""(\d+)""").Groups[1].Value;
            var name = Regex.Match(text, @"""name""\s*""([^""]+)""").Groups[1].Value;
            var installDir = Regex.Match(text, @"""installdir""\s*""([^""]+)""").Groups[1].Value;
            var sizeBytes = long.TryParse(
                Regex.Match(text, @"""SizeOnDisk""\s*""(\d+)""").Groups[1].Value, out var sz) ? sz : 0;

            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) return null;

            var fullPath = Path.Combine(appsDir, "common", installDir);
            var steamRoot = Path.GetFullPath(Path.Combine(appsDir, ".."));
            return new StoreGame
            {
                Store = StoreId.Steam,
                Id = appId,
                Title = name,
                InstallSizeBytes = sizeBytes,
                IsInstalled = Directory.Exists(fullPath),
                InstallPath = fullPath,
                StoreUrl = $"https://store.steampowered.com/app/{appId}/",
                CoverUrl = LocalImagePath(steamRoot, appId),
            };
        }
        catch { return null; }
    }

    private static string RealPath(string path)
    {
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return Path.GetFullPath(resolved?.FullName ?? path);
        }
        catch { return Path.GetFullPath(path); }
    }

    private static async Task<List<StoreGame>> FetchOnlineLibraryAsync(
        string apiKey, string steamId64, CancellationToken ct)
    {
        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                  $"?key={apiKey}&steamid={steamId64}&include_appinfo=true&include_played_free_games=true";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var result = await GetJsonAsync<SteamOwnedGamesResponse>(req, ct);
        if (result?.Response?.Games == null) return [];
        return result.Response.Games.Select(g => new StoreGame
        {
            Store = StoreId.Steam,
            Id = g.AppId.ToString(),
            Title = g.Name ?? "",
            CoverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.AppId}/library_600x900_2x.jpg",
            StoreUrl = $"https://store.steampowered.com/app/{g.AppId}/",
        }).ToList();
    }

    private record SteamOwnedGamesResponse(SteamResponseBody? Response);
    private record SteamResponseBody(List<SteamAppEntry>? Games);
    private record SteamAppEntry(
        [property: JsonPropertyName("appid")] int AppId,
        [property: JsonPropertyName("name")] string? Name);
}

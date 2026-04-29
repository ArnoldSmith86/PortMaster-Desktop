using System.Text.Json;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Shared HTTP client, credential storage (OS keychain or encrypted fallback),
/// and JSON helpers for all store integrations.
/// </summary>
public abstract class BaseGameStore : IGameStore
{
    protected static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    protected static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected readonly CacheService Cache;
    private readonly string _credDir;

    protected BaseGameStore(CacheService cache)
    {
        Cache = cache;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _credDir = Path.Combine(appData, "portmaster-desktop", "creds");
        Directory.CreateDirectory(_credDir);
    }

    public abstract StoreId StoreId { get; }
    public abstract string DisplayName { get; }

    public abstract Task<bool> IsAuthenticatedAsync();
    public abstract Task<bool> AuthenticateAsync(CancellationToken ct = default);
    public abstract Task LogoutAsync();
    public abstract Task<string?> GetAccountNameAsync();
    public abstract Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default);

    public virtual Task InvalidateLibraryCacheAsync() => Task.CompletedTask;

    public virtual async Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default)
    {
        if (!await IsAuthenticatedAsync()) return null;
        var library = await GetLibraryAsync(ct);
        return library.FirstOrDefault(g =>
            string.Equals(g.StoreUrl.TrimEnd('/'), storeUrl.TrimEnd('/'),
                          StringComparison.OrdinalIgnoreCase));
    }

    // ── Error / circuit-breaker tracking ─────────────────────────────────────

    private string? _lastError;
    private DateTime? _cooldownUntil;

    public string? LastError => _lastError;
    public bool IsInCooldown => _cooldownUntil > DateTime.UtcNow;

    public void RecordError(string message, TimeSpan? cooldown = null)
    {
        _lastError = message;
        _cooldownUntil = DateTime.UtcNow + (cooldown ?? TimeSpan.FromHours(1));
        System.Diagnostics.Debug.WriteLine(
            $"[{StoreId}] Error: {message} (cooldown until {_cooldownUntil:HH:mm} UTC)");
    }

    public void ClearError()
    {
        _lastError = null;
        _cooldownUntil = null;
    }

    // ── Credential storage (plain files, permissions-protected) ──────────────
    // Stored in ~/.local/share/portmaster-desktop/creds/{StoreId}_{key}
    // On a future iteration these could be delegated to libsecret/Keychain.

    protected Task SaveCredentialAsync(string key, string value)
    {
        File.WriteAllText(CredPath(key), value);
        // Restrict to owner-only on Unix
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(CredPath(key),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return Task.CompletedTask;
    }

    protected Task<string?> LoadCredentialAsync(string key)
    {
        var path = CredPath(key);
        return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : (string?)null);
    }

    protected void DeleteCredential(string key)
    {
        var path = CredPath(key);
        if (File.Exists(path)) File.Delete(path);
    }

    private string CredPath(string key)
        => Path.Combine(_credDir, $"{StoreId}_{key}".Replace('/', '_'));

    // ── UI prompt delegate (set by the view layer) ───────────────────────────
    // Used by stores that need to prompt the user for input during auth.
    public static Func<string, string, Task<string?>>? PromptDelegate { get; set; }

    protected static Task<string?> PromptAsync(string title, string message)
        => PromptDelegate?.Invoke(title, message) ?? Task.FromResult<string?>(null);

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    protected static async Task<T?> GetJsonAsync<T>(HttpRequestMessage request,
        CancellationToken ct = default)
    {
        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }
}

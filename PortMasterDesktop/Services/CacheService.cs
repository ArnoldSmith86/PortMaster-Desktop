using System.Text.Json;

namespace PortMasterDesktop.Services;

/// <summary>
/// File-based cache for port catalogs, game libraries, and port images.
/// All data lives under the platform app-data directory / portmaster-desktop / cache/.
/// Never auto-expires — only cleared by explicit Refresh or InvalidateAll().
/// </summary>
public class CacheService
{
    private readonly string _cacheDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public CacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDir = Path.Combine(appData, "portmaster-desktop", "cache");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(ImageDir);
    }

    // ── JSON data cache ───────────────────────────────────────────────────────

    public async Task SaveJsonAsync<T>(string key, T data)
    {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        await File.WriteAllTextAsync(JsonPath(key), json);
    }

    public async Task<T?> LoadJsonAsync<T>(string key)
    {
        var path = JsonPath(key);
        if (!File.Exists(path)) return default;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch { return default; }
    }

    public void Invalidate(string key)
    {
        var path = JsonPath(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public void InvalidateAll()
    {
        foreach (var f in Directory.GetFiles(_cacheDir, "*.json")) File.Delete(f);
        foreach (var f in Directory.GetFiles(ImageDir))            File.Delete(f);
    }

    // ── Image file cache ──────────────────────────────────────────────────────

    private string ImageDir => Path.Combine(_cacheDir, "images");

    public string? GetCachedFilePath(string key, string ext)
    {
        var p = Path.Combine(ImageDir, $"{Sanitize(key)}{ext}");
        return File.Exists(p) ? p : null;
    }

    public string ReserveCachedFilePath(string key, string ext)
        => Path.Combine(ImageDir, $"{Sanitize(key)}{ext}");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string JsonPath(string key) => Path.Combine(_cacheDir, $"{Sanitize(key)}.json");

    private static string Sanitize(string key)
        => string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}

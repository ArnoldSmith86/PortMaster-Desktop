using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using PortMasterDesktop.Models;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.PortMaster;

/// <summary>
/// All communication with the PortMaster infrastructure.
///
/// Port catalog: fetched from the PortsMaster/PortMaster-Info GitHub repository.
///   ports.json is a dict: {"portname.zip": {attr, items, source: {url, size, ...}}}
/// Images: served from portmaster.games CDN.
/// </summary>
public class PortMasterClient
{
    // Direct URL to ports.json in the PortMaster-Info repo
    private const string PortsJsonUrl = "https://github.com/PortsMaster/PortMaster-Info/raw/main/ports.json";
    // PortMaster-New releases ports.json — has a "utils" section with runtime squashfs downloads
    private const string RuntimeCatalogUrl = "https://github.com/PortsMaster/PortMaster-New/releases/latest/download/ports.json";
    // CDN for port images
    private const string ImageCdnUrl = "https://portmaster.games/images/ports/";
    // Port detail page
    public const string DetailPageBase = "https://portmaster.games/detail.html?name=";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "PortMaster-Desktop/1.0" } }
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly CacheService _cache;
    private const string CatalogCacheKey = "portmaster_catalog";

    // In-memory cache for the runtime catalog (fetched once per process)
    private Dictionary<string, RuntimeInfo>? _runtimeCatalog;

    public PortMasterClient(CacheService cache) => _cache = cache;

    // -------------------------------------------------------------------------
    // Catalog
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<Port>> GetPortsAsync(
        bool forceRefresh = false,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            var cached = await _cache.LoadJsonAsync<List<Port>>(CatalogCacheKey);
            if (cached is { Count: > 0 })
            {
                foreach (var p in cached)
                {
                    p.DownloadUrl = p.Source?.Url ?? "";
                    p.PosterUrl = $"{ImageCdnUrl}{p.Slug}/poster.png";
                    p.ScreenshotUrl = $"{ImageCdnUrl}{p.Slug}/screenshot.png";
                }
                return cached;
            }
        }

        progress?.Invoke("Downloading PortMaster catalog…");
        var json = await Http.GetStringAsync(PortsJsonUrl, ct);

        progress?.Invoke("Parsing port list…");
        var collection = JsonSerializer.Deserialize<PortsCollection>(json, JsonOpts);
        if (collection?.Ports == null || collection.Ports.Count == 0)
            throw new Exception("ports.json is empty or unparseable.");

        var portList = new List<Port>(collection.Ports.Count);
        foreach (var (key, port) in collection.Ports)
        {
            // The dict key is the canonical port name; prefer it over the inner "name" field
            if (string.IsNullOrEmpty(port.Name))
                port.Name = key;

            port.DownloadUrl = port.Source?.Url ?? "";
            port.PosterUrl = $"{ImageCdnUrl}{port.Slug}/poster.png";
            port.ScreenshotUrl = $"{ImageCdnUrl}{port.Slug}/screenshot.png";
            portList.Add(port);
        }

        await _cache.SaveJsonAsync(CatalogCacheKey, portList);
        return portList;
    }

    // -------------------------------------------------------------------------
    // Port ZIP install
    // -------------------------------------------------------------------------

    public async Task InstallPortAsync(
        Port port,
        string portsPath,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default,
        Action<string>? stepLog = null)
    {
        var downloadUrl = string.IsNullOrEmpty(port.DownloadUrl)
            ? throw new Exception($"No download URL for {port.Name}")
            : port.DownloadUrl;

        progress?.Report(($"Downloading {port.Attr.Title}…", 0));

        var tempFile = Path.Combine(Path.GetTempPath(), port.Name);
        try
        {
            await DownloadWithProgressAsync(downloadUrl, tempFile, port.Size, progress, ct);
            var sizeMb = (new FileInfo(tempFile).Length) / 1048576.0;
            stepLog?.Invoke($"✅ Downloaded {port.Attr.Title} ({sizeMb:F1} MB)");

            progress?.Report(($"Extracting {port.Name}…", 0.95));
            int fileCount = 0;
            await Task.Run(() =>
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(tempFile);
                fileCount = zip.Entries.Count(e => !e.FullName.EndsWith('/'));
                foreach (var entry in zip.Entries)
                {
                    var destPath = Path.GetFullPath(Path.Combine(portsPath, entry.FullName));
                    if (entry.FullName.EndsWith('/'))
                        Directory.CreateDirectory(destPath);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
            }, ct);
            stepLog?.Invoke($"✅ Extracted {fileCount} file(s) to {portsPath}");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // -------------------------------------------------------------------------
    // Runtime catalog
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a lookup of canonical runtime filename → download info for aarch64,
    /// e.g. "box64.squashfs" → RuntimeInfo { Url, Md5, Size }.
    /// Fetched once and cached in memory.
    /// </summary>
    public async Task<Dictionary<string, RuntimeInfo>> GetRuntimeCatalogAsync(CancellationToken ct = default)
    {
        if (_runtimeCatalog != null) return _runtimeCatalog;

        var json = await Http.GetStringAsync(RuntimeCatalogUrl, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var catalog = new Dictionary<string, RuntimeInfo>(StringComparer.OrdinalIgnoreCase);

        if (!doc.RootElement.TryGetProperty("utils", out var utils)) return catalog;

        foreach (var entry in utils.EnumerateObject())
        {
            if (!entry.Name.EndsWith(".squashfs", StringComparison.OrdinalIgnoreCase)) continue;

            var val = entry.Value;
            var arch = val.TryGetProperty("runtime_arch", out var archEl) ? archEl.GetString() : "aarch64";
            if (arch != "aarch64") continue;

            // If runtime_name is present, use it as the canonical key; otherwise use the dict key.
            var key = val.TryGetProperty("runtime_name", out var rnEl) && rnEl.GetString() is { } rn
                ? rn
                : entry.Name;

            if (!val.TryGetProperty("url", out var urlEl)) continue;
            var url = urlEl.GetString() ?? "";
            var md5 = val.TryGetProperty("md5", out var md5El) ? md5El.GetString() ?? "" : "";
            var size = val.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
            var name = val.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? key : key;

            catalog[key] = new RuntimeInfo(name, url, md5, size);
        }

        _runtimeCatalog = catalog;
        return catalog;
    }

    public record RuntimeInfo(string Name, string Url, string Md5, long Size);

    // -------------------------------------------------------------------------
    // Partition helpers
    // -------------------------------------------------------------------------

    public static bool IsPortInstalled(Port port, string portsPath)
    {
        if (port.Items.Count == 0) return false;
        foreach (var item in port.Items)
        {
            var target = Path.Combine(portsPath, item.TrimEnd('/'));
            if (!File.Exists(target) && !Directory.Exists(target))
                return false;
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static async Task DownloadWithProgressAsync(
        string url, string destPath, long expectedSize,
        IProgress<(string, double)>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destPath);

        var buffer = new byte[131072];
        long downloaded = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            var fraction = totalBytes > 0 ? (double)downloaded / totalBytes : 0;
            progress?.Report(($"Downloading… {downloaded / 1024 / 1024} MB", fraction * 0.9));
        }
    }
}

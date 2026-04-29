using System.IO.Compression;

namespace PortMasterDesktop.Services;

/// <summary>
/// Manages downloading and caching PortMaster port screenshots from GitHub releases.
/// </summary>
public class PortMasterImagesService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly CacheService _cache;
    private readonly string _imagesDir;
    private const string ImagesUrlKey = "portmaster_images_url";
    private const string ImagesChecksum = "portmaster_images_checksum";

    public PortMasterImagesService(CacheService cache)
    {
        _cache = cache;
        _imagesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "portmaster-desktop", "cache", "portmaster_images");
    }

    /// <summary>
    /// Ensures PortMaster images are downloaded and extracted. Returns the directory path.
    /// </summary>
    public async Task<string?> EnsureImagesAsync(Action<string>? progress = null)
    {
        // Check if already extracted
        if (Directory.Exists(_imagesDir) && HasScreenshots())
            return _imagesDir;

        try
        {
            Directory.CreateDirectory(_imagesDir);

            progress?.Invoke("Fetching PortMaster images...");

            // Use direct GitHub raw CDN URL for images.zip (faster, no API parsing needed)
            // This points to the latest release of PortMaster-New
            const string ImagesZipUrl = "https://github.com/PortsMaster/PortMaster-New/releases/download/2026-04-28_1830/images.zip";

            progress?.Invoke("Downloading images.zip...");
            var zipPath = Path.Combine(_imagesDir, "images.zip");

            using (var resp = await Http.GetAsync(ImagesZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) return null;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(zipPath);
                await src.CopyToAsync(dst);
            }

            progress?.Invoke("Extracting images...");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!entry.Name.EndsWith(".png") && !entry.Name.EndsWith(".jpg"))
                        continue;
                    entry.ExtractToFile(Path.Combine(_imagesDir, entry.Name), overwrite: true);
                }
            }

            File.Delete(zipPath);
            return _imagesDir;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortMasterImages] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the local file path for a port's screenshot, or null if not found.
    /// </summary>
    public string? GetScreenshotPath(string portSlug)
    {
        if (!Directory.Exists(_imagesDir)) return null;

        var pngPath = Path.Combine(_imagesDir, $"{portSlug}.screenshot.png");
        var jpgPath = Path.Combine(_imagesDir, $"{portSlug}.screenshot.jpg");

        return File.Exists(pngPath) ? pngPath : (File.Exists(jpgPath) ? jpgPath : null);
    }

    private bool HasScreenshots() =>
        Directory.GetFiles(_imagesDir, "*.screenshot.png", SearchOption.TopDirectoryOnly).Length > 0 ||
        Directory.GetFiles(_imagesDir, "*.screenshot.jpg", SearchOption.TopDirectoryOnly).Length > 0;
}

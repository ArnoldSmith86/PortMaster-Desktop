using System.IO.Compression;

namespace PortMasterDesktop.Services;

/// <summary>
/// Manages downloading and caching PortMaster port screenshots from GitHub releases.
/// </summary>
public class PortMasterImagesService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };
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
    public async Task<string?> EnsureImagesAsync(IProgress<(string message, double? fraction)>? progress = null)
    {
        if (HasCachedImages()) return _imagesDir;

        try
        {
            Directory.CreateDirectory(_imagesDir);

            // Use direct GitHub raw CDN URL for images.zip (faster, no API parsing needed)
            // This points to the latest release of PortMaster-New
            const string ImagesZipUrl = "https://github.com/PortsMaster/PortMaster-New/releases/download/2026-04-28_1830/images.zip";

            progress?.Report(("Downloading PortMaster screenshots…", null));
            var zipPath = Path.Combine(_imagesDir, "images.zip");

            using (var resp = await Http.GetAsync(ImagesZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) return null;
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);

                var buffer = new byte[131072];
                long downloaded = 0;
                int read;
                int lastReportedMb = -1;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    int mb = (int)(downloaded / 1048576);
                    if (mb != lastReportedMb)
                    {
                        lastReportedMb = mb;
                        var msg = total > 0
                            ? $"Downloading screenshots… {mb} / {total / 1048576} MB"
                            : $"Downloading screenshots… {mb} MB";
                        double? frac = total > 0 ? (double)downloaded / total : null;
                        progress?.Report((msg, frac));
                    }
                }
            }

            progress?.Report(("Extracting screenshots…", null));
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

    /// <summary>True when at least one extracted screenshot file is present on disk.</summary>
    public bool HasCachedImages()
    {
        if (!Directory.Exists(_imagesDir)) return false;
        return Directory.EnumerateFiles(_imagesDir, "*.screenshot.png").Any()
            || Directory.EnumerateFiles(_imagesDir, "*.screenshot.jpg").Any();
    }

    /// <summary>Returns the cache path if it has actual screenshot files, else null.</summary>
    public string? GetCachedImagesPath() => HasCachedImages() ? _imagesDir : null;
}

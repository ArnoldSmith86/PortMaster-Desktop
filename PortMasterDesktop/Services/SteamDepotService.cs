using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PortMasterDesktop.Stores;

namespace PortMasterDesktop.Services;

/// <summary>
/// Downloads a Steam depot for a specific platform.
///
/// Local Steam path (Steam client installed):
///   Opens steam://open/console and shows the user a one-line command to paste.
///   Then monitors the download path and reports progress until done.
///   No password prompts — Steam is already authenticated.
///
/// SteamCMD path (no local Steam):
///   Not yet implemented — reserved for remote/headless use.
///
/// Depot files land in:
///   {steamRoot}/steamapps/content/app_{appId}/depot_{depotId}/
/// </summary>
public class SteamDepotService
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the local path where a Steam console download_depot command will place files.
    /// On Linux the client stores depot content under ubuntu12_32/steamapps/content/.
    /// </summary>
    public static string DepotPath(string appId, string depotId)
    {
        var steamRoot = LocalSteamStore.FindSteamRoot()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Steam");

        // Try all known candidate sub-paths; return the first that already exists,
        // or the primary candidate if none exist yet.
        string[] candidates = OperatingSystem.IsLinux()
            ? [
                Path.Combine(steamRoot, "ubuntu12_32", "steamapps", "content", $"app_{appId}", $"depot_{depotId}"),
                Path.Combine(steamRoot, "steamapps", "content", $"app_{appId}", $"depot_{depotId}"),
              ]
            : [
                Path.Combine(steamRoot, "steamapps", "content", $"app_{appId}", $"depot_{depotId}"),
              ];

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    /// <summary>
    /// Downloads a Steam depot using the local Steam client console.
    ///
    /// Opens steam://open/console, prompts the user to paste the download_depot
    /// command, then monitors the depot path until the download completes.
    ///
    /// Returns null on success, error string on failure.
    /// </summary>
    /// <param name="showDialog">
    /// Called with (title, body) to display the paste-command dialog.
    /// The caller must await it — the method continues only after the user clicks OK.
    /// If null the download starts immediately without a confirmation step.
    /// </param>
    public async Task<string?> DownloadDepotViaLocalSteamAsync(
        string appId,
        string depotId,
        string? manifestId = null,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default,
        Func<string, string, string?, Task>? showDialog = null)
    {
        var depotPath = DepotPath(appId, depotId);

        if (Directory.Exists(depotPath) && DirectoryHasFiles(depotPath))
        {
            progress?.Report(("Depot already downloaded.", 1.0));
            return null;
        }

        var openErr = OpenSteamConsole();
        if (openErr != null) return openErr;

        var consoleCommand = manifestId != null
            ? $"download_depot {appId} {depotId} {manifestId}"
            : $"download_depot {appId} {depotId}";

        if (showDialog != null)
        {
            await showDialog(
                "Steam Console",
                "The Steam console is now open.\n\n" +
                "Paste the command below and press Enter, then click OK — " +
                "the app will monitor the download and copy the files automatically.",
                consoleCommand);
        }

        progress?.Report(("Waiting for Steam download to start…", 0.02));
        return await MonitorDepotDownloadAsync(depotPath, progress, ct);
    }

    /// <summary>
    /// Waits for a depot path to appear, fill up, and stabilise.
    /// Reports progress as size grows. Returns null when complete.
    /// </summary>
    public static async Task<string?> MonitorDepotDownloadAsync(
        string depotPath,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromHours(2));

        // Wait for the depot dir to appear (up to 5 min — user may be slow to paste)
        var appeared = await WaitForAsync(
            () => Directory.Exists(depotPath) && DirectoryHasFiles(depotPath),
            pollMs: 3000, timeoutMs: 600_000, ct);
        if (!appeared) return "Timed out waiting for depot download to start.";

        progress?.Report(("Download started…", 0.05));

        // Poll size until stable for two consecutive checks
        long prevSize = -1;
        int stableCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(4000, ct);

            var size = GetDirectorySize(depotPath);
            var sizeMb = size / 1048576.0;
            progress?.Report(($"Downloading… {sizeMb:F0} MB", Math.Min(0.05 + sizeMb / 500.0, 0.95)));

            if (size > 0 && size == prevSize)
            {
                stableCount++;
                if (stableCount >= 3) break; // size unchanged for 3 polls (~12 s) = done
            }
            else
            {
                stableCount = 0;
            }
            prevSize = size;
        }

        if (DateTime.UtcNow >= deadline) return "Depot download timed out.";
        progress?.Report(($"Download complete — {GetDirectorySize(depotPath) / 1048576.0:F0} MB", 1.0));
        return null;
    }

    // ── Regular install monitoring ────────────────────────────────────────────

    /// <summary>
    /// Triggers steam://install/{appId}, then polls until the game is fully installed.
    /// If the install hasn't started after <paramref name="startTimeoutSeconds"/> seconds,
    /// shows a reminder dialog and keeps waiting. Returns null on success.
    /// </summary>
    public static async Task<string?> RequestAndMonitorInstallAsync(
        string appId,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default,
        Func<string, string, string?, Task>? showDialog = null,
        int startTimeoutSeconds = 120)
    {
        var openErr = LocalSteamStore.RequestInstall(appId);
        if (openErr != null) return $"Could not open Steam: {openErr}";

        progress?.Report(("Waiting for Steam install to start…", 0.02));

        var appsDir = FindSteamAppsDirs();
        var startDeadline = DateTime.UtcNow.AddSeconds(startTimeoutSeconds);
        bool reminderShown = false;

        // Phase 1: wait for the appmanifest ACF to appear (install acknowledged by Steam)
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (FindAcfForApp(appsDir, appId) != null) break;

            if (!reminderShown && DateTime.UtcNow >= startDeadline)
            {
                reminderShown = true;
                if (showDialog != null)
                    await showDialog(
                        "Waiting for Steam",
                        "Steam was asked to install the game, but the download hasn't started yet.\n\n" +
                        "Make sure to accept any install prompts in Steam, then click OK to keep waiting.",
                        null);
            }

            await Task.Delay(3000, ct);
        }

        progress?.Report(("Steam is installing the game…", 0.05));

        // Phase 2: wait until StateFlags has the installed bit (4) and the game dir has content
        var totalDeadline = DateTime.UtcNow.AddHours(2);
        while (DateTime.UtcNow < totalDeadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5000, ct);

            var acf = FindAcfForApp(appsDir, appId);
            if (acf == null) continue;

            var (flags, installDir, acfAppsDir) = ParseAcfInstallInfo(acf);
            if ((flags & 4) != 0 && !string.IsNullOrEmpty(installDir))
            {
                var gamePath = Path.Combine(acfAppsDir, "common", installDir);
                if (DirectoryHasFiles(gamePath)) return null;
            }

            var downloadedMb = !string.IsNullOrEmpty(installDir)
                ? GetDirectorySize(Path.Combine(acfAppsDir, "common", installDir)) / 1048576.0
                : 0;
            progress?.Report(($"Installing… {downloadedMb:F0} MB", Math.Min(0.05 + downloadedMb / 5000.0, 0.95)));
        }

        return "Timed out waiting for Steam game installation.";
    }

    private static List<string> FindSteamAppsDirs()
    {
        var dirs = new List<string>();
        var steamRoot = LocalSteamStore.FindSteamRoot();
        if (steamRoot == null) return dirs;

        void TryAdd(string d) { if (Directory.Exists(d)) dirs.Add(d); }
        TryAdd(Path.Combine(steamRoot, "steamapps"));

        var libFolders = Path.Combine(steamRoot, "config", "libraryfolders.vdf");
        if (File.Exists(libFolders))
            foreach (Match m in Regex.Matches(File.ReadAllText(libFolders), @"""path""\s*""([^""]+)"""))
                TryAdd(Path.Combine(m.Groups[1].Value.Replace(@"\\", "/"), "steamapps"));

        return dirs;
    }

    private static string? FindAcfForApp(IEnumerable<string> appsDirs, string appId)
    {
        foreach (var dir in appsDirs)
        {
            var acf = Path.Combine(dir, $"appmanifest_{appId}.acf");
            if (File.Exists(acf)) return acf;
        }
        return null;
    }

    private static (int flags, string installDir, string appsDir) ParseAcfInstallInfo(string acfPath)
    {
        try
        {
            var text = File.ReadAllText(acfPath);
            var flags = int.TryParse(
                Regex.Match(text, @"""StateFlags""\s*""(\d+)""").Groups[1].Value, out var f) ? f : 0;
            var installDir = Regex.Match(text, @"""installdir""\s*""([^""]+)""").Groups[1].Value;
            return (flags, installDir, Path.GetDirectoryName(acfPath) ?? "");
        }
        catch { return (0, "", Path.GetDirectoryName(acfPath) ?? ""); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? OpenSteamConsole()
    {
        try
        {
            // Use xdg-open explicitly so we can suppress its stderr/stdout
            var launcher = OperatingSystem.IsLinux() ? "xdg-open" : null;
            ProcessStartInfo psi = launcher != null
                ? new ProcessStartInfo(launcher, "steam://open/console")
                  { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
                : new ProcessStartInfo("steam://open/console") { UseShellExecute = true };
            Process.Start(psi);
            return null;
        }
        catch (Exception ex) { return $"Could not open Steam console: {ex.Message}"; }
    }

    private static bool DirectoryHasFiles(string path) =>
        Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f).Length)
                .Sum();
        }
        catch { return 0; }
    }

    private static async Task<bool> WaitForAsync(
        Func<bool> condition, int pollMs, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(pollMs, ct);
        }
        return false;
    }

}

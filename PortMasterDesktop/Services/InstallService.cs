using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using PortMasterDesktop.Models;
using PortMasterDesktop.PortMaster;

namespace PortMasterDesktop.Services;

/// <summary>Store entry exposed to callers for matching and compatibility checks.</summary>
public record PortStoreEntry(
    string StoreKey,
    string? Url,
    string? AppId,
    string Compatibility,
    string? IncompatibleReason);

/// <summary>
/// Orchestrates the full installation of a port:
/// 1. Download + extract the port ZIP from PortMaster.
/// 2. (Where supported) copy/extract the required game files from the user's local install.
///
/// Step 2 is currently supported for locally installed Steam games and will be
/// expanded as store download APIs are integrated.
/// </summary>
public class InstallService
{
    private readonly PortMasterClient _portMaster;
    private GameFileInstructions? _instructions;

    public InstallService(PortMasterClient portMaster)
    {
        _portMaster = portMaster;
    }

    // -------------------------------------------------------------------------
    // Port installation
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Store info (from GameFileInstructions.json)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the store entries for a port from GameFileInstructions.json.
    /// Used by LibraryService to augment catalog-based store matching.
    /// </summary>
    public async Task<IReadOnlyList<PortStoreEntry>> GetStoreEntriesAsync(string portName)
    {
        var instructions = await LoadInstructionsAsync();
        if (!instructions.Ports.TryGetValue(portName, out var portInst) || portInst.Stores == null)
            return [];
        return portInst.Stores
            .Select(kvp => new PortStoreEntry(kvp.Key, kvp.Value.Url, kvp.Value.AppId,
                kvp.Value.Compatibility, kvp.Value.IncompatibleReason))
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Port installation
    // -------------------------------------------------------------------------

    public async Task InstallPortAsync(
        Port port,
        string portsPath,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default,
        string? fileSystem = null)
    {
        await _portMaster.InstallPortAsync(port, portsPath, progress, ct);

        // Fix permissions for ext4/ext3/overlay (zip extraction doesn't preserve Unix modes)
        if (fileSystem is "ext4" or "ext3" or "overlay" && OperatingSystem.IsLinux())
        {
            foreach (var item in port.Items.Concat(port.ItemsOpt))
            {
                var itemPath = Path.Combine(portsPath, item.TrimEnd('/'));
                if (File.Exists(itemPath) || Directory.Exists(itemPath))
                {
                    progress?.Report(($"Fixing permissions: {item}", 0.97));
                    await ChmodAsync(itemPath);
                }
            }
        }

        // Inject PortMaster signature into top-level .sh scripts
        foreach (var item in port.Items.Concat(port.ItemsOpt)
            .Where(i => i.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)))
        {
            AddPmSignature(Path.Combine(portsPath, item), port.Name, item);
        }

        // Download required runtimes
        await DownloadRuntimesAsync(port, portsPath, progress, ct);

        MergeGamelist(portsPath, port); // best-effort; error ignored in UI path
    }

    /// <summary>
    /// Merges the port's gameinfo.xml into ports/gamelist.xml, matching
    /// the format PortMaster-GUI uses on the device.
    /// </summary>
    /// <summary>Returns null on success, error string on failure.</summary>
    public static string? MergeGamelist(string portsPath, Port port)
    {
        var portDir = Path.GetFileNameWithoutExtension(port.Name);
        var gameInfoPath = Path.Combine(portsPath, portDir, "gameinfo.xml");
        if (!File.Exists(gameInfoPath)) return null;

        var gamelistPath = Path.Combine(portsPath, "gamelist.xml");
        try
        {
            var portDoc   = XDocument.Load(gameInfoPath);
            var portGames = portDoc.Root?.Elements("game").ToList() ?? [];
            if (portGames.Count == 0) return null;

            XDocument listDoc = File.Exists(gamelistPath)
                ? XDocument.Load(gamelistPath)
                : new XDocument(new XElement("gameList"));
            var root = listDoc.Root!;

            // Tags whose values should not be overwritten (user-generated runtime data)
            var preserve = new HashSet<string> { "playcount", "lastplayed", "gametime" };

            foreach (var portGame in portGames)
            {
                var path = portGame.Element("path")?.Value;
                if (path == null) continue;

                var existing = root.Elements("game")
                    .FirstOrDefault(g => g.Element("path")?.Value == path);

                if (existing != null)
                {
                    foreach (var child in portGame.Elements())
                    {
                        if (preserve.Contains(child.Name.LocalName)) continue;
                        var ex = existing.Element(child.Name);
                        if (ex != null) ex.Value = child.Value;
                        else existing.Add(new XElement(child));
                    }
                }
                else
                {
                    root.Add(new XElement(portGame));
                }
            }

            listDoc.Save(gamelistPath);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // -------------------------------------------------------------------------
    // Post-extraction steps
    // -------------------------------------------------------------------------

    private static async Task ChmodAsync(string path)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("chmod", $"-R 777 \"{path}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { /* best-effort */ }
    }

    private static void AddPmSignature(string scriptPath, string portName, string itemName)
    {
        if (!File.Exists(scriptPath)) return;
        try
        {
            var text = File.ReadAllText(scriptPath);
            var lines = text.Split('\n').ToList();
            // Remove any existing PORTMASTER signature lines
            lines = lines.Where(l => !(l.TrimStart().StartsWith('#') && l.Contains("PORTMASTER:"))).ToList();
            if (lines.Count == 0) return;
            lines.Insert(1, $"# PORTMASTER: {portName}, {itemName}");
            File.WriteAllText(scriptPath, string.Join("\n", lines));
        }
        catch { /* best-effort */ }
    }

    private async Task DownloadRuntimesAsync(
        Port port,
        string portsPath,
        IProgress<(string message, double fraction)>? progress,
        CancellationToken ct)
    {
        var runtimes = GetRuntimes(port.Attr);
        if (runtimes.Count == 0) return;

        Dictionary<string, PortMasterClient.RuntimeInfo> catalog;
        try
        {
            progress?.Report(("Fetching runtime catalog…", 0.97));
            catalog = await _portMaster.GetRuntimeCatalogAsync(ct);
        }
        catch { return; /* runtime download is best-effort */ }

        var libsDir = Path.Combine(portsPath, "PortMaster", "libs");
        Directory.CreateDirectory(libsDir);

        foreach (var runtime in runtimes)
        {
            var key = runtime.EndsWith(".squashfs", StringComparison.OrdinalIgnoreCase)
                ? runtime : runtime + ".squashfs";

            if (!catalog.TryGetValue(key, out var info)) continue;

            var destPath = Path.Combine(libsDir, key);
            if (File.Exists(destPath)) continue; // already present

            progress?.Report(($"Downloading runtime {info.Name}…", 0.97));
            try
            {
                await DownloadFileAsync(info.Url, destPath, info.Size, progress, ct);
            }
            catch { /* best-effort — port may still run if runtime is already on device */ }
        }
    }

    private static IReadOnlyList<string> GetRuntimes(PortAttr attr)
    {
        var rt = attr.Runtime;
        if (rt == null) return [];
        return rt.Value.ValueKind switch
        {
            JsonValueKind.String => rt.Value.GetString() is { Length: > 0 } s ? [s] : [],
            JsonValueKind.Array => rt.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList(),
            _ => []
        };
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, long expectedSize,
        IProgress<(string, double)>? progress, CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "PortMaster-Desktop/1.0");
        http.Timeout = TimeSpan.FromMinutes(10);

        using var response = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destPath);

        var buffer = new byte[131072];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
                progress?.Report(($"Downloading runtime… {downloaded / 1048576} MB", 0.97 + 0.02 * downloaded / total));
        }
    }

    // -------------------------------------------------------------------------
    // Game file installation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Copies game files from a locally installed Steam/GOG/etc. game
    /// to the correct location inside the already-installed port directory.
    ///
    /// Returns null on success, or an error string if files couldn't be installed.
    /// </summary>
    public async Task<string?> InstallGameFilesAsync(
        Port port,
        StoreGame sourceGame,
        string portsPath,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sourceGame.InstallPath) || !Directory.Exists(sourceGame.InstallPath))
            return $"Game install path not found: {sourceGame.InstallPath}\nPlease copy files manually according to the port instructions.";

        var instructions = await LoadInstructionsAsync();
        if (!instructions.Ports.TryGetValue(port.Name, out var portInstructions))
            return "No automatic file instructions available for this port.\nPlease follow the manual instructions.";

        // Reject incompatible store versions before attempting any file copy
        var storeKey = sourceGame.Store.ToString().ToLowerInvariant();
        var storeInfo = portInstructions.Stores?.GetValueOrDefault(storeKey);
        if (storeInfo?.Compatibility == "incompatible")
            return storeInfo.IncompatibleReason
                ?? $"The {sourceGame.Store} version of this game is not compatible with this port.";

        var steps = portInstructions.GetStepsForStore(storeKey);

        if (steps == null || steps.Count == 0)
            return "No copy instructions defined for this source. Please follow the manual installation instructions.";

        int total = steps.Count;
        int done = 0;

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(($"Installing: {step.Description}", (double)done / total));

            var error = await ExecuteStepAsync(step, sourceGame.InstallPath, portsPath, ct);
            if (error != null) return error;
            done++;
        }

        progress?.Report(("Game files installed.", 1.0));
        return null;
    }

    /// <summary>
    /// Like <see cref="InstallGameFilesAsync"/> but also handles the case where the
    /// game is owned but not locally installed. For Steam games with a known depot,
    /// opens the Steam console and monitors the download; for other stores,
    /// returns an instruction string telling the user what to do.
    /// </summary>
    public async Task<string?> DownloadAndInstallGameFilesAsync(
        Port port,
        StoreGame ownedGame,
        string portsPath,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        if (ownedGame.IsInstalled)
            return await InstallGameFilesAsync(port, ownedGame, portsPath, progress, ct);

        var instr = await LoadInstructionsAsync();
        if (!instr.Ports.TryGetValue(port.Name, out var portInst))
            return $"Please install {port.Attr.Title} locally via {ownedGame.Store}, then click Install again.";

        var storeKey = ownedGame.Store.ToString().ToLowerInvariant();

        if (ownedGame.Store == StoreId.Steam)
        {
            var storeInfo = portInst.Stores?.GetValueOrDefault(storeKey);
            var depotInfo = storeInfo?.SteamDepot;
            var appId = storeInfo?.AppId ?? "";

            if (depotInfo?.DepotId == null)
                return $"Please install {port.Attr.Title} via Steam, then click Install again.";

            var depotPath = SteamDepotService.DepotPath(appId, depotInfo.DepotId);
            bool depotReady = Directory.Exists(depotPath) &&
                Directory.EnumerateFiles(depotPath, "*", SearchOption.AllDirectories).Any();

            if (!depotReady)
            {
                var depotSvc = new SteamDepotService();
                var depotErr = await depotSvc.DownloadDepotViaLocalSteamAsync(
                    appId, depotInfo.DepotId, depotInfo.ManifestId, progress, ct);
                if (depotErr != null) return depotErr;
            }
            else
            {
                progress?.Report(("Depot already downloaded — copying files…", 0.5));
            }

            var fakeGame = new StoreGame
            {
                Store = StoreId.Steam, Id = appId, Title = port.Attr.Title,
                IsInstalled = true, InstallPath = depotPath
            };
            return await InstallGameFilesAsync(port, fakeGame, portsPath, progress, ct);
        }

        return $"Please install {port.Attr.Title} locally via {ownedGame.Store}, then click Install again.";
    }

    private static Task<string?> ExecuteStepAsync(
        CopyInstruction step, string sourceRoot, string portsRoot, CancellationToken ct)
    {
        return Task.Run<string?>(() =>
        {
            try
            {
                switch (step.Action)
                {
                    case "copy_all":
                    {
                        var src = Path.Combine(sourceRoot, step.SourceDir ?? ".");
                        var dst = Path.Combine(portsRoot, step.DestDir ?? "");
                        CopyDirectory(src, dst, ct);
                        break;
                    }
                    case "copy_dir":
                    {
                        var src = Path.Combine(sourceRoot, step.SourceDir ?? "");
                        var dst = Path.Combine(portsRoot, step.DestDir ?? "");
                        if (!Directory.Exists(src))
                            return $"Source directory not found: {src}";
                        CopyDirectory(src, dst, ct);
                        break;
                    }
                    case "copy_file":
                    {
                        var src = Path.Combine(sourceRoot, step.SourceFile ?? "");
                        var dst = Path.Combine(portsRoot, step.DestDir ?? "");
                        if (!File.Exists(src))
                            return $"Source file not found: {src}";
                        Directory.CreateDirectory(dst);
                        File.Copy(src, Path.Combine(dst, Path.GetFileName(src)), overwrite: true);
                        break;
                    }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }, ct);
    }

    private static void CopyDirectory(string src, string dst, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // -------------------------------------------------------------------------
    // Instructions loader
    // -------------------------------------------------------------------------

    private async Task<GameFileInstructions> LoadInstructionsAsync()
    {
        if (_instructions != null) return _instructions;

        var asm = Assembly.GetExecutingAssembly();
        // Embedded resource name: PortMasterDesktop.PortMaster.GameFileInstructions.json
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("GameFileInstructions.json"));

        if (resourceName == null)
        {
            _instructions = new GameFileInstructions();
            return _instructions;
        }

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        _instructions = await JsonSerializer.DeserializeAsync<GameFileInstructions>(stream)
            ?? new GameFileInstructions();
        return _instructions;
    }

    // -------------------------------------------------------------------------
    // JSON models for GameFileInstructions.json (schema v2)
    // -------------------------------------------------------------------------

    private class GameFileInstructions
    {
        [JsonPropertyName("ports")]
        public Dictionary<string, PortInstructions> Ports { get; set; } = [];
    }

    private class PortInstructions
    {
        [JsonPropertyName("stores")]
        public Dictionary<string, StoreInfo>? Stores { get; set; }

        [JsonPropertyName("fileInstructions")]
        public List<FileInstructionSet>? FileInstructions { get; set; }

        /// <summary>Returns file steps for a given store key, falling back to "any".</summary>
        public List<CopyInstruction>? GetStepsForStore(string storeKey)
        {
            var match = FileInstructions?.FirstOrDefault(fi =>
                fi.FromStore.Equals(storeKey, StringComparison.OrdinalIgnoreCase));
            match ??= FileInstructions?.FirstOrDefault(fi =>
                fi.FromStore.Equals("any", StringComparison.OrdinalIgnoreCase));
            return match?.Steps;
        }
    }

    private class StoreInfo
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("appId")]
        public string? AppId { get; set; }

        [JsonPropertyName("compatibility")]
        public string Compatibility { get; set; } = "unverified"; // compatible|incompatible|unverified|not_available

        [JsonPropertyName("incompatibleReason")]
        public string? IncompatibleReason { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("requiredVersion")]
        public string? RequiredVersion { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }

        [JsonPropertyName("steamDepot")]
        public SteamDepotInfo? SteamDepot { get; set; }

        [JsonIgnore]
        public bool IsCompatible => Compatibility == "compatible";
        [JsonIgnore]
        public bool IsAvailable => Compatibility != "not_available";
    }

    public class SteamDepotInfo
    {
        [JsonPropertyName("depotId")]
        public string? DepotId { get; set; }

        [JsonPropertyName("manifestId")]
        public string? ManifestId { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }

    private class FileInstructionSet
    {
        [JsonPropertyName("fromStore")]
        public string FromStore { get; set; } = "any";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("steamDepotCommand")]
        public string? SteamDepotCommand { get; set; }

        [JsonPropertyName("steps")]
        public List<CopyInstruction> Steps { get; set; } = [];
    }

    private class CopyInstruction
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = ""; // copy_all|copy_dir|copy_file|place_installer

        [JsonPropertyName("sourceDir")]
        public string? SourceDir { get; set; }

        [JsonPropertyName("sourceFile")]
        public string? SourceFile { get; set; }

        [JsonPropertyName("destDir")]
        public string? DestDir { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
    }
}

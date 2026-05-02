using PortMasterDesktop.Models;

namespace PortMasterDesktop.Services;

/// <summary>
/// Detects mounted volumes that contain a PortMaster-compatible ports directory.
/// Checks roms/ports/ first, then ports/ (when ports/PortMaster exists).
/// A custom path configured in settings always takes priority.
/// </summary>
public class PartitionService
{
    private readonly CacheService _cache;
    private readonly LogService   _log;

    public PartitionService(CacheService cache, LogService log)
    {
        _cache = cache;
        _log   = log;
    }

    public async Task<List<PartitionInfo>> DetectAsync()
    {
        _log.Section("Partition Detection");
        var results = new List<PartitionInfo>();

        // Custom path from settings overrides auto-detection
        var customPath = await _cache.LoadJsonAsync<string>("custom_ports_path");
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _log.Info($"Custom ports path: {customPath}");
            if (Directory.Exists(customPath))
            {
                results.Add(BuildEntry(customPath, "(custom)", ""));
                _log.Info($"  → accepted");
            }
            else
            {
                _log.Warn($"  → directory does not exist, skipping");
            }
        }

        var fsTypeMap  = BuildFsTypeMap();
        var candidates = GetMountCandidates(fsTypeMap).ToList();
        _log.Info($"Mount candidates: {candidates.Count}");

        foreach (var (mountPoint, fsType) in candidates)
        {
            try
            {
                var portsPath = FindPortsPath(mountPoint);
                if (portsPath == null)
                {
                    _log.Info($"  {mountPoint}  fs={fsType}  → no ports dir");
                    continue;
                }

                long freeBytes = 0, totalBytes = 0;
                try
                {
                    var d = new DriveInfo(mountPoint);
                    if (d.IsReady) { freeBytes = d.AvailableFreeSpace; totalBytes = d.TotalSize; }
                }
                catch { }

                var (canDir, canGamelist, canLibs) = ProbeWriteAccess(portsPath);

                var info = new PartitionInfo
                {
                    MountPoint       = mountPoint,
                    PortsPath        = portsPath,
                    Label            = TryGetVolumeLabel(mountPoint),
                    FreeBytes        = freeBytes,
                    TotalBytes       = totalBytes,
                    FileSystem       = fsType,
                    CanWritePortsDir = canDir,
                    CanWriteGamelist = canGamelist,
                    CanWriteLibsDir  = canLibs,
                };
                results.Add(info);
                _log.Info($"  {mountPoint}  fs={fsType}  ports={portsPath}  writable={info.CanWrite}  free={info.FreeSpace}");
            }
            catch (Exception ex)
            {
                _log.Warn($"  {mountPoint}  → inaccessible: {ex.Message}");
            }
        }

        _log.Info($"Partitions found: {results.Count}");
        return results;
    }

    // Check roms/ports/ first (most common layout), then ports/ if ports/PortMaster/ exists.
    private static string? FindPortsPath(string mountPoint)
    {
        var romsPortsPath = Path.Combine(mountPoint, "roms", "ports");
        if (Directory.Exists(romsPortsPath))
            return romsPortsPath;

        var portsPath = Path.Combine(mountPoint, "ports");
        if (Directory.Exists(Path.Combine(portsPath, "PortMaster")))
            return portsPath;

        return null;
    }

    private static PartitionInfo BuildEntry(string portsPath, string label, string fsType)
    {
        var mountPoint = Path.GetPathRoot(portsPath) ?? portsPath;

        long freeBytes = 0, totalBytes = 0;
        string resolvedFs = fsType;
        try
        {
            var d = new DriveInfo(mountPoint);
            if (d.IsReady)
            {
                freeBytes   = d.AvailableFreeSpace;
                totalBytes  = d.TotalSize;
                resolvedFs  = string.IsNullOrEmpty(fsType) ? (d.DriveFormat ?? "") : fsType;
            }
        }
        catch { }

        var (canDir, canGamelist, canLibs) = ProbeWriteAccess(portsPath);

        return new PartitionInfo
        {
            MountPoint       = mountPoint,
            PortsPath        = portsPath,
            Label            = label,
            FreeBytes        = freeBytes,
            TotalBytes       = totalBytes,
            FileSystem       = resolvedFs,
            CanWritePortsDir = canDir,
            CanWriteGamelist = canGamelist,
            CanWriteLibsDir  = canLibs,
        };
    }

    private static (bool canDir, bool canGamelist, bool canLibs) ProbeWriteAccess(string portsPath)
    {
        var probe = Path.Combine(portsPath, $".portmaster_probe_{Guid.NewGuid():N}");
        bool canDir;
        try { File.WriteAllText(probe, ""); File.Delete(probe); canDir = true; }
        catch { canDir = false; }

        bool canGamelist = true;
        var gamelist = Path.Combine(portsPath, "gamelist.xml");
        if (File.Exists(gamelist))
        {
            try
            {
                using var _ = new FileStream(gamelist, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch { canGamelist = false; }
        }

        bool canLibs = true;
        var libsDir = Path.Combine(portsPath, "PortMaster", "libs");
        try
        {
            Directory.CreateDirectory(libsDir);
            var libProbe = Path.Combine(libsDir, $".portmaster_probe_{Guid.NewGuid():N}");
            File.WriteAllText(libProbe, "");
            File.Delete(libProbe);
        }
        catch { canLibs = false; }

        return (canDir, canGamelist, canLibs);
    }

    private static Dictionary<string, string> BuildFsTypeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists("/proc/mounts")) return map;
        foreach (var line in File.ReadAllLines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length >= 3)
                map[parts[1].Replace(@"\040", " ")] = parts[2];
        }
        return map;
    }

    private static IEnumerable<(string mountPoint, string fsType)> GetMountCandidates(
        Dictionary<string, string> fsTypeMap)
    {
        if (OperatingSystem.IsLinux())
        {
            var seen   = new HashSet<string>(StringComparer.Ordinal);
            var mounts = new List<(string, string)>();

            // FAT/exFAT/NTFS/FUSE — typical removable media filesystems
            foreach (var (mp, fs) in fsTypeMap)
                if (fs is "vfat" or "exfat" or "ntfs" or "fuse" or "fuseblk")
                    if (seen.Add(mp)) mounts.Add((mp, fs));

            // Also sweep common mount roots (catches ext4 cards, bind mounts, etc.)
            foreach (var dir in new[] { "/media", "/mnt", "/run/media" })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (seen.Add(sub))
                        mounts.Add((sub, fsTypeMap.TryGetValue(sub, out var f1) ? f1 : ""));
                    foreach (var sub2 in Directory.GetDirectories(sub))
                        if (seen.Add(sub2))
                            mounts.Add((sub2, fsTypeMap.TryGetValue(sub2, out var f2) ? f2 : ""));
                }
            }
            return mounts;
        }

        if (OperatingSystem.IsWindows())
        {
            // Check all drive letters; Fixed includes internal drives but FindPortsPath
            // filters them out unless they actually contain a ports directory.
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Removable or DriveType.Fixed)
                .Select(d => (d.RootDirectory.FullName, d.DriveFormat ?? ""));
        }

        if (OperatingSystem.IsMacOS())
        {
            return Directory.Exists("/Volumes")
                ? Directory.GetDirectories("/Volumes").Select(v => (v, ""))
                : Enumerable.Empty<(string, string)>();
        }

        return [];
    }

    private static string TryGetVolumeLabel(string mountPoint)
    {
        try { var d = new DriveInfo(mountPoint); return d.IsReady ? d.VolumeLabel : ""; }
        catch { return ""; }
    }
}

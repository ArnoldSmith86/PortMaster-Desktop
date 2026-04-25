using PortMasterDesktop.Models;

namespace PortMasterDesktop.Services;

/// <summary>
/// Detects mounted volumes/partitions that contain a roms/ports/ directory,
/// indicating a PortMaster-compatible SD card or USB drive.
/// </summary>
public class PartitionService
{
    public List<PartitionInfo> Detect()
    {
        var results = new List<PartitionInfo>();
        var fsTypeMap = BuildFsTypeMap();
        var candidates = GetMountCandidates(fsTypeMap);

        foreach (var (mountPoint, fsType) in candidates)
        {
            try
            {
                var portsPath = Path.Combine(mountPoint, "roms", "ports");
                if (!Directory.Exists(portsPath)) continue;

                long freeBytes = 0, totalBytes = 0;
                try
                {
                    var info = new DriveInfo(mountPoint);
                    if (info.IsReady)
                    {
                        freeBytes = info.AvailableFreeSpace;
                        totalBytes = info.TotalSize;
                    }
                }
                catch { /* space info is optional */ }

                var (canWriteDir, canWriteGamelist, canWriteLibs) = ProbeWriteAccess(portsPath);

                results.Add(new PartitionInfo
                {
                    MountPoint = mountPoint,
                    Label = TryGetVolumeLabel(mountPoint),
                    FreeBytes = freeBytes,
                    TotalBytes = totalBytes,
                    FileSystem = fsType,
                    CanWritePortsDir = canWriteDir,
                    CanWriteGamelist = canWriteGamelist,
                    CanWriteLibsDir = canWriteLibs,
                });
            }
            catch { /* inaccessible mount */ }
        }

        return results;
    }

    private static (bool canWriteDir, bool canWriteGamelist, bool canWriteLibsDir) ProbeWriteAccess(string portsPath)
    {
        // Can we create new files in ports/?
        var probe = Path.Combine(portsPath, $".portmaster_probe_{Guid.NewGuid():N}");
        bool canWriteDir;
        try { File.WriteAllText(probe, ""); File.Delete(probe); canWriteDir = true; }
        catch { canWriteDir = false; }

        // Can we write to gamelist.xml if it already exists?
        bool canWriteGamelist = true;
        var gamelist = Path.Combine(portsPath, "gamelist.xml");
        if (File.Exists(gamelist))
        {
            try
            {
                using var _ = new FileStream(gamelist, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch { canWriteGamelist = false; }
        }

        // Can we write to PortMaster/libs/ (needed for runtime squashfs downloads)?
        bool canWriteLibsDir = true;
        var libsDir = Path.Combine(portsPath, "PortMaster", "libs");
        try
        {
            Directory.CreateDirectory(libsDir);
            var libsProbe = Path.Combine(libsDir, $".portmaster_probe_{Guid.NewGuid():N}");
            File.WriteAllText(libsProbe, "");
            File.Delete(libsProbe);
        }
        catch { canWriteLibsDir = false; }

        return (canWriteDir, canWriteGamelist, canWriteLibsDir);
    }

    // Returns all /proc/mounts entries as mountPoint → fsType.
    private static Dictionary<string, string> BuildFsTypeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists("/proc/mounts")) return map;
        foreach (var line in File.ReadAllLines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;
            map[parts[1].Replace(@"\040", " ")] = parts[2];
        }
        return map;
    }

    private static IEnumerable<(string mountPoint, string fsType)> GetMountCandidates(
        Dictionary<string, string> fsTypeMap)
    {
        if (OperatingSystem.IsLinux())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var mounts = new List<(string, string)>();

            // FAT/exFAT/NTFS/FUSE from /proc/mounts — typical SD card filesystems
            foreach (var (mp, fs) in fsTypeMap)
            {
                if (fs is "vfat" or "exfat" or "ntfs" or "fuse" or "fuseblk")
                {
                    if (seen.Add(mp)) mounts.Add((mp, fs));
                }
            }

            // Also scan common removable media directories (catches ext4 cards etc.)
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
        try
        {
            var info = new DriveInfo(mountPoint);
            return info.IsReady ? info.VolumeLabel : "";
        }
        catch { return ""; }
    }
}

namespace PortMasterDesktop.Models;

/// <summary>A mounted volume that contains a ports directory (roms/ports or ports/).</summary>
public class PartitionInfo
{
    public string MountPoint { get; set; } = "";
    // Set by PartitionService to the actual ports directory found (roms/ports or ports/).
    public string PortsPath { get; set; } = "";
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public bool CanWritePortsDir { get; set; }
    public bool CanWriteGamelist { get; set; }
    public bool CanWriteLibsDir { get; set; }
    public bool CanWrite => CanWritePortsDir && CanWriteGamelist;

    public string DisplayName =>
        string.IsNullOrEmpty(Label) || Label == MountPoint ? MountPoint : $"{Label} ({MountPoint})";

    public string FreeSpace => FormatBytes(FreeBytes);
    public string TotalSpace => FormatBytes(TotalBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }
}

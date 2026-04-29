using CommunityToolkit.Mvvm.ComponentModel;

namespace PortMasterDesktop.Models;

public enum PortInstallState
{
    NotInstalled,
    NeedsGameFiles,
    Ready,
    NoPartition,
}

/// <summary>
/// Compatibility level of the user's owned games with this port.
/// Compatible > Unknown > Incompatible — best level wins when multiple stores match.
/// </summary>
public enum StoreMatchCompatibility
{
    /// <summary>GFI confirms this store version works.</summary>
    Compatible,
    /// <summary>Store not listed in GFI, or GFI marks it "unverified".</summary>
    Unknown,
    /// <summary>GFI marks this store version "incompatible" or "not_available".</summary>
    Incompatible,
}

/// <summary>
/// An owned game from a store, optionally paired with a PortMaster port.
/// Port is null for owned games that have no matching PortMaster port.
/// </summary>
public partial class GameMatch : ObservableObject
{
    public Port? Port { get; set; }
    public List<StoreGame> OwnedGames { get; set; } = [];
    public PortInstallState InstallState { get; set; } = PortInstallState.NoPartition;
    /// <summary>Best compatibility level across all owned games. Null when no owned game.</summary>
    public StoreMatchCompatibility? StoreCompat { get; set; }

    /// <summary>Human-readable reason for incompatibility, when StoreCompat == Incompatible.</summary>
    public string? IncompatibleReason { get; set; }

    /// <summary>Portrait cover fetched from SteamGridDB in the background; takes priority over store cover.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayCoverUrl), nameof(DisplayImageAspectRatio))]
    private string? _sgdbCoverUrl;

    /// <summary>Whether to display PortMaster images instead of game covers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayCoverUrl), nameof(DisplayImageAspectRatio))]
    private bool _usePortMasterImages;

    /// <summary>Base path for PortMaster images on the SD card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayCoverUrl))]
    private string _portMasterImagesPath = "";

    public bool HasPort => Port != null;
    public bool IsRtr => Port?.Attr.Rtr ?? false;
    public bool HasOwnedGame => OwnedGames.Count > 0;

    // Compat badges replace the install-state badge unless the game is already fully working.
    private bool IsNotReady =>
        InstallState is not PortInstallState.Ready;

    public bool ShowIncompatibleBadge =>
        OwnedGames.Count > 0 && StoreCompat == StoreMatchCompatibility.Incompatible && IsNotReady;
    public bool ShowUnknownCompatBadge =>
        OwnedGames.Count > 0 && StoreCompat == StoreMatchCompatibility.Unknown && IsNotReady;

    // Normal install-state badge shows when no compat badge is replacing it
    public bool ShowInstallStateBadge =>
        HasPort && !ShowIncompatibleBadge && !ShowUnknownCompatBadge;

    // Title: port title is canonical; fall back to store game title
    public string DisplayTitle => Port?.Attr.Title
        ?? OwnedGames.FirstOrDefault()?.Title
        ?? "";

    // PortMaster images take priority if enabled; SteamGridDB ignored when PortMaster mode active
    public string DisplayCoverUrl
    {
        get
        {
            if (UsePortMasterImages && !string.IsNullOrEmpty(Port?.Slug) && !string.IsNullOrEmpty(PortMasterImagesPath))
            {
                // Try to find local screenshot on SD card: {path}/{port_slug}.screenshot.{png|jpg}
                var basePath = Path.Combine(PortMasterImagesPath, $"{Port.Slug}.screenshot");
                var pngPath = $"{basePath}.png";
                var jpgPath = $"{basePath}.jpg";
                if (File.Exists(pngPath)) return $"file://{pngPath}";
                if (File.Exists(jpgPath)) return $"file://{jpgPath}";
            }
            // If using PortMaster and local file not found, fall back to remote screenshot
            if (UsePortMasterImages && !string.IsNullOrEmpty(Port?.ScreenshotUrl))
                return Port.ScreenshotUrl;
            if (!UsePortMasterImages && !string.IsNullOrEmpty(SgdbCoverUrl))
                return SgdbCoverUrl;
            return OwnedGames.FirstOrDefault(g => !string.IsNullOrEmpty(g.CoverUrl))?.CoverUrl ?? "";
        }
    }

    // Aspect ratio of the displayed image: 4:3 for PortMaster images, 2:3 for game covers
    public double DisplayImageAspectRatio =>
        UsePortMasterImages && !string.IsNullOrEmpty(Port?.ScreenshotUrl) ? 4.0 / 3.0 : 2.0 / 3.0;

    public long OwnedGameSizeBytes =>
        OwnedGames.Where(g => g.InstallSizeBytes > 0).Sum(g => g.InstallSizeBytes);
}

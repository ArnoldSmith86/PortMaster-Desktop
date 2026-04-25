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
public class GameMatch
{
    public Port? Port { get; set; }
    public List<StoreGame> OwnedGames { get; set; } = [];
    public PortInstallState InstallState { get; set; } = PortInstallState.NoPartition;
    /// <summary>Best compatibility level across all owned games. Null when no owned game.</summary>
    public StoreMatchCompatibility? StoreCompat { get; set; }

    /// <summary>Human-readable reason for incompatibility, when StoreCompat == Incompatible.</summary>
    public string? IncompatibleReason { get; set; }

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

    // Cover comes from the store game only
    public string DisplayCoverUrl =>
        OwnedGames.FirstOrDefault(g => !string.IsNullOrEmpty(g.CoverUrl))?.CoverUrl ?? "";

    public long OwnedGameSizeBytes =>
        OwnedGames.Where(g => g.InstallSizeBytes > 0).Sum(g => g.InstallSizeBytes);
}

namespace PortMasterDesktop.Models;

public enum StoreId { Steam, Gog, Epic, Itch, Amazon, Humble }

/// <summary>A game from any store that the user owns.</summary>
public class StoreGame
{
    public StoreId Store { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public long InstallSizeBytes { get; set; }
    public bool IsInstalled { get; set; }
    public string? InstallPath { get; set; }

    // Store-specific: used for matching against port store URLs
    public string StoreUrl { get; set; } = "";
}

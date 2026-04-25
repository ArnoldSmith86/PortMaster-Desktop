using System.Text.Json.Serialization;

namespace PortMasterDesktop.Models;

/// <summary>Top-level structure from PortMaster's ports.json.</summary>
public class PortsCollection
{
    // ports.json has "ports" as a dict keyed by filename, e.g. {"celeste.zip": {...}}
    [JsonPropertyName("ports")]
    public Dictionary<string, Port> Ports { get; set; } = [];
}

/// <summary>One entry from ports.json.</summary>
public class Port
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = [];

    [JsonPropertyName("items_opt")]
    public List<string> ItemsOpt { get; set; } = [];

    [JsonPropertyName("attr")]
    public PortAttr Attr { get; set; } = new();

    [JsonPropertyName("source")]
    public PortSource? Source { get; set; }

    /// <summary>Direct download URL from source.url; set during catalog load.</summary>
    [JsonIgnore]
    public string DownloadUrl { get; set; } = "";

    /// <summary>Poster image URL; set during catalog load.</summary>
    [JsonIgnore]
    public string PosterUrl { get; set; } = "";

    [JsonIgnore]
    public string ScreenshotUrl { get; set; } = "";

    /// <summary>Slug derived from Name, e.g. "celeste.zip" → "celeste".</summary>
    [JsonIgnore]
    public string Slug => Name.Replace(".zip", "").ToLowerInvariant();

    /// <summary>ZIP size in bytes (from source.size).</summary>
    [JsonIgnore]
    public long Size => Source?.Size ?? 0;

    public override string ToString() => Attr.Title;
}

public class PortSource
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("date_added")]
    public string? DateAdded { get; set; }

    [JsonPropertyName("date_updated")]
    public string? DateUpdated { get; set; }

    [JsonPropertyName("repo")]
    public string? Repo { get; set; }
}

public class PortAttr
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("porter")]
    public List<string> Porter { get; set; } = [];

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("desc_md")]
    public string? DescMd { get; set; }

    [JsonPropertyName("inst")]
    public string Inst { get; set; } = "";

    [JsonPropertyName("inst_md")]
    public string? InstMd { get; set; }

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];

    /// <summary>true = Ready-To-Run (open source, no paid game files needed).</summary>
    [JsonPropertyName("rtr")]
    public bool Rtr { get; set; }

    [JsonPropertyName("exp")]
    public bool Experimental { get; set; }

    [JsonPropertyName("runtime")]
    public System.Text.Json.JsonElement? Runtime { get; set; }

    [JsonPropertyName("store")]
    public List<PortStore>? StoreMaybeNull { get; set; }

    [JsonIgnore]
    public List<PortStore> Store => StoreMaybeNull ?? [];

    [JsonPropertyName("availability")]
    public string Availability { get; set; } = "";

    [JsonPropertyName("reqs")]
    public List<string> Reqs { get; set; } = [];

    [JsonPropertyName("arch")]
    public List<string> Arch { get; set; } = [];
}

public class PortStore
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("gameurl")]
    public string GameUrl { get; set; } = "";

    [JsonPropertyName("developerurl")]
    public string DeveloperUrl { get; set; } = "";
}

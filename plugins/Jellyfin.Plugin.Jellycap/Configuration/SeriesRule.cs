namespace Jellyfin.Plugin.Jellycap.Configuration;

public enum RuleFormat
{
    /// <summary>Use the global recap format.</summary>
    Default,
    Video,
    Text,
    Both
}

public enum RuleResumeContent
{
    /// <summary>Use the global resume recap content.</summary>
    Default,
    Both,
    PreviousEpisodes,
    ThisEpisode
}

/// <summary>Per TV show override. Series without a rule follow the global settings.</summary>
public class SeriesRule
{
    /// <summary>Jellyfin series item id (any Guid format).</summary>
    public string SeriesId { get; set; } = string.Empty;

    /// <summary>Display name only (shown in the settings page).</summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>False = never recap this show.</summary>
    public bool Enabled { get; set; } = true;

    public RuleFormat Format { get; set; } = RuleFormat.Default;

    /// <summary>What a resumed episode's recap covers. Default = global setting.</summary>
    public RuleResumeContent ResumeContent { get; set; } = RuleResumeContent.Default;

    /// <summary>Seasons that get recaps, e.g. "1,3-5" or "2-" (open end). Empty = all. 0 = specials.</summary>
    public string Seasons { get; set; } = string.Empty;

    /// <summary>Episode numbers (within the allowed seasons) that get recaps, e.g. "2-" skips the first episode of each season. Empty = all.</summary>
    public string Episodes { get; set; } = string.Empty;
}

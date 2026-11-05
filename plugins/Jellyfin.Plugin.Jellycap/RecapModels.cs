namespace Jellyfin.Plugin.Jellycap;

public enum RecapMode
{
    /// <summary>Episode starts from the beginning: recap previous episodes.</summary>
    New,

    /// <summary>Episode resumed mid-way: recap previous episodes and the part of this episode already watched.</summary>
    Resume
}

public enum RecapFormat
{
    /// <summary>Video clips only.</summary>
    Video,

    /// <summary>Text cards only (episode titles + summaries).</summary>
    Text,

    /// <summary>Text card before each video clip.</summary>
    Both
}

public abstract record RecapSegment;

public sealed record ClipSpec(string Path, double StartSeconds, double DurationSeconds) : RecapSegment;

public sealed record TextCardSpec(string Title, string? Body, double Seconds) : RecapSegment;

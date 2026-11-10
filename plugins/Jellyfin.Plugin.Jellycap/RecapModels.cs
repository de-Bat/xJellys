namespace Jellyfin.Plugin.Jellycap;

public enum RecapMode
{
    /// <summary>Episode starts from the beginning: recap previous episodes.</summary>
    New,

    /// <summary>Episode resumed mid-way: recap previous episodes and the part of this episode already watched.</summary>
    Resume,

    /// <summary>Episode resumed mid-way: recap only the part of this episode already watched.</summary>
    ResumeThisEpisode
}

/// <summary>What a resumed episode's recap covers.</summary>
public enum ResumeContent
{
    /// <summary>Previous episodes, then the watched part of this episode.</summary>
    Both,

    /// <summary>Previous episodes only (same recap as starting the episode from the beginning).</summary>
    PreviousEpisodes,

    /// <summary>Only the watched part of this episode, up to the resume position.</summary>
    ThisEpisode
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

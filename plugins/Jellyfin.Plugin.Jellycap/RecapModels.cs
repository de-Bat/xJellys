namespace Jellyfin.Plugin.Jellycap;

public enum RecapMode
{
    /// <summary>Episode starts from the beginning: recap previous episodes.</summary>
    New,

    /// <summary>Episode resumed mid-way: recap previous episodes and the part of this episode already watched.</summary>
    Resume
}

public sealed record ClipSpec(string Path, double StartSeconds, double DurationSeconds);

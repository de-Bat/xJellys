using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellycap.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folder where recap videos are written. Add it as a Jellyfin library. Empty = data folder default.</summary>
    public string RecapDirectory { get; set; } = string.Empty;

    /// <summary>Comma separated, case-insensitive substrings matched against client and device name. Empty = every remote-controllable client.</summary>
    public string ClientFilter { get; set; } = "Android TV";

    public int PreviousEpisodes { get; set; } = 2;

    public int PreviousClipSeconds { get; set; } = 30;

    /// <summary>Seconds cut from the end of previous episodes (credits) before taking the clip.</summary>
    public int CreditsOffsetSeconds { get; set; } = 150;

    public int ThisEpisodeClips { get; set; } = 4;

    public int ThisEpisodeClipSeconds { get; set; } = 8;

    /// <summary>Playback starting before this position counts as a new episode.</summary>
    public int NewEpisodeMaxStartSeconds { get; set; } = 20;

    /// <summary>Playback starting after this position counts as a resumed episode.</summary>
    public int ResumeMinSeconds { get; set; } = 120;

    /// <summary>How long to wait for a recap to be built before leaving playback alone.</summary>
    public int MaxWaitSeconds { get; set; } = 45;

    public bool PreGenerateNext { get; set; } = true;

    public int OutputHeight { get; set; } = 720;

    public int RetentionDays { get; set; } = 30;
}

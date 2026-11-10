using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellycap.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folder where recap videos are written. Add it as a Jellyfin library. Empty = data folder default.</summary>
    public string RecapDirectory { get; set; } = string.Empty;

    /// <summary>Comma separated, case-insensitive substrings matched against client and device name. Empty = every remote-controllable client.</summary>
    public string ClientFilter { get; set; } = "Android TV";

    /// <summary>True = only shows listed in <see cref="SeriesRules"/> get recaps. False = every show, except rules that are disabled.</summary>
    public bool OnlyListedSeries { get; set; }

    public SeriesRule[] SeriesRules { get; set; } = Array.Empty<SeriesRule>();

    /// <summary>Default format: video clips, text cards, or both. A series rule can override it.</summary>
    public RecapFormat RecapFormat { get; set; } = RecapFormat.Video;

    /// <summary>Language of text card labels (en, he, es, fr, de, it, pt, ru, ar) and translation target.</summary>
    public string TextLanguage { get; set; } = "en";

    /// <summary>Optional LibreTranslate compatible base URL, e.g. http://localhost:5000. Empty = show summaries as stored in Jellyfin.</summary>
    public string TranslationUrl { get; set; } = string.Empty;

    public string TranslationApiKey { get; set; } = string.Empty;

    /// <summary>
    /// When the JellyXR TV app runs on the playback device, it draws the text cards over the player:
    /// Text recaps pause the episode for the cards (no video), Both recaps play the clips with the cards on top.
    /// </summary>
    public bool TvOverlay { get; set; } = true;

    /// <summary>Font for text cards. Must exist on the server and cover the language (e.g. "DejaVu Sans" for Hebrew on Linux). Empty = Arial.</summary>
    public string FontName { get; set; } = string.Empty;

    /// <summary>Resumed episode: recap previous episodes, the watched part of this episode, or both. A series rule can override it.</summary>
    public ResumeContent ResumeRecapContent { get; set; } = ResumeContent.Both;

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

    /// <summary>When an episode starts, build the recap for the following episode.</summary>
    public bool PreGenerateNext { get; set; } = true;

    /// <summary>When an episode stops: mid-way = build its resume recap, finished = build the next episode's recap.</summary>
    public bool PreBuildOnStop { get; set; } = true;

    public int OutputHeight { get; set; } = 720;

    public int RetentionDays { get; set; } = 30;
}

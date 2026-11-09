using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellySubSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Write the synced subtitle file next to the video instead of into Jellyfin's metadata folder.
    /// Needs write access to the media folder. Use it when Jellyfin does not list files from the metadata folder.
    /// </summary>
    public bool SaveNextToMedia { get; set; } = false;

    /// <summary>Only administrators may shift subtitles. The synced track is shared by every user.</summary>
    public bool AdminsOnly { get; set; } = false;

    /// <summary>Largest shift either way, in seconds.</summary>
    public int MaxOffsetSeconds { get; set; } = 600;
}

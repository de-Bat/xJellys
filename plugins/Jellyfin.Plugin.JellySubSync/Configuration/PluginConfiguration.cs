using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellySubSync.Configuration;

/// <summary>Which titles get the subtitle sync box.</summary>
public enum SyncScope
{
    /// <summary>Nowhere (default: sync is opt-in).</summary>
    Off,

    /// <summary>Every movie and episode.</summary>
    All,

    /// <summary>Only the libraries, shows and movies picked in the settings.</summary>
    Selected
}

/// <summary>A show or movie picked for sync. Name and type are only for the settings page.</summary>
public class SelectedMedia
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>"Series" or "Movie".</summary>
    public string Type { get; set; } = string.Empty;
}

public class PluginConfiguration : BasePluginConfiguration
{
    public SyncScope Scope { get; set; } = SyncScope.Off;

    /// <summary>Selected scope: libraries (collection folder ids) where every title gets sync.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Selected scope: shows (every episode) and movies that get sync.</summary>
    public SelectedMedia[] Media { get; set; } = Array.Empty<SelectedMedia>();

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

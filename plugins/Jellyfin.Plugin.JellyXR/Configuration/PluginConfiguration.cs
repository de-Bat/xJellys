using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyXR.Configuration;

public enum CastScope
{
    /// <summary>Use the global scope (only meaningful in a show rule).</summary>
    Default,

    /// <summary>People seen around the pause position.</summary>
    Scene,

    /// <summary>Whole cast of the episode / movie.</summary>
    Episode,

    /// <summary>Scene if scene data has someone for this position, else the whole episode cast.</summary>
    Current,

    /// <summary>No cards for this show.</summary>
    Off
}

public enum CardMode
{
    /// <summary>One message listing everyone.</summary>
    Combined,

    /// <summary>One message per person, shown one after the other.</summary>
    Separate
}

public class SeriesRule
{
    public string SeriesId { get; set; } = string.Empty;

    public string SeriesName { get; set; } = string.Empty;

    public CastScope Scope { get; set; } = CastScope.Default;
}

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>Comma separated, case-insensitive substrings matched against client and device name. Empty = every remote-controllable client.</summary>
    public string ClientFilter { get; set; } = "Android TV";

    public CastScope Scope { get; set; } = CastScope.Current;

    public SeriesRule[] SeriesRules { get; set; } = Array.Empty<SeriesRule>();

    public CardMode CardMode { get; set; } = CardMode.Combined;

    public int MaxCards { get; set; } = 6;

    /// <summary>Seconds each message stays on screen (per card in Separate mode).</summary>
    public int CardSeconds { get; set; } = 8;

    /// <summary>Show "Actor as Character". False = actor only.</summary>
    public bool ShowCharacter { get; set; } = true;

    /// <summary>Scene window: seconds before the pause position that still count as "on screen".</summary>
    public int SceneBackSeconds { get; set; } = 10;

    /// <summary>Scene window: seconds after the pause position.</summary>
    public int SceneForwardSeconds { get; set; } = 2;

    /// <summary>Scan: one frame every N seconds.</summary>
    public int ScanIntervalSeconds { get; set; } = 2;

    /// <summary>Cosine similarity needed to call a face "this actor" (0.25 loose .. 0.5 strict).</summary>
    public double MatchThreshold { get; set; } = 0.38;

    /// <summary>Detector confidence needed to count a face.</summary>
    public double DetectThreshold { get; set; } = 0.5;

    /// <summary>When an item without scene data starts playing, scan it in the background.</summary>
    public bool ScanOnPlayback { get; set; } = true;

    /// <summary>Zip with the SCRFD detector and ArcFace recognizer (insightface "buffalo_s": det_500m.onnx + w600k_mbf.onnx). Check the model license for your use.</summary>
    public string ModelZipUrl { get; set; } = "https://github.com/deepinsight/insightface/releases/download/v0.7/buffalo_s.zip";
}

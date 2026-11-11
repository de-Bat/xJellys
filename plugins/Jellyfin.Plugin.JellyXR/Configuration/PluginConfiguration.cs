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

public enum DataSource
{
    /// <summary>On-server face scan only.</summary>
    Local,

    /// <summary>Xrayarr only; no local face scan, no model download.</summary>
    Xrayarr,

    /// <summary>Xrayarr first; local face scan for items Xrayarr has no ready timeline for.</summary>
    XrayarrThenLocal,

    /// <summary>Local face scan first; Xrayarr for items without a local timeline.</summary>
    LocalThenXrayarr
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

    /// <summary>Most actors per pause. 0 = no limit.</summary>
    public int MaxCards { get; set; } = 0;

    /// <summary>Current scope: after the people on screen, list the rest of the cast.</summary>
    public bool AppendRestOfCast { get; set; } = true;

    /// <summary>Characters per message in Combined mode. Android TV toasts show 2 lines, about 90 characters.</summary>
    public int MessageChars { get; set; } = 90;

    /// <summary>Seconds each message stays on screen. Android TV toasts last 3.5 s, so a message is resent to reach this.</summary>
    public int CardSeconds { get; set; } = 8;

    /// <summary>Show "Actor as Character". False = actor only.</summary>
    public bool ShowCharacter { get; set; } = true;

    /// <summary>Add appearance info to each card: time on screen and when the actor is next seen.</summary>
    public bool ShowAppearance { get; set; } = true;

    /// <summary>Add the cast timeline section to movie and episode pages of the Jellyfin web client.</summary>
    public bool InjectWebUi { get; set; } = true;

    /// <summary>Web client: on pause show a panel with portraits instead of a text message (needs InjectWebUi).</summary>
    public bool WebOverlay { get; set; } = true;

    /// <summary>JellyXR TV companion app: on pause show the portraits panel over the Android TV player (and send that device no text messages).</summary>
    public bool TvOverlay { get; set; } = true;

    /// <summary>
    /// JellyTvOverlay app: false = the cast panel appears by itself on pause (passive, the remote keeps driving the player);
    /// true = it is offered on pause and opened as an interactive panel (rows selectable). A double pause always offers it.
    /// </summary>
    public bool TvPanelInteractive { get; set; } = false;

    /// <summary>JellyTvOverlay app: when an item with X-Ray data starts playing, show a small "X-Ray" chip for a few seconds.</summary>
    public bool TvStartHint { get; set; } = true;

    /// <summary>Seconds the pause panel (web and TV companion) stays up. 0 = until playback resumes.</summary>
    public int OverlaySeconds { get; set; } = 0;

    /// <summary>Mark items with X-Ray data: a tag named <see cref="MarkerName"/>, shown by every client including Android TV.</summary>
    public bool MarkItems { get; set; } = true;

    /// <summary>Also add <see cref="MarkerName"/> as a genre, so the Android TV app shows it on the movie page. Off by default: it mixes into real genres.</summary>
    public bool MarkGenre { get; set; } = false;

    /// <summary>
    /// Put an "X-RAY" badge on the poster / episode thumbnail of items with X-Ray data (needs <see cref="MarkItems"/>).
    /// The original image file is kept; a badged copy in the plugin data directory is shown instead. Off by default.
    /// </summary>
    public bool MarkPoster { get; set; } = false;

    /// <summary>Name of the tag and genre that mark items with X-Ray data.</summary>
    public string MarkerName { get; set; } = "X-Ray";

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

    /// <summary>Where "who is on screen" comes from.</summary>
    public DataSource DataSource { get; set; } = DataSource.Local;

    /// <summary>Xrayarr base URL, e.g. http://xray:8080.</summary>
    public string XrayarrUrl { get; set; } = string.Empty;

    /// <summary>Xrayarr API key (Settings -> General in Xrayarr).</summary>
    public string XrayarrApiKey { get; set; } = string.Empty;

    /// <summary>Optional path rewrite for path lookups: "jellyfinPrefix=xrayarrPrefix", several separated by ';'.</summary>
    public string XrayarrPathMap { get; set; } = string.Empty;

    /// <summary>Language for actor and character names, a TMDB tag such as "he" or "de-DE". Empty = keep Jellyfin's / Xrayarr's names. Falls back to English per person.</summary>
    public string CardLanguage { get; set; } = string.Empty;

    /// <summary>TMDB API key (v3 key or v4 read token) used to fetch names in <see cref="CardLanguage"/>.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Zip with the SCRFD detector and ArcFace recognizer (insightface "buffalo_s": det_500m.onnx + w600k_mbf.onnx). Check the model license for your use.</summary>
    public string ModelZipUrl { get; set; } = "https://github.com/deepinsight/insightface/releases/download/v0.7/buffalo_s.zip";
}

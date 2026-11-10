using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyXR.Overlay;

/// <summary>
/// Serialization for layouts sent to the JellyTvOverlay host: non-ASCII text (Hebrew) stays UTF-8 instead of
/// \uXXXX escapes, a third of the size. Layouts are JSON data for the app, never embedded in HTML. Every layout
/// JSON this client sends (cast panel, actor details, their preloads) uses these options, so a detail shown on OK
/// is the same string as the one built for the preload.
/// </summary>
public static class OverlayJson
{
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

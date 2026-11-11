using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jellyfin.Plugin.JellySubSync.Overlay;

/// <summary>
/// Serialization for layouts sent to the JellyTvOverlay host: non-ASCII text (Hebrew, arrows) stays UTF-8 instead of
/// \uXXXX escapes. Layouts are JSON data for the app, never embedded in HTML.
/// </summary>
public static class OverlayJson
{
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.JellyXR.Overlay;

/// <summary>
/// The "this title has X-Ray" chip shown for a few seconds when playback starts (JellyTvOverlay layout v1):
/// passive, top right, gone on pause (the cast panel takes over), stop or the next item.
/// </summary>
public static class HintLayoutBuilder
{
    public const string LayoutId = "xray-hint";

    /// <summary>Seconds the chip stays up.</summary>
    public const int Seconds = 6;

    public static JsonObject Build(string markerName, int seconds = Seconds) => new()
    {
        ["v"] = 1,
        ["id"] = LayoutId,
        ["focus"] = CastLayoutBuilder.FocusPassive,
        ["ttlMs"] = Math.Max(1, seconds) * 1000,
        ["closeOn"] = new JsonArray("stop", "itemStart", "pause", "doublePause"),
        ["root"] = new JsonObject
        {
            ["type"] = "row",
            ["x"] = 0.74,
            ["y"] = 0.05,
            ["w"] = 0.24,
            ["h"] = 0.1,
            ["pad"] = 0.018,
            ["gap"] = 0.014,
            ["bg"] = "#DC0C0E12",
            ["radius"] = 0.02,
            ["children"] = new JsonArray
            {
                new JsonObject { ["type"] = "icon", ["name"] = "person", ["size"] = 0.034 },
                new JsonObject
                {
                    ["type"] = "column",
                    ["children"] = new JsonArray
                    {
                        Text(string.IsNullOrWhiteSpace(markerName) ? "X-Ray" : markerName.Trim(), 0.03, "#FFFFFFFF", bold: true),
                        Text("Pause to see the cast", 0.022, "#C3FFFFFF"),
                    },
                },
            },
        },
    };

    private static JsonObject Text(string text, double size, string color, bool bold = false) => new()
    {
        ["type"] = "text",
        ["text"] = text,
        ["size"] = size,
        ["color"] = color,
        ["bold"] = bold,
        ["maxLines"] = 1,
        ["dir"] = "auto",
    };
}

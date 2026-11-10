using System.Globalization;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.JellySubSync.Overlay;

/// <summary>
/// The subtitle sync box on the JellyTvOverlay app, as JellyXR TV's SyncBox: a dark box at the bottom centre.
/// Left/right step the offset by 0.25 s and up/down by 1 s locally in the app; OK or play report <c>apply</c>
/// with the current value; back closes. The box and its status messages share the id <c>box</c>, so a message
/// replaces the box in place.
/// </summary>
public static class SyncLayoutBuilder
{
    /// <summary>Layout id (the host namespaces it as <c>JellySubSync/box</c>).</summary>
    public const string LayoutId = "box";

    /// <summary>Id of the offset value node (key <c>value.offset</c> of the apply action, in ms).</summary>
    public const string ValueId = "offset";

    /// <summary>Reported by OK and play.</summary>
    public const string ApplyAction = "apply";

    public const long SmallStep = 250;
    public const long BigStep = 1000;

    /// <summary>Idle timeout of the box (restarted on every key press).</summary>
    public const int IdleMs = 15000;

    /// <summary>How long a result / failure message stays up.</summary>
    public const int MessageMs = 6000;

    public const string Hint = "▲▼ 1 s   ◀▶ 0.25 s   OK apply and play   BACK close";

    private const string Title = "Subtitle sync";
    private const string BoxBg = "#E1101218";
    private const string BusyColor = "#FFC8D2FF";
    private const string DoneColor = "#FFAAEBB9";
    private const string FailColor = "#FFFFBE8C";

    /// <summary>The interactive box for <paramref name="trackLabel"/>, starting at <paramref name="offsetMs"/> (clamped to ±max).</summary>
    public static JsonObject Box(string trackLabel, long offsetMs, long maxOffsetMs)
    {
        var max = Math.Max(BigStep, maxOffsetMs);
        var offset = Math.Clamp(offsetMs, -max, max);

        var body = new JsonArray { TitleText() };
        if (!string.IsNullOrEmpty(trackLabel))
        {
            body.Add(Text(trackLabel, 0.024, "#AAFFFFFF", bold: false));
        }

        body.Add(new JsonObject
        {
            ["type"] = "row",
            ["gap"] = 0.02,
            ["children"] = new JsonArray
            {
                Text("◀ earlier", 0.024, "#96FFFFFF", bold: false),
                new JsonObject
                {
                    ["type"] = "value",
                    ["id"] = ValueId,
                    ["value"] = offset,
                    ["min"] = -max,
                    ["max"] = max,
                    ["scale"] = 1000,
                    ["decimals"] = 2,
                    ["suffix"] = " s",
                    ["signed"] = true,
                    ["size"] = 0.055,
                    ["bold"] = true,
                    ["color"] = "#FFFFFFFF",
                },
                Text("later ▶", 0.024, "#96FFFFFF", bold: false),
            },
        });
        body.Add(Text(Hint, 0.02, "#8CFFFFFF", bold: false));

        return new JsonObject
        {
            ["v"] = 1,
            ["id"] = LayoutId,
            ["focus"] = "interactive",
            ["ttlMs"] = IdleMs,
            ["ttlResetOnKey"] = true,
            ["closeOn"] = new JsonArray("resume", "stop", "itemStart"),
            ["keys"] = new JsonObject
            {
                ["left"] = Step(-SmallStep),
                ["right"] = Step(SmallStep),
                ["down"] = Step(-BigStep),
                ["up"] = Step(BigStep),
                ["ok"] = ApplyBinding(),
                ["play"] = ApplyBinding(),
            },
            ["root"] = Frame(body, 0.25),
        };
    }

    /// <summary>
    /// The same box with a status line only, passive (the remote drives the player). Busy: stays until replaced;
    /// otherwise closes after <see cref="MessageMs"/>. Closes only on itemStart, so "press play to resume" survives
    /// the Stop it announces.
    /// </summary>
    public static JsonObject Message(string text, bool busy, bool failed = false)
    {
        var color = busy ? BusyColor : failed ? FailColor : DoneColor;
        var body = new JsonArray
        {
            TitleText(),
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["size"] = 0.026,
                ["color"] = color,
                ["maxLines"] = 3,
                ["dir"] = "auto",
            },
        };

        return new JsonObject
        {
            ["v"] = 1,
            ["id"] = LayoutId,
            ["focus"] = "passive",
            ["ttlMs"] = busy ? 0 : MessageMs,
            ["closeOn"] = new JsonArray("itemStart"),
            ["root"] = Frame(body, 0.2),
        };
    }

    /// <summary>As SyncBox.format: +1.25 s, −0.5 s (minus sign), 0 s.</summary>
    public static string Format(long ms)
    {
        if (ms == 0)
        {
            return "0 s";
        }

        var s = (Math.Abs(ms) / 1000.0).ToString("F2", CultureInfo.InvariantCulture);
        if (s.EndsWith('0'))
        {
            s = s[..^1];
        }

        if (s.EndsWith(".0", StringComparison.Ordinal))
        {
            s = s[..^2];
        }

        return (ms > 0 ? "+" : "−") + s + " s";
    }

    private static JsonObject Step(long by) => new() { ["op"] = "step", ["target"] = ValueId, ["by"] = by };

    private static JsonObject ApplyBinding() => new() { ["op"] = "action", ["action"] = ApplyAction };

    private static JsonObject TitleText() => Text(Title, 0.032, "#FFFFFFFF", bold: true);

    private static JsonObject Text(string text, double size, string color, bool bold) => new()
    {
        ["type"] = "text",
        ["text"] = text,
        ["size"] = size,
        ["color"] = color,
        ["bold"] = bold,
        ["maxLines"] = 1,
        ["dir"] = "auto",
    };

    /// <summary>Full-screen stack with the box at the bottom centre (about SyncBox's 420 dp wide, 48 dp up).</summary>
    private static JsonObject Frame(JsonArray body, double h) => new()
    {
        ["type"] = "stack",
        ["children"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "column",
                ["x"] = 0.3,
                ["w"] = 0.4,
                ["y"] = 1 - 0.045 - h,
                ["h"] = h,
                ["pad"] = 0.025,
                ["gap"] = 0.01,
                ["bg"] = BoxBg,
                ["radius"] = 0.026,
                ["children"] = body,
            },
        },
    };
}

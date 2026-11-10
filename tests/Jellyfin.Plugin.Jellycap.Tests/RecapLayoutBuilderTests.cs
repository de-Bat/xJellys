using System.Text.Json.Nodes;
using Jellyfin.Plugin.Jellycap;
using Jellyfin.Plugin.Jellycap.Overlay;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
using Xunit;

namespace Jellyfin.Plugin.Jellycap.Tests;

public class RecapLayoutBuilderTests
{
    private static readonly OverlayCard[] Cards =
    {
        new("S01E01 · פרק ראשון", "תקציר הפרק הראשון", 0, 5),
        new("S01E02 · Two", null, 5, 7.5),
    };

    private static string[] Strings(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    [Fact]
    public void Builder_SequenceDurations()
    {
        var layout = RecapLayoutBuilder.Build(Cards, OverlayMode.Pause);

        Assert.Equal(1, layout["v"]!.GetValue<int>());
        Assert.Equal("recap", layout["id"]!.GetValue<string>());
        var root = layout["root"]!;
        Assert.Equal("sequence", root["type"]!.GetValue<string>());
        Assert.Equal("recapDone", root["finishedAction"]!.GetValue<string>());
        var durations = root["children"]!.AsArray().Select(c => c!["durationMs"]!.GetValue<int>()).ToArray();
        Assert.Equal(new[] { 5000, 7500 }, durations);
    }

    [Fact]
    public void PauseLayout_CloseOnAndSuppress()
    {
        var layout = RecapLayoutBuilder.Build(Cards, OverlayMode.Pause);

        Assert.Equal(new[] { "stop", "itemStart" }, Strings(layout["closeOn"]));
        Assert.Equal(new[] { "pause", "resume", "doublePause" }, Strings(layout["suppressTriggers"]));
        Assert.Equal("passive", layout["focus"]!.GetValue<string>());
    }

    [Fact]
    public void TimedLayout_CloseOnSeekAndNoSuppress()
    {
        var layout = RecapLayoutBuilder.Build(new[] { Cards[0] }, OverlayMode.Timed);

        Assert.Equal("recap-card", layout["id"]!.GetValue<string>());
        Assert.Equal(new[] { "stop", "itemStart", "seek" }, Strings(layout["closeOn"]));
        Assert.Null(layout["suppressTriggers"]);
    }

    [Fact]
    public void AllTextNodes_DirAuto_AndCardText()
    {
        var json = RecapLayoutBuilder.Build(Cards, OverlayMode.Pause).ToJsonString(OverlayJson.Options);
        var texts = new List<JsonObject>();
        Walk(JsonNode.Parse(json)!["root"], texts);

        Assert.Equal(3, texts.Count); // title + body, title only
        Assert.All(texts, t => Assert.Equal("auto", t["dir"]!.GetValue<string>()));
        Assert.Contains("פרק ראשון", json, StringComparison.Ordinal); // UTF-8, not \u escapes
    }

    [Theory]
    [InlineData(OverlayMode.Pause)]
    [InlineData(OverlayMode.Timed)]
    public void BuiltLayouts_PassHostValidator(OverlayMode mode)
    {
        var json = RecapLayoutBuilder.Build(mode == OverlayMode.Pause ? Cards : new[] { Cards[0] }, mode).ToJsonString(OverlayJson.Options);

        var v = LayoutValidator.Validate(json, "Jellycap");

        Assert.Equal(mode == OverlayMode.Pause ? "Jellycap/recap" : "Jellycap/recap-card", v.Id);
        Assert.Equal("passive", v.Focus);
    }

    private static void Walk(JsonNode? n, List<JsonObject> texts)
    {
        if (n is not JsonObject o)
        {
            return;
        }

        if (o["type"]?.GetValue<string>() == "text")
        {
            texts.Add(o);
        }

        if (o["children"] is JsonArray c)
        {
            foreach (var x in c)
            {
                Walk(x, texts);
            }
        }
    }
}

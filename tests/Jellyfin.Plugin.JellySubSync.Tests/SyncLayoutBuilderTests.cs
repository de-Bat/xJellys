using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellySubSync.Overlay;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
using Xunit;

namespace Jellyfin.Plugin.JellySubSync.Tests;

public class SyncLayoutBuilderTests
{
    /// <summary>Round trip, so numbers read back as any numeric type (as the host and app see them).</summary>
    private static JsonObject Rt(JsonObject o) => JsonNode.Parse(o.ToJsonString())!.AsObject();

    private static string[] Strings(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    private static IEnumerable<JsonObject> Walk(JsonNode? n)
    {
        if (n is not JsonObject o)
        {
            yield break;
        }

        yield return o;
        if (o["children"] is JsonArray kids)
        {
            foreach (var k in kids)
            {
                foreach (var d in Walk(k))
                {
                    yield return d;
                }
            }
        }
    }

    private static IEnumerable<string> Texts(JsonObject layout) =>
        Walk(layout["root"]).Where(o => o["type"]?.GetValue<string>() == "text").Select(o => o["text"]!.GetValue<string>());

    /// <summary>The app's ValueState.display rule (value/scale, decimals, leading + when signed, suffix).</summary>
    private static string Display(JsonObject v, double value)
    {
        var s = (value / v["scale"]!.GetValue<double>()).ToString("F" + v["decimals"]!.GetValue<int>(), CultureInfo.InvariantCulture);
        if (s.StartsWith('-') && s.Trim('-', '0', '.').Length == 0)
        {
            s = s[1..];
        }

        if (v["signed"]!.GetValue<bool>() && !s.StartsWith('-'))
        {
            s = "+" + s;
        }

        return s + v["suffix"]!.GetValue<string>();
    }

    [Fact]
    public void Box_KeysMatchToday()
    {
        var keys = Rt(SyncLayoutBuilder.Box("English (SRT)", 0, 600000))["keys"]!.AsObject();

        void Step(string key, int by)
        {
            Assert.Equal("step", keys[key]!["op"]!.GetValue<string>());
            Assert.Equal("offset", keys[key]!["target"]!.GetValue<string>());
            Assert.Equal(by, keys[key]!["by"]!.GetValue<int>());
        }

        Step("left", -250);
        Step("right", 250);
        Step("down", -1000);
        Step("up", 1000);
        foreach (var k in new[] { "ok", "play" })
        {
            Assert.Equal("action", keys[k]!["op"]!.GetValue<string>());
            Assert.Equal("apply", keys[k]!["action"]!.GetValue<string>());
        }

        Assert.Null(keys["back"]); // back closes the box (app default)
    }

    [Fact]
    public void Box_ValueFormatting()
    {
        var box = Rt(SyncLayoutBuilder.Box("English (SRT)", 250, 600000));
        var v = Walk(box["root"]).Single(o => o["type"]?.GetValue<string>() == "value");

        Assert.Equal("offset", v["id"]!.GetValue<string>());
        Assert.Equal(250, v["value"]!.GetValue<long>());
        Assert.Equal(1000, v["scale"]!.GetValue<int>());
        Assert.Equal(2, v["decimals"]!.GetValue<int>());
        Assert.Equal(" s", v["suffix"]!.GetValue<string>());
        Assert.True(v["signed"]!.GetValue<bool>());
        Assert.Equal(-600000, v["min"]!.GetValue<long>());
        Assert.Equal(600000, v["max"]!.GetValue<long>());
        Assert.Equal("+0.25 s", Display(v, 250));
        Assert.Equal("-1.00 s", Display(v, -1000));
        Assert.Equal("+0.00 s", Display(v, 0));
    }

    [Fact]
    public void Box_ClampsOffset_AndSmallMaxIsOneStep()
    {
        var v = Walk(Rt(SyncLayoutBuilder.Box("x", 900000, 600000))["root"]).Single(o => o["type"]?.GetValue<string>() == "value");
        Assert.Equal(600000, v["value"]!.GetValue<long>());

        // as SyncBox: max is at least one big step
        var small = Walk(Rt(SyncLayoutBuilder.Box("x", -5000, 10))["root"]).Single(o => o["type"]?.GetValue<string>() == "value");
        Assert.Equal(1000, small["max"]!.GetValue<long>());
        Assert.Equal(-1000, small["value"]!.GetValue<long>());
    }

    [Fact]
    public void Box_Fields_Hint_AndTrack()
    {
        var box = Rt(SyncLayoutBuilder.Box("English (SRT)", 0, 600000));

        Assert.Equal("box", box["id"]!.GetValue<string>());
        Assert.Equal("interactive", box["focus"]!.GetValue<string>());
        Assert.Equal(15000, box["ttlMs"]!.GetValue<int>());
        Assert.True(box["ttlResetOnKey"]!.GetValue<bool>());
        Assert.Equal(new[] { "resume", "stop", "itemStart" }, Strings(box["closeOn"]));
        var texts = Texts(box).ToList();
        Assert.Contains("Subtitle sync", texts);
        Assert.Contains("English (SRT)", texts);
        Assert.Contains("▲▼ 1 s   ◀▶ 0.25 s   OK apply and play   BACK close", texts);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 6000)]
    public void Message_PassiveStatusOnly(bool busy, int ttl)
    {
        var m = Rt(SyncLayoutBuilder.Message("Syncing subtitles…", busy));

        Assert.Equal("box", m["id"]!.GetValue<string>());
        Assert.Equal("passive", m["focus"]!.GetValue<string>());
        Assert.Equal(ttl, m["ttlMs"]!.GetValue<int>());
        Assert.Equal(new[] { "itemStart" }, Strings(m["closeOn"]));
        Assert.Null(m["keys"]);
        Assert.Contains("Syncing subtitles…", Texts(m));
        Assert.DoesNotContain(Walk(m["root"]), o => o["type"]?.GetValue<string>() == "value");
    }

    [Fact]
    public void BoxAndMessage_PassLayoutValidator()
    {
        var box = LayoutValidator.Validate(SyncLayoutBuilder.Box("עברית", 250, 600000).ToJsonString(), "JellySubSync");
        Assert.Equal("JellySubSync/box", box.Id);
        Assert.Equal(15000, box.TtlMs);
        Assert.Equal(new[] { "resume", "stop", "itemStart" }, box.CloseOn);

        var msg = LayoutValidator.Validate(SyncLayoutBuilder.Message("Pick the \"Synced\" track in the player's subtitle menu.", false, failed: true).ToJsonString(), "JellySubSync");
        Assert.Equal("JellySubSync/box", msg.Id);
        Assert.Equal("passive", msg.Focus);
        Assert.Equal(new[] { "itemStart" }, msg.CloseOn);
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(250, "+0.25 s")]
    [InlineData(-500, "−0.5 s")]
    [InlineData(1250, "+1.25 s")]
    [InlineData(3000, "+3 s")]
    [InlineData(-1000, "−1 s")]
    public void Format_AsSyncBox(long ms, string expected) => Assert.Equal(expected, SyncLayoutBuilder.Format(ms));
}

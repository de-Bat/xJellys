using System.Text.Json;
using Jellyfin.Plugin.JellyXR;
using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class SceneContextTests
{
    private static XrayarrTimeline Timeline(string? kind = "episode", int? season = 2, int? episode = 5) => new()
    {
        Status = "ready",
        Kind = kind,
        Season = season,
        Episode = episode,
        Segments =
        {
            new XrayarrSegment { Index = 0, StartMs = 0, EndMs = 60_000 },
            new XrayarrSegment { Index = 1, StartMs = 60_000, EndMs = 750_000 },
            new XrayarrSegment { Index = 2, StartMs = 750_000, EndMs = 845_000 },
        },
    };

    [Fact]
    public void Episode_And_Scene()
    {
        Assert.Equal("S02E05 · Scene 3 of 3 · 12:30–14:05", CardService.Context(1, 1, Timeline(), 800_000));
    }

    [Fact]
    public void JellyfinNumbers_WhenXrayarrHasNone()
    {
        Assert.Equal("S01E03 · Scene 2 of 3 · 1:00–12:30", CardService.Context(1, 3, Timeline(kind: null, season: null, episode: null), 60_000));
    }

    [Fact]
    public void Movie_SceneOnly()
    {
        Assert.Equal("Scene 1 of 3 · 0:00–1:00", CardService.Context(null, null, Timeline(kind: "movie", season: null, episode: null), 5_000));
    }

    [Fact]
    public void BetweenScenes_OrNoXrayarr()
    {
        Assert.Equal("S02E05", CardService.Context(null, null, Timeline(), 900_000));
        Assert.Equal("S01E03", CardService.Context(1, 3, null, 0));
        Assert.Null(CardService.Context(null, null, null, 0));
    }

    [Fact]
    public void OldServer_WithoutIndex_NumbersByOrder()
    {
        var tl = new XrayarrTimeline
        {
            Status = "ready",
            Segments = { new XrayarrSegment { StartMs = 10_000, EndMs = 20_000 }, new XrayarrSegment { StartMs = 0, EndMs = 10_000 } },
        };
        Assert.Equal(2, tl.SceneAt(15_000)!.Value.Number);
        Assert.Equal(2, tl.SceneCount);
    }

    [Fact]
    public void Parses_XrayarrTimelineJson()
    {
        const string json = """
            {"status":"ready","stale":false,"titleId":4,"title":"Show","kind":"episode","season":1,"episode":3,"year":2020,
             "durationMs":845000,"segments":[{"index":7,"startMs":1000,"endMs":2000,"cast":[{"name":"Ada","tmdbPersonId":9}]}]}
            """;
        var tl = JsonSerializer.Deserialize<XrayarrTimeline>(json)!;
        Assert.Equal(("episode", 1, 3, 2020, 845000L), (tl.Kind, tl.Season, tl.Episode, tl.Year, tl.DurationMs!.Value));
        Assert.Equal(7, tl.Segments[0].Index);
        Assert.Equal(8, tl.SceneAt(1500)!.Value.Number);
        Assert.Equal(8, tl.SceneCount);
    }
}

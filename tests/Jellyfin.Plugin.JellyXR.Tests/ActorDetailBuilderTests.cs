using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
using Jellyfin.Plugin.JellyXR.Overlay;
using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class ActorDetailBuilderTests
{
    private static readonly Guid PersonId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static PersonInfo Person(string name = "Ada Example", string? bio = "A long biography.", DateTime? born = null, string? place = null) =>
        new(PersonId, name, bio, born, place);

    private static LibraryTitle Movie(string name, int year, int minutes, string overview = "A movie.") =>
        new(Guid.NewGuid(), name, year, "Movie", TimeSpan.FromMinutes(minutes).Ticks, null, overview, new DateTime(year, 1, 1));

    private static LibraryTitle Show(string name, int year, int seasons) =>
        new(Guid.NewGuid(), name, year, "Series", null, seasons, "A show.", new DateTime(year, 1, 1));

    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        if (node is not JsonObject o)
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

    private static string? Str(JsonNode? n) => n?.GetValue<string>();

    private static List<JsonObject> OfType(JsonObject layout, string type) => Walk(layout["root"]).Where(n => Str(n["type"]) == type).ToList();

    [Fact]
    public void Build_HasParentCastAndInteractive()
    {
        var layout = ActorDetailBuilder.Build(Person(born: new DateTime(1985, 4, 30), place: "Rosh HaAyin"), new[] { Movie("M", 2019, 112) });

        Assert.Equal(1, layout["v"]!.GetValue<int>());
        Assert.Equal("actor:" + PersonId.ToString("N"), Str(layout["id"]));
        Assert.Equal("cast", Str(layout["parent"]));
        Assert.Equal("interactive", Str(layout["focus"]));
        Assert.Equal(0.55, layout["dim"]!.GetValue<double>(), 3);
        Assert.Equal(new[] { "resume", "stop", "itemStart" }, layout["closeOn"]!.AsArray().Select(Str));
        var root = layout["root"]!.AsObject();
        Assert.Equal(0.06, root["x"]!.GetValue<double>(), 3);
        Assert.Equal(0.06, root["y"]!.GetValue<double>(), 3);
        Assert.Equal(0.88, root["w"]!.GetValue<double>(), 3);
        Assert.Equal(0.88, root["h"]!.GetValue<double>(), 3);
        Assert.Equal("#E0000000", Str(root["bg"]));

        var texts = OfType(layout, "text").Select(t => Str(t["text"])).ToList();
        Assert.Contains("1985 · Rosh HaAyin", texts);
        var name = OfType(layout, "text").Single(t => Str(t["text"]) == "Ada Example");
        Assert.Equal(0.045, name["size"]!.GetValue<double>(), 4);
        Assert.True(name["bold"]!.GetValue<bool>());
        var bio = OfType(layout, "text").Single(t => Str(t["text"]) == "A long biography.");
        Assert.True(bio["scroll"]!.GetValue<bool>());
        Assert.True(bio["focusable"]!.GetValue<bool>());
        Assert.Equal(8, bio["maxLines"]!.GetValue<int>());
        Assert.NotNull(Str(bio["id"]));
        Assert.Contains(OfType(layout, "image"), i => Str(i["src"])!.StartsWith($"/Items/{PersonId:N}/Images/Primary", StringComparison.Ordinal));

        // the host accepts it, namespacing the id and the parent
        var v = LayoutValidator.Validate(layout.ToJsonString(), "JellyXR");
        Assert.Equal("JellyXR/actor:" + PersonId.ToString("N"), v.Id);
        Assert.Equal("JellyXR/cast", v.Parent);
        Assert.Equal("interactive", v.Focus);
    }

    [Fact]
    public void Build_BornLine_OmittedWhenUnknown_PartialOtherwise()
    {
        var none = OfType(ActorDetailBuilder.Build(Person(), Array.Empty<LibraryTitle>()), "text").Select(t => Str(t["text"]));
        Assert.DoesNotContain(none, t => t!.Contains('·'));
        var yearOnly = OfType(ActorDetailBuilder.Build(Person(born: new DateTime(1970, 1, 2)), Array.Empty<LibraryTitle>()), "text").Select(t => Str(t["text"]));
        Assert.Contains("1970", yearOnly);
        var placeOnly = OfType(ActorDetailBuilder.Build(Person(place: "Haifa"), Array.Empty<LibraryTitle>()), "text").Select(t => Str(t["text"]));
        Assert.Contains("Haifa", placeOnly);
    }

    [Fact]
    public void Build_DescriptionPerTitle_ShowForPoster()
    {
        var longOverview = new string('x', 300);
        var m = Movie("Example Movie", 2019, 112, longOverview);
        var s = Show("Example Show", 2015, 3);
        var one = Show("Short Show", 2012, 1);
        var layout = ActorDetailBuilder.Build(Person(), new[] { m, s, one });

        var list = OfType(layout, "list").Single();
        Assert.Equal("horizontal", Str(list["orientation"]));
        var posters = list["children"]!.AsArray().Select(p => p!.AsObject()).ToList();
        Assert.Equal(3, posters.Count);
        foreach (var (poster, title) in posters.Zip(new[] { m, s, one }))
        {
            Assert.Equal("image", Str(poster["type"]));
            Assert.True(poster["focusable"]!.GetValue<bool>());
            Assert.Equal("title:" + title.Id.ToString("N"), Str(poster["action"]));
            Assert.Equal($"/Items/{title.Id:N}/Images/Primary?maxHeight=300", Str(poster["src"]));
            Assert.NotNull(Str(poster["id"]));

            var desc = Walk(layout["root"]).Single(n => Str(n["id"]) == "desc:" + title.Id.ToString("N"));
            Assert.Equal(Str(poster["id"]), Str(desc["showFor"]));
        }

        // the descriptions sit together in one stack, under the list
        var stack = Walk(layout["root"]).Single(n => Str(n["type"]) == "stack" && n["children"] is JsonArray a
            && a.All(c => Str(c?["id"])?.StartsWith("desc:", StringComparison.Ordinal) == true));
        Assert.Equal(3, stack["children"]!.AsArray().Count);

        var texts = OfType(layout, "text").Select(t => Str(t["text"])).ToList();
        Assert.Contains("Example Movie (2019) · 1h 52m", texts);
        Assert.Contains("Example Show (2015) · 3 seasons", texts);
        Assert.Contains("Short Show (2012) · 1 season", texts);
        var overview = texts.Single(t => t!.StartsWith("xxx", StringComparison.Ordinal))!;
        Assert.True(overview.TrimEnd('…').Length <= 160);
        Assert.Contains("In your library", texts);
    }

    [Fact]
    public void Build_PlayDisabled()
    {
        var layout = ActorDetailBuilder.Build(Person(), new[] { Movie("A", 2001, 90), Show("B", 2003, 2) });

        var buttons = OfType(layout, "button");
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, b =>
        {
            Assert.Equal("Play", Str(b["label"]));
            Assert.False(b["enabled"]!.GetValue<bool>());
        });
    }

    [Fact]
    public void NoTitlesNoBio_RendersPlaceholder()
    {
        var layout = ActorDetailBuilder.Build(Person("גל גדות", bio: null), Array.Empty<LibraryTitle>());

        Assert.Empty(OfType(layout, "list"));
        var texts = OfType(layout, "text");
        Assert.Contains(texts, t => Str(t["text"]) == "No other titles in your library");
        Assert.Contains(texts, t => Str(t["text"]) == "גל גדות");
        Assert.DoesNotContain(texts, t => t["scroll"]?.GetValue<bool>() == true);
        Assert.All(texts, t => Assert.Equal("auto", Str(t["dir"])));
        Assert.DoesNotContain(Walk(layout["root"]), n => n["showFor"] is not null);
        LayoutValidator.Validate(layout.ToJsonString(), "JellyXR");
    }
    [Fact]
    public void Build_LongBio_CutAtWordWithEllipsis()
    {
        var words = string.Concat(Enumerable.Repeat("שלום world ", 400)); // 4400 chars
        var layout = ActorDetailBuilder.Build(Person(bio: words), Array.Empty<LibraryTitle>());

        var bio = OfType(layout, "text").Single(t => t["scroll"]?.GetValue<bool>() == true);
        var text = Str(bio["text"])!;
        Assert.True(text.Length <= ActorDetailBuilder.MaxBioChars + 1);
        Assert.EndsWith("…", text);
        var body = text.TrimEnd('…');
        Assert.True(body.EndsWith("שלום", StringComparison.Ordinal) || body.EndsWith("world", StringComparison.Ordinal), body[^12..]);
        Assert.Equal(1500, ActorDetailBuilder.MaxBioChars);

        // a single huge word: cut at the limit, never inside a surrogate pair
        var emoji = string.Concat(Enumerable.Repeat("😀", 1000));
        var cut = Str(OfType(ActorDetailBuilder.Build(Person(bio: emoji), Array.Empty<LibraryTitle>()), "text").Single(t => t["scroll"] is not null)["text"])!;
        Assert.False(char.IsHighSurrogate(cut.TrimEnd('…')[^1]));

        // short bios are unchanged
        Assert.Contains(OfType(ActorDetailBuilder.Build(Person(bio: "Short."), Array.Empty<LibraryTitle>()), "text"), t => Str(t["text"]) == "Short.");
    }
}

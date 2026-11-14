using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
using Jellyfin.Plugin.JellyXR;
using Jellyfin.Plugin.JellyXR.Overlay;
using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class CastLayoutBuilderTests
{
    private static Card Person(string name, bool onScreen = false, bool withId = true, string? character = "Role")
    {
        Guid? id = withId ? Guid.NewGuid() : null;
        return new Card(name, character, null, onScreen, id, id is { } g ? $"/Items/{g:N}/Images/Primary?maxHeight=128" : null);
    }

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

    private static JsonObject PagesOf(JsonObject layout) => Walk(layout["root"]).Single(n => Str(n["type"]) == "pages");

    // person rows carry ids p0..pn; the header row has none
    private static List<JsonObject> PersonRows(JsonNode page) =>
        Walk(page).Where(n => Str(n["type"]) == "row" && Str(n["id"]) is { } id && id.StartsWith('p')).ToList();

    /// <summary>The row's first text is the actor name.</summary>
    private static string? NameOf(JsonObject row) => Walk(row).Where(n => Str(n["type"]) == "text").Select(n => Str(n["text"])).FirstOrDefault();

    [Fact]
    public void Build_Basics()
    {
        var layout = CastLayoutBuilder.Build(new CardSet("Cast", new[] { Person("Ada") }), interactive: false);

        Assert.Equal(1, layout["v"]!.GetValue<int>());
        Assert.Equal("cast", Str(layout["id"]));
        Assert.Equal(new[] { "resume", "stop", "itemStart" }, layout["closeOn"]!.AsArray().Select(Str));
        var root = layout["root"]!.AsObject();
        Assert.Equal(0.62, root["x"]!.GetValue<double>(), 3);
        Assert.Equal(0.36, root["w"]!.GetValue<double>(), 3);
        Assert.Equal(8000, PagesOf(layout)["intervalMs"]!.GetValue<int>());
    }

    [Fact]
    public void Build_Passive_NoFocusable()
    {
        var set = new CardSet("Cast", new[] { Person("Ada", true), Person("Bob"), Person("Cy", withId: false) });
        var layout = CastLayoutBuilder.Build(set, interactive: false);

        Assert.Equal("passive", Str(layout["focus"]));
        Assert.DoesNotContain(Walk(layout["root"]), n => n["focusable"]?.GetValue<bool>() == true);
        Assert.DoesNotContain(Walk(layout["root"]), n => n["action"] is not null);
    }

    [Fact]
    public void Build_Interactive_RowsFocusableWithActorAction()
    {
        var ada = Person("Ada", true);
        var cy = Person("Cy", withId: false);
        var layout = CastLayoutBuilder.Build(new CardSet("Cast", new[] { ada, cy }), interactive: true);

        Assert.Equal("interactive", Str(layout["focus"]));
        var rows = PersonRows(PagesOf(layout));
        Assert.Equal(2, rows.Count);
        var adaRow = rows.Single(r => NameOf(r) == "Ada");
        Assert.True(adaRow["focusable"]!.GetValue<bool>());
        Assert.Equal($"actor:{ada.PersonId:N}", Str(adaRow["action"]));
        Assert.False(string.IsNullOrEmpty(Str(adaRow["id"])));

        var cyRow = rows.Single(r => NameOf(r) == "Cy");
        Assert.Null(cyRow["focusable"]);
        Assert.Null(cyRow["action"]);

        // ids are unique so the app can register every focusable row
        var ids = Walk(layout["root"]).Select(n => Str(n["id"])).Where(i => i is not null).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // interactive pages flip only with left/right, never under the viewer's focus
        Assert.Equal(0, PagesOf(layout)["intervalMs"]!.GetValue<int>());
    }

    [Fact]
    public void Build_RowHasRoundImageNameAndCharacter()
    {
        var ada = Person("Ada", true, character: "Queen");
        var layout = CastLayoutBuilder.Build(new CardSet("Cast", new[] { ada, Person("Cy", withId: false) }), interactive: false);
        var rows = PersonRows(PagesOf(layout));

        var adaNodes = Walk(rows[0]).ToList();
        var img = adaNodes.Single(n => Str(n["type"]) == "image");
        Assert.Equal($"/Items/{ada.PersonId:N}/Images/Primary?maxHeight=128", Str(img["src"]));
        Assert.True(img["round"]!.GetValue<bool>());
        var texts = adaNodes.Where(n => Str(n["type"]) == "text").ToList();
        Assert.Contains(texts, t => Str(t["text"]) == "Ada" && t["bold"]!.GetValue<bool>());
        Assert.Contains(texts, t => Str(t["text"]) == "Queen");

        Assert.DoesNotContain(Walk(rows[1]), n => Str(n["type"]) == "image");
    }

    [Fact]
    public void Build_OnScreenFirst()
    {
        var set = new CardSet("On screen", new[] { Person("Rest1"), Person("On1", true), Person("Rest2"), Person("On2", true) });
        var layout = CastLayoutBuilder.Build(set, interactive: false);

        var names = PersonRows(PagesOf(layout)).Select(r => NameOf(r)).ToList();
        Assert.Equal(new[] { "On1", "On2", "Rest1", "Rest2" }, names);
        var texts = Walk(layout["root"]).Where(n => Str(n["type"]) == "text").Select(n => Str(n["text"])).ToList();
        Assert.Contains("On screen", texts);
        Assert.Contains("ALSO IN CAST", texts);
    }

    [Fact]
    public void Build_ManyPeople_Paginates()
    {
        // CastPanel on a 540 dp tall TV: 416 dp per page, 74 dp per big row -> 5 on-screen people per page.
        var set = new CardSet("Cast", Enumerable.Range(1, 12).Select(i => Person("P" + i, true)).ToList());
        var pages = PagesOf(CastLayoutBuilder.Build(set, interactive: false))["children"]!.AsArray();

        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { 5, 5, 2 }, pages.Select(p => PersonRows(p!).Count));
        // header per page with its number
        var firstTexts = Walk(pages[0]).Where(n => Str(n["type"]) == "text").Select(n => Str(n["text"])).ToList();
        Assert.Contains("On screen", firstTexts);
        Assert.Contains("1/3", firstTexts);
    }

    [Fact]
    public void Build_SplitSections_SmallRestRows_Paginates()
    {
        // 2 on screen (74 each) + section (34) + small rows (54 each): 148 + 88 + 3*54 = 398 on page 1, the rest on page 2.
        var cards = new List<Card> { Person("On1", true), Person("On2", true) };
        cards.AddRange(Enumerable.Range(1, 10).Select(i => Person("R" + i)));
        var pages = PagesOf(CastLayoutBuilder.Build(new CardSet("On screen", cards), interactive: false))["children"]!.AsArray();

        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 6, 6 }, pages.Select(p => PersonRows(p!).Count));
        var secondTexts = Walk(pages[1]).Where(n => Str(n["type"]) == "text").Select(n => Str(n["text"])).ToList();
        Assert.Contains("Also in cast", secondTexts);
        Assert.Contains("2/2", secondTexts);
    }

    [Fact]
    public void Build_SinglePage_NoCounter()
    {
        var pages = PagesOf(CastLayoutBuilder.Build(new CardSet("Cast", new[] { Person("Ada") }), interactive: false))["children"]!.AsArray();
        Assert.Single(pages);
        var texts = Walk(pages[0]).Where(n => Str(n["type"]) == "text").Select(n => Str(n["text"])).ToList();
        Assert.Contains("Cast", texts);
        Assert.DoesNotContain("1/1", texts);
    }

    [Fact]
    public void Build_TextDirAuto()
    {
        var set = new CardSet("Cast", new[] { Person("גל גדות", true, character: "דיאנה"), Person("Bob") });
        var layout = CastLayoutBuilder.Build(set, interactive: true);

        var texts = Walk(layout["root"]).Where(n => Str(n["type"]) == "text").ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.Equal("auto", Str(t["dir"])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_PassesHostValidator(bool interactive)
    {
        var cards = Enumerable.Range(1, 9).Select(i => Person("P" + i, i < 3, withId: i % 2 == 0)).ToList();
        var json = CastLayoutBuilder.Build(new CardSet("On screen", cards), interactive).ToJsonString();

        var v = LayoutValidator.Validate(json, "JellyXR");

        Assert.Equal("JellyXR/cast", v.Id);
        Assert.Equal(interactive ? "interactive" : "passive", v.Focus);
        Assert.Contains("resume", v.CloseOn);
    }

    [Fact]
    public void Build_PassthroughFocus()
    {
        // R16: the panel opened from a single pause offer leaves the player keys (play, seek) to the player
        var ada = Person("Ada");
        var layout = CastLayoutBuilder.Build(new CardSet("Cast", new[] { ada, Person("Cy") }), CastLayoutBuilder.FocusPassthrough);

        Assert.Equal("interactive+passthrough", Str(layout["focus"]));
        Assert.Equal(0, PagesOf(layout)["intervalMs"]!.GetValue<int>());
        var row = PersonRows(PagesOf(layout)).First();
        Assert.True(row["focusable"]!.GetValue<bool>());
        Assert.Equal(ActorDetailBuilder.IdPrefix + ada.PersonId!.Value.ToString("N"), Str(row["action"]));

        var v = LayoutValidator.Validate(layout.ToJsonString(), "JellyXR");
        Assert.Equal("interactive+passthrough", v.Focus);
    }

    [Fact]
    public void RowHeights_AreFractionsOfThePanelInnerBox()
    {
        // layout v1: h is a fraction of the parent's inner box. A 64 dp row (540 dp screen) must come out 64 dp
        // tall on the device, with its round image 60 dp, not 64 dp of the screen applied to the panel again.
        var set = new CardSet("Cast", new[] { Person("A"), Person("B"), Person("C") });
        var layout = CastLayoutBuilder.Build(set, interactive: true);
        var root = layout["root"]!.AsObject();
        var innerDp = (root["h"]!.GetValue<double>() * 540) - (2 * root["pad"]!.GetValue<double>() * 540);

        var rows = Walk(root).Where(n => Str(n["type"]) == "row" && n["id"] is not null).ToList();
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal(64, row["h"]!.GetValue<double>() * innerDp, 0);
            var image = Walk(row).Single(n => Str(n["type"]) == "image");
            Assert.Equal(60, image["h"]!.GetValue<double>() * 64, 0);
        }

        Assert.Equal("JellyXR/cast", LayoutValidator.Validate(layout.ToJsonString(), "JellyXR").Id);
    }
    [Fact]
    public void Context_IsALineUnderEachPageTitle_AndPassesValidator()
    {
        var cards = Enumerable.Range(0, 12).Select(i => Person("P" + i, onScreen: i < 3)).ToList();
        var set = new CardSet("Cast", cards, "S01E03 · Scene 12 of 85 · 12:30–14:05");
        var layout = CastLayoutBuilder.Build(set, interactive: false);

        var pages = PagesOf(layout)["children"]!.AsArray();
        Assert.All(pages, p => Assert.Contains(Walk(p), n => Str(n["text"]) == set.Context));
        Assert.True(layout["root"]!["h"]!.GetValue<double>() <= 0.9);
        Assert.Equal("JellyXR/cast", LayoutValidator.Validate(layout.ToJsonString(), "JellyXR").Id);
    }

    [Fact]
    public void Context_LeavesLessRoomForRows()
    {
        var cards = Enumerable.Range(0, 20).Select(i => Person("P" + i, onScreen: true)).ToList();
        var plain = CastLayoutBuilder.Paginate(new CardSet("Cast", cards));
        var withCtx = CastLayoutBuilder.Paginate(new CardSet("Cast", cards, "Scene 1 of 9"));
        Assert.True(withCtx[0].Entries.Count <= plain[0].Entries.Count);
        Assert.Equal(20, withCtx.Sum(p => p.Entries.OfType<Card>().Count()));
    }
}

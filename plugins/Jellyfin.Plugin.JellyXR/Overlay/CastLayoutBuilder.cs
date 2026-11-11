using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.JellyXR.Overlay;

/// <summary>
/// The pause cast panel as a JellyTvOverlay layout (v1): a right-hand panel with one page per screenful,
/// on-screen people first. Paging and sizes follow the JellyXR TV app's CastPanel on a typical 540 dp tall TV.
/// </summary>
public static class CastLayoutBuilder
{
    public const string LayoutId = "cast";

    /// <summary>Layout focus modes the panel is built with.</summary>
    public const string FocusPassive = "passive", FocusInteractive = "interactive", FocusPassthrough = "interactive+passthrough";

    // CastPanel sizes in dp, and the 540 dp screen height they are measured against (1080p at density 2).
    private const double ScreenDp = 540;
    private const int Big = 64, Small = 44, Section = 34, Gap = 10;
    private const int PadDp = 14, HeaderDp = 26;
    private static readonly int Budget = Math.Max(Big * 3, (int)ScreenDp - 56 - 28 - 40);

    private const double PanelX = 0.62, PanelW = 0.36, PanelY = 0.05, PanelMaxH = 0.9;

    /// <summary>One page: its title and its entries (a section label string or a card).</summary>
    public sealed record Page(string Title, IReadOnlyList<object> Entries);

    public static JsonObject Build(CardSet set, bool interactive, int pageMs = 8000) =>
        Build(set, interactive ? FocusInteractive : FocusPassive, pageMs);

    /// <summary>
    /// focus: <see cref="FocusPassive"/> (auto-flipping pages), <see cref="FocusInteractive"/> (rows selectable,
    /// pages flip with left/right) or <see cref="FocusPassthrough"/> (rows selectable, play and seek keys stay with
    /// the player; no key paging).
    /// </summary>
    public static JsonObject Build(CardSet set, string focus, int pageMs = 8000)
    {
        var interactive = focus is FocusInteractive or FocusPassthrough;
        var pages = Paginate(set);
        var split = set.Cards.Any(c => c.OnScreen) && set.Cards.Any(c => !c.OnScreen);
        var pageNodes = new JsonArray();
        var maxUsed = 0;
        var rowIndex = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            var (node, used) = PageNode(pages[i], i, pages.Count, split, interactive, ref rowIndex);
            pageNodes.Add(node);
            maxUsed = Math.Max(maxUsed, used);
        }

        var h = Math.Min(PanelMaxH, Frac((2 * PadDp) + HeaderDp + maxUsed));
        return new JsonObject
        {
            ["v"] = 1,
            ["id"] = LayoutId,
            ["focus"] = interactive ? focus : FocusPassive,
            ["closeOn"] = new JsonArray("resume", "stop", "itemStart"),
            ["root"] = new JsonObject
            {
                ["type"] = "stack",
                ["x"] = PanelX,
                ["y"] = PanelY,
                ["w"] = PanelW,
                ["h"] = Round(h),
                ["pad"] = Frac(PadDp),
                ["bg"] = "#DC0C0E12",
                ["radius"] = Frac(14),
                ["children"] = new JsonArray(new JsonObject
                {
                    ["type"] = "pages",
                    ["intervalMs"] = interactive ? 0 : Math.Max(0, pageMs),
                    ["children"] = pageNodes,
                }),
            },
        };
    }

    /// <summary>Same split as CastPanel.paginate: on screen first, a new page when the next row does not fit.</summary>
    public static List<Page> Paginate(CardSet set)
    {
        var on = set.Cards.Where(c => c.OnScreen).ToList();
        var rest = set.Cards.Where(c => !c.OnScreen).ToList();
        var split = on.Count > 0 && rest.Count > 0;
        var pages = new List<(string Title, List<object> Entries)>();
        List<object>? cur = null;
        string? section = null;
        var used = 0;
        foreach (var c in on.Concat(rest))
        {
            var sec = c.OnScreen ? "On screen" : on.Count == 0 ? (string.IsNullOrEmpty(set.Header) ? "Cast" : set.Header) : "Also in cast";
            var h = RowDp(c, split) + Gap;
            var newSection = sec != section;
            var need = h + (newSection && cur is { Count: > 0 } ? Section : 0);
            if (cur is null || used + need > Budget)
            {
                cur = new List<object>();
                pages.Add((sec, cur));
                used = 0;
                section = sec;
                newSection = false;
                need = h;
            }

            if (newSection)
            {
                cur.Add(sec);
                section = sec;
            }

            cur.Add(c);
            used += need;
        }

        return pages.Select(p => new Page(p.Title, p.Entries)).ToList();
    }

    private static int RowDp(Card c, bool split) => split && !c.OnScreen ? Small : Big;

    private static (JsonObject Node, int UsedDp) PageNode(Page page, int index, int count, bool split, bool interactive, ref int rowIndex)
    {
        var header = new JsonArray { Text(page.Title, 17, "#FFFFFFFF", bold: true) };
        if (count > 1)
        {
            header.Add(Text($"{index + 1}/{count}", 13, "#A0FFFFFF"));
        }

        var children = new JsonArray
        {
            new JsonObject { ["type"] = "row", ["gap"] = Frac(12), ["children"] = header },
        };
        var used = 0;
        foreach (var e in page.Entries)
        {
            if (e is string sec)
            {
                children.Add(new JsonObject { ["type"] = "stack", ["h"] = Frac(1), ["bg"] = "#28FFFFFF" });
                children.Add(Text(sec.ToUpperInvariant(), 12, "#AAFFFFFF"));
                used += Section;
                continue;
            }

            var card = (Card)e;
            children.Add(Row(card, split && !card.OnScreen, interactive, rowIndex++));
            used += RowDp(card, split) + Gap;
        }

        return (new JsonObject { ["type"] = "column", ["gap"] = Frac(Gap), ["children"] = children }, used);
    }

    private static JsonObject Row(Card c, bool small, bool interactive, int index)
    {
        var lines = new JsonArray { Text(c.Actor, small ? 14 : 16, "#FFFFFFFF", bold: true) };
        if (!string.IsNullOrEmpty(c.Character))
        {
            lines.Add(Text(c.Character, small ? 12 : 13, "#C3FFFFFF"));
        }

        if (!small && !string.IsNullOrEmpty(c.Info))
        {
            lines.Add(Text(c.Info, 11, "#96FFFFFF"));
        }

        var kids = new JsonArray();
        if (!string.IsNullOrEmpty(c.ImageUrl))
        {
            kids.Add(new JsonObject
            {
                ["type"] = "image",
                ["src"] = c.ImageUrl,
                ["fit"] = "cover",
                ["round"] = true,
                ["bg"] = "#FF3A404E",
                ["h"] = Frac((small ? Small : Big) - 4),
            });
        }

        kids.Add(new JsonObject { ["type"] = "column", ["children"] = lines });
        var row = new JsonObject
        {
            ["type"] = "row",
            ["id"] = "p" + index,
            ["gap"] = Frac(12),
            ["children"] = kids,
        };
        if (interactive && c.PersonId is { } id)
        {
            row["focusable"] = true;
            row["action"] = "actor:" + id.ToString("N");
        }

        return row;
    }

    private static JsonObject Text(string text, int sp, string color, bool bold = false) => new()
    {
        ["type"] = "text",
        ["text"] = text,
        ["size"] = Frac(sp),
        ["color"] = color,
        ["bold"] = bold,
        ["maxLines"] = 1,
        ["dir"] = "auto",
    };

    /// <summary>dp on the 540 dp reference screen as a fraction of the screen height.</summary>
    private static double Frac(double dp) => Round(dp / ScreenDp);

    private static double Round(double v) => Math.Round(v, 4);
}

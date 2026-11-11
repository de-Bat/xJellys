using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.JellyXR.Overlay;

/// <summary>
/// The actor detail ("big card", layout B) as a JellyTvOverlay layout (v1): a child of the cast panel with the
/// portrait, name, born line and scrollable bio on top, and the person's other titles in the user's library
/// below as a poster row with a description of the focused poster (format field <c>showFor</c>).
/// </summary>
public static class ActorDetailBuilder
{
    public const string IdPrefix = "actor:";
    public const string TitleActionPrefix = "title:";
    public const int OverviewChars = 160;

    /// <summary>Longest bio kept (cut at a word, with an ellipsis): bounds the preloaded payload.</summary>
    public const int MaxBioChars = 1500;

    // Two stacked sections inside the card. The library section comes first in document order so the first
    // poster takes the initial focus (its description shows at once); it is drawn lower down by its y.
    private const double TopH = 0.43, LibY = 0.46, LibH = 0.54;

    public static string LayoutIdOf(Guid personId) => IdPrefix + personId.ToString("N");

    public static JsonObject Build(PersonInfo p, IReadOnlyList<LibraryTitle> titles)
    {
        return new JsonObject
        {
            ["v"] = 1,
            ["id"] = LayoutIdOf(p.Id),
            ["parent"] = CastLayoutBuilder.LayoutId,
            ["focus"] = "interactive",
            ["dim"] = 0.55,
            ["closeOn"] = new JsonArray("resume", "stop", "itemStart"),
            ["root"] = new JsonObject
            {
                ["type"] = "stack",
                ["x"] = 0.06,
                ["y"] = 0.06,
                ["w"] = 0.88,
                ["h"] = 0.88,
                ["pad"] = 0.03,
                ["bg"] = "#E0000000",
                ["radius"] = 0.02,
                ["children"] = new JsonArray(Library(titles), PersonSection(p)),
            },
        };
    }

    private static JsonObject PersonSection(PersonInfo p)
    {
        var lines = new JsonArray { Text(p.Name, 0.045, "#FFFFFFFF", bold: true, maxLines: 1) };
        if (BornLine(p) is { } born)
        {
            lines.Add(Text(born, 0.026, "#B0FFFFFF", maxLines: 1));
        }

        if (!string.IsNullOrWhiteSpace(p.Bio))
        {
            var bio = Text(Bio(p.Bio), 0.024, "#E6FFFFFF", maxLines: 8);
            bio["id"] = "bio";
            bio["scroll"] = true;
            bio["focusable"] = true;
            lines.Add(bio);
        }

        return new JsonObject
        {
            ["type"] = "row",
            ["x"] = 0,
            ["y"] = 0,
            ["w"] = 1,
            ["h"] = TopH,
            ["gap"] = 0.03,
            ["children"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "image",
                    ["id"] = "portrait",
                    ["src"] = $"/Items/{p.Id:N}/Images/Primary?maxHeight=400",
                    ["fit"] = "cover",
                    ["radius"] = 0.012,
                    ["bg"] = "#FF3A404E",
                    ["w"] = 0.15,
                    ["h"] = 1,
                },
                new JsonObject { ["type"] = "column", ["w"] = 0.82, ["gap"] = 0.01, ["children"] = lines }),
        };
    }

    private static JsonObject Library(IReadOnlyList<LibraryTitle> titles)
    {
        var children = new JsonArray { Text("In your library", 0.032, "#FFFFFFFF", bold: true, maxLines: 1) };
        if (titles.Count == 0)
        {
            children.Add(Text("No other titles in your library", 0.026, "#AAFFFFFF", maxLines: 1));
        }
        else
        {
            var posters = new JsonArray();
            var descs = new JsonArray();
            foreach (var t in titles)
            {
                var n = t.Id.ToString("N");
                posters.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["id"] = "poster:" + n,
                    ["src"] = $"/Items/{n}/Images/Primary?maxHeight=300",
                    ["fit"] = "cover",
                    ["radius"] = 0.008,
                    ["bg"] = "#33FFFFFF",
                    ["w"] = 0.1,
                    ["h"] = 1,
                    ["focusable"] = true,
                    ["action"] = TitleActionPrefix + n,
                });
                descs.Add(Description(t, "poster:" + n));
            }

            children.Add(new JsonObject
            {
                ["type"] = "list",
                ["id"] = "titles",
                ["orientation"] = "horizontal",
                ["h"] = 0.46,
                ["gap"] = 0.012,
                ["children"] = posters,
            });
            children.Add(new JsonObject { ["type"] = "stack", ["children"] = descs });
        }

        return new JsonObject
        {
            ["type"] = "column",
            ["x"] = 0,
            ["y"] = LibY,
            ["w"] = 1,
            ["h"] = LibH,
            ["gap"] = 0.012,
            ["children"] = children,
        };
    }

    private static JsonObject Description(LibraryTitle t, string posterId)
    {
        var kids = new JsonArray { Text(Headline(t), 0.028, "#FFFFFFFF", bold: true, maxLines: 1) };
        if (Overview(t.Overview) is { } o)
        {
            kids.Add(Text(o, 0.022, "#C8FFFFFF", maxLines: 2));
        }

        kids.Add(new JsonObject
        {
            ["type"] = "button",
            ["label"] = "Play",
            ["icon"] = "play",
            ["size"] = 0.022,
            ["enabled"] = false,
        });
        return new JsonObject
        {
            ["type"] = "column",
            ["id"] = "desc:" + t.Id.ToString("N"),
            ["showFor"] = posterId,
            ["gap"] = 0.008,
            ["children"] = kids,
        };
    }

    /// <summary>"Name (2019) · 1h 52m" for a movie, "Name (2015) · 3 seasons" for a series; unknown parts left out.</summary>
    public static string Headline(LibraryTitle t)
    {
        var s = t.Year is > 0 ? $"{t.Name} ({t.Year})" : t.Name;
        if (t.Kind == "Series")
        {
            return t.SeasonCount is > 0 and var n ? $"{s} · {n} {(n == 1 ? "season" : "seasons")}" : s;
        }

        if (t.RunTimeTicks is > 0 and var ticks)
        {
            var span = TimeSpan.FromTicks(ticks);
            var min = (int)Math.Round(span.TotalMinutes);
            if (min > 0)
            {
                return $"{s} · {(min >= 60 ? $"{min / 60}h {min % 60}m" : $"{min}m")}";
            }
        }

        return s;
    }

    /// <summary>"yyyy · place", either part alone, or null when both are unknown.</summary>
    private static string? BornLine(PersonInfo p)
    {
        var place = string.IsNullOrWhiteSpace(p.BirthPlace) ? null : p.BirthPlace.Trim();
        return (p.Born?.Year, place) switch
        {
            ({ } y, { } pl) => $"{y} · {pl}",
            ({ } y, null) => y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (null, { } pl) => pl,
            _ => null,
        };
    }

    /// <summary>The first <see cref="OverviewChars"/> characters, cut with an ellipsis (never inside a surrogate pair).</summary>
    private static string? Overview(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        var o = overview.Trim();
        if (o.Length <= OverviewChars)
        {
            return o;
        }

        var cut = char.IsHighSurrogate(o[OverviewChars - 1]) ? OverviewChars - 1 : OverviewChars;
        return o[..cut].TrimEnd() + "…";
    }

    /// <summary>The bio up to <see cref="MaxBioChars"/>, cut at the last space before that (or at the limit, surrogate-safe).</summary>
    private static string Bio(string bio)
    {
        var b = bio.Trim();
        if (b.Length <= MaxBioChars)
        {
            return b;
        }

        var cut = MaxBioChars;
        var space = b.LastIndexOf(' ', MaxBioChars);
        if (space > MaxBioChars / 2)
        {
            cut = space;
        }
        else if (char.IsHighSurrogate(b[cut - 1]))
        {
            cut--;
        }

        return b[..cut].TrimEnd() + "…";
    }

    private static JsonObject Text(string text, double size, string color, bool bold = false, int maxLines = 0) => new()
    {
        ["type"] = "text",
        ["text"] = text,
        ["size"] = size,
        ["color"] = color,
        ["bold"] = bold,
        ["maxLines"] = maxLines,
        ["dir"] = "auto",
    };
}

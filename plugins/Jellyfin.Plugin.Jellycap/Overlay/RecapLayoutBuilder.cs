using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Jellycap.Overlay;

/// <summary>
/// Layouts for Jellycap's recap text cards on the JellyTvOverlay app. A card looks like the JellyXR TV app's
/// RecapCard: a translucent dark box in the lower part of the screen, bold title over the summary. Passive: the
/// remote keeps driving the player. Every text node is <c>dir: auto</c> (Hebrew recaps).
/// </summary>
public static class RecapLayoutBuilder
{
    /// <summary>Pause mode layout id (the host namespaces it as <c>Jellycap/recap</c>).</summary>
    public const string PauseLayoutId = "recap";

    /// <summary>Timed mode layout id: one card at a time, each shown again with the same id.</summary>
    public const string CardLayoutId = "recap-card";

    /// <summary>Reported by the pause-mode sequence when its last card ends.</summary>
    public const string FinishedAction = "recapDone";

    /// <summary>
    /// Pause → one <c>sequence</c> of every card (durationMs = seconds x 1000) reporting <see cref="FinishedAction"/>;
    /// it suppresses pause/resume/doublePause for other clients (Jellycap's own pause must not open JellyXR's panel)
    /// and does not close on resume (the driver handles the user pressing play).
    /// Timed → a single card layout for <c>cards[0]</c>, closed on seek; the driver shows one per card at its time.
    /// </summary>
    public static JsonObject Build(IReadOnlyList<OverlayCard> cards, OverlayMode mode)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0)
        {
            throw new ArgumentException("at least one card is required", nameof(cards));
        }

        if (mode == OverlayMode.Pause)
        {
            var children = new JsonArray();
            foreach (var c in cards)
            {
                var card = Card(c);
                card["durationMs"] = (int)Math.Round(c.Seconds * 1000);
                children.Add(card);
            }

            return new JsonObject
            {
                ["v"] = 1,
                ["id"] = PauseLayoutId,
                ["focus"] = "passive",
                ["closeOn"] = new JsonArray("stop", "itemStart"),
                ["suppressTriggers"] = new JsonArray("pause", "resume", "doublePause"),
                ["root"] = new JsonObject
                {
                    ["type"] = "sequence",
                    ["finishedAction"] = FinishedAction,
                    ["children"] = children,
                },
            };
        }

        return new JsonObject
        {
            ["v"] = 1,
            ["id"] = CardLayoutId,
            ["focus"] = "passive",
            ["closeOn"] = new JsonArray("stop", "itemStart", "seek"),
            ["root"] = Card(cards[0]),
        };
    }

    /// <summary>A full-screen stack holding the card box at the bottom centre (72% wide, like RecapCard).</summary>
    private static JsonObject Card(OverlayCard c)
    {
        var texts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = c.Title,
                ["size"] = 0.042,
                ["bold"] = true,
                ["color"] = "#FFFFFFFF",
                ["maxLines"] = 2,
                ["dir"] = "auto",
            },
        };
        if (!string.IsNullOrWhiteSpace(c.Body))
        {
            texts.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = c.Body,
                ["size"] = 0.032,
                ["color"] = "#DCFFFFFF",
                ["maxLines"] = 7,
                ["dir"] = "auto",
            });
        }

        var hasBody = texts.Count > 1;
        return new JsonObject
        {
            ["type"] = "stack",
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "column",
                    ["x"] = 0.14,
                    ["w"] = 0.72,
                    ["y"] = hasBody ? 0.55 : 0.78,
                    ["h"] = hasBody ? 0.38 : 0.15,
                    ["pad"] = 0.035,
                    ["gap"] = 0.015,
                    ["bg"] = "#E10C0E12",
                    ["radius"] = 0.03,
                    ["children"] = texts,
                },
            },
        };
    }
}

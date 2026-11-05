using System.Globalization;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Translated labels for the text recap cards.</summary>
public static class Localization
{
    public static readonly IReadOnlyList<string> Languages = new[] { "en", "he", "es", "fr", "de", "it", "pt", "ru", "ar" };

    private static readonly Dictionary<string, (string Previously, string Earlier)> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ("Previously on {0}", "Earlier in this episode"),
        ["he"] = ("בפרקים הקודמים של {0}", "קודם לכן בפרק הזה"),
        ["es"] = ("Anteriormente en {0}", "Antes en este episodio"),
        ["fr"] = ("Précédemment dans {0}", "Plus tôt dans cet épisode"),
        ["de"] = ("Zuvor bei {0}", "Zuvor in dieser Folge"),
        ["it"] = ("Nelle puntate precedenti di {0}", "Prima in questo episodio"),
        ["pt"] = ("Anteriormente em {0}", "Antes neste episódio"),
        ["ru"] = ("Ранее в сериале {0}", "Ранее в этом эпизоде"),
        ["ar"] = ("سابقًا في {0}", "سابقًا في هذه الحلقة"),
    };

    /// <summary>"he-IL" / "HE" / "" -> supported two letter code (falls back to en).</summary>
    public static string Normalize(string? lang)
    {
        var two = (lang ?? string.Empty).Trim().ToLowerInvariant();
        if (two.Length > 2)
        {
            two = two[..2];
        }

        return Labels.ContainsKey(two) ? two : "en";
    }

    public static string Previously(string? lang, string? series)
        => string.Format(CultureInfo.InvariantCulture, Labels[Normalize(lang)].Previously, series ?? string.Empty).Trim();

    public static string EarlierInEpisode(string? lang) => Labels[Normalize(lang)].Earlier;
}

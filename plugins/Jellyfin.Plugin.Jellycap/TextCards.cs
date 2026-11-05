using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Jellycap.Configuration;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Builds text cards from episode metadata and renders them as ASS subtitle files.</summary>
public class TextCardFactory
{
    private const int MaxChars = 400;

    private readonly TextTranslator _translator;

    public TextCardFactory(TextTranslator translator)
    {
        _translator = translator;
    }

    public async Task<TextCardSpec?> BuildAsync(Episode ep, PluginConfiguration cfg, CancellationToken ct)
    {
        var overview = Shorten(ep.Overview);
        if (string.IsNullOrWhiteSpace(ep.Name) && string.IsNullOrEmpty(overview))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(overview))
        {
            overview = Shorten(await _translator.TranslateAsync(overview, cfg, ct).ConfigureAwait(false));
        }

        var code = string.Create(CultureInfo.InvariantCulture, $"S{ep.ParentIndexNumber ?? 0:00}E{ep.IndexNumber ?? 0:00}");
        var title = string.IsNullOrWhiteSpace(ep.Name) ? code : $"{code} · {ep.Name}";
        return new TextCardSpec(title, string.IsNullOrEmpty(overview) ? null : overview, ReadingSeconds(overview));
    }

    public static string Shorten(string? text, int max = MaxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length <= max)
        {
            return t;
        }

        var cut = t[..max];
        var sentence = cut.LastIndexOfAny(new[] { '.', '!', '?', '。' });
        if (sentence > max / 2)
        {
            return cut[..(sentence + 1)];
        }

        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut) + "…";
    }

    public static double ReadingSeconds(string? text)
    {
        var words = string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Clamp((words * 0.4) + 3, 4, 20);
    }

    /// <summary>ASS script for one card. libass does shaping + bidi, so RTL text (he/ar) renders correctly.</summary>
    public static string BuildAss(TextCardSpec card, int width, int height, string fontName)
    {
        var inv = CultureInfo.InvariantCulture;
        var font = string.IsNullOrWhiteSpace(fontName) ? "Arial" : fontName.Replace(",", " ");
        var titleSize = height / 16;
        var bodySize = height / 24;
        var side = width / 8;
        var hasBody = !string.IsNullOrEmpty(card.Body);
        var titleAlign = hasBody ? 8 : 5;
        var titleMargin = hasBody ? height / 8 : 0;

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine(inv, $"PlayResX: {width}");
        sb.AppendLine(inv, $"PlayResY: {height}");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine(inv, $"Style: Title,{font},{titleSize},&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,{titleAlign},{side},{side},{titleMargin},1");
        sb.AppendLine(inv, $"Style: Body,{font},{bodySize},&H00DDDDDD,&H00DDDDDD,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,8,{side},{side},{height * 3 / 10},1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        var end = Stamp(card.Seconds);
        sb.AppendLine($"Dialogue: 0,0:00:00.00,{end},Title,,0,0,0,,{{\\fad(300,300)}}{Escape(card.Title)}");
        if (hasBody)
        {
            sb.AppendLine($"Dialogue: 0,0:00:00.00,{end},Body,,0,0,0,,{{\\fad(300,300)}}{Escape(card.Body!)}");
        }

        return sb.ToString();
    }

    private static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    private static string Escape(string s)
        => s.Replace('\\', ' ').Replace('{', '(').Replace('}', ')').Replace("\r", string.Empty).Replace("\n", "\\N");
}

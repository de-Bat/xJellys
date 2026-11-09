using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellySubSync;

/// <summary>
/// Moves every cue of a text subtitle by a fixed offset. Only the timestamps change; the rest of the file
/// stays byte for byte, so callers pass text decoded as Latin-1 (one char per byte) and encode it back the
/// same way. That keeps any file encoding (UTF-8, Windows-1255, ...) intact without detecting it.
/// </summary>
public static class SubtitleShifter
{
    /// <summary>Formats <see cref="Shift"/> understands, by file extension without the dot.</summary>
    public static readonly string[] Formats = { "srt", "vtt", "ass", "ssa" };

    // SRT "00:01:02,345", WebVTT "00:01:02.345" or "01:02.345"
    private static readonly Regex CueTime = new(@"(?<!\d)(?:(\d{1,3}):)?(\d{2}):(\d{2})([,.])(\d{3})(?!\d)", RegexOptions.Compiled);

    // ASS/SSA "Dialogue: 0,0:01:02.34,0:01:04.56,..."
    private static readonly Regex AssEvent = new(@"^(\s*(?:Dialogue|Comment)\s*:\s*[^,]*,)(\d+):(\d{2}):(\d{2})[.:](\d{2}),(\d+):(\d{2}):(\d{2})[.:](\d{2}),", RegexOptions.Compiled | RegexOptions.Multiline);

    public static bool Supports(string format) => Formats.Contains(format.ToLowerInvariant());

    /// <summary>Text shifted by <paramref name="offsetMs"/> (positive = subtitles later). Times below zero become zero.</summary>
    public static string Shift(string text, string format, long offsetMs)
    {
        return format.ToLowerInvariant() switch
        {
            "ass" or "ssa" => ShiftAss(text, offsetMs),
            "srt" or "vtt" => ShiftCueLines(text, offsetMs),
            _ => throw new NotSupportedException("Unsupported subtitle format: " + format)
        };
    }

    private static string ShiftCueLines(string text, long offsetMs)
    {
        var sb = new StringBuilder(text.Length + 64);
        var start = 0;
        while (start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl + 1;
            var line = text.Substring(start, end - start);
            // only timing lines ("start --> end"); cue text that happens to contain a time stays as is
            sb.Append(line.Contains("-->", StringComparison.Ordinal) ? CueTime.Replace(line, m => ShiftCue(m, offsetMs)) : line);
            start = end;
        }

        return sb.ToString();
    }

    private static string ShiftCue(Match m, long offsetMs)
    {
        var hasHours = m.Groups[1].Success;
        var ms = (hasHours ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600000 : 0)
            + long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60000
            + long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * 1000
            + long.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
        ms = Math.Max(0, ms + offsetMs);
        var h = ms / 3600000;
        var mm = ms / 60000 % 60;
        var s = ms / 1000 % 60;
        var f = ms % 1000;
        var sep = m.Groups[4].Value;
        var inv = CultureInfo.InvariantCulture;
        return hasHours || h > 0
            ? string.Format(inv, "{0:00}:{1:00}:{2:00}{3}{4:000}", h, mm, s, sep, f)
            : string.Format(inv, "{0:00}:{1:00}{2}{3:000}", mm, s, sep, f);
    }

    private static string ShiftAss(string text, long offsetMs)
    {
        return AssEvent.Replace(text, m =>
        {
            var a = AssTime(m, 2, offsetMs);
            var b = AssTime(m, 6, offsetMs);
            return m.Groups[1].Value + a + "," + b + ",";
        });
    }

    private static string AssTime(Match m, int g, long offsetMs)
    {
        var inv = CultureInfo.InvariantCulture;
        var cs = long.Parse(m.Groups[g].Value, inv) * 360000
            + long.Parse(m.Groups[g + 1].Value, inv) * 6000
            + long.Parse(m.Groups[g + 2].Value, inv) * 100
            + long.Parse(m.Groups[g + 3].Value, inv);
        // round the offset to centiseconds, ASS precision
        cs = Math.Max(0, cs + (long)Math.Round(offsetMs / 10.0, MidpointRounding.AwayFromZero));
        return string.Format(inv, "{0}:{1:00}:{2:00}.{3:00}", cs / 360000, cs / 6000 % 60, cs / 100 % 60, cs % 100);
    }
}

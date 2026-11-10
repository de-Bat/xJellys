using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>One subtitle line: shown from <see cref="Start"/> to <see cref="End"/> (seconds).</summary>
public sealed record DialogueCue(double Start, double End, string Text);

/// <summary>The dialogue of an episode, read from its text subtitle track (external file, or embedded and extracted once with ffmpeg).</summary>
public sealed record EpisodeDialogueTrack(string? Language, IReadOnlyList<DialogueCue> Cues);

/// <summary>Reads an episode's subtitles so a text recap can quote what was said in the watched part.</summary>
public class EpisodeDialogue
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "srt", "subrip", "ass", "ssa", "webvtt", "vtt", "mov_text", "text"
    };

    private static readonly string[] ExternalFormats = { "srt", "vtt", "ass", "ssa" };

    // two letter UI language -> ISO 639-2 codes Jellyfin uses for streams
    private static readonly Dictionary<string, string[]> Iso3 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new[] { "eng" },
        ["he"] = new[] { "heb" },
        ["es"] = new[] { "spa" },
        ["fr"] = new[] { "fre", "fra" },
        ["de"] = new[] { "ger", "deu" },
        ["it"] = new[] { "ita" },
        ["pt"] = new[] { "por" },
        ["ru"] = new[] { "rus" },
        ["ar"] = new[] { "ara" },
    };

    private static readonly Regex SrtTime = new(@"(?:(\d{1,3}):)?(\d{1,2}):(\d{2})[,.](\d{1,3})\s*-->\s*(?:(\d{1,3}):)?(\d{1,2}):(\d{2})[,.](\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex AssTags = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex HtmlTags = new(@"<[^>]+>", RegexOptions.Compiled);

    private readonly IMediaSourceManager _mediaSources;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<EpisodeDialogue> _logger;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public EpisodeDialogue(IMediaSourceManager mediaSources, IMediaEncoder encoder, IApplicationPaths paths, ILogger<EpisodeDialogue> logger)
    {
        _mediaSources = mediaSources;
        _encoder = encoder;
        _logger = PluginLog.Wrap(logger);
        _cacheDir = Path.Combine(paths.CachePath, "jellycap-subtitles");
    }

    /// <summary>Dialogue of <paramref name="item"/>, preferring a track in <paramref name="language"/>. Null when it has no usable text subtitle.</summary>
    public async Task<EpisodeDialogueTrack?> GetAsync(Video item, string? language, CancellationToken ct)
    {
        try
        {
            var stream = Pick(StreamsOf(item.Id), language);
            if (stream is null)
            {
                _logger.LogDebug("No text subtitle track for {Item}; no dialogue for the text recap", item.Name);
                return null;
            }

            var text = stream.IsExternal
                ? Decode(await File.ReadAllBytesAsync(stream.Path!, ct).ConfigureAwait(false), language)
                : await EmbeddedAsync(item, stream, ct).ConfigureAwait(false);
            if (text is null)
            {
                return null;
            }

            var format = stream.IsExternal ? Path.GetExtension(stream.Path!).TrimStart('.') : "srt";
            var cues = Parse(text, format);
            _logger.LogDebug("Read {Count} subtitle line(s) for {Item} from {Source} ({Lang})", cues.Count, item.Name, stream.IsExternal ? stream.Path : "embedded stream " + stream.Index, stream.Language);
            return cues.Count == 0 ? null : new EpisodeDialogueTrack(stream.Language, cues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read subtitles of {Item}", item.Name);
            return null;
        }
    }

    /// <summary>True when the track language is already the card language (no translation needed).</summary>
    public static bool SameLanguage(string? streamLanguage, string? uiLanguage)
    {
        if (string.IsNullOrWhiteSpace(streamLanguage))
        {
            return false;
        }

        var two = Localization.Normalize(uiLanguage);
        var s = streamLanguage.Trim();
        return s.StartsWith(two, StringComparison.OrdinalIgnoreCase) && (s.Length == 2 || s[2] == '-' || s[2] == '_')
            || (Iso3.TryGetValue(two, out var codes) && codes.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Lines spoken in [from, to], joined in order, at most <paramref name="maxLines"/>.</summary>
    public static string Quote(IReadOnlyList<DialogueCue> cues, double from, double to, int maxLines = 4)
    {
        var lines = new List<string>();
        foreach (var c in cues)
        {
            if (c.End < from || c.Start > to)
            {
                continue;
            }

            // a cue shown at the very end may continue past the watched part: keep it, it was on screen
            if (lines.Count == 0 || !string.Equals(lines[^1], c.Text, StringComparison.Ordinal))
            {
                lines.Add(c.Text);
            }
        }

        if (lines.Count > maxLines)
        {
            // the last lines lead into the moment; keep those
            lines = lines.Skip(lines.Count - maxLines).ToList();
        }

        return string.Join("\n", lines.Select(l => "“" + l + "”"));
    }

    private static MediaStream? Pick(IReadOnlyList<MediaStream> streams, string? language)
    {
        var usable = streams.Where(s => s.Type == MediaStreamType.Subtitle && Usable(s)).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        // full tracks beat forced ones (a few signs only); the card language beats others; then the default track
        return usable
            .OrderBy(s => s.IsForced ? 1 : 0)
            .ThenBy(s => SameLanguage(s.Language, language) ? 0 : 1)
            .ThenBy(s => s.IsDefault ? 0 : 1)
            .ThenBy(s => s.IsExternal ? 0 : 1)
            .First();
    }

    private static bool Usable(MediaStream s)
    {
        if (s.IsExternal)
        {
            var ext = Path.GetExtension(s.Path ?? string.Empty).TrimStart('.');
            return ExternalFormats.Contains(ext, StringComparer.OrdinalIgnoreCase) && File.Exists(s.Path);
        }

        return TextCodecs.Contains(s.Codec ?? string.Empty);
    }

    private async Task<string?> EmbeddedAsync(Video item, MediaStream stream, CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheDir);
        var cache = Path.Combine(_cacheDir, $"{item.Id:N}-{stream.Index}.srt");
        var gate = _locks.GetOrAdd(cache, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < File.GetLastWriteTimeUtc(item.Path))
            {
                if (!await ExtractAsync(item.Path, stream.Index, cache).ConfigureAwait(false))
                {
                    return null;
                }
            }

            // ffmpeg writes UTF-8
            return await File.ReadAllTextAsync(cache, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ExtractAsync(string video, int index, string output)
    {
        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogWarning("ffmpeg is not set up on the server; cannot read embedded subtitles");
            return false;
        }

        var tmp = output + ".tmp";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-nostdin", "-v", "error", "-y", "-i", video, "-map", "0:" + index.ToString(CultureInfo.InvariantCulture), "-c:s", "srt", "-f", "srt", tmp })
        {
            psi.ArgumentList.Add(a);
        }

        _logger.LogInformation("Extracting subtitle stream {Index} of {Video} for the text recap", index, video);
        using var p = Process.Start(psi);
        if (p is null)
        {
            return false;
        }

        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            _logger.LogWarning("Subtitle extraction from {Video} timed out", video);
            TryDelete(tmp);
            return false;
        }

        if (p.ExitCode != 0 || !File.Exists(tmp))
        {
            _logger.LogWarning("ffmpeg subtitle extraction failed ({Code}): {Err}", p.ExitCode, (await stderr.ConfigureAwait(false)).Trim());
            TryDelete(tmp);
            return false;
        }

        File.Move(tmp, output, true);
        return true;
    }

    /// <summary>UTF-8 (with or without BOM); otherwise the legacy code page usual for the card language.</summary>
    private static string Decode(byte[] bytes, string? language)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            var codePage = Localization.Normalize(language) switch
            {
                "he" => 1255,
                "ar" => 1256,
                "ru" => 1251,
                _ => 1252
            };

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(codePage).GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }

    public static IReadOnlyList<DialogueCue> Parse(string text, string format)
    {
        var cues = format.ToLowerInvariant() is "ass" or "ssa" ? ParseAss(text) : ParseSrt(text);
        return cues.Where(c => c.Text.Length > 0).OrderBy(c => c.Start).ToList();
    }

    private static List<DialogueCue> ParseSrt(string text)
    {
        var cues = new List<DialogueCue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = SrtTime.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var body = new List<string>();
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !SrtTime.IsMatch(lines[i + 1]))
            {
                body.Add(lines[++i].Trim());
            }

            cues.Add(new DialogueCue(Seconds(m, 1), Seconds(m, 5), Clean(string.Join(' ', body))));
        }

        return cues;
    }

    private static List<DialogueCue> ParseAss(string text)
    {
        var cues = new List<DialogueCue>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimStart();
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            var parts = line["Dialogue:".Length..].Split(',', 10);
            if (parts.Length < 10 || !TryAssTime(parts[1], out var start) || !TryAssTime(parts[2], out var end))
            {
                continue;
            }

            // drawing commands and signs carry no dialogue
            if (parts[9].Contains("\\p1", StringComparison.Ordinal))
            {
                continue;
            }

            cues.Add(new DialogueCue(start, end, Clean(parts[9].Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " "))));
        }

        return cues;
    }

    private static bool TryAssTime(string s, out double seconds)
    {
        seconds = 0;
        var p = s.Trim().Split(':');
        if (p.Length != 3
            || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
        {
            return false;
        }

        seconds = (h * 3600) + (m * 60) + sec;
        return true;
    }

    private static double Seconds(Match m, int g)
    {
        var inv = CultureInfo.InvariantCulture;
        var h = m.Groups[g].Success ? int.Parse(m.Groups[g].Value, inv) : 0;
        var ms = m.Groups[g + 3].Value.PadRight(3, '0');
        return (h * 3600) + (int.Parse(m.Groups[g + 1].Value, inv) * 60) + int.Parse(m.Groups[g + 2].Value, inv) + (int.Parse(ms, inv) / 1000.0);
    }

    private static string Clean(string s)
    {
        s = AssTags.Replace(s, string.Empty);
        s = HtmlTags.Replace(s, string.Empty);
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // speaker dashes and music notes only
        return s.Trim('-', ' ', '♪', '♫', '#');
    }

    private IReadOnlyList<MediaStream> StreamsOf(Guid itemId)
    {
        _streamsMethod ??= typeof(IMediaSourceManager).GetMethods()
            .FirstOrDefault(m => m.Name == "GetMediaStreams" && m.GetParameters() is [var p] && p.ParameterType == typeof(Guid))
            ?? throw new MissingMethodException(typeof(IMediaSourceManager).FullName, "GetMediaStreams");

        // List<T> in 10.9/10.10, IReadOnlyList<T> later: bind by name, not by compile-time signature
        return (_streamsMethod.Invoke(_mediaSources, new object[] { itemId }) as IEnumerable<MediaStream>)?.ToList() ?? new List<MediaStream>();
    }

    private static MethodInfo? _streamsMethod;

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}

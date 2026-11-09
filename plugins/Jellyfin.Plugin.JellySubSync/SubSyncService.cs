using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySubSync;

/// <summary>A failure the TV app shows to the viewer as is.</summary>
public sealed class SubSyncException : Exception
{
    public SubSyncException(string message)
        : base(message)
    {
    }
}

/// <param name="Supported">False = this track cannot be shifted; <paramref name="Reason"/> says why.</param>
/// <param name="OffsetMs">Current shift of the track (0 = not synced).</param>
/// <param name="Label">Track name for the sync box, e.g. "English (SRT)".</param>
public readonly record struct TrackState(bool Supported, string? Reason, long OffsetMs, string Label);

/// <summary>
/// Writes a time-shifted copy of a subtitle track next to the item (Jellyfin's metadata folder by default),
/// so Jellyfin lists it as an extra external track. The original track is never changed. Every shift is
/// made from the original, so shifting again does not stack rounding errors.
/// </summary>
public sealed class SubSyncService
{
    private const string Suffix = "Synced";
    private static readonly Encoding Bytes = Encoding.Latin1; // 1 char per byte, see SubtitleShifter
    private static readonly string[] TextCodecs = { "srt", "subrip", "ass", "ssa", "vtt", "webvtt", "mov_text", "text" };

    private readonly IMediaSourceManager _mediaSources;
    private readonly IMediaEncoder _encoder;
    private readonly IFileSystem _fileSystem;
    private readonly SubSyncStore _store;
    private readonly ILogger<SubSyncService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SubSyncService(IMediaSourceManager mediaSources, IMediaEncoder encoder, IFileSystem fileSystem, SubSyncStore store, ILogger<SubSyncService> logger)
    {
        _mediaSources = mediaSources;
        _encoder = encoder;
        _fileSystem = fileSystem;
        _store = store;
        _logger = PluginLog.Wrap(logger);
    }

    public TrackState GetState(Video item, int streamIndex)
    {
        var streams = Streams(item);
        var stream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == streamIndex);
        if (stream is null)
        {
            return new TrackState(false, "Turn on a subtitle track first.", 0, string.Empty);
        }

        var (source, entry) = Resolve(item, streams, stream);
        if (source is null)
        {
            return new TrackState(false, "The original of this synced track is gone.", 0, Label(stream));
        }

        var why = WhyNot(source);
        return new TrackState(why is null, why, entry?.OffsetMs ?? 0, Label(source));
    }

    /// <summary>
    /// Shift the track playing as <paramref name="streamIndex"/> (an original or its synced copy) to
    /// <paramref name="offsetMs"/> from the original. Returns the stream index to play: the synced copy,
    /// or the original when the offset is 0.
    /// </summary>
    public async Task<int> ApplyAsync(Video item, int streamIndex, long offsetMs, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var max = Math.Max(1, cfg.MaxOffsetSeconds) * 1000L;
        offsetMs = Math.Clamp(offsetMs, -max, max);

        var gate = _locks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var streams = Streams(item);
            var stream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == streamIndex)
                ?? throw new SubSyncException("Turn on a subtitle track first.");
            var (source, entry) = Resolve(item, streams, stream);
            if (source is null)
            {
                throw new SubSyncException("The original of this synced track is gone.");
            }

            var why = WhyNot(source);
            if (why is not null)
            {
                throw new SubSyncException(why);
            }

            var entries = _store.Load(item.Id);
            var sourceKey = Key(source);
            entry = entries.FirstOrDefault(e => e.SourceKey == sourceKey);

            if (offsetMs == 0)
            {
                if (entry is not null)
                {
                    entries.Remove(entry);
                    _store.Save(item.Id, entries);
                    TryDelete(entry.SyncedPath);
                    await RefreshAsync(item, ct).ConfigureAwait(false);
                    _logger.LogInformation("{Item}: subtitle {Key} back to the original", item.Name, sourceKey);
                }

                return IndexOf(Streams(item), sourceKey) ?? throw new SubSyncException("The original track is gone after the refresh.");
            }

            var (text, format) = await OriginalAsync(item, source, ct).ConfigureAwait(false);
            var shifted = SubtitleShifter.Shift(text, format, offsetMs);

            var path = entry?.SyncedPath;
            if (path is null || !string.Equals(Path.GetExtension(path).TrimStart('.'), format, StringComparison.OrdinalIgnoreCase))
            {
                path = NewPath(item, source, format, entries);
            }

            var isNew = !File.Exists(path);
            WriteAtomic(path, Bytes.GetBytes(shifted));

            if (entry is null)
            {
                entry = new SyncEntry { SourceKey = sourceKey };
                entries.Add(entry);
            }
            else if (!string.Equals(entry.SyncedPath, path, StringComparison.Ordinal))
            {
                TryDelete(entry.SyncedPath);
                isNew = true;
            }

            entry.SyncedPath = path;
            entry.OffsetMs = offsetMs;
            entry.Updated = DateTime.UtcNow;
            _store.Save(item.Id, entries);

            var index = IndexOfFile(Streams(item), path);
            if (index is null || isNew)
            {
                await RefreshAsync(item, ct).ConfigureAwait(false);
                index = IndexOfFile(Streams(item), path);
            }

            if (index is null)
            {
                _logger.LogWarning("{Item}: Jellyfin does not list {Path} after a refresh", item.Name, path);
                throw new SubSyncException(cfg.SaveNextToMedia
                    ? "Jellyfin did not pick up the synced file. Rescan the library and try again."
                    : "Jellyfin did not pick up the synced file. Turn on 'Save next to the video' in the JellySubSync settings.");
            }

            _logger.LogInformation("{Item}: subtitle {Key} shifted {Offset} ms -> stream {Index} ({Path})", item.Name, sourceKey, offsetMs, index, path);
            return index.Value;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The original behind <paramref name="stream"/> (itself unless it is a synced copy), and its sync entry.</summary>
    private (MediaStream? Source, SyncEntry? Entry) Resolve(Video item, IReadOnlyList<MediaStream> streams, MediaStream stream)
    {
        var entries = _store.Load(item.Id);
        if (stream.IsExternal && stream.Path is not null)
        {
            var mine = entries.FirstOrDefault(e => SamePath(e.SyncedPath, stream.Path));
            if (mine is not null)
            {
                return (streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && Key(s) == mine.SourceKey), mine);
            }
        }

        var key = Key(stream);
        return (stream, entries.FirstOrDefault(e => e.SourceKey == key));
    }

    private static string? WhyNot(MediaStream s)
    {
        var codec = (s.Codec ?? string.Empty).ToLowerInvariant();
        if (!TextCodecs.Contains(codec) || s.IsExternal && !SubtitleShifter.Supports(Path.GetExtension(s.Path ?? string.Empty).TrimStart('.')))
        {
            return "Picture subtitles (" + (string.IsNullOrEmpty(codec) ? "unknown" : codec) + ") can't be shifted. Pick a text track (SRT, ASS).";
        }

        if (s.IsExternal && !File.Exists(s.Path))
        {
            return "The subtitle file is missing on the server.";
        }

        return null;
    }

    /// <summary>Original text (Latin-1 decoded) and its format. Embedded tracks are extracted once with ffmpeg and cached.</summary>
    private async Task<(string Text, string Format)> OriginalAsync(Video item, MediaStream source, CancellationToken ct)
    {
        if (source.IsExternal)
        {
            var ext = Path.GetExtension(source.Path!).TrimStart('.').ToLowerInvariant();
            return (Bytes.GetString(await File.ReadAllBytesAsync(source.Path!, ct).ConfigureAwait(false)), ext);
        }

        var codec = (source.Codec ?? string.Empty).ToLowerInvariant();
        var format = codec is "ass" or "ssa" ? "ass" : "srt";
        var cacheDir = Path.Combine(SubSyncStore.Dir, "original");
        Directory.CreateDirectory(cacheDir);
        var cache = Path.Combine(cacheDir, $"{item.Id:N}-{source.Index}.{format}");
        if (!File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < File.GetLastWriteTimeUtc(item.Path))
        {
            await ExtractAsync(item.Path, source.Index, format, cache, ct).ConfigureAwait(false);
        }

        return (Bytes.GetString(await File.ReadAllBytesAsync(cache, ct).ConfigureAwait(false)), format);
    }

    private async Task ExtractAsync(string video, int index, string format, string output, CancellationToken ct)
    {
        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            throw new SubSyncException("ffmpeg is not set up on the server.");
        }

        var tmp = output + ".tmp";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-nostdin", "-v", "error", "-y", "-i", video, "-map", "0:" + index, "-c:s", format == "ass" ? "copy" : "srt", "-f", format, tmp })
        {
            psi.ArgumentList.Add(a);
        }

        _logger.LogInformation("Extracting subtitle stream {Index} of {Video}", index, video);
        using var p = Process.Start(psi) ?? throw new SubSyncException("Could not start ffmpeg.");
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        // not tied to the request: if the TV app gives up waiting, the extraction still fills the cache
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

            throw new SubSyncException("Extracting the subtitle took too long.");
        }

        if (p.ExitCode != 0 || !File.Exists(tmp))
        {
            var err = (await stderr.ConfigureAwait(false)).Trim();
            _logger.LogWarning("ffmpeg subtitle extraction failed ({Code}): {Err}", p.ExitCode, err);
            TryDelete(tmp);
            throw new SubSyncException("Could not read this subtitle track from the video.");
        }

        File.Move(tmp, output, true);
    }

    /// <summary>"Movie.eng.Synced.srt", "Movie.eng.Synced 2.srt", ... Jellyfin lists the last part as the track title.</summary>
    private static string NewPath(Video item, MediaStream source, string format, List<SyncEntry> entries)
    {
        var cfg = Plugin.Instance!.Configuration;
        var dir = cfg.SaveNextToMedia ? Path.GetDirectoryName(item.Path)! : item.GetInternalMetadataPath();
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        var lang = string.IsNullOrWhiteSpace(source.Language) ? string.Empty : "." + source.Language.Trim();
        for (var n = 1; ; n++)
        {
            var title = n == 1 ? Suffix : Suffix + " " + n;
            var p = Path.Combine(dir, $"{stem}{lang}.{title}.{format}");
            if (!File.Exists(p) && !entries.Any(e => SamePath(e.SyncedPath, p)))
            {
                return p;
            }
        }
    }

    private async Task RefreshAsync(Video item, CancellationToken ct)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.None,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false
        };
        await item.RefreshMetadata(options, ct).ConfigureAwait(false);
    }

    private IReadOnlyList<MediaStream> Streams(Video item) => MediaStreamsCompat.Get(_mediaSources, item.Id);

    private static int? IndexOf(IReadOnlyList<MediaStream> streams, string key)
        => streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && Key(s) == key)?.Index;

    private static int? IndexOfFile(IReadOnlyList<MediaStream> streams, string path)
        => streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && SamePath(s.Path, path))?.Index;

    private static string Key(MediaStream s) => s.IsExternal ? "file:" + s.Path : "embedded:" + s.Index;

    private static string Label(MediaStream s)
    {
        var name = !string.IsNullOrWhiteSpace(s.DisplayTitle) ? s.DisplayTitle : (s.Language ?? "Subtitles");
        return name!;
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, true);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw new SubSyncException("The server may not write to " + Path.GetDirectoryName(path) + ". Give the Jellyfin user write access, or turn off 'Save next to the video'.");
        }
    }

    private void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}

/// <summary>
/// IMediaSourceManager.GetMediaStreams(Guid) returns List&lt;T&gt; in 10.9/10.10 and may return IReadOnlyList&lt;T&gt;
/// on newer servers. A direct call binds to the compile-time signature, so bind by name.
/// </summary>
internal static class MediaStreamsCompat
{
    private static MethodInfo? _method;

    public static IReadOnlyList<MediaStream> Get(IMediaSourceManager manager, Guid itemId)
    {
        _method ??= typeof(IMediaSourceManager).GetMethods()
            .FirstOrDefault(m => m.Name == "GetMediaStreams" && m.GetParameters() is [var p] && p.ParameterType == typeof(Guid))
            ?? throw new MissingMethodException(typeof(IMediaSourceManager).FullName, "GetMediaStreams");
        return (_method.Invoke(manager, new object[] { itemId }) as IEnumerable<MediaStream>)?.ToList() ?? new List<MediaStream>();
    }
}

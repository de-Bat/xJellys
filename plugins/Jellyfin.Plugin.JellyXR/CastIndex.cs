using System.Diagnostics;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Reference face embeddings of cast members, computed from their Jellyfin portrait and cached on disk.</summary>
public sealed class CastIndex
{
    private sealed class Entry
    {
        public string Stamp { get; set; } = string.Empty;

        public float[]? Emb { get; set; }
    }

    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly FaceEngine _engine;
    private readonly ILogger<CastIndex> _logger;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _cache;

    public CastIndex(ILibraryManager library, IMediaEncoder encoder, FaceEngine engine, ILogger<CastIndex> logger)
    {
        _library = library;
        _encoder = encoder;
        _engine = engine;
        _logger = PluginLog.Wrap(logger);
    }

    private static string CacheFile => Path.Combine(Plugin.Instance!.DataDirectory, "cast.json");

    /// <summary>Cast of an item (actors and guest stars with a portrait) with their reference embeddings.</summary>
    public async Task<List<(string Name, float[] Emb)>> GetAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var result = new List<(string, float[])>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var emb = await GetOneAsync(name, ct).ConfigureAwait(false);
            if (emb is not null)
            {
                result.Add((name, emb));
            }
        }

        return result;
    }

    private async Task<float[]?> GetOneAsync(string name, CancellationToken ct)
    {
        Person? person;
        try
        {
            person = _library.GetPerson(name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Person lookup failed for {Name}", name);
            return null;
        }

        var path = person?.GetImagePath(ImageType.Primary, 0);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _logger.LogDebug("No portrait for {Name}", name);
            return null;
        }

        var stamp = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        lock (_gate)
        {
            _cache ??= LoadCache(_logger);
            if (_cache.TryGetValue(name, out var hit) && hit.Stamp == stamp)
            {
                return hit.Emb;
            }
        }

        float[]? emb = null;
        var rgb = await FrameReader.ReadImageAsync(_encoder.EncoderPath, path, ct).ConfigureAwait(false);
        if (rgb is not null)
        {
            var faces = _engine.Detect(rgb, 0.4f);
            if (faces.Count > 0)
            {
                var biggest = faces.OrderByDescending(f => f.Area).First();
                emb = _engine.Embed(rgb, biggest);
            }
        }

        if (emb is null)
        {
            _logger.LogDebug("No usable portrait face for {Name}", name);
        }
        else
        {
            _logger.LogDebug("Computed reference face for {Name}", name);
        }

        lock (_gate)
        {
            _cache![name] = new Entry { Stamp = stamp, Emb = emb };
            try
            {
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(_cache));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write cast cache");
            }
        }

        return emb;
    }

    private static Dictionary<string, Entry> LoadCache(ILogger logger)
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CacheFile))
                    ?? new Dictionary<string, Entry>();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cast face cache {File} unreadable; rebuilding", CacheFile);
        }

        return new Dictionary<string, Entry>();
    }
}

/// <summary>ffmpeg -> raw RGB24 frames, letterboxed to <see cref="FaceEngine.Size"/> squares.</summary>
public static class FrameReader
{
    private const int FrameBytes = FaceEngine.Size * FaceEngine.Size * 3;

    private static string Letterbox =>
        $"scale={FaceEngine.Size}:{FaceEngine.Size}:force_original_aspect_ratio=decrease," +
        $"pad={FaceEngine.Size}:{FaceEngine.Size}:(ow-iw)/2:(oh-ih)/2";

    public static async Task<byte[]?> ReadImageAsync(string ffmpeg, string imagePath, CancellationToken ct)
    {
        var psi = Base(ffmpeg);
        foreach (var a in new[] { "-i", imagePath, "-vf", Letterbox, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => TryKill(p));
        var buf = new byte[FrameBytes];
        var ok = await ReadExactAsync(p.StandardOutput.BaseStream, buf, ct).ConfigureAwait(false);
        TryKill(p);
        return ok ? buf : null;
    }

    /// <summary>Streams one frame per <paramref name="intervalSec"/> seconds. Frame i is at i * interval seconds.</summary>
    public static async IAsyncEnumerable<byte[]> ReadVideoAsync(
        string ffmpeg, string videoPath, int intervalSec, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var psi = Base(ffmpeg);
        foreach (var a in new[]
        {
            "-i", videoPath, "-an", "-sn", "-dn", "-vf", $"fps=1/{Math.Max(1, intervalSec)}," + Letterbox,
            "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => TryKill(p));
        var stream = p.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                var buf = new byte[FrameBytes];
                if (!await ReadExactAsync(stream, buf, ct).ConfigureAwait(false))
                {
                    yield break;
                }

                yield return buf;
            }
        }
        finally
        {
            TryKill(p);
        }
    }

    private static ProcessStartInfo Base(string ffmpeg)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" })
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
            }
        }
        catch (Exception)
        {
            // already gone
        }
    }
}

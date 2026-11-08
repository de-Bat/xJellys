using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Builds the face timeline of an item: who is on screen, one sample every N seconds.</summary>
public sealed class FaceScanner
{
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly FaceEngine _engine;
    private readonly CastIndex _cast;
    private readonly TimelineStore _store;
    private readonly XrayarrClient _xrayarr;
    private readonly ILogger<FaceScanner> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly HashSet<Guid> _queued = new();

    public FaceScanner(
        ILibraryManager library,
        IMediaEncoder encoder,
        FaceEngine engine,
        CastIndex cast,
        TimelineStore store,
        XrayarrClient xrayarr,
        ILogger<FaceScanner> logger)
    {
        _library = library;
        _encoder = encoder;
        _engine = engine;
        _cast = cast;
        _store = store;
        _xrayarr = xrayarr;
        _logger = PluginLog.Wrap(logger);
    }

    public IReadOnlyList<BaseItem> AllScannable()
    {
        return _library.ItemsOf(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        }).Where(i => !string.IsNullOrEmpty(i.Path)).ToList();
    }

    public bool NeedsScan(BaseItem item) => item is Episode or Movie && !_store.Exists(item.Id);

    /// <summary>False when the data source setting makes a local face scan pointless for this item (Xrayarr covers it).</summary>
    public async Task<bool> WantsLocalScanAsync(BaseItem item, CancellationToken ct)
    {
        switch (Plugin.Instance!.Configuration.DataSource)
        {
            case DataSource.Xrayarr:
                _logger.LogDebug("No local scan for {Item}: data source is Xrayarr only", item.Name);
                return false;
            case DataSource.XrayarrThenLocal:
                var xr = await _xrayarr.GetAsync(item, ct).ConfigureAwait(false);
                if (xr is { Segments.Count: > 0 })
                {
                    _logger.LogDebug("No local scan for {Item}: Xrayarr already has it", item.Name);
                    return false;
                }

                return true;
            default:
                return true;
        }
    }

    /// <summary>Fire and forget: scan one item in the background (one scan at a time).</summary>
    public void Enqueue(BaseItem item)
    {
        if (!NeedsScan(item))
        {
            return;
        }

        lock (_queued)
        {
            if (!_queued.Add(item.Id))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ScanAsync(item, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyXR scan failed for {Item}", item.Name);
            }
            finally
            {
                lock (_queued)
                {
                    _queued.Remove(item.Id);
                }
            }
        });
    }

    public async Task<bool> ScanAsync(BaseItem item, IProgress<double>? progress, CancellationToken ct)
    {
        if (!NeedsScan(item) || !await WantsLocalScanAsync(item, ct).ConfigureAwait(false))
        {
            return false;
        }

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.Exists(item.Id))
            {
                return false;
            }

            if (!File.Exists(item.Path))
            {
                _logger.LogWarning("Cannot scan {Item}: file not found at {Path}", item.Name, item.Path);
                return false;
            }

            var cfg = Plugin.Instance!.Configuration;
            var names = CastNames(item);
            if (names.Count == 0)
            {
                _logger.LogInformation("No cast metadata for {Item}; nothing to scan", item.Name);
                _store.Save(item.Id, new Timeline { Interval = cfg.ScanIntervalSeconds });
                return false;
            }

            await _engine.EnsureLoadedAsync(ct).ConfigureAwait(false);
            var refs = await _cast.GetAsync(names, ct).ConfigureAwait(false);
            var interval = Math.Max(1, cfg.ScanIntervalSeconds);
            var timeline = new Timeline { Interval = interval };
            if (refs.Count == 0)
            {
                _logger.LogInformation("No cast portraits with faces for {Item}; nothing to scan", item.Name);
                _store.Save(item.Id, timeline);
                return false;
            }

            _logger.LogInformation("JellyXR scanning {Item} ({Count} cast with portraits)", item.Name, refs.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var totalFrames = item.RunTimeTicks is > 0 ? (int)(TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds / interval) : 0;
            var frame = 0;
            await foreach (var rgb in FrameReader.ReadVideoAsync(_encoder.EncoderPath, item.Path, interval, ct).ConfigureAwait(false))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var face in _engine.Detect(rgb, (float)cfg.DetectThreshold))
                {
                    var emb = _engine.Embed(rgb, face);
                    var best = refs.Select(r => (r.Name, Sim: FaceEngine.Cosine(emb, r.Emb))).OrderByDescending(x => x.Sim).First();
                    if (best.Sim >= cfg.MatchThreshold)
                    {
                        seen.Add(best.Name);
                    }
                }

                if (seen.Count > 0)
                {
                    timeline.Entries.Add(new TimelineEntry { T = frame * interval, P = seen.ToArray() });
                }

                frame++;
                if (totalFrames > 0 && frame % 20 == 0)
                {
                    progress?.Report(Math.Min(99, 100.0 * frame / totalFrames));
                }
            }

            _store.Save(item.Id, timeline);
            _logger.LogInformation(
                "JellyXR scan done for {Item} in {Seconds:0}s: {Frames} frames, {N} samples with known faces",
                item.Name,
                sw.Elapsed.TotalSeconds,
                frame,
                timeline.Entries.Count);
            return true;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Actors and guest stars of an item.</summary>
    public List<PersonInfo> CastOf(BaseItem item)
    {
        return _library.GetPeopleOf(item)
            .Where(p => p.Type is PersonKind.Actor or PersonKind.GuestStar && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
    }

    private List<string> CastNames(BaseItem item) => CastOf(item).Select(p => p.Name).ToList();
}

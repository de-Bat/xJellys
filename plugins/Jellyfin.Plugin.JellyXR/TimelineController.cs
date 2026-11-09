using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Cast appearance timeline for the web client and the settings page.</summary>
[ApiController]
[Route("JellyXR")]
public sealed class TimelineController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly CastTimelineService _timelines;
    private readonly FaceScanner _scanner;
    private readonly TimelineStore _store;
    private readonly CardService _cards;
    private readonly MarkerService _markers;

    public TimelineController(ILibraryManager library, CastTimelineService timelines, FaceScanner scanner, TimelineStore store, CardService cards, MarkerService markers)
    {
        _markers = markers;
        _cards = cards;
        _library = library;
        _timelines = timelines;
        _scanner = scanner;
        _store = store;
    }

    /// <summary>Per actor appearance spans of a movie or episode. 404 when there is no scene data yet.</summary>
    [HttpGet("Timeline/{itemId}")]
    [Authorize]
    public async Task<ActionResult<CastTimelineView>> Get([FromRoute] Guid itemId, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var item = _library.GetItemById(itemId);
        if (item is not (Movie or Episode))
        {
            return NotFound();
        }

        var view = await _timelines.GetAsync(item, cfg, ct).ConfigureAwait(false);
        return view is null ? NotFound() : view;
    }

    /// <summary>Pause cards for the web overlay: same people as the TV messages, plus portraits. 204 = nothing to show.</summary>
    [HttpGet("Cards/{itemId}")]
    [Authorize]
    public async Task<ActionResult<object>> Cards([FromRoute] Guid itemId, [FromQuery] long positionTicks, [FromQuery] string? source, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var item = _library.GetItemById(itemId);
        var wanted = string.Equals(source, "tv", StringComparison.OrdinalIgnoreCase) ? cfg.TvOverlay : cfg.WebOverlay;
        if (!cfg.Enabled || !wanted || item is not (Movie or Episode))
        {
            return NoContent();
        }

        var set = await _cards.BuildAsync(item, positionTicks, cfg, ct).ConfigureAwait(false);
        if (set is null)
        {
            return NoContent();
        }

        return new
        {
            set.Value.Header,
            OverlaySeconds = Math.Max(0, cfg.OverlaySeconds),
            Cards = set.Value.Cards.Select(c =>
            {
                var (id, hasImage) = _timelines.PersonOf(c.Actor);
                return new { c.Actor, Character = cfg.ShowCharacter ? c.Character : null, c.Info, c.OnScreen, PersonId = id, HasImage = hasImage };
            }).ToList()
        };
    }

    /// <summary>
    /// JellyXR TV companion app heartbeat: it watches the playback session of <paramref name="deviceId"/>,
    /// so that device gets the portraits panel instead of text messages. Returns whether the panel is on.
    /// </summary>
    [HttpPost("Companion/Heartbeat")]
    [Authorize]
    public ActionResult<object> Heartbeat([FromQuery] string deviceId)
    {
        var cfg = Plugin.Instance!.Configuration;
        var on = cfg.Enabled && cfg.TvOverlay;
        if (on)
        {
            CompanionRegistry.Beat(deviceId);
        }

        return new { Enabled = on, Version = typeof(Plugin).Assembly.GetName().Version?.ToString() };
    }

    /// <summary>Queue a background face scan. force = drop the existing local timeline first.</summary>
    [HttpPost("Timeline/{itemId}/Scan")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> Scan([FromRoute] Guid itemId, [FromQuery] bool force = false)
    {
        var item = _library.GetItemById(itemId);
        if (item is not (Movie or Episode))
        {
            return NotFound();
        }

        if (force)
        {
            _store.Delete(item.Id);
            _markers.Queue(item); // unmarked until the rescan (or Xrayarr) has data again
        }

        var queued = _scanner.NeedsScan(item);
        _scanner.Enqueue(item);
        return new { queued, message = queued ? "Scan queued; it runs in the background (one item at a time)." : "Already scanned. Use Rescan to scan again." };
    }

    /// <summary>The web client script (loaded by a script tag, so no auth header).</summary>
    [HttpGet("Web/xray.js")]
    [AllowAnonymous]
    public ActionResult Script()
    {
        var stream = typeof(TimelineController).Assembly.GetManifestResourceStream($"{typeof(TimelineController).Namespace}.Web.xray.js");
        return stream is null ? NotFound() : File(stream, "application/javascript; charset=utf-8");
    }
}

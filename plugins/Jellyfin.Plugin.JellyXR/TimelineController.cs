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

    public TimelineController(ILibraryManager library, CastTimelineService timelines, FaceScanner scanner, TimelineStore store)
    {
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

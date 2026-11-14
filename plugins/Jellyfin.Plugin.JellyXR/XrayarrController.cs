using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyXR;

public sealed class XrayarrTestRequest
{
    public string? Url { get; set; }

    public string? ApiKey { get; set; }
}

public sealed class XrayarrDebugRequest
{
    /// <summary>Path under the Xrayarr base URL, starting with /api/v1/.</summary>
    public string? Path { get; set; }

    /// <summary>Query string without the leading '?'.</summary>
    public string? Query { get; set; }
}

/// <summary>Settings page helpers. Admin only.</summary>
[ApiController]
[Route("JellyXR/Xrayarr")]
[Authorize(Policy = "RequiresElevation")]
public sealed class XrayarrController : ControllerBase
{
    private const string DebugOff = "Debug tools are off. Tick \"Debug tools\" in the JellyXR settings and save.";

    private readonly XrayarrClient _client;
    private readonly ILibraryManager _library;
    private readonly CardService _cards;
    private readonly TimelineStore _store;

    public XrayarrController(XrayarrClient client, ILibraryManager library, CardService cards, TimelineStore store)
    {
        _client = client;
        _library = library;
        _cards = cards;
        _store = store;
    }

    /// <summary>Tests the URL and key typed in the settings page (not the saved ones).</summary>
    [HttpPost("Test")]
    public async Task<ActionResult<object>> Test([FromBody] XrayarrTestRequest body, CancellationToken ct)
    {
        var (ok, message) = await _client.TestAsync(body.Url, body.ApiKey, ct).ConfigureAwait(false);
        return new { ok, message };
    }

    /// <summary>
    /// Debug: what JellyXR knows and would show for an item at a position (lookups, Xrayarr timeline summary,
    /// local scan, cards). fresh = forget the cached Xrayarr timeline first.
    /// </summary>
    [HttpGet("Debug/Item/{itemId}")]
    public async Task<ActionResult<object>> DebugItem([FromRoute] Guid itemId, [FromQuery] int positionSeconds = 0, [FromQuery] bool fresh = false, CancellationToken ct = default)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (!cfg.DebugTools)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = DebugOff });
        }

        var item = _library.GetItemById(itemId);
        if (item is not (Movie or Episode))
        {
            return NotFound(new { error = "Not a movie or episode." });
        }

        if (fresh)
        {
            _client.Forget(item.Id);
        }

        var ep = item as Episode;
        var root = cfg.XrayarrUrl?.TrimEnd('/') ?? string.Empty;
        var lookups = XrayarrClient.LookupsFor(item, cfg)
            .Select(q => new { query = q, path = "/api/v1/xray/timeline", url = XrayarrClient.Configured(cfg) ? root + "/api/v1/xray/timeline?" + q : null })
            .ToList();

        var ms = positionSeconds * 1000L;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var xr = await _client.GetAsync(item, ct).ConfigureAwait(false);
        var xrMs = sw.ElapsedMilliseconds;
        object? xray = null;
        if (xr is not null)
        {
            var scene = xr.SceneAt(ms);
            xray = new
            {
                xr.Status,
                xr.Stale,
                xr.TitleId,
                xr.Title,
                xr.Kind,
                xr.Season,
                xr.Episode,
                xr.Year,
                xr.DurationMs,
                Segments = xr.Segments.Count,
                Scenes = xr.SceneCount,
                SceneAt = scene is { } s
                    ? new { s.Number, s.Segment.Index, s.Segment.StartMs, s.Segment.EndMs, Cast = s.Segment.Cast.Select(p => new { p.Name, p.Character, p.TmdbPersonId, p.Confidence }) }
                    : null,
            };
        }

        var local = _store.Load(item.Id);
        var set = await _cards.BuildAsync(item, TimeSpan.FromSeconds(positionSeconds).Ticks, cfg, ct).ConfigureAwait(false);

        return new
        {
            Item = new
            {
                item.Id,
                item.Name,
                Type = item.GetType().Name,
                item.Path,
                RuntimeSeconds = (int)TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds,
                TmdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb),
                ImdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb),
                SeriesTvdbId = ep?.Series?.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tvdb),
                Season = ep?.ParentIndexNumber,
                Episode = ep?.IndexNumber,
            },
            Config = new
            {
                Sources = CardService.SourcesFor(cfg).Select(x => x.ToString()),
                Scope = CardService.ScopeFor(item, cfg).ToString(),
                XrayarrConfigured = XrayarrClient.Configured(cfg),
                PathRewrite = string.IsNullOrEmpty(item.Path) ? null : XrayarrClient.RewritePath(item.Path, cfg.XrayarrPathMap),
            },
            PositionSeconds = positionSeconds,
            Lookups = lookups,
            Xrayarr = xray,
            XrayarrLookupMs = xrMs,
            XrayarrNote = XrayarrClient.Configured(cfg)
                ? xr is null ? "No ready timeline (unknown title, not scanned yet, unreachable or key rejected; misses are cached 2 min, use Fresh)." : null
                : "Xrayarr URL not set.",
            Local = local is null ? null : new { local.Interval, Entries = local.Entries.Count },
            Cards = set is { } c
                ? new { c.Header, c.Context, Cards = c.Cards.Select(k => new { k.Actor, k.Character, k.Info, k.OnScreen, k.TmdbId }) }
                : null,
        };
    }

    /// <summary>Debug REST client: GET <c>/api/v1/...</c> on the saved Xrayarr with the saved key. Read only.</summary>
    [HttpPost("Debug/Request")]
    public async Task<ActionResult<object>> DebugRequest([FromBody] XrayarrDebugRequest body, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (!cfg.DebugTools)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = DebugOff });
        }

        if (!XrayarrClient.Configured(cfg))
        {
            return BadRequest(new { error = "Xrayarr URL not set (save the settings first)." });
        }

        var url = XrayarrClient.DebugUrl(cfg.XrayarrUrl, body.Path, body.Query);
        if (url is null)
        {
            return BadRequest(new { error = "Path must start with /api/v1/ (no '..', '//', '?', '#'); put parameters in the query field." });
        }

        var r = await _client.DebugGetAsync(cfg, url, ct).ConfigureAwait(false);
        return new { url, r.Status, r.Ms, r.ContentType, r.Body, r.Truncated, r.Error };
    }
}

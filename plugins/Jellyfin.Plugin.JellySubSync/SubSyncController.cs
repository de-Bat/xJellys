using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySubSync;

/// <summary>Subtitle sync for the JellyXR TV app.</summary>
[ApiController]
[Route("JellySubSync")]
[Authorize]
public sealed class SubSyncController : ControllerBase
{
    // Jellyfin's own claim names (Jellyfin.Api InternalClaimTypes / UserRoles), not in the Controller package
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string AdminRole = "Administrator";

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly SubSyncService _sync;
    private readonly ILogger<SubSyncController> _logger;

    public SubSyncController(ILibraryManager library, IUserManager users, SubSyncService sync, ILogger<SubSyncController> logger)
    {
        _library = library;
        _users = users;
        _sync = sync;
        _logger = PluginLog.Wrap(logger);
    }

    /// <summary>Whether the subtitle track <paramref name="streamIndex"/> can be shifted, and its current offset.</summary>
    [HttpGet("Track/{itemId}")]
    public ActionResult<object> Track([FromRoute] Guid itemId, [FromQuery] int streamIndex, [FromQuery] string? mediaSourceId)
    {
        var (item, error) = Find(itemId, mediaSourceId);
        if (item is null)
        {
            return error!;
        }

        var cfg = Plugin.Instance!.Configuration;
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString();
        if (!SyncScopeFilter.Applies(item, cfg, _library))
        {
            // the TV app opens no box for this title
            return new { Allowed = false, Supported = false, Reason = "Subtitle sync is not turned on for this title.", OffsetMs = 0L, Label = string.Empty, MaxOffsetMs = 0L, Version = version };
        }

        var s = _sync.GetState(item, streamIndex);
        var maxMs = Math.Max(1, cfg.MaxOffsetSeconds) * 1000L;
        return new { Allowed = true, s.Supported, s.Reason, s.OffsetMs, s.Label, MaxOffsetMs = maxMs, Version = version };
    }

    /// <summary>
    /// Shift the track to <paramref name="offsetMs"/> from its original (positive = later, 0 = original).
    /// Returns the stream index to switch playback to.
    /// </summary>
    [HttpPost("Track/{itemId}")]
    public async Task<ActionResult<object>> Apply([FromRoute] Guid itemId, [FromQuery] int streamIndex, [FromQuery] long offsetMs, [FromQuery] string? mediaSourceId, CancellationToken ct)
    {
        var (item, error) = Find(itemId, mediaSourceId);
        if (item is null)
        {
            return error!;
        }

        if (!SyncScopeFilter.Applies(item, Plugin.Instance!.Configuration, _library))
        {
            return Problem("Subtitle sync is not turned on for this title (JellySubSync settings).", statusCode: StatusCodes.Status403Forbidden);
        }

        if (Plugin.Instance!.Configuration.AdminsOnly && !User.IsInRole(AdminRole))
        {
            return Problem("Only administrators may sync subtitles (JellySubSync settings).", statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            var index = await _sync.ApplyAsync(item, streamIndex, offsetMs, ct).ConfigureAwait(false);
            return new { StreamIndex = index, OffsetMs = offsetMs };
        }
        catch (SubSyncException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Subtitle sync failed for {Item}", item.Name);
            return Problem("Subtitle sync failed: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>The video (the playing version when <paramref name="mediaSourceId"/> is given), visible to the caller.</summary>
    private (Video? Item, ActionResult? Error) Find(Guid itemId, string? mediaSourceId)
    {
        var id = Guid.TryParse(mediaSourceId, out var ms) && ms != Guid.Empty ? ms : itemId;
        if (_library.GetItemById(id) is not Video item)
        {
            return (null, NotFound());
        }

        var claim = User.FindFirst(UserIdClaim)?.Value;
        var user = Guid.TryParse(claim, out var uid) ? _users.GetUserById(uid) : null;
        if (user is null || !item.IsVisible(user))
        {
            return (null, NotFound());
        }

        return (item, null);
    }
}

using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Endpoints for the JellyXR TV companion app, which draws Jellycap's text cards over the player.</summary>
[ApiController]
[Route("Jellycap")]
public sealed class OverlayController : ControllerBase
{
    private readonly OverlayStore _overlays;
    private readonly ISessionManager _sessions;
    private readonly ILogger<OverlayController> _logger;

    public OverlayController(OverlayStore overlays, ISessionManager sessions, ILogger<OverlayController> logger)
    {
        _overlays = overlays;
        _sessions = sessions;
        _logger = PluginLog.Wrap(logger);
    }

    /// <summary>The app watches the playback session of <paramref name="deviceId"/>. Returns whether Jellycap uses its overlay.</summary>
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

    /// <summary>Recap cards for <paramref name="itemId"/> playing on <paramref name="deviceId"/>. 204 = none; Status "preparing" = ask again.</summary>
    [HttpGet("Overlay/{itemId}")]
    [Authorize]
    public ActionResult<object> Get([FromRoute] Guid itemId, [FromQuery] string deviceId)
    {
        var r = _overlays.Get(deviceId, itemId);
        if (r is null || r.State == OverlayRecap.Released)
        {
            return NoContent();
        }

        if (!r.Ready)
        {
            return new { Status = "preparing" };
        }

        return new { Status = "ready", Mode = r.Mode.ToString(), r.Cards };
    }

    /// <summary>Pause mode: the app is about to show the cards. Pauses the episode, and resumes it later if the app never releases it.</summary>
    [HttpPost("Overlay/{itemId}/Hold")]
    [Authorize]
    public async Task<ActionResult> Hold([FromRoute] Guid itemId, [FromQuery] string deviceId, CancellationToken ct)
    {
        var r = _overlays.Get(deviceId, itemId);
        if (r is null || !r.Ready || r.Mode != OverlayMode.Pause || r.State == OverlayRecap.Released)
        {
            return NotFound();
        }

        if (Interlocked.CompareExchange(ref r.State, OverlayRecap.Held, OverlayRecap.Waiting) == OverlayRecap.Waiting)
        {
            _logger.LogInformation("Showing text recap ({Cards} card(s)) on the JellyXR TV overlay; pausing the episode", r.Cards.Count);
            var fallback = TimeSpan.FromSeconds(r.TotalSeconds + 30);
            _ = Task.Run(async () =>
            {
                await Task.Delay(fallback).ConfigureAwait(false);
                if (r.State == OverlayRecap.Held)
                {
                    _logger.LogInformation("JellyXR TV did not finish the text recap in {Seconds}s; resuming the episode", fallback.TotalSeconds);
                    await ReleaseAsync(deviceId, r, true, CancellationToken.None).ConfigureAwait(false);
                }
            });
        }

        await Command(r.SessionId, new PlaystateRequest { Command = PlaystateCommand.Pause }, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Pause mode: the cards are done. resume = seek the episode back to where it started and play it;
    /// false = the user already pressed play during the cards, leave playback alone.
    /// </summary>
    [HttpPost("Overlay/{itemId}/Release")]
    [Authorize]
    public async Task<ActionResult> Release([FromRoute] Guid itemId, [FromQuery] string deviceId, [FromQuery] bool resume = true, CancellationToken ct = default)
    {
        var r = _overlays.Get(deviceId, itemId);
        if (r is null || r.Mode != OverlayMode.Pause)
        {
            return NotFound();
        }

        await ReleaseAsync(deviceId, r, resume, ct).ConfigureAwait(false);
        return NoContent();
    }

    private async Task ReleaseAsync(string deviceId, OverlayRecap r, bool resume, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref r.State, OverlayRecap.Released) == OverlayRecap.Released)
        {
            return;
        }

        _overlays.Remove(deviceId, r);
        if (!resume)
        {
            return;
        }

        await Command(r.SessionId, new PlaystateRequest { Command = PlaystateCommand.Seek, SeekPositionTicks = r.ResumeTicks }, ct).ConfigureAwait(false);
        await Command(r.SessionId, new PlaystateRequest { Command = PlaystateCommand.Unpause }, ct).ConfigureAwait(false);
    }

    private async Task Command(string sessionId, PlaystateRequest req, CancellationToken ct)
    {
        try
        {
            await _sessions.SendPlaystateCommand(null, sessionId, req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playstate command {Command} to session {Session} failed", req.Command, sessionId);
        }
    }
}

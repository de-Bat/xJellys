using System.Text;
using Jellyfin.Plugin.JellyTvOverlay.Host;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTvOverlay.Api;

/// <summary>Endpoints for the JellyTvOverlay app (Poll, Event, Heartbeat) and the settings page (Clients, Devices). Never logs headers or tokens.</summary>
[ApiController]
[Route("JellyTvOverlay")]
[Authorize]
public sealed class OverlayController : ControllerBase
{
    // Jellyfin's own claim names (Jellyfin.Api InternalClaimTypes / UserRoles), not in the Controller package.
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string AdminRole = "Administrator";
    private const int MaxEventBytes = 64 * 1024;
    private const int MaxDebugLayoutBytes = 1024 * 1024;

    private readonly OverlayApi _api;
    private readonly OverlayRuntime _rt;
    private readonly ISessionManager _sessions;

    public OverlayController(OverlayApi api, OverlayRuntime rt, ISessionManager sessions)
    {
        _api = api;
        _rt = rt;
        _sessions = sessions;
    }

    /// <summary>Long-poll (up to 25 s) for ops after <paramref name="since"/>: <c>{"seq":n,"ops":[...]}</c>.</summary>
    [HttpGet("Poll")]
    public async Task<ActionResult> Poll([FromQuery] string? deviceId, [FromQuery] long since = 0)
    {
        var r = await _api.PollAsync(deviceId ?? string.Empty, since, CanUse, OverlayApi.PollHold, HttpContext.RequestAborted).ConfigureAwait(false);
        return ToResult(r);
    }

    /// <summary>App event (action or closed) for a layout shown on the device. 204.</summary>
    [HttpPost("Event")]
    public async Task<ActionResult> Event()
    {
        var body = await ReadBoundedAsync(MaxEventBytes).ConfigureAwait(false);
        if (body is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        return ToResult(await _api.EventAsync(body, CanUse).ConfigureAwait(false));
    }

    /// <summary>The app is alive on the device: <c>{"enabled":bool,"protocol":1}</c>.</summary>
    [HttpPost("Heartbeat")]
    public ActionResult Heartbeat([FromQuery] string? deviceId) => ToResult(_api.Heartbeat(deviceId ?? string.Empty, CanUse));

    /// <summary>Debug aid: shows the body layout as client "debug" on the device. Admins only; 404 unless DebugEndpoints is on.</summary>
    [HttpPost("Debug/Show")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> DebugShow([FromQuery] string? deviceId)
    {
        if (Plugin.Instance?.Configuration.DebugEndpoints != true)
        {
            return NotFound();
        }

        var body = await ReadBoundedAsync(MaxDebugLayoutBytes).ConfigureAwait(false);
        if (body is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        return ToResult(_api.DebugShow(deviceId ?? string.Empty, body, CanUse));
    }

    /// <summary>Registered client plugins (settings page).</summary>
    [HttpGet("Clients")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> Clients()
        => OverlayHost.Registered().Select(c => new { c.Id, c.DisplayName }).ToList();

    /// <summary>Devices with a fresh heartbeat plus switched-off ones (settings page).</summary>
    [HttpGet("Devices")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> Devices()
    {
        var off = Plugin.Instance?.Configuration.DisabledDevices ?? Array.Empty<string>();
        var sessions = _sessions.Sessions.ToList();
        return _rt.Heartbeats.FreshDevices().Union(off, StringComparer.Ordinal).Select(id =>
        {
            var s = sessions.FirstOrDefault(x => x.DeviceId == id);
            return new
            {
                DeviceId = id,
                DeviceName = s?.DeviceName ?? string.Empty,
                Client = s?.Client ?? string.Empty,
                UserName = s?.UserName ?? string.Empty,
                Fresh = _rt.Heartbeats.IsFresh(id),
                Enabled = !off.Contains(id, StringComparer.Ordinal),
            };
        }).ToList();
    }

    /// <summary>The request body, or null when it is larger than <paramref name="max"/> (by Content-Length or as read).</summary>
    private async Task<string?> ReadBoundedAsync(int max)
    {
        if (Request.ContentLength is { } len && len > max)
        {
            return null;
        }

        var buffer = new char[max + 1];
        int read;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            read = await reader.ReadBlockAsync(buffer.AsMemory(), HttpContext.RequestAborted).ConfigureAwait(false);
        }

        return read > max ? null : new string(buffer, 0, read);
    }

    private bool CanUse(string deviceId)
    {
        var userId = Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var g) ? g : Guid.Empty;
        return DeviceAccess.CanUse(userId, User.IsInRole(AdminRole), deviceId, _sessions.Sessions.Select(s => (s.DeviceId ?? string.Empty, s.UserId)));
    }

    private ActionResult ToResult(ApiResult r)
        => r.Json is null ? StatusCode(r.Status) : new ContentResult { StatusCode = r.Status, Content = r.Json, ContentType = "application/json; charset=utf-8" };
}

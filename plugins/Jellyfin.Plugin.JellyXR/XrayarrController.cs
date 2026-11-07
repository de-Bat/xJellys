using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyXR;

public sealed class XrayarrTestRequest
{
    public string? Url { get; set; }

    public string? ApiKey { get; set; }
}

/// <summary>Settings page helpers. Admin only.</summary>
[ApiController]
[Route("JellyXR/Xrayarr")]
[Authorize(Policy = "RequiresElevation")]
public sealed class XrayarrController : ControllerBase
{
    private readonly XrayarrClient _client;

    public XrayarrController(XrayarrClient client)
    {
        _client = client;
    }

    /// <summary>Tests the URL and key typed in the settings page (not the saved ones).</summary>
    [HttpPost("Test")]
    public async Task<ActionResult<object>> Test([FromBody] XrayarrTestRequest body, CancellationToken ct)
    {
        var (ok, message) = await _client.TestAsync(body.Url, body.ApiKey, ct).ConfigureAwait(false);
        return new { ok, message };
    }
}

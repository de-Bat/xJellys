using Jellyfin.Plugin.JellyXR;
using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class XrayarrDebugUrlTests
{
    [Theory]
    [InlineData("http://xray:8080", "/api/v1/system/status", null, "http://xray:8080/api/v1/system/status")]
    [InlineData("http://xray:8080/", "/api/v1/xray/at", "?tmdbId=42&t=12", "http://xray:8080/api/v1/xray/at?tmdbId=42&t=12")]
    [InlineData("https://h.lan/xr/", "/api/v1/xray/scenes", "tvdbId=7&season=1&episode=3&format=vtt", "https://h.lan/xr/api/v1/xray/scenes?tvdbId=7&season=1&episode=3&format=vtt")]
    [InlineData("http://xray:8080", "/api/v1/xray/timeline", "path=%2Fmedia%2Fa.mkv", "http://xray:8080/api/v1/xray/timeline?path=%2Fmedia%2Fa.mkv")]
    public void Allowed(string root, string path, string? query, string expected)
    {
        Assert.Equal(expected, XrayarrClient.DebugUrl(root, path, query));
    }

    [Theory]
    [InlineData("http://xray:8080", "/openapi.json", null)]
    [InlineData("http://xray:8080", "api/v1/system/status", null)]
    [InlineData("http://xray:8080", "/api/v1/../../etc/passwd", null)]
    [InlineData("http://xray:8080", "/api/v1//evil.com/x", null)]
    [InlineData("http://xray:8080", "/api/v1/x@evil.com", null)]
    [InlineData("http://xray:8080", "/api/v1/x\\y", null)]
    [InlineData("http://xray:8080", "/api/v1/x?a=1", null)]
    [InlineData("http://xray:8080", "/api/v1/x", "a=1#frag")]
    [InlineData("", "/api/v1/system/status", null)]
    [InlineData("file:///etc", "/api/v1/system/status", null)]
    [InlineData("http://xray:8080", "", null)]
    public void Refused(string root, string path, string? query)
    {
        Assert.Null(XrayarrClient.DebugUrl(root, path, query));
    }
}

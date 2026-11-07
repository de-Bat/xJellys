using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

public sealed class XrayarrPerson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("character")]
    public string? Character { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class XrayarrSegment
{
    [JsonPropertyName("startMs")]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    public long EndMs { get; set; }

    [JsonPropertyName("cast")]
    public List<XrayarrPerson> Cast { get; set; } = new();
}

/// <summary>Body of <c>GET /api/v1/xray/timeline</c>. Only <c>status == "ready"</c> carries segments.</summary>
public sealed class XrayarrTimeline
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stale")]
    public bool Stale { get; set; }

    [JsonPropertyName("titleId")]
    public int? TitleId { get; set; }

    [JsonPropertyName("segments")]
    public List<XrayarrSegment> Segments { get; set; } = new();

    public bool IsReady => string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>People in segments overlapping [fromMs, toMs]; the segment holding <paramref name="atMs"/> first, in Xrayarr's order.</summary>
    public List<XrayarrPerson> PeopleAround(long atMs, long fromMs, long toMs)
    {
        var hits = Segments
            .Where(s => s.StartMs <= toMs && s.EndMs > fromMs)
            .OrderBy(s => s.StartMs <= atMs && atMs < s.EndMs ? 0 : 1)
            .ThenBy(s => Math.Abs(((s.StartMs + s.EndMs) / 2) - atMs));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<XrayarrPerson>();
        foreach (var p in hits.SelectMany(s => s.Cast))
        {
            if (!string.IsNullOrWhiteSpace(p.Name) && seen.Add(p.Name))
            {
                list.Add(p);
            }
        }

        return list;
    }
}

/// <summary>Reads scene timelines from an Xrayarr server (https://github.com/de-Bat/xrayarr).</summary>
public sealed class XrayarrClient
{
    private static readonly TimeSpan ReadyTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<Guid, (DateTime Expires, XrayarrTimeline? Value)> _cache = new();
    private readonly ConcurrentDictionary<Guid, Task<XrayarrTimeline?>> _inflight = new();
    private readonly ILogger<XrayarrClient> _logger;
    private string _cacheOwner = string.Empty;

    public XrayarrClient(ILogger<XrayarrClient> logger)
    {
        _logger = logger;
    }

    public static bool Configured(Configuration.PluginConfiguration cfg) => !string.IsNullOrWhiteSpace(cfg.XrayarrUrl);

    /// <summary>Ready timeline for the item, or null (not configured, unknown, not scanned yet, unreachable). Cached per item.</summary>
    public Task<XrayarrTimeline?> GetAsync(BaseItem item, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (!Configured(cfg) || item is not (Movie or Episode))
        {
            return Task.FromResult<XrayarrTimeline?>(null);
        }

        // new server or key: forget everything cached from the old one
        var owner = cfg.XrayarrUrl.Trim() + "|" + cfg.XrayarrApiKey.Trim() + "|" + cfg.XrayarrPathMap;
        if (!string.Equals(owner, Volatile.Read(ref _cacheOwner), StringComparison.Ordinal))
        {
            _cache.Clear();
            Volatile.Write(ref _cacheOwner, owner);
        }

        if (_cache.TryGetValue(item.Id, out var hit) && hit.Expires > DateTime.UtcNow)
        {
            return Task.FromResult(hit.Value);
        }

        return _inflight.GetOrAdd(item.Id, _ => FetchAndCacheAsync(item, cfg, ct));
    }

    private async Task<XrayarrTimeline?> FetchAndCacheAsync(BaseItem item, Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        try
        {
            XrayarrTimeline? result = null;
            foreach (var query in Identities(item, cfg))
            {
                var (found, tl) = await FetchAsync(cfg, query, ct).ConfigureAwait(false);
                if (found)
                {
                    result = tl;
                    break;
                }
            }

            _cache[item.Id] = (DateTime.UtcNow + (result is null ? MissTtl : ReadyTtl), result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Xrayarr lookup failed for {Item}: {Error}", item.Name, ex.Message);
            _cache[item.Id] = (DateTime.UtcNow + MissTtl, null);
            return null;
        }
        finally
        {
            _inflight.TryRemove(item.Id, out _);
        }
    }

    /// <summary>(found, ready timeline). found = Xrayarr knows the title (even if not scanned yet), so no other identity is tried.</summary>
    private async Task<(bool Found, XrayarrTimeline? Timeline)> FetchAsync(Configuration.PluginConfiguration cfg, string query, CancellationToken ct)
    {
        var url = cfg.XrayarrUrl.TrimEnd('/') + "/api/v1/xray/timeline?" + query;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(cfg.XrayarrApiKey))
        {
            req.Headers.Add("X-Api-Key", cfg.XrayarrApiKey.Trim());
        }

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return (false, null);
        }

        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Xrayarr rejected the request ({Code}); check the API key", (int)res.StatusCode);
            return (true, null);
        }

        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Xrayarr returned {Code} for {Query}", (int)res.StatusCode, query);
            return (false, null);
        }

        await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var tl = await JsonSerializer.DeserializeAsync<XrayarrTimeline>(body, Json, ct).ConfigureAwait(false);
        if (tl is null || !tl.IsReady)
        {
            _logger.LogDebug("Xrayarr has {Query} but status is {Status}", query, tl?.Status);
            return (true, null);
        }

        return (true, tl);
    }

    /// <summary>Lookup forms in preference order: tmdbId (movies), tvdbId + season + episode (episodes), then the file path.</summary>
    private static IEnumerable<string> Identities(BaseItem item, Configuration.PluginConfiguration cfg)
    {
        static string Q(string k, string v) => k + "=" + Uri.EscapeDataString(v);

        if (item is Movie && int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdb))
        {
            yield return Q("tmdbId", tmdb.ToString(CultureInfo.InvariantCulture));
        }

        if (item is Episode ep && ep.ParentIndexNumber is { } season && ep.IndexNumber is { } episode
            && int.TryParse(ep.Series?.GetProviderId(MetadataProvider.Tvdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvdb))
        {
            yield return Q("tvdbId", tvdb.ToString(CultureInfo.InvariantCulture)) + "&"
                + Q("season", season.ToString(CultureInfo.InvariantCulture)) + "&"
                + Q("episode", episode.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(item.Path))
        {
            yield return Q("path", RewritePath(item.Path, cfg.XrayarrPathMap));
        }
    }

    /// <summary>Applies the first matching "jellyfinPrefix=xrayarrPrefix" rule (rules separated by ';' or new lines).</summary>
    public static string RewritePath(string path, string? map)
    {
        var p = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(map))
        {
            return p;
        }

        var rules = map.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.Split('=', 2))
            .Where(r => r.Length == 2 && r[0].Length > 0)
            .Select(r => (From: r[0].Replace('\\', '/').TrimEnd('/'), To: r[1].Replace('\\', '/').TrimEnd('/')))
            .OrderByDescending(r => r.From.Length);
        foreach (var (from, to) in rules)
        {
            if (p.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
            {
                return to + p.Substring(from.Length);
            }
        }

        return p;
    }
}

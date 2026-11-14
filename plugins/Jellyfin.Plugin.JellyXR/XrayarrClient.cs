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

    /// <summary>TMDB person id; the id shared with Jellyfin's person item, so matching does not depend on spelling or language.</summary>
    [JsonPropertyName("tmdbPersonId")]
    public int? TmdbPersonId { get; set; }

    [JsonPropertyName("imdbPersonId")]
    public string? ImdbPersonId { get; set; }
}

public sealed class XrayarrSegment
{
    /// <summary>0-based scene number (newer Xrayarr); null from older servers.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

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

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>"movie" or "episode" (newer Xrayarr).</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    public bool IsReady => string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>The scene holding <paramref name="atMs"/> with its 1-based number, or null between scenes.</summary>
    public (XrayarrSegment Segment, int Number)? SceneAt(long atMs)
    {
        var ordered = Segments.OrderBy(s => s.StartMs).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            if (s.StartMs <= atMs && atMs < s.EndMs)
            {
                return (s, (s.Index ?? i) + 1);
            }
        }

        return null;
    }

    /// <summary>Scene count: the highest index + 1 when Xrayarr numbers them, else the segment count.</summary>
    public int SceneCount => Segments.Count == 0 ? 0 : Math.Max(Segments.Count, Segments.Max(s => s.Index ?? -1) + 1);

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
            // one person once, whether Xrayarr spells the name the same way in every segment or not
            var key = p.TmdbPersonId is { } t ? "tmdb:" + t : p.Name;
            if (!string.IsNullOrWhiteSpace(p.Name) && seen.Add(key))
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
        _logger = PluginLog.Wrap(logger);
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
                    _logger.LogDebug("Xrayarr matched {Item} by {Query}: {State}", item.Name, query, tl is null ? "not ready" : tl.Segments.Count + " segments");
                    result = tl;
                    break;
                }
            }

            if (result is null)
            {
                _logger.LogDebug("No ready Xrayarr timeline for {Item}", item.Name);
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

    /// <summary>Drops the cached timeline of an item, so the next lookup asks Xrayarr again.</summary>
    public void Forget(Guid itemId) => _cache.TryRemove(itemId, out _);

    /// <summary>Lookup query strings JellyXR tries for the item, in order (debug view).</summary>
    public static IReadOnlyList<string> LookupsFor(BaseItem item, Configuration.PluginConfiguration cfg) => Identities(item, cfg).ToList();

    /// <summary>Largest response body the debug client hands back.</summary>
    internal const int DebugMaxBody = 512 * 1024;

    /// <summary>
    /// Debug client URL: the saved base plus an <c>/api/v1/...</c> path and query. Null for anything else (other hosts,
    /// schemes, dot segments, fragments), so the proxy can only read this Xrayarr's API.
    /// </summary>
    internal static string? DebugUrl(string? baseUrl, string? path, string? query)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var root)
            || root.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim();
        if (!p.StartsWith("/api/v1/", StringComparison.Ordinal) || p.Contains("..", StringComparison.Ordinal)
            || p.IndexOfAny(new[] { '?', '#', '\\', '@' }) >= 0 || p.Contains("//", StringComparison.Ordinal))
        {
            return null;
        }

        var q = (query ?? string.Empty).Trim().TrimStart('?');
        if (q.Contains('#', StringComparison.Ordinal))
        {
            return null;
        }

        var url = root.ToString().TrimEnd('/') + p + (q.Length > 0 ? "?" + q : string.Empty);
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == root.Host && u.Port == root.Port ? url : null;
    }

    /// <summary>GET against the saved Xrayarr with the saved key (debug client). The key is never part of the result.</summary>
    public async Task<(int Status, long Ms, string? ContentType, string Body, bool Truncated, string? Error)> DebugGetAsync(
        Configuration.PluginConfiguration cfg, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(cfg.XrayarrApiKey))
        {
            req.Headers.Add("X-Api-Key", cfg.XrayarrApiKey.Trim());
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[DebugMaxBody + 1];
            var read = 0;
            int n;
            while (read < buf.Length && (n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read), ct).ConfigureAwait(false)) > 0)
            {
                read += n;
            }

            var truncated = read > DebugMaxBody;
            var body = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(read, DebugMaxBody));
            return ((int)res.StatusCode, sw.ElapsedMilliseconds, res.Content.Headers.ContentType?.ToString(), body, truncated, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, sw.ElapsedMilliseconds, null, string.Empty, false, "timed out");
        }
        catch (HttpRequestException ex)
        {
            return (0, sw.ElapsedMilliseconds, null, string.Empty, false, ex.Message);
        }
    }

    /// <summary>Checks URL and key without touching the saved settings. Returns a human readable verdict.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(string? baseUrl, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return (false, "Enter a URL like http://xray:8080");
        }

        var root = uri.ToString().TrimEnd('/');
        var key = apiKey?.Trim();

        async Task<(HttpStatusCode? Code, string Body, string? Error)> Get(string path, string? withKey)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, root + path);
            req.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrEmpty(withKey))
            {
                req.Headers.Add("X-Api-Key", withKey);
            }

            try
            {
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                return (res.StatusCode, await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false), null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, string.Empty, "timed out");
            }
            catch (HttpRequestException ex)
            {
                return (null, string.Empty, ex.Message);
            }
        }

        var ping = await Get("/api/v1/system/ping", null).ConfigureAwait(false);
        if (ping.Error is not null)
        {
            return (false, $"Cannot reach {root}: {ping.Error}");
        }

        if (ping.Code != HttpStatusCode.OK || !ping.Body.Contains("\"ok\"", StringComparison.Ordinal))
        {
            return (false, $"{root} answered {(int)ping.Code!} on /api/v1/system/ping; is this an Xrayarr server (base URL, no /api path)?");
        }

        var status = await Get("/api/v1/system/status", key).ConfigureAwait(false);
        if (status.Code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (false, string.IsNullOrEmpty(key)
                ? "Xrayarr requires an API key (Xrayarr: Settings -> General)"
                : "Xrayarr rejected the API key");
        }

        if (status.Error is not null || status.Code != HttpStatusCode.OK)
        {
            return (false, $"Xrayarr status check failed: {status.Error ?? ((int)status.Code!).ToString(CultureInfo.InvariantCulture)}");
        }

        var version = "?";
        try
        {
            using var doc = JsonDocument.Parse(status.Body);
            if (doc.RootElement.TryGetProperty("version", out var v))
            {
                version = v.ToString();
            }
        }
        catch (JsonException)
        {
            // version is cosmetic
        }

        var msg = $"Connected to Xrayarr {version}";
        if (!string.IsNullOrEmpty(key))
        {
            // Xrayarr's default auth mode lets local addresses in without a key, so a wrong key would also "work".
            var anon = await Get("/api/v1/system/status", null).ConfigureAwait(false);
            if (anon.Code == HttpStatusCode.OK)
            {
                msg += ". Note: Xrayarr lets this server in without a key (auth disabled for local addresses), so the key itself was not verified";
            }
        }

        return (true, msg);
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
    internal static IEnumerable<string> Identities(BaseItem item, Configuration.PluginConfiguration cfg)
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

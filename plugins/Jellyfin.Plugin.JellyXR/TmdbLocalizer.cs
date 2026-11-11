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

/// <summary>Actor name and character as TMDB knows them in one language.</summary>
public readonly record struct LocalizedCredit(string? Name, string? Character);

/// <summary>
/// Looks up actor and character names in the language set in the plugin settings, straight from TMDB credits.
/// Per person the requested language wins, then English, else the caller keeps what it has.
/// </summary>
public sealed class TmdbLocalizer
{
    private const string Api = "https://api.themoviedb.org/3";
    private const string Fallback = "en-US";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<string, (DateTime Expires, Dictionary<int, LocalizedCredit> Value)> _cache = new();
    private readonly ILogger<TmdbLocalizer> _logger;

    public TmdbLocalizer(ILogger<TmdbLocalizer> logger)
    {
        _logger = PluginLog.Wrap(logger);
    }

    /// <summary>True when a TMDB key and a language are both set.</summary>
    public static bool Active(Configuration.PluginConfiguration cfg)
        => !string.IsNullOrWhiteSpace(cfg.TmdbApiKey) && !string.IsNullOrWhiteSpace(Normalize(cfg.CardLanguage));

    /// <summary>"he", "he-IL", "pt_BR" to a TMDB language tag; empty for blank input.</summary>
    public static string Normalize(string? language)
    {
        var l = (language ?? string.Empty).Trim().Replace('_', '-');
        return l.Length < 2 ? string.Empty : l;
    }

    /// <summary>TMDB person id to localized credit, for the item's cast. Empty when TMDB has nothing or is unreachable.</summary>
    public async Task<IReadOnlyDictionary<int, LocalizedCredit>> GetAsync(BaseItem item, Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        var path = CreditsPath(item);
        var lang = Normalize(cfg.CardLanguage);
        if (path is null || !Active(cfg))
        {
            return new Dictionary<int, LocalizedCredit>();
        }

        var key = path + "|" + lang + "|" + cfg.TmdbApiKey.Trim();
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow)
        {
            return hit.Value;
        }

        try
        {
            var wanted = await FetchAsync(path, lang, cfg.TmdbApiKey.Trim(), ct).ConfigureAwait(false);
            var english = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? wanted
                : await FetchAsync(path, Fallback, cfg.TmdbApiKey.Trim(), ct).ConfigureAwait(false);
            var merged = Merge(wanted, english);
            _cache[key] = (DateTime.UtcNow + (merged.Count == 0 ? MissTtl : Ttl), merged);
            return merged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("TMDB lookup failed for {Item}: {Error}", item.Name, ex.Message);
            var none = new Dictionary<int, LocalizedCredit>();
            _cache[key] = (DateTime.UtcNow + MissTtl, none);
            return none;
        }
    }

    /// <summary>Per person: requested-language name / character if present, else the English one.</summary>
    internal static Dictionary<int, LocalizedCredit> Merge(Dictionary<int, LocalizedCredit> wanted, Dictionary<int, LocalizedCredit> english)
    {
        static string? Pick(string? first, string? second)
            => !string.IsNullOrWhiteSpace(first) ? first : (!string.IsNullOrWhiteSpace(second) ? second : null);

        var result = new Dictionary<int, LocalizedCredit>();
        foreach (var id in wanted.Keys.Concat(english.Keys).Distinct())
        {
            wanted.TryGetValue(id, out var w);
            english.TryGetValue(id, out var e);
            result[id] = new LocalizedCredit(Pick(w.Name, e.Name), Pick(w.Character, e.Character));
        }

        return result;
    }

    /// <summary>Movies: /movie/{tmdb}/credits. Episodes: /tv/{seriesTmdb}/season/{s}/episode/{e}/credits.</summary>
    internal static string? CreditsPath(BaseItem item)
    {
        static bool Id(string? s, out int id) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

        if (item is Movie && Id(item.GetProviderId(MetadataProvider.Tmdb), out var movie))
        {
            return $"/movie/{movie}/credits";
        }

        if (item is Episode ep && ep.ParentIndexNumber is { } season && ep.IndexNumber is { } episode
            && Id(ep.Series?.GetProviderId(MetadataProvider.Tmdb), out var tv))
        {
            return string.Create(CultureInfo.InvariantCulture, $"/tv/{tv}/season/{season}/episode/{episode}/credits");
        }

        return null;
    }

    private async Task<Dictionary<int, LocalizedCredit>> FetchAsync(string path, string language, string key, CancellationToken ct)
    {
        // v4 read tokens are JWTs ("eyJ..."), sent as a bearer; a v3 key goes in the query string
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var url = Api + path + "?language=" + Uri.EscapeDataString(language) + (bearer ? string.Empty : "&api_key=" + Uri.EscapeDataString(key));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (bearer)
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode is HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("TMDB rejected the API key");
            return new Dictionary<int, LocalizedCredit>();
        }

        if (!res.IsSuccessStatusCode)
        {
            _logger.LogDebug("TMDB answered {Code} for {Path}", (int)res.StatusCode, path);
            return new Dictionary<int, LocalizedCredit>();
        }

        await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var credits = await JsonSerializer.DeserializeAsync<TmdbCredits>(body, Json, ct).ConfigureAwait(false);
        var map = new Dictionary<int, LocalizedCredit>();
        foreach (var c in (credits?.Cast ?? new List<TmdbCastEntry>()).Concat(credits?.GuestStars ?? new List<TmdbCastEntry>()))
        {
            map.TryAdd(c.Id, new LocalizedCredit(c.Name, c.Character));
        }

        return map;
    }

    private sealed class TmdbCredits
    {
        [JsonPropertyName("cast")]
        public List<TmdbCastEntry>? Cast { get; set; }

        [JsonPropertyName("guest_stars")]
        public List<TmdbCastEntry>? GuestStars { get; set; }
    }

    private sealed class TmdbCastEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("character")]
        public string? Character { get; set; }
    }
}

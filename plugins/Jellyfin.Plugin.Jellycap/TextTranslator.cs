using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Jellycap.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>
/// Optional machine translation of episode summaries through a LibreTranslate compatible endpoint
/// (POST {url}/translate). Results are cached on disk. Any failure falls back to the original text.
/// </summary>
public class TextTranslator
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TextTranslator> _logger;
    private readonly string _cacheFile;
    private readonly object _lock = new();
    private Dictionary<string, string>? _cache;

    public TextTranslator(IHttpClientFactory http, IApplicationPaths paths, ILogger<TextTranslator> logger)
    {
        _http = http;
        _logger = PluginLog.Wrap(logger);
        _cacheFile = Path.Combine(paths.CachePath, "jellycap-translations.json");
    }

    public async Task<string> TranslateAsync(string text, PluginConfiguration cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(cfg.TranslationUrl))
        {
            return text;
        }

        var target = Localization.Normalize(cfg.TextLanguage);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target + "\n" + text)));
        lock (_lock)
        {
            if (Cache().TryGetValue(key, out var hit))
            {
                _logger.LogDebug("Translation cache hit ({Target})", target);
                return hit;
            }
        }

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var url = cfg.TranslationUrl.TrimEnd('/') + "/translate";
            var body = new Dictionary<string, string>
            {
                ["q"] = text,
                ["source"] = "auto",
                ["target"] = target,
                ["format"] = "text",
            };
            if (!string.IsNullOrWhiteSpace(cfg.TranslationApiKey))
            {
                body["api_key"] = cfg.TranslationApiKey;
            }

            using var resp = await client.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var translated = doc.RootElement.GetProperty("translatedText").GetString();
            if (string.IsNullOrWhiteSpace(translated))
            {
                _logger.LogWarning("Translation service at {Url} returned empty text; using original", url);
                return text;
            }

            _logger.LogDebug("Translated {Chars} chars to {Target}", text.Length, target);

            lock (_lock)
            {
                Cache()[key] = translated;
                Save();
            }

            return translated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Translation failed; using original text");
            return text;
        }
    }

    private Dictionary<string, string> Cache()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            _cache = File.Exists(_cacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFile)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Translation cache {File} unreadable; starting empty", _cacheFile);
            _cache = new();
        }

        return _cache;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_cache));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save translation cache");
        }
    }
}

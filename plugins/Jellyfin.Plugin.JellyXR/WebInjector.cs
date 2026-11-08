using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Body the File Transformation plugin passes to <see cref="WebInjector.TransformIndex"/>.</summary>
public sealed class IndexPatchPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}

/// <summary>
/// Adds the JellyXR script to jellyfin-web's index.html so movie and episode pages show the cast timeline.
/// Uses the File Transformation plugin when installed (works with a read-only web folder), else edits index.html.
/// </summary>
public sealed class WebInjector : IHostedService
{
    private const string Tag = "<script plugin=\"JellyXR\" defer=\"defer\" src=\"../JellyXR/Web/xray.js\"></script>";

    private readonly IApplicationPaths _paths;
    private readonly ILogger<WebInjector> _logger;

    public WebInjector(IApplicationPaths paths, ILogger<WebInjector> logger)
    {
        _paths = paths;
        _logger = PluginLog.Wrap(logger);
    }

    private static bool Enabled => Plugin.Instance?.Configuration.InjectWebUi ?? false;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled && TryRegisterFileTransformation())
        {
            RemoveFromIndex(); // an older direct patch would load the script twice
            return Task.CompletedTask;
        }

        if (Enabled)
        {
            AddToIndex();
        }
        else
        {
            RemoveFromIndex();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>File Transformation callback. Also checks the setting, since registration lives until restart.</summary>
    public static string TransformIndex(IndexPatchPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        return Enabled ? Insert(html) : html;
    }

    private static string Insert(string html)
    {
        if (html.Contains("plugin=\"JellyXR\"", StringComparison.Ordinal))
        {
            return html;
        }

        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + Tag : html.Insert(at, Tag);
    }

    private bool TryRegisterFileTransformation()
    {
        try
        {
            var assemblies = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).ToList();
            var ft = assemblies.FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
            var api = ft?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var register = api?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            if (register is null)
            {
                return false;
            }

            // the plugin takes a Newtonsoft JObject; build it without a compile time dependency
            var jobject = assemblies.Select(a => a.GetType("Newtonsoft.Json.Linq.JObject")).FirstOrDefault(t => t is not null);
            var parse = jobject?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            if (parse is null)
            {
                _logger.LogWarning("File Transformation found but Newtonsoft.Json is not loaded; editing index.html instead");
                return false;
            }

            var json = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["id"] = Plugin.Instance!.Id.ToString(),
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = typeof(WebInjector).Assembly.FullName,
                ["callbackClass"] = typeof(WebInjector).FullName,
                ["callbackMethod"] = nameof(TransformIndex)
            });
            register.Invoke(null, new[] { parse.Invoke(null, new object[] { json }) });
            _logger.LogInformation("Cast timeline added to the web client via File Transformation");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("File Transformation registration failed ({Error}); editing index.html instead", ex.Message);
            return false;
        }
    }

    private string? IndexPath()
    {
        var p = string.IsNullOrEmpty(_paths.WebPath) ? null : Path.Combine(_paths.WebPath, "index.html");
        return p is not null && File.Exists(p) ? p : null;
    }

    private void AddToIndex()
    {
        var p = IndexPath();
        if (p is null)
        {
            _logger.LogInformation("Web client not found (server runs without jellyfin-web); no cast timeline on item pages");
            return;
        }

        try
        {
            var html = File.ReadAllText(p);
            var patched = Insert(html);
            if (!ReferenceEquals(patched, html))
            {
                File.WriteAllText(p, patched);
                _logger.LogInformation("Cast timeline added to the web client ({Path}). Reload the browser page.", p);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Cannot edit {Path} ({Error}). Install the File Transformation plugin to show the cast timeline on item pages.",
                p,
                ex.Message);
        }
    }

    private void RemoveFromIndex()
    {
        var p = IndexPath();
        if (p is null)
        {
            return;
        }

        try
        {
            var html = File.ReadAllText(p);
            if (html.Contains(Tag, StringComparison.Ordinal))
            {
                File.WriteAllText(p, html.Replace(Tag, string.Empty, StringComparison.Ordinal));
                _logger.LogInformation("Cast timeline script removed from {Path}", p);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cannot clean {Path}: {Error}", p, ex.Message);
        }
    }
}

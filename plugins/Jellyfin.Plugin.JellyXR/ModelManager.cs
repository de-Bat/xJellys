using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Downloads the face models once into the plugin data folder.</summary>
public sealed class ModelManager
{
    public const string DetectorFile = "det_500m.onnx";
    public const string RecognizerFile = "w600k_mbf.onnx";

    private readonly ILogger<ModelManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ModelManager(ILogger<ModelManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Test hook: use this folder instead of the plugin data folder.</summary>
    public static string? DirOverride { get; set; }

    private static string ModelDir => DirOverride ?? Path.Combine(Plugin.Instance!.DataDirectory, "models");

    public string DetectorPath => Path.Combine(ModelDir, DetectorFile);

    public string RecognizerPath => Path.Combine(ModelDir, RecognizerFile);

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (File.Exists(DetectorPath) && File.Exists(RecognizerPath))
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(DetectorPath) && File.Exists(RecognizerPath))
            {
                return;
            }

            Directory.CreateDirectory(ModelDir);
            var url = Plugin.Instance!.Configuration.ModelZipUrl;
            _logger.LogInformation("Downloading JellyXR face models from {Url}", url);
            var zipPath = Path.Combine(ModelDir, "models.zip.tmp");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var name in new[] { DetectorFile, RecognizerFile })
                {
                    var entry = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException($"{name} not found in model zip");
                    entry.ExtractToFile(Path.Combine(ModelDir, name), true);
                }
            }

            File.Delete(zipPath);
            _logger.LogInformation("JellyXR face models ready in {Dir}", ModelDir);
        }
        finally
        {
            _lock.Release();
        }
    }
}

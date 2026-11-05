using System.Collections.Concurrent;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Builds (and caches) recap files and resolves them to library items.</summary>
public class RecapService
{
    private readonly RecapPlanner _planner;
    private readonly FfmpegRecapBuilder _builder;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<RecapService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    public RecapService(
        RecapPlanner planner,
        FfmpegRecapBuilder builder,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IApplicationPaths paths,
        ILogger<RecapService> logger)
    {
        _planner = planner;
        _builder = builder;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _paths = paths;
        _logger = logger;
    }

    public RecapPlanner Planner => _planner;

    public string RecapDirectory
    {
        get
        {
            var dir = Plugin.Instance?.Configuration.RecapDirectory;
            return string.IsNullOrWhiteSpace(dir) ? Path.Combine(_paths.DataPath, "jellycap") : dir;
        }
    }

    public bool IsRecapPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var root = Path.GetFullPath(RecapDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public string GetRecapPath(Episode ep, RecapMode mode, TimeSpan pos)
    {
        var series = Sanitize(ep.SeriesName ?? "Series");
        var code = $"S{ep.ParentIndexNumber ?? 0:00}E{ep.IndexNumber ?? 0:00}";
        var suffix = mode == RecapMode.Resume ? $" (resume m{(int)pos.TotalMinutes})" : string.Empty;
        return Path.Combine(RecapDirectory, series, $"{series} - {code} - Recap{suffix}.mp4");
    }

    /// <summary>Starts (or joins) generation. Result is the file path, or null if nothing to recap / failure.</summary>
    public Task<string?> EnsureRecapFileAsync(Episode ep, RecapMode mode, TimeSpan pos)
    {
        var path = GetRecapPath(ep, mode, pos);
        if (File.Exists(path))
        {
            return Task.FromResult<string?>(path);
        }

        return _inflight.GetOrAdd(path, p => Task.Run(async () =>
        {
            try
            {
                return await GenerateAsync(ep, mode, pos, p).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recap generation failed for {Path}", p);
                return null;
            }
            finally
            {
                _inflight.TryRemove(p, out _);
            }
        }));
    }

    /// <summary>Waits for the library to know the file; nudges the library monitor.</summary>
    public async Task<BaseItem?> ResolveItemAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var reported = false;
        while (true)
        {
            var item = _libraryManager.FindByPath(path, false);
            if (item is not null)
            {
                return item;
            }

            if (!reported)
            {
                _libraryMonitor.ReportFileSystemChanged(path);
                reported = true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
    }

    public int CleanOld(int retentionDays)
    {
        var dir = RecapDirectory;
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        var removed = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.mp4", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(f) < cutoff)
            {
                File.Delete(f);
                removed++;
            }
        }

        return removed;
    }

    private async Task<string?> GenerateAsync(Episode ep, RecapMode mode, TimeSpan pos, string output)
    {
        var cfg = Plugin.Instance!.Configuration;
        var clips = _planner.Plan(ep, mode, pos, cfg);
        if (clips.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var tmp = output + ".tmp";
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(output))
            {
                return output;
            }

            _logger.LogInformation("Building recap {Output} from {Count} clips", output, clips.Count);
            var ok = await _builder.BuildAsync(clips, cfg.OutputHeight, tmp, CancellationToken.None).ConfigureAwait(false);
            if (!ok)
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }

                return null;
            }

            File.Move(tmp, output, true);
            return output;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }
}

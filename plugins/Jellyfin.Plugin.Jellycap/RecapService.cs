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
        _logger = PluginLog.Wrap(logger);
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

    /// <summary>A new-episode recap does not depend on the start position.</summary>
    private static (RecapMode Mode, TimeSpan Pos) Normalize(RecapMode mode, TimeSpan pos, RecapFormat fmt)
        => mode == RecapMode.New ? (RecapMode.New, TimeSpan.Zero) : (mode, pos);

    public string GetRecapPath(Episode ep, RecapMode mode, TimeSpan pos, RecapFormat fmt)
    {
        (mode, pos) = Normalize(mode, pos, fmt);
        var cfg = Plugin.Instance!.Configuration;
        var series = Sanitize(ep.SeriesName ?? "Series");
        var code = $"S{ep.ParentIndexNumber ?? 0:00}E{ep.IndexNumber ?? 0:00}";
        var lang = Localization.Normalize(cfg.TextLanguage);
        var format = fmt switch
        {
            RecapFormat.Text => $" [text-{lang}]",
            RecapFormat.Both => $" [both-{lang}]",
            _ => string.Empty
        };
        var suffix = mode switch
        {
            RecapMode.Resume => $" (resume m{(int)pos.TotalMinutes})",
            RecapMode.ResumeThisEpisode => $" (resume-this m{(int)pos.TotalMinutes})",
            _ => string.Empty
        };
        return Path.Combine(RecapDirectory, series, $"{series} - {code} - Recap{format}{suffix}.mp4");
    }

    /// <summary>Starts (or joins) generation. Result is the file path, or null if nothing to recap / failure.</summary>
    public Task<string?> EnsureRecapFileAsync(Episode ep, RecapMode mode, TimeSpan pos, RecapFormat fmt)
    {
        (mode, pos) = Normalize(mode, pos, fmt);
        var path = GetRecapPath(ep, mode, pos, fmt);
        if (File.Exists(path))
        {
            _logger.LogDebug("Recap already built: {Path}", path);
            return Task.FromResult<string?>(path);
        }

        return _inflight.GetOrAdd(path, p => Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Recap generation queued: {Path}", p);
                return await GenerateAsync(ep, mode, pos, fmt, p).ConfigureAwait(false);
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
                _logger.LogDebug("Library did not pick up {Path} within {Timeout}", path, timeout);
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
                _logger.LogDebug("Deleted old recap {File}", f);
                removed++;
            }
        }

        _logger.LogInformation("Recap cleanup: removed {Count} recap(s) older than {Days} days from {Dir}", removed, Math.Max(1, retentionDays), dir);
        return removed;
    }

    private async Task<string?> GenerateAsync(Episode ep, RecapMode mode, TimeSpan pos, RecapFormat fmt, string output)
    {
        var cfg = Plugin.Instance!.Configuration;
        var clips = await _planner.PlanAsync(ep, mode, pos, fmt, cfg, CancellationToken.None).ConfigureAwait(false);
        if (clips.Count == 0)
        {
            _logger.LogInformation("Nothing to recap for {Series} {Episode} ({Mode}, {Format}): no earlier episodes, clips or summaries", ep.SeriesName, ep.Name, mode, fmt);
            return null;
        }

        _logger.LogDebug(
            "Recap plan for {Episode}: {Clips} clip(s), {Cards} text card(s)",
            ep.Name,
            clips.Count(s => s is ClipSpec),
            clips.Count(s => s is TextCardSpec));

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var tmp = output + ".tmp";
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(output))
            {
                return output;
            }

            _logger.LogInformation("Building recap {Output} from {Count} segments", output, clips.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = await _builder.BuildAsync(clips, cfg.OutputHeight, cfg.FontName, tmp,CancellationToken.None).ConfigureAwait(false);
            if (!ok)
            {
                _logger.LogWarning("Recap build failed for {Output}", output);
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }

                return null;
            }

            File.Move(tmp, output, true);
            _logger.LogInformation("Recap built in {Seconds:0.0}s: {Output}", sw.Elapsed.TotalSeconds, output);
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

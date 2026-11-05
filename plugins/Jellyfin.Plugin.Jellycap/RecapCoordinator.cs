using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>
/// Watches playback. When a matching client starts an episode, plays the recap on that session first,
/// then sends the episode back (at its original start position) when the recap ends.
/// </summary>
public sealed class RecapCoordinator : IHostedService
{
    private sealed record Pending(Guid RecapId, Guid EpisodeId, long StartTicks);

    private readonly ISessionManager _sessions;
    private readonly RecapService _recaps;
    private readonly ILogger<RecapCoordinator> _logger;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<(string Session, Guid Episode), byte> _skip = new();

    public RecapCoordinator(ISessionManager sessions, RecapService recaps, ILogger<RecapCoordinator> logger)
    {
        _sessions = sessions;
        _recaps = recaps;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackStart;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleStartAsync(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellycap start handler failed");
            }
        });
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleStopAsync(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellycap stop handler failed");
            }
        });
    }

    private async Task HandleStartAsync(PlaybackProgressEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || e.Item is not Episode episode || _recaps.IsRecapPath(episode.Path))
        {
            return;
        }

        var session = e.Session;
        if (session is null || !session.SupportsRemoteControl || !ClientMatches(cfg.ClientFilter, session, e))
        {
            return;
        }

        if (_skip.TryRemove((session.Id, episode.Id), out _))
        {
            return; // this start is us returning to the episode after the recap
        }

        var startTicks = e.PlaybackPositionTicks ?? 0;
        var pos = TimeSpan.FromTicks(startTicks);
        RecapMode mode;
        if (pos.TotalSeconds <= cfg.NewEpisodeMaxStartSeconds)
        {
            mode = RecapMode.New;
            startTicks = 0;
            pos = TimeSpan.Zero;
            if (cfg.PreGenerateNext)
            {
                var next = _recaps.Planner.GetNext(episode);
                if (next is not null)
                {
                    _ = _recaps.EnsureRecapFileAsync(next, RecapMode.New, TimeSpan.Zero);
                }
            }
        }
        else if (pos.TotalSeconds >= cfg.ResumeMinSeconds)
        {
            mode = RecapMode.Resume;
        }
        else
        {
            return;
        }

        var wait = TimeSpan.FromSeconds(Math.Max(5, cfg.MaxWaitSeconds));
        var started = DateTime.UtcNow;
        var fileTask = _recaps.EnsureRecapFileAsync(episode, mode, pos);
        if (await Task.WhenAny(fileTask, Task.Delay(wait)).ConfigureAwait(false) != fileTask)
        {
            _logger.LogInformation("Recap for {Episode} not ready in {Wait}s; skipping this time", episode.Name, wait.TotalSeconds);
            return;
        }

        var file = await fileTask.ConfigureAwait(false);
        if (file is null)
        {
            return;
        }

        var left = wait - (DateTime.UtcNow - started);
        var item = await _recaps.ResolveItemAsync(file, left > TimeSpan.FromSeconds(10) ? left : TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        if (item is null)
        {
            _logger.LogWarning("Recap {File} is not in any library. Add {Dir} as a Jellyfin library.", file, _recaps.RecapDirectory);
            return;
        }

        _pending[session.Id] = new Pending(item.Id, episode.Id, startTicks);
        await _sessions.SendPlayCommand(
            null,
            session.Id,
            new PlayRequest { ItemIds = new[] { item.Id }, PlayCommand = PlayCommand.PlayNow, StartPositionTicks = 0 },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleStopAsync(PlaybackStopEventArgs e)
    {
        var session = e.Session;
        if (session is null || e.Item is null || !_pending.TryGetValue(session.Id, out var pending) || pending.RecapId != e.Item.Id)
        {
            return;
        }

        _pending.TryRemove(session.Id, out _);

        var runtime = e.Item.RunTimeTicks ?? 0;
        var finished = e.PlayedToCompletion || (runtime > 0 && (e.PlaybackPositionTicks ?? 0) >= runtime * 0.9);
        if (!finished)
        {
            return; // user backed out of the recap: do not force the episode
        }

        _skip[(session.Id, pending.EpisodeId)] = 0;
        await _sessions.SendPlayCommand(
            null,
            session.Id,
            new PlayRequest { ItemIds = new[] { pending.EpisodeId }, PlayCommand = PlayCommand.PlayNow, StartPositionTicks = pending.StartTicks },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static bool ClientMatches(string filter, SessionInfo session, PlaybackProgressEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var haystack = $"{session.Client} {session.DeviceName} {e.ClientName} {e.DeviceName}";
        return filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(f => haystack.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}

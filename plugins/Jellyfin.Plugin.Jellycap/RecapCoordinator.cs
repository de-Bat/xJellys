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
        _logger.LogInformation("Jellycap started (enabled: {Enabled}, recap folder: {Dir})", Plugin.Instance?.Configuration.Enabled, _recaps.RecapDirectory);
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
        if (session is null)
        {
            _logger.LogDebug("No recap for {Episode}: playback has no session", episode.Name);
            return;
        }

        if (!session.SupportsRemoteControl)
        {
            _logger.LogDebug("No recap for {Episode}: client {Client} on {Device} does not support remote control", episode.Name, session.Client, session.DeviceName);
            return;
        }

        if (!ClientMatches(cfg.ClientFilter, session, e))
        {
            _logger.LogDebug("No recap for {Episode}: client {Client} on {Device} does not match filter '{Filter}'", episode.Name, session.Client, session.DeviceName, cfg.ClientFilter);
            return;
        }

        if (_skip.TryRemove((session.Id, episode.Id), out _))
        {
            _logger.LogDebug("Episode {Episode} started after its recap; not recapping again", episode.Name);
            return; // this start is us returning to the episode after the recap
        }

        var decision = RecapRules.Decide(episode.SeriesId, episode.ParentIndexNumber, episode.IndexNumber, cfg);
        if (!decision.Allowed)
        {
            _logger.LogDebug("No recap for {Series} {Episode}: excluded by series rules", episode.SeriesName, episode.Name);
            return;
        }

        var fmt = decision.Format;
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
                    var nextDecision = RecapRules.Decide(next.SeriesId, next.ParentIndexNumber, next.IndexNumber, cfg);
                    if (nextDecision.Allowed)
                    {
                        _ = _recaps.EnsureRecapFileAsync(next, RecapMode.New, TimeSpan.Zero, nextDecision.Format);
                    }
                }
            }
        }
        else if (pos.TotalSeconds >= cfg.ResumeMinSeconds)
        {
            mode = RecapMode.Resume;
        }
        else
        {
            _logger.LogDebug(
                "No recap for {Episode}: start position {Pos} is past the new-episode window ({New}s) but before the resume threshold ({Resume}s)",
                episode.Name,
                pos,
                cfg.NewEpisodeMaxStartSeconds,
                cfg.ResumeMinSeconds);
            return;
        }

        _logger.LogDebug("Recap wanted for {Episode}: mode {Mode}, format {Format}, position {Pos}", episode.Name, mode, fmt, pos);
        var wait = TimeSpan.FromSeconds(Math.Max(5, cfg.MaxWaitSeconds));
        var started = DateTime.UtcNow;
        var fileTask = _recaps.EnsureRecapFileAsync(episode, mode, pos, fmt);
        if (await Task.WhenAny(fileTask, Task.Delay(wait)).ConfigureAwait(false) != fileTask)
        {
            _logger.LogInformation("Recap for {Episode} not ready in {Wait}s; skipping this time", episode.Name, wait.TotalSeconds);
            return;
        }

        var file = await fileTask.ConfigureAwait(false);
        if (file is null)
        {
            _logger.LogInformation("No recap for {Episode}: nothing to recap or generation failed", episode.Name);
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
        _logger.LogInformation("Playing {Mode} recap for {Episode} on {Device} ({Client})", mode, episode.Name, session.DeviceName, session.Client);
        await _sessions.SendPlayCommand(
            null,
            session.Id,
            new PlayRequest { ItemIds = new[] { item.Id }, PlayCommand = PlayCommand.PlayNow, StartPositionTicks = 0 },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleStopAsync(PlaybackStopEventArgs e)
    {
        var session = e.Session;
        if (session is null || e.Item is null)
        {
            return;
        }

        if (!_pending.TryGetValue(session.Id, out var pending) || pending.RecapId != e.Item.Id)
        {
            PreBuildAfterStop(e, session);
            return;
        }

        _pending.TryRemove(session.Id, out _);

        var runtime = e.Item.RunTimeTicks ?? 0;
        var finished = e.PlayedToCompletion || (runtime > 0 && (e.PlaybackPositionTicks ?? 0) >= runtime * 0.9);
        if (!finished)
        {
            _logger.LogInformation("Recap stopped early on {Device}; not starting the episode", session.DeviceName);
            return; // user backed out of the recap: do not force the episode
        }

        _logger.LogInformation("Recap finished on {Device}; starting the episode at {Pos}", session.DeviceName, TimeSpan.FromTicks(pending.StartTicks));
        _skip[(session.Id, pending.EpisodeId)] = 0;
        await _sessions.SendPlayCommand(
            null,
            session.Id,
            new PlayRequest { ItemIds = new[] { pending.EpisodeId }, PlayCommand = PlayCommand.PlayNow, StartPositionTicks = pending.StartTicks },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// An episode stopped: build the recap ahead of time so it is ready next time.
    /// Stopped mid-way -> resume recap at that position. Finished -> new-episode recap for the next episode.
    /// </summary>
    private void PreBuildAfterStop(PlaybackStopEventArgs e, SessionInfo session)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.PreBuildOnStop || e.Item is not Episode episode
            || _recaps.IsRecapPath(episode.Path) || !ClientMatches(cfg.ClientFilter, session, e))
        {
            return;
        }

        var ticks = e.PlaybackPositionTicks ?? 0;
        var runtime = episode.RunTimeTicks ?? 0;
        var finished = e.PlayedToCompletion || (runtime > 0 && ticks >= runtime * 0.9);

        if (finished)
        {
            var next = _recaps.Planner.GetNext(episode);
            if (next is null)
            {
                return;
            }

            var d = RecapRules.Decide(next.SeriesId, next.ParentIndexNumber, next.IndexNumber, cfg);
            if (d.Allowed)
            {
                _logger.LogInformation("Pre-building recap for next episode {Episode}", next.Name);
                _ = _recaps.EnsureRecapFileAsync(next, RecapMode.New, TimeSpan.Zero, d.Format);
            }
        }
        else
        {
            var pos = TimeSpan.FromTicks(ticks);
            if (pos.TotalSeconds < cfg.ResumeMinSeconds)
            {
                return;
            }

            var d = RecapRules.Decide(episode.SeriesId, episode.ParentIndexNumber, episode.IndexNumber, cfg);
            if (d.Allowed)
            {
                _logger.LogInformation("Pre-building resume recap for {Episode} at {Pos}", episode.Name, pos);
                _ = _recaps.EnsureRecapFileAsync(episode, RecapMode.Resume, pos, d.Format);
            }
        }
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

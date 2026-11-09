using System.Collections.Concurrent;
using Jellyfin.Plugin.Jellycap.Configuration;
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
    private readonly OverlayStore _overlays;
    private readonly ILogger<RecapCoordinator> _logger;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<(string Session, Guid Episode), byte> _skip = new();

    public RecapCoordinator(ISessionManager sessions, RecapService recaps, OverlayStore overlays, ILogger<RecapCoordinator> logger)
    {
        _sessions = sessions;
        _recaps = recaps;
        _overlays = overlays;
        _logger = PluginLog.Wrap(logger);
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
        var companion = UsesCompanion(cfg, session);
        var overlay = companion && fmt != RecapFormat.Video;
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
                    if (nextDecision.Allowed && VideoFormat(nextDecision.Format, companion) is { } nextFmt)
                    {
                        _ = _recaps.EnsureRecapFileAsync(next, RecapMode.New, TimeSpan.Zero, nextFmt);
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

        _logger.LogDebug("Recap wanted for {Episode}: mode {Mode}, format {Format}, position {Pos}, JellyXR TV overlay {Overlay}", episode.Name, mode, fmt, pos, overlay);
        var wait = TimeSpan.FromSeconds(Math.Max(5, cfg.MaxWaitSeconds));
        if (overlay && fmt == RecapFormat.Text)
        {
            await PrepareTextOverlayAsync(session, episode, mode, pos, startTicks, wait, cfg).ConfigureAwait(false);
            return;
        }

        var started = DateTime.UtcNow;
        // Both + JellyXR TV: the video has the clips only, the app draws each text card over its clip
        var fileTask = _recaps.EnsureRecapFileAsync(episode, mode, pos, overlay ? RecapFormat.Video : fmt);
        var cardsTask = overlay ? _recaps.Planner.PlanAsync(episode, mode, pos, RecapFormat.Both, cfg, CancellationToken.None) : null;
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

        if (cardsTask is not null)
        {
            await ShareCardsAsync(session, item.Id, cardsTask, wait - (DateTime.UtcNow - started)).ConfigureAwait(false);
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

        var companion = UsesCompanion(cfg, session);
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
            if (d.Allowed && VideoFormat(d.Format, companion) is { } f)
            {
                _logger.LogInformation("Pre-building recap for next episode {Episode}", next.Name);
                _ = _recaps.EnsureRecapFileAsync(next, RecapMode.New, TimeSpan.Zero, f);
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
            if (d.Allowed && VideoFormat(d.Format, companion) is { } f)
            {
                _logger.LogInformation("Pre-building resume recap for {Episode} at {Pos}", episode.Name, pos);
                _ = _recaps.EnsureRecapFileAsync(episode, RecapMode.Resume, pos, f);
            }
        }
    }

    /// <summary>Text recap on the JellyXR TV overlay: no video, the app pauses the episode and shows the cards.</summary>
    private async Task PrepareTextOverlayAsync(SessionInfo session, Episode episode, RecapMode mode, TimeSpan pos, long startTicks, TimeSpan wait, PluginConfiguration cfg)
    {
        var entry = new OverlayRecap { ItemId = episode.Id, SessionId = session.Id, Mode = OverlayMode.Pause, ResumeTicks = startTicks };
        _overlays.Set(session.DeviceId, entry);
        try
        {
            var planTask = _recaps.Planner.PlanAsync(episode, mode, pos, RecapFormat.Text, cfg, CancellationToken.None);
            if (await Task.WhenAny(planTask, Task.Delay(wait)).ConfigureAwait(false) != planTask)
            {
                _logger.LogInformation("Text recap for {Episode} not ready in {Wait}s; skipping this time", episode.Name, wait.TotalSeconds);
                return;
            }

            var cards = OverlayStore.Sequential(await planTask.ConfigureAwait(false));
            if (cards.Count == 0)
            {
                _logger.LogInformation("No recap for {Episode}: nothing to recap", episode.Name);
                return;
            }

            entry.Cards = cards;
            entry.Ready = true;
            _logger.LogInformation("Text recap for {Episode} ready for the JellyXR TV overlay on {Device} ({Cards} card(s))", episode.Name, session.DeviceName, cards.Count);
        }
        finally
        {
            if (!entry.Ready)
            {
                _overlays.Remove(session.DeviceId, entry);
            }
        }
    }

    /// <summary>Both recap on the JellyXR TV overlay: hands the app the cards to draw over the clips-only video.</summary>
    private async Task ShareCardsAsync(SessionInfo session, Guid recapItemId, Task<IReadOnlyList<RecapSegment>> plan, TimeSpan left)
    {
        try
        {
            var limit = left > TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5);
            if (await Task.WhenAny(plan, Task.Delay(limit)).ConfigureAwait(false) != plan)
            {
                _logger.LogInformation("Recap text cards not ready in time; playing the clips without them");
                return;
            }

            var cards = OverlayStore.OverClips(await plan.ConfigureAwait(false));
            if (cards.Count > 0)
            {
                _overlays.Set(session.DeviceId, new OverlayRecap { ItemId = recapItemId, SessionId = session.Id, Mode = OverlayMode.Timed, Cards = cards, Ready = true });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recap text cards failed; playing the clips without them");
        }
    }

    private static bool UsesCompanion(PluginConfiguration cfg, SessionInfo session)
        => cfg.TvOverlay && CompanionRegistry.IsActive(session.DeviceId);

    /// <summary>Format of the video to build. With the JellyXR TV overlay the text lives in the app: Text = no video, Both = clips only.</summary>
    private static RecapFormat? VideoFormat(RecapFormat fmt, bool companion)
        => !companion ? fmt : fmt == RecapFormat.Text ? null : RecapFormat.Video;

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

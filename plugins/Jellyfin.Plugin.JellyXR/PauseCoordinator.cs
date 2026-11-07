using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyXR.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Watches playback; on pause sends cast cards to that client as DisplayMessage commands.</summary>
public sealed class PauseCoordinator : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly CardService _cards;
    private readonly FaceScanner _scanner;
    private readonly XrayarrClient _xrayarr;
    private readonly ILogger<PauseCoordinator> _logger;
    private readonly ConcurrentDictionary<string, bool> _paused = new();

    public PauseCoordinator(ISessionManager sessions, CardService cards, FaceScanner scanner, XrayarrClient xrayarr, ILogger<PauseCoordinator> logger)
    {
        _xrayarr = xrayarr;
        _sessions = sessions;
        _cards = cards;
        _scanner = scanner;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        var cfg = Plugin.Instance?.Configuration;
        _logger.LogInformation("JellyXR started (enabled: {Enabled}, data source: {Source})", cfg?.Enabled, cfg?.DataSource);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        return Task.CompletedTask;
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (e.Session is not null)
        {
            _paused[e.Session.Id] = false;
        }

        if (cfg is not { Enabled: true } || e.Item is not (Episode or Movie))
        {
            return;
        }

        _logger.LogDebug("Playback started: {Item} on {Device} ({Client})", e.Item.Name, e.Session?.DeviceName, e.Session?.Client);

        // warm the Xrayarr cache so the first pause needs no network round trip
        if (cfg.DataSource != DataSource.Local && XrayarrClient.Configured(cfg))
        {
            _ = _xrayarr.GetAsync(e.Item, CancellationToken.None);
        }

        if (cfg.ScanOnPlayback)
        {
            _scanner.Enqueue(e.Item);
        }
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session is not null)
        {
            _paused.TryRemove(e.Session.Id, out _);
        }
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        if (session is null)
        {
            return;
        }

        var wasPaused = _paused.TryGetValue(session.Id, out var p) && p;
        var isPaused = e.IsPaused;
        _paused[session.Id] = isPaused;
        if (!isPaused || wasPaused)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ShowAsync(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyXR pause handler failed");
            }
        });
    }

    private async Task ShowAsync(PlaybackProgressEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        var session = e.Session;
        if (cfg is null || !cfg.Enabled || session is null || e.Item is null)
        {
            return;
        }

        if (!session.SupportsRemoteControl)
        {
            _logger.LogDebug("No cards for {Item}: client {Client} on {Device} does not support remote control", e.Item.Name, session.Client, session.DeviceName);
            return;
        }

        if (!ClientMatches(cfg.ClientFilter, session, e))
        {
            _logger.LogDebug("No cards for {Item}: client {Client} on {Device} does not match filter '{Filter}'", e.Item.Name, session.Client, session.DeviceName, cfg.ClientFilter);
            return;
        }

        var pos = TimeSpan.FromTicks(e.PlaybackPositionTicks ?? 0);
        var set = await _cards.BuildAsync(e.Item, e.PlaybackPositionTicks ?? 0, cfg, CancellationToken.None).ConfigureAwait(false);
        if (set is null || set.Value.Cards.Count == 0)
        {
            _logger.LogDebug("No cards for {Item} at {Pos}: nobody to show", e.Item.Name, pos);
            return;
        }

        _logger.LogInformation(
            "Paused {Item} at {Pos} on {Device}: showing {Count} card(s) ({Header})",
            e.Item.Name,
            pos,
            session.DeviceName,
            set.Value.Cards.Count,
            set.Value.Header);
        var timeout = Math.Max(2, cfg.CardSeconds) * 1000;
        if (cfg.CardMode == CardMode.Combined)
        {
            var text = string.Join("\n", set.Value.Cards.Select(c => CardService.Format(c, cfg.ShowCharacter)));
            await Send(session.Id, set.Value.Header, text, timeout).ConfigureAwait(false);
            return;
        }

        foreach (var card in set.Value.Cards)
        {
            // stop if the user resumed in the meantime
            if (!_paused.TryGetValue(session.Id, out var still) || !still)
            {
                _logger.LogDebug("Playback resumed on {Device}; stopping cards", session.DeviceName);
                return;
            }

            await Send(session.Id, set.Value.Header, CardService.Format(card, cfg.ShowCharacter), timeout).ConfigureAwait(false);
            await Task.Delay(timeout).ConfigureAwait(false);
        }
    }

    private Task Send(string sessionId, string header, string text, int timeoutMs)
    {
        return _sessions.SendMessageCommand(
            null,
            sessionId,
            new MessageCommand { Header = header, Text = text, TimeoutMs = timeoutMs },
            CancellationToken.None);
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

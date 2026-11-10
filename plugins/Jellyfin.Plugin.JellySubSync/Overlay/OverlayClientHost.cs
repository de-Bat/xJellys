using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellySubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xjellys.Overlay;

namespace Jellyfin.Plugin.JellySubSync.Overlay;

/// <summary>What the client host reads from a Jellyfin session.</summary>
internal sealed record SessionView(string Id, Guid? NowPlayingItemId, int? SubtitleStreamIndex, string? MediaSourceId, bool CanSetSubtitle, bool IsPaused = false);

/// <summary>A remote command to a session: <c>Stop</c> or <c>SetSubtitleStreamIndex</c> (with Index). Never PlayNow.</summary>
internal sealed record SessionCommand(string Name, int? Index);

/// <summary>
/// JellySubSync as a client of the JellyTvOverlay host. A double pause on an item in the sync scope, on a device
/// running the JellyTvOverlay app, offers "Sync subtitles"; chosen, the sync box is shown. OK/play applies on the
/// server, stops playback and waits until the session reports nothing playing; the viewer resumes from the item page
/// and the next playback samples switch the player to the synced track (SetSubtitleStreamIndex, retried).
/// No remote PlayNow: Jellyfin for Android TV crashes on it (JellyXR TV 1.2.2). Without the host or the app: nothing
/// (the old JellySubSync endpoints keep serving the JellyXR TV app).
/// </summary>
public sealed class OverlayClientHost : IHostedService
{
    public const string ClientId = "JellySubSync";
    public const string DisplayName = "Sync subtitles";
    public const string OfferId = "sync";

    /// <summary>JellyXR TV WatchService STOP_WAIT_MS: longest wait for the session to report nothing playing after Stop.</summary>
    public const int StopWaitMs = 6000;

    /// <summary>Session poll interval while waiting after Stop (as WatchService.stopAndWait).</summary>
    public const int StopPollMs = 300;

    /// <summary>Session poll interval while a subtitle switch is pending (JellyXR TV FAST_POLL_MS).</summary>
    public const int SwitchPollMs = 400;

    /// <summary>Every remote command this client sends. No PlayNow.</summary>
    internal static readonly string[] CommandNames = { "Stop", "SetSubtitleStreamIndex" };

    internal const string NoTrackText = "Turn on a subtitle track in the player first.";
    internal const string PickHintText = "Pick the \"Synced\" track in the player's subtitle menu.";

    private static readonly string OfferJson = new JsonObject
    {
        ["offer"] = new JsonObject { ["id"] = OfferId, ["label"] = DisplayName },
    }.ToJsonString();

    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly SubSyncService _sync;
    private readonly ILogger<OverlayClientHost> _logger;
    private readonly object _queueGate = new();
    private readonly ConcurrentDictionary<string, string> _userOf = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BoxContext> _boxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private Task _tail = Task.CompletedTask;
    private int _timersRunning;

    public OverlayClientHost(ISessionManager sessions, ILibraryManager library, IUserManager users, SubSyncService sync, ILogger<OverlayClientHost> logger)
    {
        _sessions = sessions;
        _library = library;
        _users = users;
        _sync = sync;
        _logger = PluginLog.Wrap(logger);
        Clock = TimeProvider.System;
        Config = () => Plugin.Instance?.Configuration;
        AppPresent = d => EnsureBound()?.HasApp(d) == true;
        ShowLayout = (d, json) => EnsureBound()?.Show(d, json) == true;
        Allowed = ItemAllowed;
        IsAdmin = UserIsAdmin;
        FindSession = FindSessionView;
        GetTrack = TrackOf;
        Apply = ApplyCoreAsync;
        SendCommand = SendCoreAsync;
    }

    // Seams for tests; production values are set in the constructor.
    internal TimeProvider Clock { get; set; }

    internal Func<PluginConfiguration?> Config { get; set; }

    internal Func<string, bool> AppPresent { get; set; }

    internal Func<string, string, bool> ShowLayout { get; set; }

    /// <summary>Item id → in the sync scope (SyncScopeFilter).</summary>
    internal Func<Guid, bool> Allowed { get; set; }

    /// <summary>User id → administrator (for AdminsOnly).</summary>
    internal Func<string?, bool> IsAdmin { get; set; }

    internal Func<string?, SessionView?> FindSession { get; set; }

    /// <summary>(item, media source, stream index) → track state; null = not a video.</summary>
    internal Func<Guid, string?, int, TrackState?> GetTrack { get; set; }

    /// <summary>(item, media source, stream index, offset ms) → stream index to play.</summary>
    internal Func<Guid, string?, int, long, CancellationToken, Task<int>> Apply { get; set; }

    internal Func<string, SessionCommand, Task> SendCommand { get; set; }

    /// <summary>The latest apply sequence started (tests).</summary>
    internal Task? LastApply { get; private set; }

    /// <summary>Completes when the queued host events handled so far are done (tests).</summary>
    internal Task Idle()
    {
        lock (_queueGate)
        {
            return _tail;
        }
    }

    internal bool HasPending(string deviceId) => _pending.TryGetValue(deviceId, out var p) && p.Switch.Active;

    /// <summary>Switch poll timers running (tests).</summary>
    internal int TimersRunning => Volatile.Read(ref _timersRunning);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bound = EnsureBound() is not null;
        _sessions.PlaybackStart += OnPlayback;
        _sessions.PlaybackProgress += OnPlayback;
        _logger.LogInformation("JellyTvOverlay host {State}", bound ? "found: the sync box goes through it" : "not found yet: no sync box from the server until it loads");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sessions is not null)
        {
            _sessions.PlaybackStart -= OnPlayback;
            _sessions.PlaybackProgress -= OnPlayback;
        }

        foreach (var (deviceId, p) in _pending)
        {
            Finish(deviceId, p);
        }

        return Task.CompletedTask;
    }

    /// <summary>Binds to the host (rescans at most every ~5 s while it is missing). The host may load after JellySubSync.</summary>
    internal OverlayClient? EnsureBound() => OverlayClient.EnsureBound(ClientId, DisplayName, OnEventAsync);

    /// <summary>Host event handler. Answers offers at once; everything else runs on the serial queue after it returns.</summary>
    internal Task<string?> OnEventAsync(string json)
    {
        try
        {
            return Task.FromResult(Handle(json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("JellyTvOverlay event failed: {Error}", ex.Message);
            return Task.FromResult<string?>(null);
        }
    }

    private string? Handle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        var deviceId = Str(e, "deviceId");
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        switch (Str(e, "type"))
        {
            case "trigger":
                // "chosen" carries no user: the box uses the user of the trigger that offered it
                if (Str(e, "userId") is { Length: > 0 } userId)
                {
                    _userOf[deviceId] = userId;
                }

                return Str(e, "trigger") == "doublePause" && Offers(deviceId, Str(e, "itemId")) ? OfferJson : null;

            case "chosen":
                if (Str(e, "offerId") == OfferId)
                {
                    var sessionId = Str(e, "sessionId");
                    var itemId = Str(e, "itemId");
                    Enqueue(() => OpenBox(deviceId, sessionId, itemId));
                }

                return null;

            case "action":
                if (Str(e, "layoutId") == ClientId + "/" + SyncLayoutBuilder.LayoutId && Str(e, "action") == SyncLayoutBuilder.ApplyAction)
                {
                    var offset = e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object
                        && v.TryGetProperty(SyncLayoutBuilder.ValueId, out var o) && o.ValueKind == JsonValueKind.Number
                        ? (long)Math.Round(o.GetDouble())
                        : 0;
                    Enqueue(() => StartApply(deviceId, offset));
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Sync on, the device has the app, the item is in the scope, the user may sync, and no apply or switch is under way.</summary>
    private bool Offers(string deviceId, string? itemId)
    {
        var cfg = Config();
        if (cfg is null || !AppPresent(deviceId) || !Guid.TryParse(itemId, out var id))
        {
            return false;
        }

        if (HasPending(deviceId) || (_boxes.TryGetValue(deviceId, out var box) && box.Applying))
        {
            _logger.LogDebug("No sync offer on {Device}: a sync is still under way", deviceId);
            return false;
        }

        if (!Allowed(id))
        {
            _logger.LogDebug("No sync offer on {Device}: {Item} is not in the sync scope", deviceId, id);
            return false;
        }

        if (cfg.AdminsOnly && !IsAdmin(_userOf.TryGetValue(deviceId, out var u) ? u : null))
        {
            _logger.LogDebug("No sync offer on {Device}: only administrators may sync", deviceId);
            return false;
        }

        return true;
    }

    /// <summary>Chosen: reads the subtitle track and media source from the session, then shows the box (or why not).</summary>
    private Task OpenBox(string deviceId, string? sessionId, string? itemId)
    {
        var cfg = Config();
        if (cfg is null || !Guid.TryParse(itemId, out var item) || FindSession(sessionId) is not { } s)
        {
            _logger.LogDebug("No sync box on {Device}: session {Session} or item {Item} not found", deviceId, sessionId, itemId);
            return Task.CompletedTask;
        }

        var sub = s.SubtitleStreamIndex is >= 0 ? s.SubtitleStreamIndex.Value : -1;
        _boxes[deviceId] = new BoxContext(s.Id, item, s.MediaSourceId, sub, s.CanSetSubtitle);
        if (sub < 0)
        {
            Show(deviceId, SyncLayoutBuilder.Message(NoTrackText, busy: false, failed: true));
            return Task.CompletedTask;
        }

        try
        {
            var track = GetTrack(item, s.MediaSourceId, sub);
            if (track is not { } t)
            {
                _logger.LogDebug("No sync box on {Device}: {Item} is not a video", deviceId, item);
                return Task.CompletedTask;
            }

            if (!t.Supported)
            {
                Show(deviceId, SyncLayoutBuilder.Message(t.Reason ?? "This track can't be shifted.", busy: false, failed: true));
                return Task.CompletedTask;
            }

            var max = Math.Max(1, cfg.MaxOffsetSeconds) * 1000L;
            Show(deviceId, SyncLayoutBuilder.Box(t.Label, t.OffsetMs, max));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle track lookup failed for {Item}, stream {Index}", item, sub);
            Show(deviceId, SyncLayoutBuilder.Message(ex is SubSyncException ? ex.Message : "Subtitle sync failed on the server: " + ex.Message, busy: false, failed: true));
        }

        return Task.CompletedTask;
    }

    /// <summary>OK/play on the box: the apply sequence runs in the background (ffmpeg and the stop wait take seconds).</summary>
    private Task StartApply(string deviceId, long offsetMs)
    {
        if (!_boxes.TryGetValue(deviceId, out var box) || box.Applying || box.SubtitleIndex < 0)
        {
            _logger.LogDebug("Sync apply on {Device} ignored: no box open, or already applying", deviceId);
            return Task.CompletedTask;
        }

        box.Applying = true;
        LastApply = Task.Run(async () =>
        {
            try
            {
                await ApplySequenceAsync(deviceId, box, offsetMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subtitle sync sequence failed on {Device}", deviceId);
            }
            finally
            {
                box.Applying = false;

                // the box was replaced by a message: its context is used up
                _boxes.TryRemove(new KeyValuePair<string, BoxContext>(deviceId, box));
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// As WatchService.applySync: shift on the server, stop the player, wait until the session reports nothing playing
    /// (or another item), then wait for the viewer to resume and switch the player to the synced track.
    /// </summary>
    private async Task ApplySequenceAsync(string deviceId, BoxContext box, long offsetMs)
    {
        Show(deviceId, SyncLayoutBuilder.Message(offsetMs == 0 ? "Back to the original subtitles…" : "Syncing subtitles…", busy: true));
        int index;
        try
        {
            index = await Apply(box.ItemId, box.MediaSourceId, box.SubtitleIndex, offsetMs, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = ex is SubSyncException ? ex.Message : "Subtitle sync failed on the server: " + ex.Message;
            _logger.LogInformation("Sync failed on {Device}: {Reason}", deviceId, msg);
            Show(deviceId, SyncLayoutBuilder.Message(msg, busy: false, failed: true));
            return;
        }

        Show(deviceId, SyncLayoutBuilder.Message("Stopping playback…", busy: true));
        await StopAndWaitAsync(box).ConfigureAwait(false);

        StartPending(deviceId, box.SessionId, box.ItemId, index, box.CanSetSubtitle);

        Show(deviceId, SyncLayoutBuilder.Message(
            "Subtitles " + SyncLayoutBuilder.Format(offsetMs) + (offsetMs == 0 ? " (original)" : string.Empty) + ". Press play to resume.",
            busy: false));
        _logger.LogInformation("Synced subtitles on {Device} to {Offset} ms: stream {Index} once the viewer resumes", deviceId, offsetMs, index);
    }

    /// <summary>As WatchService.stopAndWait: Stop, then poll the session every 300 ms for up to STOP_WAIT_MS.</summary>
    private async Task StopAndWaitAsync(BoxContext box)
    {
        try
        {
            await SendCommand(box.SessionId, new SessionCommand("Stop", null)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Stop before the restart failed: {Error}", ex.Message);
            return;
        }

        var until = Clock.GetUtcNow().AddMilliseconds(StopWaitMs);
        while (Clock.GetUtcNow() < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(StopPollMs), Clock).ConfigureAwait(false);
            try
            {
                var s = FindSession(box.SessionId);
                if (s?.NowPlayingItemId is not { } now || now != box.ItemId)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Session check after stop failed: {Error}", ex.Message);
            }
        }

        _logger.LogWarning("Player still reports the item {Ms} ms after stop", StopWaitMs);
    }

    private void OnPlayback(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (e.Session is not { } session)
            {
                return;
            }

            var deviceId = string.IsNullOrEmpty(e.DeviceId) ? session.DeviceId : e.DeviceId;
            if (string.IsNullOrEmpty(deviceId) || !_pending.ContainsKey(deviceId))
            {
                return;
            }

            _ = OnProgressAsync(deviceId, session.Id, e.Item?.Id, e.IsPaused, session.PlayState?.SubtitleStreamIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Playback sample for the subtitle switch failed: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Starts the switch for a device and its ~400 ms session poll (as JellyXR TV's fast poll): progress reports alone
    /// come only every ~10 s. Playback events stay an extra input. Replaces a previous switch of the device.
    /// </summary>
    private void StartPending(string deviceId, string sessionId, Guid itemId, int index, bool canSet)
    {
        var pending = new Pending(new PendingSubtitleSwitch(Clock), sessionId);
        lock (pending)
        {
            pending.Switch.Start(itemId, index, canSet);
        }

        if (_pending.TryRemove(deviceId, out var old))
        {
            StopTimer(old);
        }

        _pending[deviceId] = pending;
        var period = TimeSpan.FromMilliseconds(SwitchPollMs);
        var timer = Clock.CreateTimer(_ => _ = PollAsync(deviceId, pending), null, period, period);
        Interlocked.Increment(ref _timersRunning);
        lock (pending)
        {
            pending.Timer = timer;
        }

        if (!_pending.TryGetValue(deviceId, out var cur) || !ReferenceEquals(cur, pending))
        {
            StopTimer(pending); // ended or replaced before the timer was stored
        }
    }

    /// <summary>Timer tick: the device's session as one sample (nothing playing when the session is gone, so the give-up still ends it).</summary>
    private Task PollAsync(string deviceId, Pending pending)
    {
        SessionView? s = null;
        try
        {
            s = FindSession(pending.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Session poll for the subtitle switch failed: {Error}", ex.Message);
        }

        return SampleAsync(deviceId, pending, s?.Id, s?.NowPlayingItemId, s?.IsPaused ?? false, s?.SubtitleStreamIndex);
    }

    /// <summary>A playback event of a device: drives its pending switch to the synced track.</summary>
    internal Task OnProgressAsync(string deviceId, string? sessionId, Guid? itemId, bool paused, int? currentSub)
        => _pending.TryGetValue(deviceId, out var pending)
            ? SampleAsync(deviceId, pending, sessionId, itemId, paused, currentSub)
            : Task.CompletedTask;

    private async Task SampleAsync(string deviceId, Pending pending, string? sessionId, Guid? itemId, bool paused, int? currentSub)
    {
        try
        {
            SwitchDecision d;
            string target;
            lock (pending)
            {
                d = pending.Switch.OnSample(itemId, paused, currentSub);
                target = string.IsNullOrEmpty(sessionId) ? pending.SessionId : sessionId;
            }

            switch (d.Kind)
            {
                case SwitchKind.SendSet:
                    _logger.LogDebug("Asking {Device} to switch to subtitle stream {Index}", deviceId, d.Index);
                    try
                    {
                        await SendCommand(target, new SessionCommand("SetSubtitleStreamIndex", d.Index)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("SetSubtitleStreamIndex failed: {Error}", ex.Message);
                    }

                    break;
                case SwitchKind.Done:
                    _logger.LogInformation("{Device} plays the synced subtitle track", deviceId);
                    Finish(deviceId, pending);
                    break;
                case SwitchKind.GiveUp:
                    _logger.LogInformation("Gave up switching {Device} to the synced track{Hint}", deviceId, d.ShowHint ? ": asking the viewer" : string.Empty);
                    Finish(deviceId, pending);
                    if (d.ShowHint)
                    {
                        Show(deviceId, SyncLayoutBuilder.Message(PickHintText, busy: false, failed: true));
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Subtitle switch sample on {Device} failed: {Error}", deviceId, ex.Message);
        }
    }

    /// <summary>The switch ended (or the plugin stops): stop its timer and drop the device's entries.</summary>
    private void Finish(string deviceId, Pending pending)
    {
        StopTimer(pending);
        if (_pending.TryRemove(new KeyValuePair<string, Pending>(deviceId, pending)))
        {
            _boxes.TryRemove(deviceId, out _);
            _userOf.TryRemove(deviceId, out _);
        }
    }

    private void StopTimer(Pending pending)
    {
        ITimer? t;
        lock (pending)
        {
            t = pending.Timer;
            pending.Timer = null;
        }

        if (t is not null)
        {
            t.Dispose();
            Interlocked.Decrement(ref _timersRunning);
        }
    }

    private void Show(string deviceId, JsonObject layout)
    {
        if (!ShowLayout(deviceId, layout.ToJsonString(OverlayJson.Options)))
        {
            _logger.LogDebug("Sync layout on {Device} rejected by the host", deviceId);
        }
    }

    /// <summary>Runs host events one after another, in arrival order.</summary>
    private void Enqueue(Func<Task> work)
    {
        lock (_queueGate)
        {
            _tail = _tail.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await work().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "JellySubSync overlay event failed");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    // ---- production seams ----

    private bool ItemAllowed(Guid itemId)
    {
        var cfg = Config();
        return cfg is not null && _library.GetItemById(itemId) is Video item && SyncScopeFilter.Applies(item, cfg, _library);
    }

    private bool UserIsAdmin(string? userId) => Guid.TryParse(userId, out var uid) && ServerCompat.IsAdministrator(_users, uid);

    private SessionView? FindSessionView(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)
            || _sessions.Sessions.FirstOrDefault(x => string.Equals(x.Id, sessionId, StringComparison.Ordinal)) is not { } s)
        {
            return null;
        }

        return new SessionView(
            s.Id,
            s.NowPlayingItem?.Id,
            s.PlayState?.SubtitleStreamIndex,
            s.PlayState?.MediaSourceId,
            ServerCompat.SupportsCommand(s, "SetSubtitleStreamIndex"),
            s.PlayState?.IsPaused ?? false);
    }

    /// <summary>The playing version (media source) when given, as SubSyncController.Find.</summary>
    private Video? VideoOf(Guid itemId, string? mediaSourceId)
    {
        var id = Guid.TryParse(mediaSourceId, out var ms) && ms != Guid.Empty ? ms : itemId;
        return _library.GetItemById(id) as Video;
    }

    private TrackState? TrackOf(Guid itemId, string? mediaSourceId, int index)
        => VideoOf(itemId, mediaSourceId) is { } v ? _sync.GetState(v, index) : null;

    private Task<int> ApplyCoreAsync(Guid itemId, string? mediaSourceId, int index, long offsetMs, CancellationToken ct)
    {
        var v = VideoOf(itemId, mediaSourceId) ?? throw new SubSyncException("This title is gone from the library.");
        _logger.LogInformation("Sync {Item} stream {Index} to {Offset} ms (JellyTvOverlay)", v.Name, index, offsetMs);
        return _sync.ApplyAsync(v, index, offsetMs, ct);
    }

    private Task SendCoreAsync(string sessionId, SessionCommand cmd) => cmd.Name switch
    {
        "Stop" => _sessions.SendPlaystateCommand(null, sessionId, new PlaystateRequest { Command = PlaystateCommand.Stop }, CancellationToken.None),
        "SetSubtitleStreamIndex" when cmd.Index is { } i => _sessions.SendGeneralCommand(
            null, sessionId, ServerCompat.SetSubtitleCommand(i.ToString(CultureInfo.InvariantCulture)), CancellationToken.None),
        _ => throw new InvalidOperationException("JellySubSync sends no " + cmd.Name),
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>What was playing when the box opened.</summary>
    private sealed class BoxContext(string sessionId, Guid itemId, string? mediaSourceId, int subtitleIndex, bool canSetSubtitle)
    {
        public string SessionId { get; } = sessionId;

        public Guid ItemId { get; } = itemId;

        public string? MediaSourceId { get; } = mediaSourceId;

        public int SubtitleIndex { get; } = subtitleIndex;

        public bool CanSetSubtitle { get; } = canSetSubtitle;

        public volatile bool Applying;
    }

    private sealed class Pending(PendingSubtitleSwitch sw, string sessionId)
    {
        public PendingSubtitleSwitch Switch { get; } = sw;

        public string SessionId { get; } = sessionId;

        /// <summary>The ~400 ms session poll; null once stopped. Guarded by the instance lock.</summary>
        public ITimer? Timer { get; set; }
    }
}

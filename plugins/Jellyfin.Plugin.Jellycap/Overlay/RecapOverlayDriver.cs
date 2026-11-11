using System.Globalization;
using Microsoft.Extensions.Logging;
using Xjellys.Overlay;

namespace Jellyfin.Plugin.Jellycap.Overlay;

/// <summary>
/// Drives Jellycap's recap text cards on the JellyTvOverlay app, per device. A state machine over host events;
/// commands go out through <c>playstate(sessionId, command)</c> with command <c>Pause</c>, <c>Unpause</c> or
/// <c>Seek:&lt;ticks&gt;</c>, and layouts through <see cref="ShowLayout"/> / <see cref="CloseLayout"/>.
/// <list type="bullet">
/// <item>Pause mode: Show the sequence, then Pause (so the pause is suppressed for other clients). recapDone or the
/// fallback timer (total + 30 s) → Unpause (only while paused), seek back to ResumeTicks, then close (the layout's suppressTriggers
/// cover the Unpause). The user pressing play (resume) → close,
/// no seek. Host rejects the Show → nothing is paused; the caller falls back.</item>
/// <item>Timed mode: on the recap video's itemStart, a Show/Close per card at its At / At+Seconds offset from the
/// trigger position; pause drops the schedule, resume and seek re-sync it from the trigger position.</item>
/// </list>
/// </summary>
public sealed class RecapOverlayDriver(Func<string, string, Task> playstate, TimeProvider clock)
{
    public const string Pause = "Pause";
    public const string Unpause = "Unpause";
    public const string SeekPrefix = "Seek:";

    private static readonly TimeSpan PendingExpiry = TimeSpan.FromMinutes(30);

    /// <summary>How long after a Pause/Unpause the session state is checked again (and the command re-sent).</summary>
    public static readonly TimeSpan PlayStateCheck = TimeSpan.FromSeconds(2);

    /// <summary>Pause/Unpause commands sent at most per hold and per resume.</summary>
    public const int MaxPlayStateTries = 6;

    private readonly object _gate = new();

    // Playstate sends are toggles on some players: "check state, then send" must not interleave between a retry
    // timer, Finish (Seek + Unpause) and a new hold's Pause, or a stale command lands after a newer one.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (OverlayRecap Recap, DateTimeOffset At)> _timedPending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timed> _timed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Resume> _resuming = new(StringComparer.Ordinal);

    /// <summary>(deviceId, layout JSON) → shown. Default: the bound JellyTvOverlay client.</summary>
    public Func<string, string, bool> ShowLayout { get; set; } = (d, json) => OverlayClient.Current?.Show(d, json) == true;

    /// <summary>(deviceId, layout id). Default: the bound JellyTvOverlay client.</summary>
    public Action<string, string> CloseLayout { get; set; } = (d, id) => OverlayClient.Current?.Close(d, id);

    public ILogger? Logger { get; set; }

    /// <summary>
    /// (sessionId, itemId) → whether that session is paused while playing that item; null = unknown or another
    /// item. Some players (Jellyfin for Android TV) treat Pause and Unpause as a play/pause toggle and ignore it
    /// while buffering, so commands are only sent while the state differs, and re-checked after
    /// <see cref="PlayStateCheck"/>. No probe (default) = send once, never re-check.
    /// </summary>
    public Func<string, Guid, bool?>? IsPaused { get; set; }

    public static string Seek(long ticks) => SeekPrefix + ticks.ToString(CultureInfo.InvariantCulture);

    public bool IsHeld(string deviceId)
    {
        lock (_gate)
        {
            return _held.ContainsKey(deviceId);
        }
    }

    /// <summary>
    /// Cards are ready. Pause mode: Show, then Pause; false = the host rejected the Show (nothing paused).
    /// Timed mode: remembered until the recap video (recap.ItemId) starts; always true.
    /// </summary>
    public async Task<bool> OnReady(string deviceId, OverlayRecap recap)
    {
        ArgumentNullException.ThrowIfNull(recap);
        if (string.IsNullOrEmpty(deviceId) || recap.Cards.Count == 0)
        {
            return false;
        }

        if (recap.Mode == OverlayMode.Timed)
        {
            lock (_gate)
            {
                DropTimedLocked(deviceId);
                _timedPending[deviceId] = (recap, clock.GetUtcNow());
            }

            return true;
        }

        var json = RecapLayoutBuilder.Build(recap.Cards, OverlayMode.Pause).ToJsonString(OverlayJson.Options);
        Held held;
        lock (_gate)
        {
            if (_held.Remove(deviceId, out var old))
            {
                old.Fallback?.Dispose();
                old.PauseCheck?.Dispose();
            }

            // show first: the pause that follows must reach the host while this layout suppresses it
            if (!ShowLayout(deviceId, json))
            {
                Logger?.LogInformation("JellyTvOverlay rejected the text recap on {Device}", deviceId);
                return false;
            }

            DropResumeLocked(deviceId);
            held = new Held(recap, json);
            _held[deviceId] = held;
            var fallback = TimeSpan.FromSeconds(recap.TotalSeconds + 30);
            held.Fallback = clock.CreateTimer(_ => _ = OnFallback(deviceId, held, fallback), null, fallback, Timeout.InfiniteTimeSpan);
        }

        Logger?.LogInformation("Showing text recap ({Cards} card(s)) on the TV overlay of {Device}; pausing the episode", recap.Cards.Count, deviceId);
        await EnsurePaused(deviceId, held).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sends Pause unless the episode is known to be paused; with a probe, re-checks while the hold lasts. A retry
    /// goes out only when the probe says playing (false): unknown (null) may mean a lagging report of a pause
    /// that already took, and another toggle would undo it.
    /// </summary>
    private async Task EnsurePaused(string deviceId, Held held)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsurePausedLocked(deviceId, held).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task EnsurePausedLocked(string deviceId, Held held)
    {
        int tries;
        lock (_gate)
        {
            if (!_held.TryGetValue(deviceId, out var cur) || !ReferenceEquals(cur, held) || held.Paused)
            {
                return;
            }

            var state = Probe(held.Recap);
            if (state == true)
            {
                held.Paused = true;
                return;
            }

            if (held.PauseTries >= MaxPlayStateTries)
            {
                Logger?.LogWarning("The episode on {Device} did not pause after {Tries} checks", deviceId, MaxPlayStateTries);
                return;
            }

            // the first Pause always goes out; a retry only while the probe says playing
            var send = held.PauseTries == 0 || state == false;
            tries = ++held.PauseTries;
            if (IsPaused is not null)
            {
                held.PauseCheck?.Dispose();
                held.PauseCheck = clock.CreateTimer(_ => _ = EnsurePaused(deviceId, held), null, PlayStateCheck, Timeout.InfiniteTimeSpan);
            }

            if (!send)
            {
                return;
            }
        }

        if (tries > 1)
        {
            Logger?.LogInformation("The episode on {Device} did not pause yet; sending Pause again ({Try})", deviceId, tries);
        }

        await Send(held.Recap.SessionId, Pause).ConfigureAwait(false);
    }

    /// <summary>Sends Unpause while the episode is paused on the recap's item; with a probe, re-checks (a seek may buffer).</summary>
    private async Task EnsurePlaying(string deviceId, Resume resume)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsurePlayingLocked(deviceId, resume).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task EnsurePlayingLocked(string deviceId, Resume resume)
    {
        int tries;
        lock (_gate)
        {
            if (!_resuming.TryGetValue(deviceId, out var cur) || !ReferenceEquals(cur, resume))
            {
                return;
            }

            // no probe answer: one Unpause (the first), only when the episode was seen paused; never a blind retry
            var paused = Probe(resume.Recap) ?? (resume.Tries == 0 && resume.PausedSeen);
            resume.Check?.Dispose();
            if (!paused || resume.Tries >= MaxPlayStateTries)
            {
                _resuming.Remove(deviceId);
                return;
            }

            tries = ++resume.Tries;
            if (IsPaused is not null)
            {
                resume.Check = clock.CreateTimer(_ => _ = EnsurePlaying(deviceId, resume), null, PlayStateCheck, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _resuming.Remove(deviceId);
            }
        }

        if (tries > 1)
        {
            Logger?.LogInformation("The episode on {Device} is still paused after the recap; sending Unpause again ({Try})", deviceId, tries);
        }

        await Send(resume.Recap.SessionId, Unpause).ConfigureAwait(false);
    }

    private bool? Probe(OverlayRecap recap)
    {
        try
        {
            return IsPaused?.Invoke(recap.SessionId, recap.ItemId);
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Play state probe failed");
            return null;
        }
    }

    private void DropResumeLocked(string deviceId)
    {
        if (_resuming.Remove(deviceId, out var r))
        {
            r.Check?.Dispose();
        }
    }

    /// <summary>An action from one of Jellycap's layouts.</summary>
    public Task OnAction(string deviceId, string? layoutId, string? action)
    {
        if (action == RecapLayoutBuilder.FinishedAction && IsPauseLayout(layoutId))
        {
            return Finish(deviceId, null, resume: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>One of Jellycap's layouts closed (reason: back, ttl, finished, replaced, or the closing trigger).</summary>
    public Task OnClosed(string deviceId, string? layoutId, string? reason)
    {
        // closes by trigger are handled in OnTrigger; a finished sequence ends the recap like recapDone
        if (reason == "finished" && IsPauseLayout(layoutId))
        {
            return Finish(deviceId, null, resume: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>A playback trigger for the device (Jellycap also gets the ones its layouts suppress).</summary>
    public async Task OnTrigger(string deviceId, string? trigger, string? itemId, long positionTicks)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        Guid.TryParse(itemId, out var item);
        Held? held;
        lock (_gate)
        {
            _held.TryGetValue(deviceId, out held);
        }

        if (held is not null)
        {
            switch (trigger)
            {
                case "pause" or "doublePause":
                    // our Pause took (the recap layout suppresses this trigger for other clients)
                    lock (_gate)
                    {
                        held.Paused = true;
                        held.PauseCheck?.Dispose();
                    }

                    break;
                case "resume":
                    // the user pressed play during the cards: drop them, leave the episode playing
                    Logger?.LogInformation("Play pressed during the text recap on {Device}; closing the cards", deviceId);
                    await Finish(deviceId, held, resume: false).ConfigureAwait(false);
                    break;
                case "stop":
                    await Finish(deviceId, held, resume: false, close: false).ConfigureAwait(false);
                    break;
                case "itemStart" when item == held.Recap.ItemId:
                    // the host noticed the episode start only after the cards went up and closed them (closeOn itemStart)
                    Reshow(deviceId, held);
                    break;
                case "itemStart":
                    await Finish(deviceId, held, resume: false, close: false).ConfigureAwait(false);
                    break;
            }

            return;
        }

        lock (_gate)
        {
            if (trigger is "stop" or "itemStart" or "pause" or "doublePause" or "resume")
            {
                // playing again (done), the episode moved on, or the user paused it: stop pushing Unpause
                DropResumeLocked(deviceId);
            }

            var pos = TimeSpan.FromTicks(Math.Max(0, positionTicks));
            switch (trigger)
            {
                case "itemStart":
                    DropTimedLocked(deviceId);
                    if (_timedPending.TryGetValue(deviceId, out var p))
                    {
                        if (clock.GetUtcNow() - p.At > PendingExpiry)
                        {
                            _timedPending.Remove(deviceId);
                        }
                        else if (p.Recap.ItemId == item)
                        {
                            _timedPending.Remove(deviceId);
                            var t = new Timed(p.Recap);
                            _timed[deviceId] = t;
                            ScheduleLocked(deviceId, t, pos);
                        }
                    }

                    break;
                case "seek" or "resume" when _timed.TryGetValue(deviceId, out var timed) && timed.Recap.ItemId == item:
                    ScheduleLocked(deviceId, timed, pos);
                    break;
                case "pause" or "doublePause" when _timed.TryGetValue(deviceId, out var timed):
                    // the video stopped moving: the card on screen stays, the rest re-syncs on resume
                    timed.Clear();
                    break;
                case "stop":
                    DropTimedLocked(deviceId);
                    break;
            }
        }
    }

    private static bool IsPauseLayout(string? layoutId)
        => layoutId is RecapLayoutBuilder.PauseLayoutId || layoutId == OverlayClientHost.ClientId + "/" + RecapLayoutBuilder.PauseLayoutId;

    private async Task OnFallback(string deviceId, Held held, TimeSpan after)
    {
        try
        {
            Logger?.LogInformation("The text recap on {Device} did not finish in {Seconds}s; resuming the episode", deviceId, after.TotalSeconds);
            await Finish(deviceId, held, resume: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Text recap fallback failed on {Device}", deviceId);
        }
    }

    private void Reshow(string deviceId, Held held)
    {
        bool ok;
        lock (_gate)
        {
            if (!_held.TryGetValue(deviceId, out var cur) || !ReferenceEquals(cur, held) || held.Reshown)
            {
                return;
            }

            held.Reshown = true;
            ok = ShowLayout(deviceId, held.Json);
        }

        if (!ok)
        {
            _ = Finish(deviceId, held, resume: true, close: false);
        }
    }

    /// <summary>Ends the hold once. <paramref name="expected"/> null = whatever hold the device has.</summary>
    private async Task Finish(string deviceId, Held? expected, bool resume, bool close = true)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await FinishLocked(deviceId, expected, resume, close).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task FinishLocked(string deviceId, Held? expected, bool resume, bool close)
    {
        Held held;
        lock (_gate)
        {
            if (!_held.TryGetValue(deviceId, out var cur) || (expected is not null && !ReferenceEquals(cur, expected)))
            {
                return;
            }

            held = cur;
            _held.Remove(deviceId);
            held.Fallback?.Dispose();
            held.PauseCheck?.Dispose();
        }

        // Unpause + Seek first: the recap layout's suppressTriggers must still cover our own Unpause (resume), or it
        // would reach other clients (e.g. JellyXR seeing pause-play-pause). Close only after they went out.
        // Unpause only while paused: on a toggling player it would pause a playing episode. Unpause before the
        // seek: Jellyfin for Android TV restores the paused state when a seek started while paused completes, which
        // undid an Unpause sent right after the Seek. Only this first Unpause is covered by the layout's
        // suppressTriggers; a retry goes out after the close.
        if (resume)
        {
            var r = new Resume(held.Recap) { PausedSeen = held.Paused || IsPaused is null };
            lock (_gate)
            {
                DropResumeLocked(deviceId);
                _resuming[deviceId] = r;
            }

            await EnsurePlayingLocked(deviceId, r).ConfigureAwait(false);
            await Send(held.Recap.SessionId, Seek(held.Recap.ResumeTicks)).ConfigureAwait(false);
        }

        if (close)
        {
            CloseLayout(deviceId, RecapLayoutBuilder.PauseLayoutId);
        }
    }

    /// <summary>Drops the schedule and plans every card still ahead of <paramref name="pos"/>.</summary>
    private void ScheduleLocked(string deviceId, Timed t, TimeSpan pos)
    {
        t.Clear();
        t.Shown = -1;
        var generation = ++t.Generation;
        for (var i = 0; i < t.Recap.Cards.Count; i++)
        {
            var card = t.Recap.Cards[i];
            var start = TimeSpan.FromSeconds(card.At) - pos;
            var end = TimeSpan.FromSeconds(card.At + card.Seconds) - pos;
            if (end <= TimeSpan.Zero)
            {
                continue;
            }

            var index = i;
            if (start <= TimeSpan.Zero)
            {
                ShowCardLocked(deviceId, t, index, end);
            }
            else
            {
                t.Timers.Add(clock.CreateTimer(_ => OnCardTimer(deviceId, t, generation, index, end - start, show: true), null, start, Timeout.InfiniteTimeSpan));
            }

            t.Timers.Add(clock.CreateTimer(_ => OnCardTimer(deviceId, t, generation, index, TimeSpan.Zero, show: false), null, end, Timeout.InfiniteTimeSpan));
        }
    }

    private void OnCardTimer(string deviceId, Timed t, long generation, int index, TimeSpan duration, bool show)
    {
        try
        {
            lock (_gate)
            {
                if (!_timed.TryGetValue(deviceId, out var cur) || !ReferenceEquals(cur, t) || t.Generation != generation)
                {
                    return;
                }

                if (show)
                {
                    ShowCardLocked(deviceId, t, index, duration);
                }
                else if (t.Shown == index)
                {
                    // a later card already replaced this one (same layout id): leave it up
                    t.Shown = -1;
                    CloseLayout(deviceId, RecapLayoutBuilder.CardLayoutId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Recap card timer failed on {Device}", deviceId);
        }
    }

    private void ShowCardLocked(string deviceId, Timed t, int index, TimeSpan duration)
    {
        var layout = RecapLayoutBuilder.Build(new[] { t.Recap.Cards[index] }, OverlayMode.Timed);
        // the host closes it too if this driver loses track (e.g. the schedule is dropped on a pause)
        layout["ttlMs"] = Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds));
        if (ShowLayout(deviceId, layout.ToJsonString(OverlayJson.Options)))
        {
            t.Shown = index;
        }
    }

    private void DropTimedLocked(string deviceId)
    {
        if (_timed.Remove(deviceId, out var t))
        {
            t.Clear();
        }
    }

    private async Task Send(string sessionId, string command)
    {
        try
        {
            await playstate(sessionId, command).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Playstate command {Command} to session {Session} failed", command, sessionId);
        }
    }

    private sealed class Held(OverlayRecap recap, string json)
    {
        public OverlayRecap Recap { get; } = recap;

        public string Json { get; } = json;

        public ITimer? Fallback { get; set; }

        public bool Reshown { get; set; }

        /// <summary>A pause trigger (or the probe) confirmed the episode is paused.</summary>
        public bool Paused { get; set; }

        public int PauseTries { get; set; }

        public ITimer? PauseCheck { get; set; }
    }

    private sealed class Resume(OverlayRecap recap)
    {
        public OverlayRecap Recap { get; } = recap;

        /// <summary>For a missing probe answer: whether the episode was seen paused (then one Unpause goes out).</summary>
        public bool PausedSeen { get; set; }

        public int Tries { get; set; }

        public ITimer? Check { get; set; }
    }

    private sealed class Timed(OverlayRecap recap)
    {
        public OverlayRecap Recap { get; } = recap;

        public List<ITimer> Timers { get; } = new();

        public long Generation { get; set; }

        /// <summary>Index of the card on screen, -1 = none.</summary>
        public int Shown { get; set; } = -1;

        public void Clear()
        {
            foreach (var timer in Timers)
            {
                timer.Dispose();
            }

            Timers.Clear();
            Generation++;
        }
    }
}

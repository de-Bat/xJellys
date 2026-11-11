namespace Jellyfin.Plugin.JellySubSync.Overlay;

public enum SwitchKind
{
    /// <summary>Nothing to do now.</summary>
    None,

    /// <summary>Ask the player to switch to <see cref="SwitchDecision.Index"/> (SetSubtitleStreamIndex).</summary>
    SendSet,

    /// <summary>The player is on the synced track.</summary>
    Done,

    /// <summary>Stopped waiting; <see cref="SwitchDecision.ShowHint"/> = tell the viewer to pick the track by hand.</summary>
    GiveUp,
}

public readonly record struct SwitchDecision(SwitchKind Kind, int Index = -1, bool ShowHint = false)
{
    public static SwitchDecision None => default;

    public static SwitchDecision Done => new(SwitchKind.Done);

    public static SwitchDecision SendSet(int index) => new(SwitchKind.SendSet, index);

    public static SwitchDecision GiveUp(bool showHint) => new(SwitchKind.GiveUp, -1, showHint);
}

/// <summary>
/// After a sync and Stop: the viewer resumes from the item page and the player starts on the track of the last
/// used language (the original). Once it really plays, ask it to switch to the synced stream; a command sent while
/// it still loads is dropped, so retry a few times. A port of JellyXR TV's WatchService pendingSub logic, fed with
/// playback samples instead of polls. Not thread-safe: the caller serializes samples.
/// </summary>
public sealed class PendingSubtitleSwitch(TimeProvider clock)
{
    // JellyXR TV WatchService constants
    public const long SubGiveUpMs = 600000;
    public const long SubSettleMs = 2500;
    public const long SubRetryMs = 4000;
    public const int SubTries = 3;

    private Guid _item;
    private int _index = -1;
    private bool _canSet;
    private DateTimeOffset _since;
    private DateTimeOffset? _playing;
    private DateTimeOffset? _lastSent;
    private int _tries;

    /// <summary>A switch is pending.</summary>
    public bool Active => _index >= 0;

    /// <summary>The synced stream the player should be on (-1 = none pending).</summary>
    public int StreamIndex => _index;

    public void Start(Guid itemId, int streamIndex, bool canSet)
    {
        _item = itemId;
        _index = streamIndex;
        _canSet = canSet;
        _since = clock.GetUtcNow();
        _playing = null;
        _lastSent = null;
        _tries = 0;
    }

    /// <param name="itemId">Item now playing in the session, null = nothing.</param>
    /// <param name="paused">The player reports paused.</param>
    /// <param name="currentSub">Subtitle stream the player is on, null = none.</param>
    public SwitchDecision OnSample(Guid? itemId, bool paused, int? currentSub)
    {
        if (!Active)
        {
            return SwitchDecision.None;
        }

        var now = clock.GetUtcNow();
        if ((now - _since).TotalMilliseconds > SubGiveUpMs)
        {
            // the viewer did not resume this title: drop it quietly
            Stop();
            return SwitchDecision.GiveUp(false);
        }

        if (itemId is not { } id || paused)
        {
            // stopped, loading or paused: wait for the viewer to resume
            _playing = null;
            return SwitchDecision.None;
        }

        if (id != _item)
        {
            // something else plays now
            Stop();
            return SwitchDecision.GiveUp(false);
        }

        _playing ??= now;
        if ((currentSub ?? -1) == _index)
        {
            Stop();
            return SwitchDecision.Done;
        }

        if (!_canSet || (_tries >= SubTries && Since(_lastSent, now) >= SubRetryMs))
        {
            Stop();
            return SwitchDecision.GiveUp(true);
        }

        if ((now - _playing.Value).TotalMilliseconds >= SubSettleMs && Since(_lastSent, now) >= SubRetryMs)
        {
            _tries++;
            _lastSent = now;
            return SwitchDecision.SendSet(_index);
        }

        return SwitchDecision.None;
    }

    private void Stop() => _index = -1;

    private static double Since(DateTimeOffset? t, DateTimeOffset now) => t is { } at ? (now - at).TotalMilliseconds : double.MaxValue;
}

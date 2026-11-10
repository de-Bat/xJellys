using Jellyfin.Plugin.JellyTvOverlay.Triggers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.JellyTvOverlay.Tests;

public class TriggerDetectorTests
{
    private const long Sec = 10_000_000;
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new();
    private readonly TriggerDetector _d;

    public TriggerDetectorTests() => _d = new TriggerDetector(_clock);

    private IReadOnlyList<Trigger> S(bool paused, long posSec, Guid? item = null, string session = "s1", string device = "d1") =>
        _d.OnSample(device, session, "u1", item ?? Item, paused, posSec * Sec);

    private static TriggerKind[] K(IReadOnlyList<Trigger> t) => t.Select(x => x.Kind).ToArray();

    [Fact]
    public void FirstSample_ItemStart()
    {
        var t = S(false, 0);
        Assert.Equal(new[] { TriggerKind.ItemStart }, K(t));
        Assert.Equal("d1", t[0].DeviceId);
        Assert.Equal("s1", t[0].SessionId);
        Assert.Equal("u1", t[0].UserId);
        Assert.Equal(Item, t[0].ItemId);
    }

    [Fact]
    public void Pause_Then_Resume()
    {
        S(false, 0);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { TriggerKind.Pause }, K(S(true, 5)));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { TriggerKind.Resume }, K(S(false, 5)));
    }

    [Fact]
    public void PausePlayPause_Within4s_DoublePause()
    {
        S(false, 0);
        _clock.Advance(TimeSpan.FromSeconds(2));
        S(true, 2);
        _clock.Advance(TimeSpan.FromSeconds(1));
        S(false, 2);
        _clock.Advance(TimeSpan.FromMilliseconds(4000));
        Assert.Equal(new[] { TriggerKind.DoublePause }, K(S(true, 2)));
    }

    [Fact]
    public void PausePlayPause_After4s_Pause()
    {
        S(false, 0);
        S(true, 0);
        S(false, 0);
        _clock.Advance(TimeSpan.FromMilliseconds(4001));
        Assert.Equal(new[] { TriggerKind.Pause }, K(S(true, 4)));
    }

    [Fact]
    public void NewItem_ResetsDoublePause()
    {
        S(false, 0);
        S(true, 0);
        S(false, 0); // resume
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { TriggerKind.ItemStart }, K(S(false, 0, Other)));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { TriggerKind.Pause }, K(S(true, 1, Other)));
    }

    [Fact]
    public void Jump_Seek()
    {
        S(false, 0);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(S(false, 5)); // normal progress
        _clock.Advance(TimeSpan.FromSeconds(5));
        var t = S(false, 600);
        Assert.Equal(new[] { TriggerKind.Seek }, K(t));
        Assert.Equal(600 * Sec, t[0].PositionTicks);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { TriggerKind.Seek }, K(S(false, 10))); // backwards
    }

    [Fact]
    public void Seek_SmallDrift_NoSeek()
    {
        S(false, 0);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(S(false, 9)); // 4 s off, under 5 s
    }

    [Fact]
    public void Resume_WithPositionChange_NoSeek()
    {
        S(false, 0);
        S(true, 0);
        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { TriggerKind.Resume }, K(S(false, 300)));
    }

    [Fact]
    public void Paused_PositionJump_NoSeek()
    {
        S(false, 0);
        S(true, 0);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(S(true, 500));
    }

    [Fact]
    public void RepeatedPausedSamples_NoDuplicate()
    {
        S(false, 0);
        Assert.Single(S(true, 1));
        Assert.Empty(S(true, 1));
        Assert.Empty(S(true, 1));
    }

    [Fact]
    public void Stop_ClearsState()
    {
        S(false, 0);
        var t = _d.OnStopped("d1", "s1", "u1", Item, 7 * Sec);
        Assert.Equal(new[] { TriggerKind.Stop }, K(t));
        Assert.Equal(7 * Sec, t[0].PositionTicks);
        Assert.Empty(_d.OnStopped("d1", "s1", "u1", Item, 7 * Sec));
        Assert.Equal(new[] { TriggerKind.ItemStart }, K(S(false, 7)));
    }

    [Fact]
    public void Stop_ClearsDoublePauseWindow()
    {
        S(false, 0);
        S(true, 0);
        S(false, 0);
        _d.OnStopped("d1", "s1", "u1", Item, 0);
        S(false, 0);
        Assert.Equal(new[] { TriggerKind.Pause }, K(S(true, 0)));
    }

    [Fact]
    public void NewSession_SameItem_NoItemStart_Reconnect()
    {
        S(false, 0);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(S(false, 5, session: "s2"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        var p = S(true, 10, session: "s2");
        Assert.Equal(new[] { TriggerKind.Pause }, K(p));
        Assert.Equal("s2", p[0].SessionId);
    }

    [Fact]
    public void DevicesAreIndependent()
    {
        Assert.Single(S(false, 0, device: "d1"));
        Assert.Single(S(false, 0, device: "d2"));
        Assert.Equal(new[] { TriggerKind.Pause }, K(S(true, 0, device: "d1")));
    }
}

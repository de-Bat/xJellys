using Jellyfin.Plugin.JellySubSync.Overlay;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.JellySubSync.Tests;

public class PendingSubtitleSwitchTests
{
    private static readonly Guid Item = Guid.NewGuid();
    private readonly FakeTimeProvider _clock = new();

    private PendingSubtitleSwitch Started(bool canSet = true, int index = 5)
    {
        var p = new PendingSubtitleSwitch(_clock);
        p.Start(Item, index, canSet);
        return p;
    }

    private void Ms(long ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Constants_AsJellyXrTv()
    {
        Assert.Equal(600000, PendingSubtitleSwitch.SubGiveUpMs);
        Assert.Equal(2500, PendingSubtitleSwitch.SubSettleMs);
        Assert.Equal(4000, PendingSubtitleSwitch.SubRetryMs);
        Assert.Equal(3, PendingSubtitleSwitch.SubTries);
    }

    [Fact]
    public void NotStarted_None()
    {
        var p = new PendingSubtitleSwitch(_clock);
        Assert.False(p.Active);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 1));
    }

    [Fact]
    public void Pending_WaitsForSettleThenSends()
    {
        var p = Started();

        // stopped / paused: the settle clock does not run
        Assert.Equal(SwitchDecision.None, p.OnSample(null, false, null));
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, true, 2));
        Ms(10000);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2)); // first seen playing
        Ms(2499);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
        Ms(1);
        Assert.Equal(SwitchDecision.SendSet(5), p.OnSample(Item, false, 2));
        Assert.True(p.Active);
    }

    [Fact]
    public void Pending_PauseRestartsSettle()
    {
        var p = Started();
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
        Ms(2000);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, true, 2));
        Ms(1000);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
        Ms(2499);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
        Ms(1);
        Assert.Equal(SwitchDecision.SendSet(5), p.OnSample(Item, false, 2));
    }

    [Fact]
    public void Pending_RetriesUntilTries_ThenGiveUp()
    {
        var p = Started();
        p.OnSample(Item, false, 2);
        Ms(2500);
        Assert.Equal(SwitchDecision.SendSet(5), p.OnSample(Item, false, 2)); // try 1
        Ms(3999);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
        Ms(1);
        Assert.Equal(SwitchDecision.SendSet(5), p.OnSample(Item, false, 2)); // try 2
        Ms(4000);
        Assert.Equal(SwitchDecision.SendSet(5), p.OnSample(Item, false, 2)); // try 3
        Ms(3999);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
        Ms(1);
        Assert.Equal(SwitchDecision.GiveUp(true), p.OnSample(Item, false, 2));
        Assert.False(p.Active);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
    }

    [Fact]
    public void Pending_DoneWhenCurrentMatches()
    {
        var p = Started();
        p.OnSample(Item, false, 2);
        Ms(2500);
        Assert.Equal(SwitchDecision.SendSet(5), p.OnSample(Item, false, 2));
        Ms(500);
        Assert.Equal(SwitchDecision.Done, p.OnSample(Item, false, 5));
        Assert.False(p.Active);
    }

    [Fact]
    public void Pending_AlreadyOnTrack_DoneAtOnce()
    {
        var p = Started();
        Assert.Equal(SwitchDecision.Done, p.OnSample(Item, false, 5));
    }

    [Fact]
    public void Pending_OtherItem_Cancels()
    {
        var p = Started();
        Assert.Equal(SwitchDecision.None, p.OnSample(Guid.NewGuid(), true, 2)); // paused on another item: wait
        Assert.True(p.Active);
        Assert.Equal(SwitchDecision.GiveUp(false), p.OnSample(Guid.NewGuid(), false, 2));
        Assert.False(p.Active);
    }

    [Fact]
    public void Pending_TimesOut_Quietly()
    {
        var p = Started();
        Ms(600000);
        Assert.Equal(SwitchDecision.None, p.OnSample(null, false, null));
        Ms(1);
        Assert.Equal(SwitchDecision.GiveUp(false), p.OnSample(Item, false, 2));
        Assert.False(p.Active);
    }

    [Fact]
    public void Pending_CannotSet_GiveUpWithHint()
    {
        var p = Started(canSet: false);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, true, 2));
        Assert.Equal(SwitchDecision.GiveUp(true), p.OnSample(Item, false, 2));
        Assert.False(p.Active);
    }

    [Fact]
    public void Pending_CannotSet_ButOnTrack_Done()
    {
        var p = Started(canSet: false);
        Assert.Equal(SwitchDecision.Done, p.OnSample(Item, false, 5));
    }

    [Fact]
    public void Start_Again_Resets()
    {
        var p = Started();
        p.OnSample(Item, false, 2);
        Ms(2500);
        p.OnSample(Item, false, 2);
        p.Start(Item, 7, true);
        Assert.Equal(SwitchDecision.None, p.OnSample(Item, false, 2));
        Ms(2500);
        Assert.Equal(SwitchDecision.SendSet(7), p.OnSample(Item, false, 2));
    }
}

using Jellyfin.Plugin.JellyXR.Overlay;
using Xunit;

namespace Jellyfin.Plugin.JellyXR.Tests;

public class CastShowGateTests
{
    private readonly List<string> _shown = new();

    private Func<bool> Show(string what) => () =>
    {
        _shown.Add(what);
        return true;
    };

    [Fact]
    public void NewestWins_OlderGenerationDropped()
    {
        var gate = new CastShowGate();
        var older = gate.Begin("d1");
        var newer = gate.Begin("d1");

        Assert.True(gate.TryShow("d1", newer, interactive: false, Show("newer")));
        Assert.False(gate.TryShow("d1", older, interactive: false, Show("older")));
        Assert.Equal(new[] { "newer" }, _shown);
    }

    [Fact]
    public void LatePassiveAfterInteractive_Dropped()
    {
        // pause -> passive build; resume; double pause -> chosen -> interactive build; the passive one finishes last
        var gate = new CastShowGate();
        var passive = gate.Begin("d1");
        gate.Invalidate("d1");
        var interactive = gate.Begin("d1");

        Assert.True(gate.TryShow("d1", interactive, interactive: true, Show("interactive")));
        Assert.False(gate.TryShow("d1", passive, interactive: false, Show("passive")));
        Assert.Equal(new[] { "interactive" }, _shown);
    }

    [Fact]
    public void PassiveNeverReplacesInteractiveUp_EvenWhenCurrent()
    {
        var gate = new CastShowGate();
        Assert.True(gate.TryShow("d1", gate.Begin("d1"), interactive: true, Show("interactive")));

        Assert.False(gate.TryShow("d1", gate.Begin("d1"), interactive: false, Show("passive")));
        Assert.True(gate.TryShow("d1", gate.Begin("d1"), interactive: true, Show("interactive2")));
        Assert.Equal(new[] { "interactive", "interactive2" }, _shown);
    }

    [Fact]
    public void AfterInteractiveClosed_PassiveAllowed()
    {
        var gate = new CastShowGate();
        Assert.True(gate.TryShow("d1", gate.Begin("d1"), interactive: true, Show("interactive")));
        gate.Closed("d1");

        Assert.True(gate.TryShow("d1", gate.Begin("d1"), interactive: false, Show("passive")));
    }

    [Fact]
    public void Invalidate_ClearsInteractiveAndStalesPending()
    {
        var gate = new CastShowGate();
        Assert.True(gate.TryShow("d1", gate.Begin("d1"), interactive: true, Show("interactive")));
        var pending = gate.Begin("d1");
        gate.Invalidate("d1"); // resume closes the panel

        Assert.False(gate.TryShow("d1", pending, interactive: true, Show("stale")));
        Assert.True(gate.TryShow("d1", gate.Begin("d1"), interactive: false, Show("passive")));
    }

    [Fact]
    public void RejectedShow_DoesNotMarkInteractiveUp()
    {
        var gate = new CastShowGate();
        Assert.False(gate.TryShow("d1", gate.Begin("d1"), interactive: true, () => false));

        Assert.True(gate.TryShow("d1", gate.Begin("d1"), interactive: false, Show("passive")));
    }

    [Fact]
    public void Devices_AreIndependent()
    {
        var gate = new CastShowGate();
        var a = gate.Begin("a");
        gate.Begin("b");
        gate.Invalidate("b");

        Assert.True(gate.TryShow("a", a, interactive: false, Show("a")));
    }
}

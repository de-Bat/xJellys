using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Delivery;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.JellyTvOverlay.Tests;

public class OverlayQueueTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    private static ValidatedLayout L(string id, int ttlMs = 0) =>
        new(id, "passive", ttlMs, null, new[] { "stop" }, Array.Empty<string>(), new JsonObject { ["id"] = id, ["v"] = 1 });

    [Fact]
    public async Task Show_ThenWait_ReturnsOp()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        var seq = q.Show("d1", L("a"));
        var ops = await q.WaitAsync("d1", 0, Short, CancellationToken.None);
        var op = Assert.Single(ops);
        Assert.Equal("show", op.Op);
        Assert.Equal("a", op.LayoutId);
        Assert.Equal(seq, op.Seq);
        Assert.NotNull(op.Layout);
    }

    [Fact]
    public async Task Wait_BlocksUntilShow()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        var first = q.Show("d1", L("a"));
        var task = q.WaitAsync("d1", first, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);
        var seq = q.Show("d1", L("b"));
        var ops = await task.WaitAsync(TimeSpan.FromSeconds(2));
        var op = Assert.Single(ops);
        Assert.Equal(seq, op.Seq);
        Assert.Equal("b", op.LayoutId);
    }

    [Fact]
    public async Task Wait_TimesOut_Empty()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        var head = q.Show("d1", L("a"));
        var ops = await q.WaitAsync("d1", head, Short, CancellationToken.None);
        Assert.Empty(ops);
    }

    [Fact]
    public async Task Snapshot_AfterClose_OmitsClosed()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        q.Show("d1", L("a"));
        q.Show("d1", L("b"));
        var head = q.Close("d1", "a");
        var ops = await q.WaitAsync("d1", 0, Short, CancellationToken.None);
        var op = Assert.Single(ops);
        Assert.Equal("show", op.Op);
        Assert.Equal("b", op.LayoutId);
        Assert.Equal(head, op.Seq);
    }

    [Fact]
    public void Replace_SameId_SingleShown()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        q.Show("d1", L("a"));
        q.Show("d1", L("a"));
        Assert.Single(q.Shown("d1"));
    }

    [Fact]
    public async Task Ttl_Expires_EmitsClose()
    {
        var clock = new FakeTimeProvider();
        var q = new OverlayQueue(clock);
        var head = q.Show("d1", L("a", 1000));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        q.Tick();
        Assert.Single(q.Shown("d1"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        q.Tick();
        Assert.Empty(q.Shown("d1"));
        var ops = await q.WaitAsync("d1", head, Short, CancellationToken.None);
        var op = Assert.Single(ops);
        Assert.Equal("close", op.Op);
        Assert.Equal("a", op.LayoutId);
        Assert.Null(op.Layout);
    }

    [Fact]
    public async Task Reconnect_NewSessionId_SameDevice_KeepsQueue()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        q.Show("d1", L("a"));
        var ops = await q.WaitAsync("d1", 0, Short, CancellationToken.None);
        Assert.Single(ops);
        // reconnect: same deviceId, fresh poll from 0
        var again = await q.WaitAsync("d1", 0, Short, CancellationToken.None);
        Assert.Equal("a", Assert.Single(again).LayoutId);
    }

    [Fact]
    public async Task OldSince_BeyondRetainedLog_ReturnsSnapshot()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        q.Show("d1", L("keep"));
        for (var i = 0; i < 300; i++)
        {
            q.Show("d1", L("x"));
        }

        var ops = await q.WaitAsync("d1", 1, Short, CancellationToken.None);
        Assert.Equal(2, ops.Count);
        Assert.All(ops, o => Assert.Equal("show", o.Op));
        Assert.Equal(301, ops[0].Seq);
    }

    [Fact]
    public async Task ConcurrentShows_NoLostWakeups()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        long since = 0;
        var consumer = Task.Run(async () =>
        {
            while (since < 500)
            {
                var ops = await q.WaitAsync("d1", since, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.NotEmpty(ops);
                foreach (var o in ops)
                {
                    since = Math.Max(since, o.Seq);
                }
            }
        });
        for (var i = 0; i < 500; i++)
        {
            q.Show("d1", L("l" + i));
            if (i % 50 == 0)
            {
                await Task.Yield();
            }
        }

        await consumer.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(500, since);
    }
}

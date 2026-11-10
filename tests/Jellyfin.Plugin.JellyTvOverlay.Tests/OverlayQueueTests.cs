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
        Assert.Equal(2, ops.Count);
        Assert.Equal("reset", ops[0].Op);
        Assert.Equal(seq, ops[0].Seq);
        var op = ops[1];
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
        Assert.Equal(2, ops.Count);
        Assert.Equal("reset", ops[0].Op);
        var op = ops[1];
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

    [Theory]
    [InlineData(1000, 300000)]    // floor: 5 min
    [InlineData(100000, 400000)]  // 4 x ttl
    public void Ttl_ResetOnKey_LeftToTheApp_WithHardExpiry(int ttlMs, int hardMs)
    {
        var clock = new FakeTimeProvider();
        var q = new OverlayQueue(clock);
        var l = L("a", ttlMs);
        l.Json["ttlResetOnKey"] = true;
        q.Show("d1", l);
        // the app restarts the ttl on each key and reports closed(ttl) itself
        clock.Advance(TimeSpan.FromMilliseconds(hardMs - 1));
        q.Tick();
        Assert.Single(q.Shown("d1"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        q.Tick();
        Assert.Empty(q.Shown("d1"));
    }

    [Fact]
    public async Task Reconnect_NewSessionId_SameDevice_KeepsQueue()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        q.Show("d1", L("a"));
        var ops = await q.WaitAsync("d1", 0, Short, CancellationToken.None);
        Assert.Equal(2, ops.Count);
        // reconnect: same deviceId, fresh poll from 0
        var again = await q.WaitAsync("d1", 0, Short, CancellationToken.None);
        Assert.Equal("reset", again[0].Op);
        Assert.Equal("a", Assert.Single(again.Skip(1)).LayoutId);
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
        Assert.Equal(3, ops.Count);
        Assert.Equal("reset", ops[0].Op);
        Assert.All(ops.Skip(1), o => Assert.Equal("show", o.Op));
        Assert.All(ops, o => Assert.Equal(301, o.Seq));
    }

    [Fact]
    public async Task ConcurrentShows_NoLostWakeups_GapFreeSeqs()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        const int Writers = 4;
        const int PerWriter = 45; // 181 ops total stays within the 200-op retained log
        const int Total = Writers * PerWriter;
        q.Show("d1", L("seed"));
        long since = 1;
        var seqs = new List<long>();
        var consumer = Task.Run(async () =>
        {
            while (since < Total + 1)
            {
                var ops = await q.WaitAsync("d1", since, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.NotEmpty(ops);
                Assert.DoesNotContain(ops, o => o.Op == "reset");
                foreach (var o in ops)
                {
                    seqs.Add(o.Seq);
                    since = o.Seq;
                }
            }
        });
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < PerWriter; i++)
            {
                q.Show("d1", L("w" + w + "_" + i));
                if (i % 10 == 0)
                {
                    await Task.Yield();
                }
            }
        })).ToArray();
        await Task.WhenAll(writers);
        await consumer.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(Total, seqs.Count);
        for (var i = 0; i < seqs.Count; i++)
        {
            Assert.Equal(i + 2, seqs[i]);
        }
    }

    [Fact]
    public async Task Close_DeliveredViaLogPath()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        var head = q.Show("d1", L("a"));
        var task = q.WaitAsync("d1", head, TimeSpan.FromSeconds(5), CancellationToken.None);
        var seq = q.Close("d1", "a");
        var op = Assert.Single(await task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("close", op.Op);
        Assert.Equal("a", op.LayoutId);
        Assert.Equal(seq, op.Seq);
    }

    [Fact]
    public async Task SinceAboveHead_ReturnsReset()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        q.Show("d1", L("a"));
        q.Close("d1", "a");
        var ops = await q.WaitAsync("d1", 999, TimeSpan.FromSeconds(5), CancellationToken.None);
        var op = Assert.Single(ops);
        Assert.Equal("reset", op.Op);
        Assert.Equal(2, op.Seq);
    }

    [Fact]
    public async Task StaleSince_NothingShown_SingleResetImmediately()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        var ops = await q.WaitAsync("d1", 50, TimeSpan.FromSeconds(5), CancellationToken.None);
        var op = Assert.Single(ops);
        Assert.Equal("reset", op.Op);
        Assert.Equal(0, op.Seq);
    }

    [Fact]
    public async Task Cancellation_ReturnsEmpty()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        var head = q.Show("d1", L("a"));
        using var cts = new CancellationTokenSource();
        var task = q.WaitAsync("d1", head, TimeSpan.FromSeconds(30), cts.Token);
        await Task.Delay(30);
        cts.Cancel();
        Assert.Empty(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TwoDevices_AreIsolated()
    {
        var q = new OverlayQueue(new FakeTimeProvider());
        q.Show("d1", L("a"));
        Assert.Empty(q.Shown("d2"));
        var ops = await q.WaitAsync("d2", 0, Short, CancellationToken.None);
        Assert.Empty(ops);
        var other = q.Show("d2", L("b"));
        Assert.Equal(1, other);
        Assert.Equal("a", Assert.Single(q.Shown("d1")).Id);
    }

    [Fact]
    public async Task ExpiredUnticked_AbsentFromSnapshotAndShown()
    {
        var clock = new FakeTimeProvider();
        var q = new OverlayQueue(clock);
        q.Show("d1", L("a", 1000));
        q.Show("d1", L("b"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("b", Assert.Single(q.Shown("d1")).Id);
        var ops = await q.WaitAsync("d1", 0, Short, CancellationToken.None);
        Assert.Equal(new[] { "reset", "show" }, ops.Select(o => o.Op));
        Assert.Equal("b", ops[1].LayoutId);
    }
}

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Layout;

namespace Jellyfin.Plugin.JellyTvOverlay.Delivery;

/// <summary>One delivery op. Op is show, close or reset. A reset (LayoutId empty) starts every snapshot: the app closes everything not shown again by the show ops that follow (all with Seq = head).</summary>
public sealed record OverlayOp(long Seq, string Op, string LayoutId, JsonObject? Layout);

/// <summary>Per-device ordered op log with snapshot, long-poll and ttl expiry (not for ttlResetOnKey layouts: the app owns their ttl). Thread-safe.</summary>
public sealed class OverlayQueue(TimeProvider clock)
{
    private const int MaxLog = 200;

    private readonly ConcurrentDictionary<string, DeviceQueue> _devices = new(StringComparer.Ordinal);

    public long Show(string deviceId, ValidatedLayout l)
    {
        var d = Get(deviceId);
        lock (d.Gate)
        {
            // ttlResetOnKey: the app restarts the ttl on each key press and reports closed(ttl) itself
            var expires = l.TtlMs > 0 && !ResetsOnKey(l) ? clock.GetUtcNow().AddMilliseconds(l.TtlMs) : (DateTimeOffset?)null;
            d.Remove(l.Id);
            d.Order.Add(l.Id);
            d.Shown[l.Id] = new Entry(l, expires);
            return Append(d, "show", l.Id, l.Json);
        }
    }

    public long Close(string deviceId, string layoutId)
    {
        var d = Get(deviceId);
        lock (d.Gate)
        {
            if (!d.Remove(layoutId))
            {
                return d.Head;
            }

            return Append(d, "close", layoutId, null);
        }
    }

    public IReadOnlyList<ValidatedLayout> Shown(string deviceId)
    {
        var d = Get(deviceId);
        lock (d.Gate)
        {
            return Live(d);
        }
    }

    public void Tick()
    {
        var now = clock.GetUtcNow();
        foreach (var d in _devices.Values)
        {
            lock (d.Gate)
            {
                var expired = d.Order.Where(id => d.Shown[id].ExpiresAt is { } at && at <= now).ToList();
                foreach (var id in expired)
                {
                    d.Remove(id);
                    Append(d, "close", id, null);
                }
            }
        }
    }

    /// <summary>Returns ops with Seq greater than since (op values show|close), waiting up to timeout. Snapshot responses (since 0, above head or older than the retained log) are [reset, show...] with all Seq = head; since 0 on a never-used queue waits instead.</summary>
    public async Task<IReadOnlyList<OverlayOp>> WaitAsync(string deviceId, long since, TimeSpan timeout, CancellationToken ct)
    {
        var d = Get(deviceId);
        var deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        while (true)
        {
            Task signal;
            lock (d.Gate)
            {
                var result = Collect(d, since);
                if (result is not null)
                {
                    return result;
                }

                // Captured under the same lock as the check: any append after this completes this signal.
                signal = d.Signal.Task;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0 || ct.IsCancellationRequested)
            {
                return Array.Empty<OverlayOp>();
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(TimeSpan.FromMilliseconds(remaining), delayCts.Token);
            var done = await Task.WhenAny(signal, delay).ConfigureAwait(false);
            delayCts.Cancel();
            if (done != signal)
            {
                // Timeout or cancellation: one last check so a racing append is not missed.
                lock (d.Gate)
                {
                    return Collect(d, since) ?? Array.Empty<OverlayOp>();
                }
            }
        }
    }

    // Returns null when there is nothing to deliver yet (caller waits). Caller holds d.Gate.
    private IReadOnlyList<OverlayOp>? Collect(DeviceQueue d, long since)
    {
        var oldest = d.Log.Count > 0 ? d.Log[0].Seq : d.Head + 1;
        var stale = since == 0 || since > d.Head || since < oldest - 1;
        if (stale)
        {
            var live = Live(d);
            if (since == 0 && d.Head == 0 && live.Count == 0)
            {
                return null;
            }

            var snap = new List<OverlayOp>(live.Count + 1) { new OverlayOp(d.Head, "reset", string.Empty, null) };
            foreach (var l in live)
            {
                snap.Add(new OverlayOp(d.Head, "show", l.Id, l.Json));
            }

            return snap;
        }

        if (since == d.Head)
        {
            return null;
        }

        return d.Log.Where(o => o.Seq > since).ToList();
    }

    // Layouts not yet expired per the clock, in show order. Caller holds d.Gate.
    private List<ValidatedLayout> Live(DeviceQueue d)
    {
        var now = clock.GetUtcNow();
        return d.Order.Select(id => d.Shown[id]).Where(e => e.ExpiresAt is not { } at || at > now).Select(e => e.Layout).ToList();
    }

    // Caller holds d.Gate.
    private static long Append(DeviceQueue d, string op, string id, JsonObject? json)
    {
        var seq = ++d.Head;
        d.Log.Add(new OverlayOp(seq, op, id, json));
        if (d.Log.Count > MaxLog)
        {
            d.Log.RemoveRange(0, d.Log.Count - MaxLog);
        }

        var old = d.Signal;
        d.Signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
        return seq;
    }

    private static bool ResetsOnKey(ValidatedLayout l)
        => l.Json["ttlResetOnKey"] is JsonValue v && v.TryGetValue<bool>(out var on) && on;

    private DeviceQueue Get(string deviceId) => _devices.GetOrAdd(deviceId, _ => new DeviceQueue());

    private sealed record Entry(ValidatedLayout Layout, DateTimeOffset? ExpiresAt);

    private sealed class DeviceQueue
    {
        public readonly object Gate = new();
        public readonly List<OverlayOp> Log = new();
        public readonly List<string> Order = new();
        public readonly Dictionary<string, Entry> Shown = new(StringComparer.Ordinal);
        public long Head;
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Remove(string id)
        {
            if (!Shown.Remove(id))
            {
                return false;
            }

            Order.Remove(id);
            return true;
        }
    }
}

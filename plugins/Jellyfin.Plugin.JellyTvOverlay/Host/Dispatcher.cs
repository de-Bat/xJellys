using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Delivery;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
using Jellyfin.Plugin.JellyTvOverlay.Triggers;

namespace Jellyfin.Plugin.JellyTvOverlay.Host;

/// <summary>
/// Routes triggers to clients, collects offers (chooser when several), arbitrates remote focus per device
/// and routes app events to layout owners. No Jellyfin types; thread-safe.
/// Trigger handlers run on one pool task per client; app-event and chosen sends are invoked directly.
/// </summary>
public sealed class Dispatcher(OverlayQueue queue, TimeProvider clock, Action<string>? log = null)
{
    public const int OfferWindowMs = 400;
    public const string HostOwner = "host";
    public const string ChooserId = HostOwner + "/chooser";
    private const string ChoosePrefix = "choose:";

    private sealed record Client(string Id, string DisplayName, Func<string, Task<string?>> OnEvent);

    private sealed record Offer(string ClientId, string OfferId, string Label);

    private sealed record PendingChoice(Trigger Trigger, Dictionary<string, Offer> ByAction);

    /// <summary>Playback state from the latest trigger. Unknown until the first trigger for the device.</summary>
    private enum PlayState { Unknown, None, Playing, Paused }

    private sealed class DeviceState
    {
        public readonly object Gate = new();
        public PendingChoice? Pending;
        public string? SessionId;
        public long Generation; // bumped per trigger under Gate
        public PlayState Play;
    }

    private readonly ConcurrentDictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);

    /// <summary>'/' separates owner from id and "host*" is reserved, so no client can reach another owner's namespace.</summary>
    public static bool IsValidClientId(string? clientId)
        => !string.IsNullOrWhiteSpace(clientId) && !clientId.Contains('/')
            && !clientId.StartsWith(HostOwner, StringComparison.OrdinalIgnoreCase);

    public bool Register(string clientId, string displayName, Func<string, Task<string?>> onEvent)
    {
        if (!IsValidClientId(clientId) || onEvent is null)
        {
            Log($"register rejected: invalid client id '{clientId}'");
            return false;
        }

        _clients[clientId] = new Client(clientId, displayName, onEvent);
        return true;
    }

    public async Task OnTriggerAsync(Trigger t)
    {
        var name = TriggerName(t.Kind);
        var st = State(t.DeviceId);
        var closedFor = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HashSet<string>? only = null;
        long gen;
        lock (st.Gate)
        {
            gen = ++st.Generation;
            st.SessionId = t.SessionId;
            st.Play = t.Kind switch
            {
                TriggerKind.Pause or TriggerKind.DoublePause => PlayState.Paused,
                TriggerKind.Stop => PlayState.None,
                _ => PlayState.Playing,
            };
            foreach (var l in queue.Shown(t.DeviceId).Where(l => l.CloseOn.Contains(name)))
            {
                CloseLocked(st, t.DeviceId, l.Id);
                var owner = OwnerOf(l);
                if (!closedFor.TryGetValue(owner, out var events))
                {
                    closedFor[owner] = events = new List<string>();
                }

                events.Add(ClosedEvent(t.DeviceId, l.Id, name));
            }

            foreach (var l in queue.Shown(t.DeviceId).Where(l => l.SuppressTriggers.Contains(name)))
            {
                (only ??= new HashSet<string>(StringComparer.Ordinal)).Add(OwnerOf(l));
            }
        }

        using var cts = new CancellationTokenSource();
        var window = Task.Delay(TimeSpan.FromMilliseconds(OfferWindowMs), clock, cts.Token);
        var pending = new ConcurrentBag<Task>();

        var json = new JsonObject
        {
            ["type"] = "trigger",
            ["trigger"] = name,
            ["deviceId"] = t.DeviceId,
            ["sessionId"] = t.SessionId,
            ["itemId"] = t.ItemId.ToString("N"),
            ["positionTicks"] = t.PositionTicks,
            ["userId"] = t.UserId,
        }.ToJsonString();

        // One pool task per client, so a handler that blocks cannot hold up the others. Within a client,
        // its closed events are invoked before the trigger (their completion is not awaited first).
        var calls = new List<(Client Client, Task<string?> Task)>();
        foreach (var c in _clients.Values)
        {
            closedFor.TryGetValue(c.Id, out var closedEvents);
            var trigger = only is null || only.Contains(c.Id) ? json : null;
            if (trigger is null && closedEvents is null)
            {
                continue;
            }

            var task = Task.Run(() =>
            {
                foreach (var e in closedEvents ?? new List<string>())
                {
                    pending.Add(Invoke(c, e));
                }

                return trigger is null ? Task.FromResult<string?>(null) : Invoke(c, trigger);
            });
            if (trigger is null)
            {
                pending.Add(task);
            }
            else
            {
                calls.Add((c, task));
            }
        }

        await Task.WhenAny(Task.WhenAll(calls.Select(c => c.Task)), window).ConfigureAwait(false);
        cts.Cancel();

        // Snapshot: answers arriving after this point are ignored.
        var offers = new List<Offer>();
        foreach (var (c, task) in calls)
        {
            if (task.IsCompletedSuccessfully && ParseOffer(c.Id, task.Result) is { } o)
            {
                offers.Add(o);
            }
        }

        bool current;
        lock (st.Gate)
        {
            // A newer trigger (e.g. resume during the window) makes this one's offers stale.
            current = st.Generation == gen;
        }

        if (!current)
        {
            Log($"{name} offers on {t.DeviceId} dropped: a newer trigger arrived");
        }
        else if (offers.Count == 1)
        {
            pending.Add(SendTo(offers[0].ClientId, ChosenEvent(t, offers[0].OfferId)));
        }
        else if (offers.Count > 1)
        {
            ShowChooser(st, t, gen, offers);
        }

        // Late trigger answers are not awaited; Invoke never faults, it logs.
        await Task.WhenAll(pending.ToArray()).ConfigureAwait(false);
    }

    public bool Show(string clientId, string deviceId, string layoutJson)
    {
        if (!_clients.ContainsKey(clientId))
        {
            Log($"show rejected: unknown client '{clientId}'");
            return false;
        }

        ValidatedLayout l;
        try
        {
            l = LayoutValidator.Validate(layoutJson, clientId);
        }
        catch (LayoutException ex)
        {
            Log($"show rejected for {clientId}: {ex.Message}");
            return false;
        }

        var st = State(deviceId);
        lock (st.Gate)
        {
            // A layout meant to close on resume (or stop) that arrives after that already happened would
            // never close by itself: reject it. Before the first trigger the state is unknown: allow.
            if ((st.Play == PlayState.Playing && l.CloseOn.Contains("resume"))
                || (st.Play == PlayState.None && l.CloseOn.Contains("stop")))
            {
                Log($"show rejected for {clientId}: {l.Id} is stale (closes on {(st.Play == PlayState.None ? "stop" : "resume")}, which already happened on {deviceId})");
                return false;
            }

            var shown = queue.Shown(deviceId);
            l = ResolveParent(l, clientId, shown);
            if (IsInteractive(l) && Holder(shown) is { } holder && holder.Id != l.Id
                && OwnerOf(holder) != clientId && !ChainReaches(l, holder.Id, shown))
            {
                Log($"show rejected for {clientId}: {holder.Id} holds the remote on {deviceId}");
                return false;
            }

            queue.Show(deviceId, l);
            return true;
        }
    }

    public void Close(string clientId, string deviceId, string layoutId)
    {
        var id = layoutId.StartsWith(clientId + "/", StringComparison.Ordinal) ? layoutId : clientId + "/" + layoutId;
        var st = State(deviceId);
        lock (st.Gate)
        {
            if (queue.Shown(deviceId).FirstOrDefault(s => s.Id == id) is not { } l || OwnerOf(l) != clientId)
            {
                Log($"close ignored for {clientId}: '{id}' is not its shown layout on {deviceId}");
                return;
            }

            CloseLocked(st, deviceId, id);
        }
    }

    public async Task OnAppEventAsync(string deviceId, JsonObject evt)
    {
        var layoutId = Str(evt["layoutId"]);
        var type = Str(evt["type"]);
        if (string.IsNullOrEmpty(layoutId) || (type != "action" && type != "closed"))
        {
            Log($"app event ignored: type '{type}', layout '{layoutId}'");
            return;
        }

        // "replaced" reports the app swapping in a newer show of the same id: the layout stays shown.
        var removes = type == "closed" && Str(evt["reason"]) != "replaced";
        var st = State(deviceId);
        string owner;
        string? session;
        Offer? picked = null;
        Trigger? ctx = null;
        lock (st.Gate)
        {
            if (queue.Shown(deviceId).FirstOrDefault(s => s.Id == layoutId) is not { } l)
            {
                Log($"app event dropped: '{layoutId}' is not shown on {deviceId}");
                return;
            }

            owner = OwnerOf(l);
            session = st.SessionId;
            if (layoutId == ChooserId && type == "action")
            {
                if (st.Pending is null || Str(evt["action"]) is not { } a || !st.Pending.ByAction.TryGetValue(a, out picked))
                {
                    return;
                }

                ctx = st.Pending.Trigger;
                CloseLocked(st, deviceId, ChooserId);
            }
            else if (removes)
            {
                CloseLocked(st, deviceId, layoutId);
            }
        }

        if (layoutId == ChooserId)
        {
            if (picked is not null && ctx is not null)
            {
                await SendTo(picked.ClientId, ChosenEvent(ctx, picked.OfferId)).ConfigureAwait(false);
            }

            return;
        }

        string json;
        if (type == "closed")
        {
            json = ClosedEvent(deviceId, layoutId, Str(evt["reason"]) ?? string.Empty);
        }
        else
        {
            json = new JsonObject
            {
                ["type"] = "action",
                ["deviceId"] = deviceId,
                ["sessionId"] = session,
                ["layoutId"] = layoutId,
                ["action"] = Str(evt["action"]),
                ["elementId"] = Str(evt["elementId"]),
                ["value"] = evt["value"] is JsonObject v ? v.DeepClone() : new JsonObject(),
            }.ToJsonString();
        }

        await SendTo(owner, json).ConfigureAwait(false);
    }

    public string? HolderOf(string deviceId)
    {
        var st = State(deviceId);
        lock (st.Gate)
        {
            return Holder(queue.Shown(deviceId))?.Id;
        }
    }

    private void ShowChooser(DeviceState st, Trigger t, long gen, List<Offer> offers)
    {
        var buttons = new JsonArray();
        var byAction = new Dictionary<string, Offer>(StringComparer.Ordinal);
        foreach (var o in offers)
        {
            var action = ChoosePrefix + o.ClientId + ":" + o.OfferId;
            byAction[action] = o;
            buttons.Add(new JsonObject
            {
                ["type"] = "button",
                ["id"] = "opt" + buttons.Count,
                ["label"] = o.Label,
                ["action"] = action,
                ["focusable"] = true,
            });
        }

        var json = new JsonObject
        {
            ["v"] = 1,
            ["id"] = "chooser",
            ["focus"] = "interactive",
            ["closeOn"] = new JsonArray("resume", "stop", "itemStart"),
            ["dim"] = 0.4,
            ["root"] = new JsonObject
            {
                ["type"] = "list",
                ["id"] = "choices",
                ["orientation"] = "vertical",
                ["x"] = 0.3,
                ["y"] = 0.3,
                ["w"] = 0.4,
                ["h"] = 0.4,
                ["pad"] = 0.02,
                ["gap"] = 0.02,
                ["bg"] = "#E0202020",
                ["radius"] = 0.02,
                ["children"] = buttons,
            },
        }.ToJsonString();

        ValidatedLayout layout;
        try
        {
            layout = LayoutValidator.Validate(json, HostOwner);
        }
        catch (LayoutException ex)
        {
            Log($"chooser layout invalid: {ex.Message}");
            return;
        }

        lock (st.Gate)
        {
            if (st.Generation != gen)
            {
                return; // a newer trigger arrived while building the chooser
            }

            // The host's chooser is not subject to focus arbitration: it answers a user gesture.
            st.Pending = new PendingChoice(t, byAction);
            queue.Show(t.DeviceId, layout);
        }
    }

    // Caller holds st.Gate.
    private void CloseLocked(DeviceState st, string deviceId, string layoutId)
    {
        if (layoutId == ChooserId)
        {
            st.Pending = null;
        }

        queue.Close(deviceId, layoutId);
    }

    // Validator namespaces the parent under the caller; when that is not shown but the raw value names
    // another client's shown layout ("a/menu" given by client b), use that id.
    private static ValidatedLayout ResolveParent(ValidatedLayout l, string clientId, IReadOnlyList<ValidatedLayout> shown)
    {
        if (l.Parent is not { } p || shown.Any(s => s.Id == p))
        {
            return l;
        }

        var raw = p.Substring(clientId.Length + 1);
        if (!shown.Any(s => s.Id == raw))
        {
            return l;
        }

        l.Json["parent"] = raw;
        return l with { Parent = raw };
    }

    private static bool ChainReaches(ValidatedLayout l, string holderId, IReadOnlyList<ValidatedLayout> shown)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parent = l.Parent;
        while (parent is not null && seen.Add(parent))
        {
            if (parent == holderId)
            {
                return true;
            }

            parent = shown.FirstOrDefault(s => s.Id == parent)?.Parent;
        }

        return false;
    }

    // Newest shown interactive layout (child layouts stack after their parent).
    private static ValidatedLayout? Holder(IReadOnlyList<ValidatedLayout> shown) => shown.LastOrDefault(IsInteractive);

    private static bool IsInteractive(ValidatedLayout l) => l.Focus.StartsWith("interactive", StringComparison.Ordinal);

    private static string OwnerOf(ValidatedLayout l) => Str(l.Json["owner"]) ?? string.Empty;

    private Offer? ParseOffer(string clientId, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(answer) is JsonObject o && o["offer"] is JsonObject offer
                && Str(offer["id"]) is { Length: > 0 } id)
            {
                return new Offer(clientId, id, Str(offer["label"]) is { Length: > 0 } label ? label : id);
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            Log($"offer from {clientId} ignored: {ex.Message}");
        }

        return null;
    }

    private Task SendTo(string clientId, string json)
        => _clients.TryGetValue(clientId, out var c) ? Invoke(c, json) : Task.CompletedTask;

    // Never throws or faults: handler failures are logged and read as "no answer".
    private Task<string?> Invoke(Client c, string json)
    {
        Task<string?>? task;
        try
        {
            task = c.OnEvent(json);
        }
        catch (Exception ex)
        {
            Log($"client {c.Id} handler threw: {ex.Message}");
            return Task.FromResult<string?>(null);
        }

        if (task is null)
        {
            return Task.FromResult<string?>(null);
        }

        return task.ContinueWith(
            tk =>
            {
                if (tk.IsCompletedSuccessfully)
                {
                    return tk.Result;
                }

                Log($"client {c.Id} handler failed: {tk.Exception?.GetBaseException().Message ?? "canceled"}");
                return null;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string ClosedEvent(string deviceId, string layoutId, string reason)
        => new JsonObject { ["type"] = "closed", ["deviceId"] = deviceId, ["layoutId"] = layoutId, ["reason"] = reason }.ToJsonString();

    private static string ChosenEvent(Trigger t, string offerId) => new JsonObject
    {
        ["type"] = "chosen",
        ["deviceId"] = t.DeviceId,
        ["sessionId"] = t.SessionId,
        ["offerId"] = offerId,
        ["itemId"] = t.ItemId.ToString("N"),
        ["positionTicks"] = t.PositionTicks,
    }.ToJsonString();

    private static string TriggerName(TriggerKind k) => k switch
    {
        TriggerKind.ItemStart => "itemStart",
        TriggerKind.Pause => "pause",
        TriggerKind.Resume => "resume",
        TriggerKind.DoublePause => "doublePause",
        TriggerKind.Seek => "seek",
        _ => "stop",
    };

    private DeviceState State(string deviceId) => _devices.GetOrAdd(deviceId, _ => new DeviceState());

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private void Log(string message)
    {
        try
        {
            log?.Invoke("JellyTvOverlay dispatcher: " + message);
        }
        catch
        {
            // Logging must never break dispatch.
        }
    }
}

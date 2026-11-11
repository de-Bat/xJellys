using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xjellys.Overlay;

namespace Jellyfin.Plugin.JellyXR.Overlay;

/// <summary>
/// JellyXR as a client of the JellyTvOverlay host: the cast panel on pause (passive), or offered on pause /
/// double pause and shown when chosen: interactive+passthrough from a single pause (play and seek stay with the
/// player), interactive from a double pause or the chooser. Without the host or the app, PauseCoordinator sends toasts.
/// The interactive panel carries every actor's detail layout in <c>preload</c>; OK on an actor shows that exact
/// JSON, so the app opens it from its cache. BACK on a detail returns to the panel in the app (its parent).
/// </summary>
public sealed class OverlayClientHost : IHostedService
{
    public const string ClientId = "JellyXR";
    public const string DisplayName = "Cast";
    public const string OfferId = "cast";

    private static readonly string OfferJson = new JsonObject
    {
        ["offer"] = new JsonObject { ["id"] = OfferId, ["label"] = "Cast" },
    }.ToJsonString();

    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly CardService _cards;
    private readonly ILogger<OverlayClientHost> _logger;
    private readonly CastShowGate _gate = new();
    private readonly ConcurrentDictionary<string, string> _userOf = new(StringComparer.Ordinal);

    /// <summary>Per device: the focus a "chosen" builds, from the trigger that offered (pause: passthrough).</summary>
    private readonly ConcurrentDictionary<string, string> _offerFocus = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DetailSet> _details = new(StringComparer.Ordinal);
    private int _showsStarted;

    public OverlayClientHost(ISessionManager sessions, ILibraryManager library, IUserManager users, CardService cards, ILogger<OverlayClientHost> logger)
    {
        _sessions = sessions;
        _library = library;
        _users = users;
        _cards = cards;
        _logger = PluginLog.Wrap(logger);
        Config = () => Plugin.Instance?.Configuration;
        AppPresent = d => OverlayClient.Current?.HasApp(d) == true;
        ShowLayout = (d, json) => OverlayClient.Current?.Show(d, json) == true;
        FindSession = id => _sessions.Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) is { } s
            ? (s.Client, s.DeviceName)
            : null;
        LoadPerson = LoadPersonAsync;
    }

    /// <summary>Most actor details preloaded with one panel (bounds the payload and the TV's image preloads).</summary>
    internal const int MaxPreloads = 16;

    /// <summary>Most titles in a detail built with the panel (any of them may be preloaded).</summary>
    internal const int PreloadTitles = 20;

    /// <summary>Actor detail builds running at once for one panel.</summary>
    internal const int PreloadConcurrency = 8;

    // Seams for tests; production values are set in the constructor.
    internal Func<Configuration.PluginConfiguration?> Config { get; set; }

    internal Func<string, bool> AppPresent { get; set; }

    internal Func<string, string, bool> ShowLayout { get; set; }

    internal Func<string?, (string? Client, string? DeviceName)?> FindSession { get; set; }

    /// <summary>The person and their titles for (person, user id, playing item), or null.</summary>
    internal Func<Guid, string?, Guid, CancellationToken, Task<(PersonInfo Person, IReadOnlyList<LibraryTitle> Titles)?>> LoadPerson { get; set; }

    /// <summary>How long the interactive panel waits for actor detail preloads; later ones are shown on demand.</summary>
    internal TimeSpan PreloadWait { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>User id of the device's latest trigger (tests).</summary>
    internal string? UserOf(string deviceId) => _userOf.TryGetValue(deviceId, out var u) ? u : null;

    internal CastShowGate Gate => _gate;

    /// <summary>The focus of the last cast build started (tests).</summary>
    internal string? LastFocus { get; private set; }

    /// <summary>Background builds started (tests).</summary>
    internal int ShowsStarted => Volatile.Read(ref _showsStarted);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bound = EnsureBound() is not null;
        _logger.LogInformation("JellyTvOverlay host {State}", bound ? "found: cast panel goes through it" : "not found yet: toasts until it loads");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Binds to the host (rescans at most every ~5 s while it is missing). The host may load after JellyXR.</summary>
    internal OverlayClient? EnsureBound() => OverlayClient.EnsureBound(ClientId, DisplayName, OnEventAsync);

    /// <summary>True when the device runs the JellyTvOverlay app and the host is bound.</summary>
    public bool HasApp(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId) && EnsureBound()?.HasApp(deviceId) == true;

    /// <summary>Host event handler. Must answer fast: card building runs after it returns.</summary>
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
        var type = Str(e, "type");
        var deviceId = Str(e, "deviceId");
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        switch (type)
        {
            case "trigger":
            {
                var trigger = Str(e, "trigger");
                if (trigger is "resume" or "stop" or "itemStart")
                {
                    // the cast panel and its actor details close on these (closeOn); builds still running are stale
                    _gate.Invalidate(deviceId);
                    _details.TryRemove(deviceId, out _);
                    _offerFocus.TryRemove(deviceId, out _);
                    return null;
                }

                // "chosen" carries no user: the interactive panel uses the user of the trigger that offered it
                if (Str(e, "userId") is { Length: > 0 } userId)
                {
                    _userOf[deviceId] = userId;
                }

                if (trigger is not ("pause" or "doublePause") || Wanted(deviceId, Str(e, "sessionId")) is not { } cfg)
                {
                    return null;
                }

                if (trigger == "doublePause" || cfg.TvPanelInteractive)
                {
                    // a single pause leaves play and seek with the player (interactive+passthrough);
                    // the double pause (or the chooser after it) takes the remote (interactive)
                    _offerFocus[deviceId] = trigger == "pause" ? CastLayoutBuilder.FocusPassthrough : CastLayoutBuilder.FocusInteractive;
                    _logger.LogDebug("Offering the cast panel on {Device} ({Trigger})", deviceId, trigger);
                    return OfferJson;
                }

                StartShow(deviceId, Str(e, "itemId"), Long(e, "positionTicks"), CastLayoutBuilder.FocusPassive);
                return null;
            }

            case "chosen":
                if (Str(e, "offerId") == OfferId && Wanted(deviceId, Str(e, "sessionId")) is not null)
                {
                    var focus = _offerFocus.TryRemove(deviceId, out var f) ? f : CastLayoutBuilder.FocusInteractive;
                    StartShow(deviceId, Str(e, "itemId"), Long(e, "positionTicks"), focus);
                }

                return null;

            case "closed":
                // "replaced" = the app swapped in a newer show of the same id (e.g. passive -> interactive): still up.
                // An actor detail closing (JellyXR/actor:*) leaves the panel up: the app hands it the keys again.
                if (Str(e, "layoutId") == ClientId + "/" + CastLayoutBuilder.LayoutId && Str(e, "reason") != "replaced")
                {
                    _gate.Closed(deviceId);
                    _details.TryRemove(deviceId, out _);
                }

                return null;

            case "action":
                if (Str(e, "layoutId") == ClientId + "/" + CastLayoutBuilder.LayoutId
                    && Str(e, "action") is { } action
                    && action.StartsWith(ActorDetailBuilder.IdPrefix, StringComparison.Ordinal)
                    && Guid.TryParse(action.AsSpan(ActorDetailBuilder.IdPrefix.Length), out var personId))
                {
                    ShowDetail(deviceId, personId);
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>TV overlay on, the device has the app, and the session's client matches the client filter.</summary>
    private Configuration.PluginConfiguration? Wanted(string deviceId, string? sessionId)
    {
        var cfg = Config();
        if (cfg is not { Enabled: true, TvOverlay: true } || !AppPresent(deviceId))
        {
            return null;
        }

        if (FindSession(sessionId) is not { } session)
        {
            _logger.LogDebug("No cast panel on {Device}: session {Session} not found", deviceId, sessionId);
            return null;
        }

        if (!PauseCoordinator.ClientMatches(cfg.ClientFilter, session.Client, session.DeviceName))
        {
            _logger.LogDebug("No cast panel: client {Client} on {Device} does not match filter '{Filter}'", session.Client, session.DeviceName, cfg.ClientFilter);
            return null;
        }

        return cfg;
    }

    private void StartShow(string deviceId, string? itemId, long positionTicks, string focus)
    {
        LastFocus = focus;
        var generation = _gate.Begin(deviceId);
        Interlocked.Increment(ref _showsStarted);
        _ = Task.Run(async () =>
        {
            try
            {
                await ShowAsync(deviceId, itemId, positionTicks, focus, generation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyXR cast panel failed");
            }
        });
    }

    private async Task ShowAsync(string deviceId, string? itemId, long positionTicks, string focus, long generation)
    {
        var interactive = focus != CastLayoutBuilder.FocusPassive;
        var cfg = Config();
        if (cfg is null || !Guid.TryParse(itemId, out var id))
        {
            return;
        }

        var item = _library.GetItemById(id);
        if (item is not (Movie or Episode))
        {
            return;
        }

        var built = await _cards.BuildAsync(item, positionTicks, cfg, CancellationToken.None).ConfigureAwait(false);
        if (built is not { Cards.Count: > 0 } set)
        {
            _logger.LogDebug("No cast panel for {Item}: nobody to show", item.Name);
            return;
        }

        set = _cards.WithPeople(item, set);
        if (!cfg.ShowCharacter)
        {
            set = set with { Cards = set.Cards.Select(c => c with { Character = null }).ToList() };
        }

        var json = await CastJsonAsync(deviceId, set, interactive, cfg.OverlaySeconds, item.Id, UserOf(deviceId), focus == CastLayoutBuilder.FocusPassthrough).ConfigureAwait(false);
        var shown = _gate.TryShow(deviceId, generation, interactive, () => ShowLayout(deviceId, json));
        if (shown)
        {
            _logger.LogInformation(
                "Cast panel ({Mode}) for {Item} on {Device}: {Count} people",
                focus,
                item.Name,
                deviceId,
                set.Cards.Count);
        }
        else
        {
            // a newer build or an interactive panel won, or the host rejected a stale show (playback resumed meanwhile)
            _logger.LogDebug("Cast panel for {Item} on {Device} not shown (superseded or rejected by the host)", item.Name, deviceId);
        }
    }

    /// <summary>The cast panel layout JSON; interactive panels carry the actor details ready in time in <c>preload</c>.</summary>
    internal async Task<string> CastJsonAsync(string deviceId, CardSet set, bool interactive, int overlaySeconds, Guid itemId, string? userId, bool passthrough = false)
    {
        var layout = CastLayoutBuilder.Build(set, !interactive ? CastLayoutBuilder.FocusPassive : passthrough ? CastLayoutBuilder.FocusPassthrough : CastLayoutBuilder.FocusInteractive);
        if (!interactive && overlaySeconds > 0)
        {
            layout["ttlMs"] = overlaySeconds * 1000;
        }

        if (interactive)
        {
            var people = set.Cards.Select(c => c.PersonId).OfType<Guid>().Distinct().ToList();
            var preload = await PreloadAsync(deviceId, people, userId, itemId).ConfigureAwait(false);
            if (preload.Count > 0)
            {
                layout["preload"] = preload;
            }
        }

        return layout.ToJsonString(OverlayJson.Options);
    }

    /// <summary>
    /// Starts the actor detail builds for a panel (at most <see cref="PreloadConcurrency"/> at once) and waits up to
    /// <see cref="PreloadWait"/>. Returns the details ready by then, in panel order; the rest stay available for
    /// OK on that actor. Replaces the device's previous set.
    /// </summary>
    internal async Task<JsonArray> PreloadAsync(string deviceId, IReadOnlyList<Guid> people, string? userId, Guid itemId)
    {
        var limit = new SemaphoreSlim(PreloadConcurrency);
        var builds = new Dictionary<Guid, Task<string?>>();
        foreach (var person in people)
        {
            builds[person] = BuildLimitedAsync(person);
        }

        _details[deviceId] = new DetailSet(builds);
        if (builds.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(builds.Values), Task.Delay(PreloadWait)).ConfigureAwait(false);
        }

        var preload = new JsonArray();
        foreach (var person in people)
        {
            if (preload.Count < MaxPreloads && builds[person] is { IsCompletedSuccessfully: true, Result: { } json })
            {
                preload.Add(JsonNode.Parse(json));
            }
        }

        _logger.LogDebug("Actor details for {Device}: {Ready} of {Count} preloaded", deviceId, preload.Count, people.Count);
        return preload;

        async Task<string?> BuildLimitedAsync(Guid person)
        {
            await limit.WaitAsync().ConfigureAwait(false);
            try
            {
                var loaded = await LoadPerson(person, userId, itemId, CancellationToken.None).ConfigureAwait(false);
                if (loaded is not { } l)
                {
                    _logger.LogDebug("No actor detail for {Person}: no person item or no user", person);
                    return null;
                }

                var titles = l.Titles.Count > PreloadTitles ? l.Titles.Take(PreloadTitles).ToList() : l.Titles;
                return ActorDetailBuilder.Build(l.Person, titles).ToJsonString(OverlayJson.Options);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Actor detail for {Person} failed: {Error}", person, ex.Message);
                return null;
            }
            finally
            {
                limit.Release();
            }
        }
    }

    /// <summary>OK on an actor: shows the detail built with the panel (the preloaded JSON), or once its build ends.</summary>
    private void ShowDetail(string deviceId, Guid personId)
    {
        if (!_details.TryGetValue(deviceId, out var set) || !set.Builds.TryGetValue(personId, out var build))
        {
            _logger.LogDebug("No actor detail for {Person} on {Device}", personId, deviceId);
            return;
        }

        if (build.IsCompleted)
        {
            Show(build);
            return;
        }

        // not ready when the panel was shown: show it when its build finishes, if the panel is still up
        _ = build.ContinueWith(
            t =>
            {
                if (_details.TryGetValue(deviceId, out var cur) && ReferenceEquals(cur, set))
                {
                    Show(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        void Show(Task<string?> t)
        {
            if (t is { IsCompletedSuccessfully: true, Result: { } json })
            {
                var shown = ShowLayout(deviceId, json);
                _logger.LogDebug("Actor detail {Person} on {Device}: {State}", personId, deviceId, shown ? "shown" : "rejected by the host");
            }
        }
    }

    private async Task<(PersonInfo Person, IReadOnlyList<LibraryTitle> Titles)?> LoadPersonAsync(Guid personId, string? userId, Guid itemId, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var uid) || _users.UserById(uid) is not { } user)
        {
            _logger.LogDebug("No actor detail for {Person}: user {User} not found", personId, userId);
            return null;
        }

        return await PersonLibrary.LoadAsync(_library, personId, user, itemId, ct, PreloadTitles).ConfigureAwait(false);
    }

    /// <summary>The actor detail builds of the panel shown on a device: person id to its layout JSON (null = none).</summary>
    private sealed record DetailSet(IReadOnlyDictionary<Guid, Task<string?>> Builds);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
}

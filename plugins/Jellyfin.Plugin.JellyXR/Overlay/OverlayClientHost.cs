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
/// double pause and shown interactive when chosen. Without the host or the app, PauseCoordinator sends toasts.
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
    private readonly CardService _cards;
    private readonly ILogger<OverlayClientHost> _logger;
    private readonly CastShowGate _gate = new();
    private int _showsStarted;

    public OverlayClientHost(ISessionManager sessions, ILibraryManager library, CardService cards, ILogger<OverlayClientHost> logger)
    {
        _sessions = sessions;
        _library = library;
        _cards = cards;
        _logger = PluginLog.Wrap(logger);
        Config = () => Plugin.Instance?.Configuration;
        AppPresent = d => OverlayClient.Current?.HasApp(d) == true;
        ShowLayout = (d, json) => OverlayClient.Current?.Show(d, json) == true;
        FindSession = id => _sessions.Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) is { } s
            ? (s.Client, s.DeviceName)
            : null;
    }

    // Seams for tests; production values are set in the constructor.
    internal Func<Configuration.PluginConfiguration?> Config { get; set; }

    internal Func<string, bool> AppPresent { get; set; }

    internal Func<string, string, bool> ShowLayout { get; set; }

    internal Func<string?, (string? Client, string? DeviceName)?> FindSession { get; set; }

    internal CastShowGate Gate => _gate;

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
                    // the cast panel closes on these (closeOn); builds still running are stale
                    _gate.Invalidate(deviceId);
                    return null;
                }

                if (trigger is not ("pause" or "doublePause") || Wanted(deviceId, Str(e, "sessionId")) is not { } cfg)
                {
                    return null;
                }

                if (trigger == "doublePause" || cfg.TvPanelInteractive)
                {
                    _logger.LogDebug("Offering the cast panel on {Device} ({Trigger})", deviceId, trigger);
                    return OfferJson;
                }

                StartShow(deviceId, Str(e, "itemId"), Long(e, "positionTicks"), interactive: false);
                return null;
            }

            case "chosen":
                if (Str(e, "offerId") == OfferId && Wanted(deviceId, Str(e, "sessionId")) is not null)
                {
                    StartShow(deviceId, Str(e, "itemId"), Long(e, "positionTicks"), interactive: true);
                }

                return null;

            case "closed":
                // "replaced" = the app swapped in a newer show of the same id (e.g. passive -> interactive): still up
                if (Str(e, "layoutId") == ClientId + "/" + CastLayoutBuilder.LayoutId && Str(e, "reason") != "replaced")
                {
                    _gate.Closed(deviceId);
                }

                return null;

            case "action":
                if (Str(e, "action") is { } action && action.StartsWith("actor:", StringComparison.Ordinal))
                {
                    // Actor detail comes with the next release step; nothing to do yet.
                    _logger.LogDebug("Actor {Action} chosen on {Device}", action, deviceId);
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

    private void StartShow(string deviceId, string? itemId, long positionTicks, bool interactive)
    {
        var generation = _gate.Begin(deviceId);
        Interlocked.Increment(ref _showsStarted);
        _ = Task.Run(async () =>
        {
            try
            {
                await ShowAsync(deviceId, itemId, positionTicks, interactive, generation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyXR cast panel failed");
            }
        });
    }

    private async Task ShowAsync(string deviceId, string? itemId, long positionTicks, bool interactive, long generation)
    {
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

        var layout = CastLayoutBuilder.Build(set, interactive);
        if (!interactive && cfg.OverlaySeconds > 0)
        {
            layout["ttlMs"] = cfg.OverlaySeconds * 1000;
        }

        var json = layout.ToJsonString();
        var shown = _gate.TryShow(deviceId, generation, interactive, () => ShowLayout(deviceId, json));
        if (shown)
        {
            _logger.LogInformation(
                "Cast panel ({Mode}) for {Item} on {Device}: {Count} people",
                interactive ? "interactive" : "passive",
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

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
}

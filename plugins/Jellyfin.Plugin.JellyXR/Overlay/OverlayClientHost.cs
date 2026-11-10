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

    public OverlayClientHost(ISessionManager sessions, ILibraryManager library, CardService cards, ILogger<OverlayClientHost> logger)
    {
        _sessions = sessions;
        _library = library;
        _cards = cards;
        _logger = PluginLog.Wrap(logger);
    }

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
    private Task<string?> OnEventAsync(string json)
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
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is not { Enabled: true, TvOverlay: true } || Current()?.HasApp(deviceId) != true)
        {
            return null;
        }

        var session = _sessions.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        if (session is null)
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

    private static OverlayClient? Current() => OverlayClient.Current;

    private void StartShow(string deviceId, string? itemId, long positionTicks, bool interactive)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ShowAsync(deviceId, itemId, positionTicks, interactive).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyXR cast panel failed");
            }
        });
    }

    private async Task ShowAsync(string deviceId, string? itemId, long positionTicks, bool interactive)
    {
        var cfg = Plugin.Instance?.Configuration;
        var client = Current();
        if (cfg is null || client is null || !Guid.TryParse(itemId, out var id))
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

        var shown = client.Show(deviceId, layout.ToJsonString());
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
            // e.g. playback resumed while the cards were built (the host rejects a stale show)
            _logger.LogDebug("Cast panel for {Item} on {Device} not shown (rejected by the host)", item.Name, deviceId);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
}

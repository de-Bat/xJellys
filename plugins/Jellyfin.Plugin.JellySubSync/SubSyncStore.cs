using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySubSync;

/// <summary>One synced copy of a subtitle track.</summary>
public sealed class SyncEntry
{
    /// <summary>Which original track: "file:&lt;path&gt;" for an external file, "embedded:&lt;index&gt;" for a track inside the video.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Path of the shifted copy Jellyfin lists as an extra subtitle track.</summary>
    public string SyncedPath { get; set; } = string.Empty;

    public long OffsetMs { get; set; }

    public DateTime Updated { get; set; }
}

/// <summary>Per item JSON files with the synced tracks, in the plugin data folder.</summary>
public sealed class SubSyncStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ILogger<SubSyncStore> _logger;
    private readonly object _gate = new();

    public SubSyncStore(ILogger<SubSyncStore> logger)
    {
        _logger = PluginLog.Wrap(logger);
    }

    public static string Dir => Plugin.Instance!.DataDirectory;

    private static string PathFor(Guid itemId) => Path.Combine(Dir, itemId.ToString("N") + ".json");

    public List<SyncEntry> Load(Guid itemId)
    {
        lock (_gate)
        {
            try
            {
                var p = PathFor(itemId);
                return File.Exists(p) ? JsonSerializer.Deserialize<List<SyncEntry>>(File.ReadAllText(p), Json) ?? new() : new();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync entries for item {Id} unreadable", itemId);
                return new();
            }
        }
    }

    public void Save(Guid itemId, List<SyncEntry> entries)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Dir);
            var p = PathFor(itemId);
            if (entries.Count == 0)
            {
                File.Delete(p);
                return;
            }

            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
            File.Move(tmp, p, true);
        }
    }
}

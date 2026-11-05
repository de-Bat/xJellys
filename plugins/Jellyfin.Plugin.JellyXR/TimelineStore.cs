using System.Text.Json;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Who was seen at second <see cref="T"/>. People are cast names as stored in Jellyfin.</summary>
public sealed class TimelineEntry
{
    public int T { get; set; }

    public string[] P { get; set; } = Array.Empty<string>();
}

public sealed class Timeline
{
    public int Interval { get; set; }

    public List<TimelineEntry> Entries { get; set; } = new();
}

/// <summary>Per item JSON files with the face timeline.</summary>
public sealed class TimelineStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string Dir => Path.Combine(Plugin.Instance!.DataDirectory, "timelines");

    private static string PathFor(Guid itemId) => Path.Combine(Dir, itemId.ToString("N") + ".json");

    public bool Exists(Guid itemId) => File.Exists(PathFor(itemId));

    public Timeline? Load(Guid itemId)
    {
        try
        {
            var p = PathFor(itemId);
            return File.Exists(p) ? JsonSerializer.Deserialize<Timeline>(File.ReadAllText(p), Json) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(Guid itemId, Timeline timeline)
    {
        Directory.CreateDirectory(Dir);
        var tmp = PathFor(itemId) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(timeline, Json));
        File.Move(tmp, PathFor(itemId), true);
    }

    /// <summary>People seen in [fromSec, toSec], most frequent first.</summary>
    public static List<string> PeopleBetween(Timeline timeline, int fromSec, int toSec)
    {
        return timeline.Entries
            .Where(e => e.T >= fromSec && e.T <= toSec)
            .SelectMany(e => e.P)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
    }
}

using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Tags (and optionally genres) every movie and episode that has X-Ray data.</summary>
public class MarkTask : IScheduledTask
{
    private readonly FaceScanner _scanner;
    private readonly MarkerService _markers;

    public MarkTask(FaceScanner scanner, MarkerService markers)
    {
        _scanner = scanner;
        _markers = markers;
    }

    public string Name => "Mark items with X-Ray data";

    public string Key => "JellyXRMark";

    public string Description => "Adds the X-Ray tag and (if enabled) genre and poster badge to movies and episodes with scene data, and removes them from the rest. Run it after changing the marker settings.";

    public string Category => "JellyXR";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _markers.SyncAllAsync(_scanner.AllScannable(), progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // after the 03:00 face scan
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
        };
    }
}

using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Jellycap;

public class CleanupTask : IScheduledTask
{
    private readonly RecapService _recaps;

    public CleanupTask(RecapService recaps)
    {
        _recaps = recaps;
    }

    public string Name => "Delete old Jellycap recaps";

    public string Key => "JellycapCleanup";

    public string Description => "Removes recap videos older than the configured retention.";

    public string Category => "Jellycap";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _recaps.CleanOld(Plugin.Instance?.Configuration.RetentionDays ?? 30);
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerInterval,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }
}

using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyXR;

public class ScanTask : IScheduledTask
{
    private readonly FaceScanner _scanner;

    public ScanTask(FaceScanner scanner)
    {
        _scanner = scanner;
    }

    public string Name => "Scan videos for cast faces";

    public string Key => "JellyXRScan";

    public string Description => "Finds which cast members appear when, so pausing shows who is on screen. Slow on first run; already scanned items are skipped.";

    public string Category => "JellyXR";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance!.Configuration.DataSource == Configuration.DataSource.Xrayarr)
        {
            progress.Report(100); // scene data comes from Xrayarr only
            return;
        }

        var todo = _scanner.AllScannable().Where(_scanner.NeedsScan).ToList();
        for (var i = 0; i < todo.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var done = i;
            var inner = new Progress<double>(p => progress.Report(100.0 * (done + (p / 100.0)) / todo.Count));
            await _scanner.ScanAsync(todo[i], inner, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / todo.Count);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}

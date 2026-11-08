using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

public class ScanTask : IScheduledTask
{
    private readonly FaceScanner _scanner;
    private readonly ILogger<ScanTask> _logger;

    public ScanTask(FaceScanner scanner, ILogger<ScanTask> logger)
    {
        _scanner = scanner;
        _logger = PluginLog.Wrap(logger);
    }

    public string Name => "Scan videos for cast faces";

    public string Key => "JellyXRScan";

    public string Description => "Finds which cast members appear when, so pausing shows who is on screen. Slow on first run; already scanned items are skipped.";

    public string Category => "JellyXR";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance!.Configuration.DataSource == Configuration.DataSource.Xrayarr)
        {
            _logger.LogInformation("Face scan task skipped: data source is Xrayarr only");
            progress.Report(100); // scene data comes from Xrayarr only
            return;
        }

        var todo = _scanner.AllScannable().Where(_scanner.NeedsScan).ToList();
        _logger.LogInformation("Face scan task: {Count} item(s) not scanned yet", todo.Count);
        var scanned = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var done = i;
            var inner = new Progress<double>(p => progress.Report(100.0 * (done + (p / 100.0)) / todo.Count));
            if (await _scanner.ScanAsync(todo[i], inner, cancellationToken).ConfigureAwait(false))
            {
                scanned++;
            }

            progress.Report(100.0 * (i + 1) / todo.Count);
        }

        _logger.LogInformation("Face scan task done: {Scanned} of {Count} item(s) scanned", scanned, todo.Count);
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

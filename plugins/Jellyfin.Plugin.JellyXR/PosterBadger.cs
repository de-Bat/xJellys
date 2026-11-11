using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

/// <summary>Where the badge goes on an image, in pixels.</summary>
public readonly record struct BadgeBox(int Width, int Height, int X, int Y);

/// <summary>
/// Puts an "X-RAY" badge on the primary image (movie poster, episode thumbnail) of items with X-Ray data, so the
/// Android TV app shows it wherever it shows the image. The original file is never touched: ffmpeg writes a badged
/// copy into the plugin data directory and the item's primary image points at the copy; removing the badge points
/// it back at the original.
/// </summary>
public sealed class PosterBadger
{
    /// <summary>Bump when the badge look or placement changes: existing copies are then rebuilt.</summary>
    internal const int Style = 1;

    /// <summary>Width / height of the embedded badge image.</summary>
    internal const double BadgeAspect = 2.5;

    private const string BadgeResource = "Jellyfin.Plugin.JellyXR.Assets.xray-badge.png";
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(30);

    private readonly IMediaEncoder _encoder;
    private readonly IImageProcessor _images;
    private readonly ILogger<PosterBadger> _logger;

    public PosterBadger(IMediaEncoder encoder, IImageProcessor images, ILogger<PosterBadger> logger)
    {
        _encoder = encoder;
        _images = images;
        _logger = PluginLog.Wrap(logger);
    }

    private static string Dir => Path.Combine(Plugin.Instance!.DataDirectory, "badges");

    private static string RecordPath(Guid id) => Path.Combine(Dir, id.ToString("N") + ".json");

    /// <summary>
    /// Top-left badge sized from the image area, so a 2:3 poster and a 16:9 thumbnail get a badge of similar weight.
    /// Top-left because clients draw the played tick / unplayed count top-right and the progress bar at the bottom.
    /// Null when the image is too small to carry one.
    /// </summary>
    public static BadgeBox? Place(int width, int height)
    {
        if (width < 120 || height < 120)
        {
            return null;
        }

        var h = Even(Math.Sqrt((double)width * height) * 0.085);
        var w = Even(h * BadgeAspect);
        var maxW = Even(width * 0.6);
        if (w > maxW)
        {
            w = maxW;
            h = Even(w / BadgeAspect);
        }

        var margin = Math.Max(2, (int)Math.Round(h * 0.35));
        return new BadgeBox(w, h, margin, margin);
    }

    /// <summary>The ffmpeg arguments that write <paramref name="output"/> = <paramref name="source"/> with the badge.</summary>
    public static IReadOnlyList<string> FfmpegArgs(string source, string badge, string output, BadgeBox box) => new[]
    {
        "-y", "-v", "error", "-i", source, "-i", badge,
        "-filter_complex",
        string.Create(CultureInfo.InvariantCulture, $"[1:v]scale={box.Width}:{box.Height}[b];[0:v][b]overlay={box.X}:{box.Y}"),
        "-frames:v", "1", "-update", "1", "-q:v", "2", "-pix_fmt", "yuvj420p", output,
    };

    internal static bool IsUnder(string? path, string dir) =>
        !string.IsNullOrEmpty(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Add (<paramref name="want"/>) or remove the badge on the item's primary image. True when the item changed.</summary>
    public async Task<bool> SyncAsync(BaseItem item, bool want, CancellationToken ct)
    {
        var info = item.GetImageInfo(ImageType.Primary, 0);
        var ours = info is not null && IsUnder(info.Path, Dir);
        var record = LoadRecord(item.Id);

        if (!want)
        {
            if (!ours)
            {
                DeleteFiles(item.Id, keep: null); // e.g. a metadata refresh already put another image back
                return false;
            }

            return await RestoreAsync(item, info!, record, ct).ConfigureAwait(false);
        }

        if (info is null || !info.IsLocalFile)
        {
            return false;
        }

        string source;
        if (ours)
        {
            var originalOk = record is not null && File.Exists(record.Original);
            if (originalOk && File.Exists(info.Path) && record!.Style == Style
                && File.GetLastWriteTimeUtc(record.Original).Ticks == record.OriginalTicks)
            {
                return false; // up to date
            }

            if (!originalOk)
            {
                if (File.Exists(info.Path))
                {
                    return false; // the original is gone: the badged copy is the only image left, keep it
                }

                return await RestoreAsync(item, info, record, ct).ConfigureAwait(false);
            }

            source = record!.Original;
        }
        else
        {
            source = info.Path;
        }

        if (!File.Exists(source))
        {
            return false;
        }

        var (width, height) = Dimensions(item, info);
        if (Place(width, height) is not { } box)
        {
            _logger.LogDebug("No X-Ray badge on {Item}: image size unknown or too small ({W}x{H})", item.Name, width, height);
            return false;
        }

        Directory.CreateDirectory(Dir);
        var badge = EnsureBadgeFile();

        // a new file name per build: the image path changes, so server and client image caches cannot serve the old one
        var output = Path.Combine(Dir, item.Id.ToString("N") + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ".jpg");
        if (!await RunFfmpegAsync(FfmpegArgs(source, badge, output, box), ct).ConfigureAwait(false) || !File.Exists(output))
        {
            _logger.LogWarning("Could not put the X-Ray badge on the image of {Item}", item.Name);
            TryDelete(output);
            return false;
        }

        SaveRecord(item.Id, new BadgeRecord { Original = source, OriginalTicks = File.GetLastWriteTimeUtc(source).Ticks, Style = Style });
        item.SetImage(
            new ItemImageInfo
            {
                Path = output,
                Type = ImageType.Primary,
                DateModified = File.GetLastWriteTimeUtc(output),
                Width = width,
                Height = height,
                BlurHash = info.BlurHash,
            },
            0);
        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
        DeleteFiles(item.Id, keep: output);
        _logger.LogDebug("X-Ray badge on the image of {Item}", item.Name);
        return true;
    }

    /// <summary>Point the item back at its original image (or drop the image entry when that file is gone).</summary>
    private async Task<bool> RestoreAsync(BaseItem item, ItemImageInfo info, BadgeRecord? record, CancellationToken ct)
    {
        if (record is not null && File.Exists(record.Original))
        {
            item.SetImage(
                new ItemImageInfo
                {
                    Path = record.Original,
                    Type = ImageType.Primary,
                    DateModified = File.GetLastWriteTimeUtc(record.Original),
                    Width = info.Width,
                    Height = info.Height,
                    BlurHash = info.BlurHash,
                },
                0);
        }
        else
        {
            // nothing to go back to: without an entry the next library scan fetches the image again
            _logger.LogInformation("Original image of {Item} is gone; removing the badged one", item.Name);
            item.RemoveImage(info);
        }

        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
        DeleteFiles(item.Id, keep: null);
        return true;
    }

    private (int Width, int Height) Dimensions(BaseItem item, ItemImageInfo info)
    {
        if (info.Width > 0 && info.Height > 0)
        {
            return (info.Width, info.Height);
        }

        try
        {
            var size = _images.GetImageDimensions(item, info);
            return (size.Width, size.Height);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Image size of {Item} unknown: {Error}", item.Name, ex.Message);
            return (0, 0);
        }
    }

    /// <summary>The embedded badge as a file ffmpeg can read (rewritten when a plugin update changed it).</summary>
    private static string EnsureBadgeFile()
    {
        var path = Path.Combine(Dir, "badge.png");
        using var res = typeof(PosterBadger).Assembly.GetManifestResourceStream(BadgeResource)
            ?? throw new InvalidOperationException("Embedded badge image missing");
        if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
        {
            using var file = File.Create(path);
            res.CopyTo(file);
        }

        return path;
    }

    private async Task<bool> RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_encoder.EncoderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        if (p is null)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FfmpegTimeout);
        try
        {
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var err = await stderr.ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                _logger.LogDebug("ffmpeg badge exit {Code}: {Error}", p.ExitCode, err.Trim());
            }

            return p.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch (Exception)
            {
                // already gone
            }

            ct.ThrowIfCancellationRequested();
            return false; // timeout
        }
    }

    private void DeleteFiles(Guid id, string? keep)
    {
        try
        {
            if (!Directory.Exists(Dir))
            {
                return;
            }

            foreach (var f in Directory.EnumerateFiles(Dir, id.ToString("N") + "-*.jpg"))
            {
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(f);
                }
            }

            if (keep is null)
            {
                TryDelete(RecordPath(id));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not clean badge files of {Id}: {Error}", id, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private static BadgeRecord? LoadRecord(Guid id)
    {
        try
        {
            var path = RecordPath(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<BadgeRecord>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SaveRecord(Guid id, BadgeRecord record) =>
        File.WriteAllText(RecordPath(id), JsonSerializer.Serialize(record));

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>What a badged copy was made from.</summary>
    internal sealed class BadgeRecord
    {
        public string Original { get; set; } = string.Empty;

        public long OriginalTicks { get; set; }

        public int Style { get; set; }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Cuts clips, renders text cards and concatenates everything to one 16:9 mp4 with ffmpeg.</summary>
public class FfmpegRecapBuilder
{
    private const string CardColor = "0x12161c";

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegRecapBuilder> _logger;

    public FfmpegRecapBuilder(IMediaEncoder mediaEncoder, ILogger<FfmpegRecapBuilder> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = PluginLog.Wrap(logger);
    }

    public static (int Width, int Height) Size(int height)
    {
        height = Math.Max(240, height) / 2 * 2;
        return (height * 16 / 9 / 2 * 2, height);
    }

    /// <summary>Text cards reference "card{index}.ass" in ffmpeg's working directory (see <see cref="WriteCardFiles"/>).</summary>
    public static IReadOnlyList<string> BuildArguments(IReadOnlyList<RecapSegment> segments, int height, string output)
    {
        var (width, h) = Size(height);
        var inv = CultureInfo.InvariantCulture;

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var f = new StringBuilder();
        var input = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            switch (segments[i])
            {
                case ClipSpec c:
                    args.AddRange(new[]
                    {
                        "-ss", c.StartSeconds.ToString("0.###", inv),
                        "-t", c.DurationSeconds.ToString("0.###", inv),
                        "-i", c.Path
                    });
                    f.Append(inv, $"[{input}:v:0]scale={width}:{h}:force_original_aspect_ratio=decrease,pad={width}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=24,format=yuv420p[v{i}];");
                    f.Append(inv, $"[{input}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
                    input++;
                    break;

                case TextCardSpec t:
                    var secs = t.Seconds.ToString("0.###", inv);
                    args.AddRange(new[]
                    {
                        "-f", "lavfi", "-t", secs, "-i", $"color=c={CardColor}:s={width}x{h}:r=24",
                        "-f", "lavfi", "-t", secs, "-i", "anullsrc=r=48000:cl=stereo"
                    });
                    f.Append(inv, $"[{input}:v]ass=card{i}.ass,setsar=1,fps=24,format=yuv420p[v{i}];");
                    f.Append(inv, $"[{input + 1}:a]aformat=channel_layouts=stereo[a{i}];");
                    input += 2;
                    break;
            }
        }

        for (var i = 0; i < segments.Count; i++)
        {
            f.Append(inv, $"[v{i}][a{i}]");
        }

        f.Append(inv, $"concat=n={segments.Count}:v=1:a=1[v][a]");

        args.AddRange(new[]
        {
            "-filter_complex", f.ToString(),
            "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "+faststart",
            "-f", "mp4", output
        });
        return args;
    }

    public static void WriteCardFiles(IReadOnlyList<RecapSegment> segments, string dir, int height, string fontName)
    {
        var (width, h) = Size(height);
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i] is TextCardSpec t)
            {
                File.WriteAllText(Path.Combine(dir, $"card{i}.ass"), TextCardFactory.BuildAss(t, width, h, fontName), new UTF8Encoding(false));
            }
        }
    }

    public async Task<bool> BuildAsync(IReadOnlyList<RecapSegment> segments, int height, string fontName, string output, CancellationToken ct)
    {
        if (segments.Count == 0)
        {
            return false;
        }

        var work = Path.Combine(Path.GetTempPath(), "jellycap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            WriteCardFiles(segments, work, height, fontName);

            var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = work
            };
            foreach (var a in BuildArguments(segments, height, Path.GetFullPath(output)))
            {
                psi.ArgumentList.Add(a);
            }

            _logger.LogDebug("Running {Ffmpeg} {Args}", psi.FileName, string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)));
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            _ = proc.StandardOutput.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("ffmpeg recap timed out after 5 minutes; killing it ({Output})", output);
                }

                try
                {
                    proc.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            if (proc.ExitCode != 0)
            {
                _logger.LogError("ffmpeg recap failed ({Code}): {Err}", proc.ExitCode, await stderr.ConfigureAwait(false));
                return false;
            }

            return true;
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete ffmpeg work folder {Dir}", work);
            }
        }
    }
}

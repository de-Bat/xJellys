using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Cuts clips and concatenates them to one 16:9 mp4 with ffmpeg.</summary>
public class FfmpegRecapBuilder
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegRecapBuilder> _logger;

    public FfmpegRecapBuilder(IMediaEncoder mediaEncoder, ILogger<FfmpegRecapBuilder> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public static IReadOnlyList<string> BuildArguments(IReadOnlyList<ClipSpec> clips, int height, string output)
    {
        height = Math.Max(240, height) / 2 * 2;
        var width = height * 16 / 9 / 2 * 2;
        var inv = CultureInfo.InvariantCulture;

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var c in clips)
        {
            args.Add("-ss");
            args.Add(c.StartSeconds.ToString("0.###", inv));
            args.Add("-t");
            args.Add(c.DurationSeconds.ToString("0.###", inv));
            args.Add("-i");
            args.Add(c.Path);
        }

        var f = new StringBuilder();
        for (var i = 0; i < clips.Count; i++)
        {
            f.Append(inv, $"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=24,format=yuv420p[v{i}];");
            f.Append(inv, $"[{i}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
        }

        for (var i = 0; i < clips.Count; i++)
        {
            f.Append(inv, $"[v{i}][a{i}]");
        }

        f.Append(inv, $"concat=n={clips.Count}:v=1:a=1[v][a]");

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

    public async Task<bool> BuildAsync(IReadOnlyList<ClipSpec> clips, int height, string output, CancellationToken ct)
    {
        if (clips.Count == 0)
        {
            return false;
        }

        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in BuildArguments(clips, height, output))
        {
            psi.ArgumentList.Add(a);
        }

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
}

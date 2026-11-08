using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyXR;

public readonly record struct DetectedFace(float X1, float Y1, float X2, float Y2, float Score, float[] Landmarks)
{
    public float Area => (X2 - X1) * (Y2 - Y1);
}

/// <summary>
/// SCRFD face detector + ArcFace recognizer on ONNX Runtime (CPU).
/// Images are raw RGB24, <see cref="Size"/> x <see cref="Size"/> (letterboxed by ffmpeg).
/// </summary>
public sealed class FaceEngine : IDisposable
{
    public const int Size = 640;
    private const int RecSize = 112;

    private static readonly float[] Template =
    {
        38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f
    };

    private readonly ModelManager _models;
    private readonly ILogger<FaceEngine> _logger;
    private readonly SemaphoreSlim _init = new(1, 1);
    private InferenceSession? _det;
    private InferenceSession? _rec;

    public FaceEngine(ModelManager models, ILogger<FaceEngine> logger)
    {
        _models = models;
        _logger = PluginLog.Wrap(logger);
    }

    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_rec is not null)
        {
            return;
        }

        await _init.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rec is not null)
            {
                return;
            }

            await _models.EnsureAsync(ct).ConfigureAwait(false);
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = 2 };
            _det = new InferenceSession(_models.DetectorPath, opts);
            _rec = new InferenceSession(_models.RecognizerPath, opts);
            _logger.LogInformation("JellyXR face models loaded");
        }
        finally
        {
            _init.Release();
        }
    }

    public List<DetectedFace> Detect(byte[] rgb, float threshold)
    {
        var det = _det ?? throw new InvalidOperationException("engine not loaded");
        var data = new float[3 * Size * Size];
        const int plane = Size * Size;
        for (var i = 0; i < plane; i++)
        {
            data[i] = (rgb[i * 3] - 127.5f) / 128f;
            data[plane + i] = (rgb[(i * 3) + 1] - 127.5f) / 128f;
            data[(2 * plane) + i] = (rgb[(i * 3) + 2] - 127.5f) / 128f;
        }

        var input = new DenseTensor<float>(data, new[] { 1, 3, Size, Size });
        var inputName = det.InputMetadata.Keys.First();
        using var results = det.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });

        // Outputs grouped by last dimension: 1 = scores, 4 = box distances, 10 = landmark offsets.
        // Within a group the stride follows from the row count (more rows = smaller stride).
        var scores = new List<float[]>();
        var boxes = new List<float[]>();
        var kps = new List<float[]>();
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            var last = t.Dimensions[t.Dimensions.Length - 1];
            var arr = t.ToArray();
            if (last == 1)
            {
                scores.Add(arr);
            }
            else if (last == 4)
            {
                boxes.Add(arr);
            }
            else if (last == 10)
            {
                kps.Add(arr);
            }
        }

        if (kps.Count == 0)
        {
            throw new InvalidOperationException("detector model has no landmark output (need an SCRFD *_kps model)");
        }

        scores = scores.OrderByDescending(a => a.Length).ToList();
        boxes = boxes.OrderByDescending(a => a.Length).ToList();
        kps = kps.OrderByDescending(a => a.Length).ToList();

        var faces = new List<DetectedFace>();
        var strides = new[] { 8, 16, 32 };
        for (var level = 0; level < Math.Min(strides.Length, scores.Count); level++)
        {
            var stride = strides[level];
            var cells = Size / stride;
            var sc = scores[level];
            var bx = boxes[level];
            var kp = kps[level];
            var anchors = Math.Max(1, sc.Length / (cells * cells)); // 2 for SCRFD
            for (var idx = 0; idx < sc.Length; idx++)
            {
                if (sc[idx] < threshold)
                {
                    continue;
                }

                var cell = idx / anchors;
                var cx = (cell % cells) * stride;
                var cy = (cell / cells) * stride;
                var lm = new float[10];
                for (var k = 0; k < 5; k++)
                {
                    lm[k * 2] = cx + (kp[(idx * 10) + (k * 2)] * stride);
                    lm[(k * 2) + 1] = cy + (kp[(idx * 10) + (k * 2) + 1] * stride);
                }

                faces.Add(new DetectedFace(
                    cx - (bx[idx * 4] * stride),
                    cy - (bx[(idx * 4) + 1] * stride),
                    cx + (bx[(idx * 4) + 2] * stride),
                    cy + (bx[(idx * 4) + 3] * stride),
                    sc[idx],
                    lm));
            }
        }

        return Nms(faces, 0.4f);
    }

    /// <summary>Aligned 512-d unit embedding of a detected face.</summary>
    public float[] Embed(byte[] rgb, DetectedFace face)
    {
        var rec = _rec ?? throw new InvalidOperationException("engine not loaded");
        var data = AlignedInput(rgb, face.Landmarks);
        var input = new DenseTensor<float>(data, new[] { 1, 3, RecSize, RecSize });
        var inputName = rec.InputMetadata.Keys.First();
        using var results = rec.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var emb = results.First().AsTensor<float>().ToArray();
        var norm = MathF.Sqrt(emb.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < emb.Length; i++)
            {
                emb[i] /= norm;
            }
        }

        return emb;
    }

    public static float Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        float s = 0;
        for (var i = 0; i < n; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    /// <summary>Warp the face to the 112x112 ArcFace template (similarity transform, bilinear) as normalized NCHW floats.</summary>
    private static float[] AlignedInput(byte[] rgb, float[] lm)
    {
        // Least-squares similarity src(landmarks) -> dst(template): x' = a*x - b*y + tx ; y' = b*x + a*y + ty
        double sx = 0, sy = 0, dx = 0, dy = 0;
        for (var i = 0; i < 5; i++)
        {
            sx += lm[i * 2];
            sy += lm[(i * 2) + 1];
            dx += Template[i * 2];
            dy += Template[(i * 2) + 1];
        }

        sx /= 5;
        sy /= 5;
        dx /= 5;
        dy /= 5;
        double num1 = 0, num2 = 0, den = 0;
        for (var i = 0; i < 5; i++)
        {
            var px = lm[i * 2] - sx;
            var py = lm[(i * 2) + 1] - sy;
            var qx = Template[i * 2] - dx;
            var qy = Template[(i * 2) + 1] - dy;
            num1 += (px * qx) + (py * qy);
            num2 += (px * qy) - (py * qx);
            den += (px * px) + (py * py);
        }

        var outp = new float[3 * RecSize * RecSize];
        if (den < 1e-6)
        {
            return outp;
        }

        var a = num1 / den;
        var b = num2 / den;
        var tx = dx - ((a * sx) - (b * sy));
        var ty = dy - ((b * sx) + (a * sy));
        var det = (a * a) + (b * b);

        const int plane = RecSize * RecSize;
        for (var v = 0; v < RecSize; v++)
        {
            for (var u = 0; u < RecSize; u++)
            {
                // inverse: src = M^-1 (dst - t), M = [[a,-b],[b,a]]
                var ex = u - tx;
                var ey = v - ty;
                var srcX = ((a * ex) + (b * ey)) / det;
                var srcY = ((-b * ex) + (a * ey)) / det;
                var o = (v * RecSize) + u;
                for (var c = 0; c < 3; c++)
                {
                    outp[(c * plane) + o] = (Bilinear(rgb, srcX, srcY, c) - 127.5f) / 127.5f;
                }
            }
        }

        return outp;
    }

    private static float Bilinear(byte[] rgb, double x, double y, int c)
    {
        if (x < 0 || y < 0 || x > Size - 1 || y > Size - 1)
        {
            return 0;
        }

        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, Size - 1);
        var y1 = Math.Min(y0 + 1, Size - 1);
        var fx = (float)(x - x0);
        var fy = (float)(y - y0);
        float P(int px, int py) => rgb[(((py * Size) + px) * 3) + c];
        var top = (P(x0, y0) * (1 - fx)) + (P(x1, y0) * fx);
        var bot = (P(x0, y1) * (1 - fx)) + (P(x1, y1) * fx);
        return (top * (1 - fy)) + (bot * fy);
    }

    private static List<DetectedFace> Nms(List<DetectedFace> faces, float iouThreshold)
    {
        var keep = new List<DetectedFace>();
        foreach (var f in faces.OrderByDescending(f => f.Score))
        {
            if (keep.All(k => Iou(k, f) < iouThreshold))
            {
                keep.Add(f);
            }
        }

        return keep;
    }

    private static float Iou(DetectedFace a, DetectedFace b)
    {
        var ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        var iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = ix * iy;
        var union = a.Area + b.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose()
    {
        _det?.Dispose();
        _rec?.Dispose();
    }
}

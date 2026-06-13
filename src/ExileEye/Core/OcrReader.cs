using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Tesseract;

namespace ExileEye.Core;

/// <summary>One recognized panel row: parsed name + stack quantity at a vertical position.</summary>
public sealed record OcrLine(string Name, int Quantity, string RawText, int CenterY);

/// <summary>
/// Tesseract front-end tuned for PoE2 list panels. Two engines run two segmentation passes
/// concurrently (engines are single-threaded internally): SingleColumn reads clean lists,
/// SparseText rescues panels whose beveled row dividers confuse column segmentation. Results
/// merge by vertical position, keeping the fuller read per row.
/// </summary>
public sealed class OcrReader : IDisposable
{
    // The panel draws 2–3 cost glyphs left of each name; cropping that column removes the
    // worst OCR garbage. A sliver off the right edge drops border artifacts.
    public const double LeftCropFraction = 0.30;
    public const double RightCropFraction = 0.02;

    private const float MinLineConfidence = 10f;
    private const int Upscale = 3;             // more pixels per glyph for the LSTM
    private const int SameRowTolerance = 25;   // px between merged reads of one row

    private readonly TesseractEngine _columnEngine;
    private readonly TesseractEngine _sparseEngine;
    private readonly Action<string>? _log;

    /// <summary>When set, the exact binarized image handed to Tesseract is saved here each
    /// Read — the single most useful artifact for diagnosing a bad recognition.</summary>
    public string? DebugInputPath { get; set; }

    public OcrReader(string tessdataDir, string language, Action<string>? log = null)
    {
        // Russian ships as a tessdata_fast model (LSTM-only) downloaded at runtime; opening it
        // with the default mode would look for the absent legacy engine and throw.
        var (model, mode) = language == "ru" ? ("rus", EngineMode.LstmOnly) : ("eng", EngineMode.Default);
        _columnEngine = new TesseractEngine(tessdataDir, model, mode);
        _sparseEngine = new TesseractEngine(tessdataDir, model, mode);
        _log = log;
    }

    public IReadOnlyList<OcrLine> Read(Bitmap region)
    {
        byte[] png = PrepareImage(region);
        int height = region.Height;

        var colPass = Task.Run(() => RunPass(_columnEngine, png, PageSegMode.SingleColumn, height));
        var sparsePass = Task.Run(() => RunPass(_sparseEngine, png, PageSegMode.SparseText, height));
        Task.WaitAll(colPass, sparsePass);

        return MergePasses(colPass.Result, sparsePass.Result);
    }

    /// <summary>
    /// Crop decorations → grayscale → bicubic upscale → Otsu binarize to crisp black-on-white.
    /// Binarization is the key step on the parchment "combinations" book: the textured background
    /// and faint map scribbles throw off Tesseract's internal thresholding, so an explicit global
    /// threshold that isolates the ink strokes is what makes those rows read reliably. Polarity is
    /// handled for free — the majority class is the background, whichever way round it is.
    /// </summary>
    private byte[] PrepareImage(Bitmap region)
    {
        int left = Math.Max(1, (int)(region.Width * LeftCropFraction));
        int right = (int)(region.Width * RightCropFraction);
        int w = Math.Max(1, region.Width - left - right);

        using var work = new Bitmap(w * Upscale, region.Height * Upscale, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(work))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(region,
                new Rectangle(0, 0, work.Width, work.Height),
                new Rectangle(left, 0, w, region.Height),
                GraphicsUnit.Pixel);
        }

        Binarize(work);

        if (DebugInputPath is { } path)
            try { work.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }

        using var ms = new MemoryStream();
        work.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>In-place grayscale → Otsu threshold → black text on white background.</summary>
    private static void Binarize(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);

            // Grayscale (luma) + histogram in one pass; overwrite each pixel's blue channel with
            // its gray value so the threshold pass needn't recompute it.
            var hist = new int[256];
            for (int y = 0; y < bmp.Height; y++)
            {
                int rowStart = y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int o = rowStart + x * 3;
                    int gray = (buf[o] * 28 + buf[o + 1] * 151 + buf[o + 2] * 77) >> 8; // BGR luma
                    buf[o] = (byte)gray;
                    hist[gray]++;
                }
            }

            int threshold = OtsuThreshold(hist, bmp.Width * bmp.Height);

            // The background is the majority class; make it white and the text black so Tesseract
            // always sees dark-on-light regardless of the panel's native polarity.
            long darkCount = 0;
            for (int i = 0; i <= threshold; i++) darkCount += hist[i];
            bool backgroundIsDark = darkCount > (long)bmp.Width * bmp.Height / 2;

            for (int y = 0; y < bmp.Height; y++)
            {
                int rowStart = y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int o = rowStart + x * 3;
                    bool isText = backgroundIsDark ? buf[o] > threshold : buf[o] <= threshold;
                    byte v = isText ? (byte)0 : (byte)255;
                    buf[o] = buf[o + 1] = buf[o + 2] = v;
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Otsu's method: the threshold maximizing between-class variance.</summary>
    private static int OtsuThreshold(int[] hist, int total)
    {
        long sum = 0;
        for (int i = 0; i < 256; i++) sum += (long)i * hist[i];

        long sumB = 0;
        int weightB = 0;
        double maxVar = -1;
        int threshold = 127;
        for (int t = 0; t < 256; t++)
        {
            weightB += hist[t];
            if (weightB == 0) continue;
            int weightF = total - weightB;
            if (weightF == 0) break;
            sumB += (long)t * hist[t];
            double meanB = (double)sumB / weightB;
            double meanF = (double)(sum - sumB) / weightF;
            double between = (double)weightB * weightF * (meanB - meanF) * (meanB - meanF);
            if (between > maxVar) { maxVar = between; threshold = t; }
        }
        return threshold;
    }

    private List<OcrLine> RunPass(TesseractEngine engine, byte[] png, PageSegMode mode, int regionHeight)
    {
        var lines = new List<OcrLine>();
        var rejected = new List<string>();
        using var pix = Pix.LoadFromMemory(png);
        using var page = engine.Process(pix, mode);
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box)) continue;
            var text = iter.GetText(PageIteratorLevel.TextLine);
            if (string.IsNullOrWhiteSpace(text)) continue;
            float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
            // Box coords are in the upscaled image — map back to region space.
            int centerY = Math.Clamp((box.Y1 + box.Y2) / 2 / Upscale, 0, regionHeight - 1);

            if (conf < MinLineConfidence) { rejected.Add($"lowconf y={centerY} '{text.Trim()}'"); continue; }
            var parsed = ItemText.Parse(text);
            if (!ItemText.LooksLikeName(parsed.Name)) { rejected.Add($"noise y={centerY} '{text.Trim()}'"); continue; }

            lines.Add(new OcrLine(parsed.Name, parsed.Quantity, text.Trim(), centerY));
        }
        while (iter.Next(PageIteratorLevel.TextLine));

        // When a pass comes back nearly empty, show what Tesseract actually said — distinguishes
        // "saw nothing" from "saw lines that the filters dropped".
        if (lines.Count <= 1 && rejected.Count > 0)
            _log?.Invoke($"OCR {mode} kept {lines.Count}, dropped: {string.Join(" | ", rejected)}");

        return lines;
    }

    private static IReadOnlyList<OcrLine> MergePasses(List<OcrLine> a, List<OcrLine> b)
    {
        static int LetterCount(string s) => s.Count(char.IsLetter);

        var merged = new List<OcrLine>(a);
        foreach (var line in b)
        {
            int at = merged.FindIndex(m => Math.Abs(m.CenterY - line.CenterY) <= SameRowTolerance);
            if (at < 0) merged.Add(line);
            else if (LetterCount(line.Name) > LetterCount(merged[at].Name)) merged[at] = line;
        }
        merged.Sort((x, y) => x.CenterY.CompareTo(y.CenterY));
        return merged;
    }

    public void Dispose()
    {
        _columnEngine.Dispose();
        _sparseEngine.Dispose();
    }
}

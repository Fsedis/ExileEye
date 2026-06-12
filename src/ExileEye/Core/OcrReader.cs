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
    private const int Upscale = 2;
    private const int SameRowTolerance = 25;   // px between merged reads of one row

    private readonly TesseractEngine _columnEngine;
    private readonly TesseractEngine _sparseEngine;
    private readonly Action<string>? _log;

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

    /// <summary>Crop decorations → fix polarity → 2× bicubic upscale → PNG.</summary>
    private static byte[] PrepareImage(Bitmap region)
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

        // Tesseract reads dark-on-light best, and the game uses both polarities: the exchange
        // panel is light text on a dark surface (invert it), the runeshaping-combinations book
        // is dark text on light parchment (inverting THAT degraded marginal rows to misses —
        // only rows brightened by mouse hover survived). Mean luminance picks the polarity.
        if (MeanLuminance(work) < 128) InvertInPlace(work);

        using var ms = new MemoryStream();
        work.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private static int MeanLuminance(Bitmap bmp)
    {
        const int step = 8;   // sparse grid is plenty for a brightness average
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            long sum = 0;
            int samples = 0;
            var row = new byte[data.Stride];
            for (int y = 0; y < bmp.Height; y += step)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < bmp.Width; x += step)
                {
                    int o = x * 3;
                    sum += (row[o] + row[o + 1] + row[o + 2]) / 3;
                    samples++;
                }
            }
            return samples > 0 ? (int)(sum / samples) : 0;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void InvertInPlace(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);
            for (int i = 0; i < len; i++) buf[i] = (byte)~buf[i];
            Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
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

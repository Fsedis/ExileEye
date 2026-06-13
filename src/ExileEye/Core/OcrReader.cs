using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Tesseract;

namespace ExileEye.Core;

/// <summary>One recognized panel row: parsed name + stack quantity at a vertical position.</summary>
public sealed record OcrLine(string Name, int Quantity, string RawText, int CenterY);

/// <summary>A recognized line with its full bounding box (image coords) — used by full-window
/// locating to compute where the panel sits on screen.</summary>
public sealed record LocatedLine(string Name, int Quantity, string RawText, int Left, int Right, int CenterY);

/// <summary>
/// Tesseract front-end tuned for PoE2 list panels. Two engines run two segmentation passes
/// concurrently (engines are single-threaded internally): SingleColumn reads clean lists,
/// SparseText rescues panels whose beveled row dividers confuse column segmentation. Results
/// merge by vertical position, keeping the fuller read per row.
/// </summary>
public sealed class OcrReader : IDisposable
{
    // The panel draws 2–3 cost glyphs left of each name; cropping that column removes the
    // worst OCR garbage. A sliver off the right edge drops border artifacts. LeftCrop is an
    // instance setting: a manually-calibrated region spans the whole panel (crop the icons),
    // but an auto-located region already hugs the names (crop nothing, or it eats the text).
    public const double DefaultLeftCrop = 0.30;
    public const double RightCropFraction = 0.02;

    /// <summary>Fraction of the region width trimmed from the left before OCR. Set 0 for
    /// auto-located regions that already start at the names.</summary>
    public double LeftCrop { get; set; } = DefaultLeftCrop;

    private const float MinLineConfidence = 10f;
    private const int Upscale = 2;
    private const int SameRowTolerance = 25;   // px between merged reads of one row

    // Vertical content trim. PoE panels are calibrated tall enough for the busiest case, so a
    // short list leaves a large empty area below — on the parchment "combinations" book that
    // empty area is covered in faint map scribbles which wreck Tesseract's layout analysis and
    // garble the real rows. Text rows show strong horizontal luma gradients (stroke edges); the
    // scribbles do not, so trimming everything below the last "busy" scanline removes the noise.
    private const int EdgeGradient = 40;       // |Δluma| that counts as a text-stroke edge
    private const int RowActivityMin = 8;      // stroke edges on a scanline to call it a text row
    private const int BottomMargin = 14;       // keep a little below the last row for descenders

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
    /// Locate text anywhere in a full-window capture (no region cropping/trimming/inversion — the
    /// screen is mostly the dark game world, so the per-region polarity heuristic doesn't apply).
    /// Returns every plausible line with its bounding box, so the caller can match names against
    /// the price book and bound the panel from the rows that hit. Auto page segmentation finds
    /// text blocks scattered across the screen.
    /// </summary>
    public IReadOnlyList<LocatedLine> ReadFull(Bitmap window)
    {
        using var work = new Bitmap(window.Width * Upscale, window.Height * Upscale, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(work))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(window, new Rectangle(0, 0, work.Width, work.Height));
        }
        byte[] png;
        using (var ms = new MemoryStream()) { work.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray(); }

        var lines = new List<LocatedLine>();
        using var pix = Pix.LoadFromMemory(png);
        using var page = _columnEngine.Process(pix, PageSegMode.SparseText);
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box)) continue;
            var text = iter.GetText(PageIteratorLevel.TextLine);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (iter.GetConfidence(PageIteratorLevel.TextLine) < MinLineConfidence) continue;
            var parsed = ItemText.Parse(text);
            if (!ItemText.LooksLikeName(parsed.Name)) continue;
            lines.Add(new LocatedLine(parsed.Name, parsed.Quantity, text.Trim(),
                box.X1 / Upscale, box.X2 / Upscale, (box.Y1 + box.Y2) / 2 / Upscale));
        }
        while (iter.Next(PageIteratorLevel.TextLine));
        return lines;
    }

    /// <summary>
    /// Crop decorations → fix polarity → bicubic upscale → PNG. The exchange panel is light text
    /// on dark (invert it), the parchment book is dark text on light (leave it); mean luminance
    /// picks which. Tesseract's own adaptive thresholding then handles the textured background —
    /// an explicit global binarize was tried and amplified the parchment's faint map scribbles
    /// into solid noise across the whole region, which was far worse.
    /// </summary>
    private byte[] PrepareImage(Bitmap region)
    {
        int left = Math.Max(LeftCrop > 0 ? 1 : 0, (int)(region.Width * LeftCrop));
        int right = (int)(region.Width * RightCropFraction);
        int w = Math.Max(1, region.Width - left - right);

        // Trim the empty (scribbled) area below the last text row before OCR.
        int contentBottom = ContentBottom(region, left, w);
        int h = contentBottom < 0 ? region.Height
            : Math.Min(region.Height, contentBottom + BottomMargin);

        using var work = new Bitmap(w * Upscale, h * Upscale, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(work))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(region,
                new Rectangle(0, 0, work.Width, work.Height),
                new Rectangle(left, 0, w, h),
                GraphicsUnit.Pixel);
        }

        if (MeanLuminance(work) < 128) InvertInPlace(work);

        if (DebugInputPath is { } path)
            try { work.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }

        using var ms = new MemoryStream();
        work.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// The y of the last scanline (in region coordinates, within the x-crop) that carries text —
    /// many strong horizontal luma gradients. Returns -1 if the region looks empty. Used to drop
    /// the faint-scribble area below the rows, which otherwise derails recognition.
    /// </summary>
    private static int ContentBottom(Bitmap region, int left, int w)
    {
        var data = region.LockBits(new Rectangle(0, 0, region.Width, region.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            int lastActive = -1;
            int xEnd = Math.Min(left + w, region.Width);
            for (int y = 0; y < region.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                int edges = 0, prev = -1;
                for (int x = left; x < xEnd; x++)
                {
                    int o = x * 3;
                    int gray = (row[o] * 28 + row[o + 1] * 151 + row[o + 2] * 77) >> 8;
                    if (prev >= 0 && Math.Abs(gray - prev) > EdgeGradient) edges++;
                    prev = gray;
                }
                if (edges >= RowActivityMin) lastActive = y;
            }
            return lastActive;
        }
        finally { region.UnlockBits(data); }
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

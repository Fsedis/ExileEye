using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>
/// Cheap "is a UI panel covering the region?" gate so OCR (the expensive step) only runs when
/// something panel-like is actually on screen. Panels are large flat surfaces noticeably
/// brighter than the dungeon behind them; average luminance over a sparse pixel grid separates
/// the two well. False positives (bright outdoor areas) are harmless — the scan loop only shows
/// the overlay after OCR resolves a real priced item.
/// </summary>
public static class PanelProbe
{
    private const int GridStep = 16;           // sample every 16th pixel in both axes
    private const int OpenThreshold = 45;      // mean luminance above this looks like a panel

    public static bool LooksOpen(Bitmap region, out int meanLuminance)
    {
        var data = region.LockBits(new Rectangle(0, 0, region.Width, region.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            long sum = 0;
            int samples = 0;
            var row = new byte[data.Stride];
            for (int y = 0; y < region.Height; y += GridStep)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < region.Width; x += GridStep)
                {
                    int o = x * 3;
                    sum += (row[o] + row[o + 1] + row[o + 2]) / 3;
                    samples++;
                }
            }
            meanLuminance = samples > 0 ? (int)(sum / samples) : 0;
            return meanLuminance >= OpenThreshold;
        }
        finally { region.UnlockBits(data); }
    }
}

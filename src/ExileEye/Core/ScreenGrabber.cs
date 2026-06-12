using System.Drawing;
using System.Drawing.Imaging;

namespace ExileEye.Core;

public static class ScreenGrabber
{
    /// <summary>Snapshot of a screen rectangle in physical pixels. Caller disposes.</summary>
    public static Bitmap Capture(Rectangle region)
    {
        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(region.X, region.Y, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }
}

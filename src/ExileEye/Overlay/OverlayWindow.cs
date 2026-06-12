using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ExileEye.Core;

namespace ExileEye.Overlay;

/// <summary>
/// Click-through price strip drawn next to the calibrated region. A per-pixel-alpha layered
/// window (UpdateLayeredWindow) so labels sit on soft translucent plates over the game art —
/// WS_EX_TRANSPARENT makes every click fall through to the game underneath.
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int LabelWidth = 240;
    private static readonly Color TopValueColor = Color.FromArgb(96, 255, 128);
    private static readonly Color DivineColor = Color.Gold;
    private static readonly Color ExaltedColor = Color.White;
    private static readonly Color HintColor = Color.FromArgb(170, 170, 170);

    private readonly Font _font = new("Segoe UI", 11f, FontStyle.Bold);
    private Rectangle _anchor;   // the calibrated game-panel region this strip docks to
    private int _gap;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20,
                      WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void DockTo(Rectangle anchor, int gap)
    {
        _anchor = anchor;
        _gap = gap;
        Bounds = new Rectangle(anchor.Right + gap, anchor.Y, LabelWidth, anchor.Height);
    }

    /// <summary>Repaint the strip. Empty rows + no hint → fully transparent (invisible).</summary>
    public void Render(IReadOnlyList<DisplayRow> rows, bool showReadingHint)
    {
        using var canvas = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (showReadingHint && rows.Count == 0)
                DrawLabel(g, Height / 2, "…", HintColor);

            decimal top = rows.Where(r => r.Price is not null)
                              .Select(r => r.Price!.Divine * r.Quantity)
                              .DefaultIfEmpty(0m).Max();

            foreach (var row in rows)
            {
                if (row.Price is null) continue;
                bool useDivine = row.Price.Divine >= 1.0m;
                decimal unit = useDivine ? row.Price.Divine : row.Price.Exalted;
                decimal total = unit * row.Quantity;
                string fmt = useDivine ? "0.00" : "0.#";
                // Always dot-decimal: PoE prices are written that way everywhere, and it avoids
                // "0,1" confusion on comma-decimal locales.
                string text = row.Quantity > 1
                    ? $"{total.ToString(fmt, CultureInfo.InvariantCulture)} ({unit.ToString(fmt, CultureInfo.InvariantCulture)} ea) {(useDivine ? "div" : "ex")}"
                    : $"{total.ToString(fmt, CultureInfo.InvariantCulture)} {(useDivine ? "div" : "ex")}";

                bool isTop = top > 0 && row.Price.Divine * row.Quantity == top;
                DrawLabel(g, row.CenterY, text, isTop ? TopValueColor : useDivine ? DivineColor : ExaltedColor);
            }
        }
        Push(canvas);
    }

    private void DrawLabel(Graphics g, int centerY, string text, Color color)
    {
        var size = g.MeasureString(text, _font);
        var plate = new RectangleF(0, centerY - size.Height / 2 - 3, size.Width + 12, size.Height + 6);
        if (plate.Bottom > Height) plate.Y = Height - plate.Height;
        if (plate.Y < 0) plate.Y = 0;

        using (var path = RoundedRect(plate, 6))
        using (var bg = new SolidBrush(Color.FromArgb(150, 18, 18, 24)))
            g.FillPath(bg, path);
        using var fg = new SolidBrush(color);
        g.DrawString(text, _font, fg, plate.X + 6, plate.Y + 3);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void ReassertTopmost()
    {
        const int HWND_TOPMOST = -1;
        const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // --- layered-window plumbing -------------------------------------------------------------

    private void Push(Bitmap frame)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = frame.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE(Width, Height);
            var source = new POINT(0, 0);
            var topLeft = new POINT(Left, Top);
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            UpdateLayeredWindow(Handle, screenDc, ref topLeft, ref size, memDc, ref source, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE(int cx, int cy) { public int Cx = cx, Cy = cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr dcSrc, ref POINT pptSrc, int crKey,
        ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, int after,
        int x, int y, int cx, int cy, uint flags);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ExileEye;

/// <summary>
/// System-tray presence: minimize the window here instead of cluttering the taskbar. Double-click
/// or "Show" restores it; "Exit" really quits. The icon is drawn at runtime (a small eye) so no
/// .ico asset is needed yet.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action onShow, Action onExit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show ExileEye", null, (_, _) => onShow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit());

        _icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "ExileEye",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => onShow();
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(28, 28, 36));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.FromArgb(225, 225, 230));
            g.FillEllipse(white, 6, 11, 20, 11);       // eye white
            using var iris = new SolidBrush(Color.FromArgb(96, 255, 128));
            g.FillEllipse(iris, 12, 12, 8, 8);          // green iris
            using var pupil = new SolidBrush(Color.FromArgb(20, 20, 28));
            g.FillEllipse(pupil, 14, 14, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

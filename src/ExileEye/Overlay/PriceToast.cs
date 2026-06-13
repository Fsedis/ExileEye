using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ExileEye.Overlay;

/// <summary>
/// A small, topmost, non-activating popup shown next to the cursor with a clipboard price-check
/// result. Auto-closes after a few seconds; a click dismisses it early. Runs on its own STA
/// message loop so it's independent of the WPF dispatcher and the scan overlay.
/// </summary>
public static class PriceToast
{
    private static Form? _current;

    public static void Show(Point cursor, string title, string body, Color accent)
    {
        Close();
        var thread = new Thread(() =>
        {
            using var form = new ToastForm(cursor, title, body, accent);
            _current = form;
            Application.Run(form);
            _current = null;
        })
        { IsBackground = true, Name = "ExileEye.Toast" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Close()
    {
        var f = _current;
        if (f is { IsHandleCreated: true })
            try { f.BeginInvoke(f.Close); } catch { }
    }

    private sealed class ToastForm : Form
    {
        private const int LifetimeMs = 5000;

        public ToastForm(Point cursor, string title, string body, Color accent)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(18, 18, 24);
            Padding = new Padding(12, 10, 12, 10);
            DoubleBuffered = true;

            var titleLabel = new Label
            {
                Text = title, AutoSize = true, ForeColor = accent,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold), Dock = DockStyle.Top,
            };
            var bodyLabel = new Label
            {
                Text = body, AutoSize = true, ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Segoe UI", 13f, FontStyle.Bold), Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 0),
            };
            Controls.Add(bodyLabel);
            Controls.Add(titleLabel);

            // Size to content, then place near the cursor, nudged onto the screen if it would clip.
            using (var g = CreateGraphics())
            {
                var tw = g.MeasureString(title, titleLabel.Font).Width;
                var bw = g.MeasureString(body, bodyLabel.Font).Width;
                Width = (int)Math.Ceiling(Math.Max(tw, bw)) + Padding.Horizontal + 4;
                Height = titleLabel.PreferredHeight + bodyLabel.PreferredHeight + Padding.Vertical + 6;
            }
            var screen = Screen.FromPoint(cursor).WorkingArea;
            int x = Math.Min(cursor.X + 18, screen.Right - Width - 4);
            int y = Math.Min(cursor.Y + 18, screen.Bottom - Height - 4);
            Location = new Point(Math.Max(screen.Left, x), Math.Max(screen.Top, y));

            var timer = new System.Windows.Forms.Timer { Interval = LifetimeMs };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
            Click += (_, _) => Close();
            foreach (Control c in Controls) c.Click += (_, _) => Close();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(60, 60, 70));
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}

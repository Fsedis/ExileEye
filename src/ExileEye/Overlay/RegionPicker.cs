using System.Drawing;
using System.Windows.Forms;

namespace ExileEye.Overlay;

/// <summary>
/// One-drag region calibration: dims the whole (virtual) screen, the user rubber-bands the
/// game panel, Esc cancels. Runs its own STA thread so it can be invoked from anywhere.
/// </summary>
public static class RegionPicker
{
    public static Rectangle? Pick()
    {
        Rectangle? result = null;
        var t = new Thread(() =>
        {
            using var form = new PickerForm();
            Application.Run(form);
            result = form.Selection;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private sealed class PickerForm : Form
    {
        public Rectangle? Selection { get; private set; }
        private Point _start;
        private Rectangle _band;
        private bool _dragging;

        public PickerForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            Opacity = 0.4;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _start = e.Location;
            _dragging = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            _band = Rectangle.FromLTRB(
                Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            if (_band.Width >= 40 && _band.Height >= 40)
            {
                // Form-relative → screen coordinates (virtual screen can start at negative X).
                Selection = new Rectangle(_band.X + Bounds.X, _band.Y + Bounds.Y, _band.Width, _band.Height);
            }
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_band.IsEmpty) return;
            using var pen = new Pen(Color.FromArgb(96, 255, 128), 2);
            e.Graphics.DrawRectangle(pen, _band);
            using var fill = new SolidBrush(Color.FromArgb(40, 96, 255, 128));
            e.Graphics.FillRectangle(fill, _band);
        }
    }
}

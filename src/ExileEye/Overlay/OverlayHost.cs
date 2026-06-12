using System.Drawing;
using System.Windows.Forms;
using ExileEye.Core;

namespace ExileEye.Overlay;

/// <summary>
/// Owns the overlay window on a dedicated STA message-loop thread (the scan loop is a plain
/// worker and WPF's dispatcher must stay free). All public members are safe to call from any
/// thread; updates marshal onto the overlay thread.
/// </summary>
public static class OverlayHost
{
    private static OverlayWindow? _window;
    private static Thread? _thread;
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Ready = new();

    public static void Show(Rectangle anchor, int gap)
    {
        lock (Gate)
        {
            if (_window is null)
            {
                Ready.Reset();
                _thread = new Thread(() =>
                {
                    _window = new OverlayWindow();
                    _window.DockTo(anchor, gap);
                    _window.Render([], showReadingHint: false);
                    _window.Show();
                    Ready.Set();
                    Application.Run(_window);
                })
                { IsBackground = true, Name = "ExileEye.Overlay" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                Ready.Wait(TimeSpan.FromSeconds(5));
            }
            else
            {
                Invoke(w => { w.DockTo(anchor, gap); w.Render([], false); });
            }
        }
    }

    public static void Update(IReadOnlyList<DisplayRow> rows, bool showReadingHint) =>
        Invoke(w => w.Render(rows, showReadingHint));

    public static void SetIcons(System.Drawing.Bitmap? divine, System.Drawing.Bitmap? exalted) =>
        Invoke(w => w.SetIcons(divine, exalted));

    public static void Clear() => Invoke(w => w.Render([], false));

    public static void ReassertTopmost() => Invoke(w => w.ReassertTopmost());

    public static void Close()
    {
        lock (Gate)
        {
            var w = _window;
            _window = null;
            if (w is not null && w.IsHandleCreated)
                try { w.BeginInvoke(w.Close); } catch { }
            _thread = null;
        }
    }

    private static void Invoke(Action<OverlayWindow> action)
    {
        var w = _window;
        if (w is null || !w.IsHandleCreated) return;
        try { w.BeginInvoke(() => action(w)); }
        catch { /* window torn down concurrently */ }
    }
}

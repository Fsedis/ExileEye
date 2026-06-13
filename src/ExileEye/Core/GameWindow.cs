using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>
/// Locates the Path of Exile 2 client window so scanning needs no manual calibration — the app
/// knows where the game is and at what resolution. Falls back to the foreground window, then the
/// primary screen, so it still does something sane if the title ever changes.
/// </summary>
public static class GameWindow
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Client-ish bounds of the game window in screen coordinates, or the primary screen.</summary>
    public static Rectangle Bounds()
    {
        var handle = FindGameHandle();
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var r))
        {
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (rect.Width > 200 && rect.Height > 200) return rect;
        }
        return System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? new Rectangle(0, 0, 1920, 1080);
    }

    private static IntPtr FindGameHandle()
    {
        // PoE2's window title is "Path of Exile 2"; match by prefix to be resilient to suffixes.
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero &&
                    p.MainWindowTitle.StartsWith("Path of Exile", StringComparison.OrdinalIgnoreCase))
                    return p.MainWindowHandle;
            }
            catch { /* access denied on some system processes — skip */ }
        }
        return GetForegroundWindow();
    }
}

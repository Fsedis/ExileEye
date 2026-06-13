using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>
/// Win32 clipboard text access with retries. The managed WPF/WinForms Clipboard throws
/// CLIPBRD_E_CANT_OPEN whenever another process has the clipboard open — and price-check
/// overlays (incl. ones the user may run alongside) poll it constantly, so a single attempt
/// often fails. OpenClipboard is retried, as Microsoft recommends.
/// </summary>
public static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;
    private const int Retries = 12;
    private const int RetryDelayMs = 12;

    public static string? Read()
    {
        if (!OpenWithRetry()) return null;
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static void Write(string text)
    {
        if (!OpenWithRetry()) return;
        try
        {
            EmptyClipboard();
            if (text.Length == 0) return;   // intentional clear
            IntPtr mem = Marshal.StringToHGlobalUni(text);
            if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero) Marshal.FreeHGlobal(mem);
            // On success the system owns `mem`; do not free it.
        }
        finally { CloseClipboard(); }
    }

    private static bool OpenWithRetry()
    {
        for (int i = 0; i < Retries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
}

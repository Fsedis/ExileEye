using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>Synthesises the Ctrl+C the game needs to copy the hovered item to the clipboard.</summary>
public static class InputSender
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private const byte VK_CONTROL = 0x11, VK_C = 0x43;
    private const uint KEYUP = 0x2;

    public static void SendCtrlC()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_C, 0, 0, UIntPtr.Zero);
        keybd_event(VK_C, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }
}

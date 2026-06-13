using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>
/// Synthesises the Ctrl+C the game needs to copy the hovered item to the clipboard. Uses
/// SendInput with hardware scan codes — PoE reads raw input, and a vk-only keybd_event is often
/// ignored. (If the game runs elevated and ExileEye does not, Windows blocks synthetic input to
/// it entirely; ExileEye then needs to run as administrator too.)
/// </summary>
public static class InputSender
{
    private const ushort ScanLCtrl = 0x1D;
    private const ushort ScanC = 0x2E;

    public static void SendCtrlC()
    {
        var inputs = new[]
        {
            Key(ScanLCtrl, down: true),
            Key(ScanC, down: true),
            Key(ScanC, down: false),
            Key(ScanLCtrl, down: false),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort scan, bool down) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = scan,
                dwFlags = (uint)(KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP)),
            },
        },
    };

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}

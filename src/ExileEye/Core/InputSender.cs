using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>
/// Sends the Ctrl+C the game uses to copy the hovered item (confirmed by Exiled Exchange 2, MIT —
/// PoE2 copies item text on Ctrl+C). Uses SendInput with hardware scan codes, which is how
/// AutoHotkey and uiohook drive PoE. Before pressing, it releases any modifiers the user may be
/// holding (e.g. the hotkey's Ctrl) so the game sees a clean Ctrl+C and nothing else.
/// (If the game runs elevated, ExileEye must run elevated too, or Windows blocks the input.)
/// </summary>
public static class InputSender
{
    private const ushort ScanLCtrl = 0x1D;
    private const ushort ScanLShift = 0x2A;
    private const ushort ScanLAlt = 0x38;
    private const ushort ScanRCtrl = 0x1D;   // extended
    private const ushort ScanC = 0x2E;

    public static void SendCtrlC()
    {
        // Release whatever the user might be holding from the trigger hotkey.
        Send(
            Up(ScanLCtrl), Up(ScanLShift), Up(ScanLAlt), UpExt(ScanRCtrl));
        // Clean Ctrl+C.
        Send(Down(ScanLCtrl), Down(ScanC), Up(ScanC), Up(ScanLCtrl));
    }

    private static INPUT Down(ushort scan) => Key(scan, KEYEVENTF_SCANCODE);
    private static INPUT Up(ushort scan) => Key(scan, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP);
    private static INPUT UpExt(ushort scan) =>
        Key(scan, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY);

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT Key(ushort scan, uint flags) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } },
    };

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

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

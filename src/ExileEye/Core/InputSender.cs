using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>
/// Triggers the game's "copy hovered item" the way Exiled Exchange 2 does (MIT — see THIRD-PARTY):
/// the price-check hotkey is Ctrl+D, so when it fires the user is already holding Ctrl. We release
/// the non-modifier key (D) and then tap C — Ctrl is still physically held, so the game sees a
/// clean Ctrl+C. Blasting a full synthetic Ctrl+C instead fails, because D is still down and the
/// game sees Ctrl+D+C. Uses SendInput with hardware scan codes (PoE reads raw input).
/// (If the game runs elevated, ExileEye must run elevated too, or Windows blocks the input.)
/// </summary>
public static class InputSender
{
    private const ushort ScanD = 0x20;
    private const ushort ScanC = 0x2E;

    public static void SendItemCopy()
    {
        Send(Key(ScanD, down: false));                       // release the held hotkey letter
        Send(Key(ScanC, down: true), Key(ScanC, down: false)); // tap C (Ctrl still held by the user)
    }

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

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

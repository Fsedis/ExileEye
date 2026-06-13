using System.Runtime.InteropServices;

namespace ExileEye.Core;

/// <summary>
/// Sends the Ctrl+C the game uses to copy the hovered item (PoE2 copies item text on Ctrl+C,
/// confirmed via Exiled Exchange 2). Uses SendInput with virtual-key codes — the same form
/// robotjs/keybd_event tools (e.g. PoE Overlay) use to drive PoE. Releases any modifiers the user
/// might be holding from the trigger hotkey first, so the game sees a clean Ctrl+C.
/// Returns the number of input events the OS accepted (0 ⇒ blocked, e.g. the game is elevated and
/// ExileEye is not).
/// </summary>
public static class InputSender
{
    private const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_C = 0x43;

    public static uint SendCtrlC()
    {
        uint n = Send(Up(VK_CONTROL), Up(VK_SHIFT), Up(VK_MENU));   // drop any held modifiers
        Thread.Sleep(8);
        n += Send(Down(VK_CONTROL), Down(VK_C), Up(VK_C), Up(VK_CONTROL));
        return n;
    }

    private static INPUT Down(ushort vk) => Key(vk, 0);
    private static INPUT Up(ushort vk) => Key(vk, KEYEVENTF_KEYUP);

    private static uint Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT Key(ushort vk, uint flags) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };

    private const uint KEYEVENTF_KEYUP = 0x0002;

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

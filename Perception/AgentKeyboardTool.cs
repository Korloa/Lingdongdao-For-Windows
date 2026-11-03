using System.Runtime.InteropServices;

namespace LingDongDao.Perception;

// Sends only the browser address-bar shortcut, Unicode URL text, and Enter.
// It never touches the clipboard or provides a general command-execution tool.
internal static class AgentKeyboardTool
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002, Unicode = 0x0004;
    private const ushort Control = 0x11, L = 0x4C, Enter = 0x0D;

    public static bool Navigate(string url)
    {
        if (!AgentBrowserTool.TryNormalizeUrl(url, out string normalized)) return false;
        return PressChord(Control, L) && TypeUnicode(normalized) && Press(Enter);
    }

    private static bool PressChord(ushort modifier, ushort key)
    {
        Input[] inputs =
        [
            Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true)
        ];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return true;
        SendInput(2, [Key(key, true), Key(modifier, true)], Marshal.SizeOf<Input>());
        return false;
    }

    private static bool Press(ushort key)
    {
        Input[] inputs = [Key(key, false), Key(key, true)];
        uint sent = SendInput(2, inputs, Marshal.SizeOf<Input>());
        if (sent == 2) return true;
        SendInput(1, [Key(key, true)], Marshal.SizeOf<Input>());
        return false;
    }

    private static bool TypeUnicode(string text)
    {
        foreach (char character in text)
        {
            Input[] inputs =
            [
                new Input { Type = InputKeyboard, KeyData = new KeyInput
                    { Scan = character, Flags = Unicode } },
                new Input { Type = InputKeyboard, KeyData = new KeyInput
                    { Scan = character, Flags = Unicode | KeyUp } }
            ];
            uint sent = SendInput(2, inputs, Marshal.SizeOf<Input>());
            if (sent == 2) continue;
            SendInput(1, [inputs[1]], Marshal.SizeOf<Input>());
            return false;
        }
        return true;
    }

    private static Input Key(ushort virtualKey, bool up) => new()
    {
        Type = InputKeyboard,
        KeyData = new KeyInput { VirtualKey = virtualKey, Flags = up ? KeyUp : 0 }
    };

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyInput KeyData;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nint ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}

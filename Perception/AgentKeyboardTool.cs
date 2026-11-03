using System.Runtime.InteropServices;

namespace LingDongDao.Perception;

// Sends approved shortcuts and Unicode text without using the clipboard.
internal static class AgentKeyboardTool
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002, Unicode = 0x0004;
    private const ushort Control = 0x11, L = 0x4C, Enter = 0x0D;
    private static readonly HashSet<string> Approved = new(StringComparer.OrdinalIgnoreCase)
    {
        "WIN", "WIN+E", "WIN+D", "ENTER", "ESC", "TAB", "SHIFT+TAB",
        "ALT+TAB", "ALT+F4", "CTRL+A", "CTRL+L", "CTRL+C", "CTRL+V",
        "CTRL+S", "CTRL+Z", "CTRL+F", "CTRL+W", "CTRL+ENTER",
        "BACKSPACE", "DELETE", "UP", "DOWN", "LEFT", "RIGHT", "SPACE", "F2"
    };
    private static readonly Dictionary<string, ushort> VirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WIN"] = 0x5B, ["CTRL"] = Control, ["ALT"] = 0x12,
        ["SHIFT"] = 0x10, ["ENTER"] = Enter, ["ESC"] = 0x1B,
        ["TAB"] = 0x09, ["E"] = 0x45, ["D"] = 0x44,
        ["A"] = 0x41, ["L"] = L, ["C"] = 0x43, ["V"] = 0x56,
        ["S"] = 0x53, ["Z"] = 0x5A, ["F"] = 0x46, ["W"] = 0x57,
        ["F4"] = 0x73, ["F2"] = 0x71, ["BACKSPACE"] = 0x08,
        ["DELETE"] = 0x2E, ["UP"] = 0x26, ["DOWN"] = 0x28,
        ["LEFT"] = 0x25, ["RIGHT"] = 0x27, ["SPACE"] = 0x20
    };

    public static bool IsApprovedShortcut(string? shortcut) =>
        shortcut is not null && Approved.Contains(shortcut.Trim());

    public static bool PressKeys(string shortcut)
    {
        shortcut = shortcut.Trim().ToUpperInvariant();
        if (!IsApprovedShortcut(shortcut)) return false;
        string[] parts = shortcut.Split('+');
        ushort[] keys = parts.Select(part => VirtualKeys[part]).ToArray();
        var inputs = new List<Input>();
        foreach (ushort key in keys) inputs.Add(Key(key, false));
        foreach (ushort key in keys.Reverse()) inputs.Add(Key(key, true));
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        if (sent == inputs.Count) return true;
        SendInput((uint)keys.Length, keys.Reverse().Select(key => Key(key, true)).ToArray(),
            Marshal.SizeOf<Input>());
        return false;
    }

    public static bool TypeText(string text) => text.Length is >= 1 and <= 1000 &&
        !text.Any(char.IsControl) && TypeUnicode(text);

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

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
        "CTRL+S", "CTRL+SHIFT+S", "CTRL+Z", "CTRL+F", "CTRL+W", "CTRL+ENTER",
        "CTRL+N", "CTRL+SHIFT+N", "CTRL+O", "CTRL+P", "CTRL+SHIFT+P",
        "CTRL+T", "CTRL+TAB", "ALT+D", "ALT+LEFT", "ALT+RIGHT",
        "BACKSPACE", "DELETE", "UP", "DOWN", "LEFT", "RIGHT", "SPACE",
        "F2", "F5"
    };
    private static readonly Dictionary<string, ushort> VirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WIN"] = 0x5B, ["CTRL"] = Control, ["ALT"] = 0x12,
        ["SHIFT"] = 0x10, ["ENTER"] = Enter, ["ESC"] = 0x1B,
        ["TAB"] = 0x09, ["E"] = 0x45, ["D"] = 0x44,
        ["A"] = 0x41, ["L"] = L, ["C"] = 0x43, ["V"] = 0x56,
        ["S"] = 0x53, ["Z"] = 0x5A, ["F"] = 0x46, ["W"] = 0x57,
        ["N"] = 0x4E, ["O"] = 0x4F, ["P"] = 0x50, ["T"] = 0x54,
        ["F4"] = 0x73, ["F2"] = 0x71, ["F5"] = 0x74, ["BACKSPACE"] = 0x08,
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

    public static bool TypeText(string text) => text.Length is >= 1 and <= 20000 &&
        !text.Any(char.IsControl) && TypeUnicode(text);

    public static async Task<bool> TypeTextAsync(string text, int intervalMs,
        CancellationToken cancellationToken)
    {
        if (text.Length is < 1 or > 20000 || text.Any(character =>
            char.IsControl(character) && character is not ('\r' or '\n' or '\t'))) return false;
        for (int index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            char character = text[index];
            bool sent = character switch
            {
                '\r' => Press(Enter),
                '\n' when index > 0 && text[index - 1] == '\r' => true,
                '\n' => Press(Enter),
                '\t' => Press(0x09),
                _ => TypeUnicode(character.ToString())
            };
            if (!sent) return false;
            if (intervalMs > 0) await Task.Delay(intervalMs, cancellationToken);
        }
        return true;
    }

    public static bool Navigate(string url)
    {
        if (!AgentBrowserTool.TryNormalizeUrl(url, out string normalized)) return false;
        return PressChord(Control, L) && TypeUnicode(normalized) && Press(Enter);
    }

    public static async Task<bool> NavigateAsync(string url, int intervalMs,
        CancellationToken cancellationToken)
    {
        if (!AgentBrowserTool.TryNormalizeUrl(url, out string normalized)) return false;
        return PressChord(Control, L) &&
            await TypeTextAsync(normalized, intervalMs, cancellationToken) && Press(Enter);
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

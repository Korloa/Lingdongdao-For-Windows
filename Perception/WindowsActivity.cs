using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LingDongDao.Perception;

public sealed class WindowsActivity : IDisposable
{
    private const int MouseHook = 14, KeyboardHook = 13;
    private const uint ForegroundEvent = 0x0003, FocusEvent = 0x8005;
    private const int ButtonDown = 0x0201, RightButtonDown = 0x0204, MiddleButtonDown = 0x0207;
    private const int MouseWheel = 0x020A, MouseHorizontalWheel = 0x020E;
    private const int KeyDown = 0x0100, SysKeyDown = 0x0104;
    private readonly ConcurrentQueue<ActivityEvent> _events = new();
    private readonly HookProc _mouseCallback;
    private readonly HookProc _keyboardCallback;
    private readonly WinEventProc _windowCallback;
    private nint _mouseHook, _keyboardHook, _foregroundHook, _focusHook;
    private DateTimeOffset _lastKeyEvent;
    private DateTimeOffset _lastFocusEvent;
    private bool _disposed;

    public WindowsActivity()
    {
        _mouseCallback = OnMouse;
        _keyboardCallback = OnKeyboard;
        _windowCallback = OnWindow;
    }

    // Install on a thread with a message pump, such as the WPF UI thread.
    public void Start()
    {
        if (_mouseHook != 0 || _disposed) return;
        nint module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(MouseHook, _mouseCallback, module, 0);
        _keyboardHook = SetWindowsHookEx(KeyboardHook, _keyboardCallback, module, 0);
        _foregroundHook = SetWinEventHook(ForegroundEvent, ForegroundEvent, 0,
            _windowCallback, 0, 0, 0);
        _focusHook = SetWinEventHook(FocusEvent, FocusEvent, 0,
            _windowCallback, 0, 0, 0);
        if (_mouseHook == 0 || _keyboardHook == 0)
            throw new InvalidOperationException("Windows input hook installation failed.");
    }

    public IReadOnlyList<ActivityEvent> Drain(DateTimeOffset since)
    {
        var events = new List<ActivityEvent>();
        while (_events.TryDequeue(out var item))
            if (item.Time >= since) events.Add(item);
        return events;
    }

    public (int X, int Y) MousePosition() => GetCursorPos(out NativePoint point)
        ? (point.X, point.Y) : (0, 0);

    public WindowInfo? Foreground()
    {
        nint handle = GetForegroundWindow();
        if (handle == 0) return null;
        GetWindowThreadProcessId(handle, out uint processId);
        string processName = "unknown";
        try { using Process process = Process.GetProcessById((int)processId); processName = process.ProcessName; }
        catch (Exception) { }
        var text = new StringBuilder(512);
        GetWindowText(handle, text, text.Capacity);
        GetWindowRect(handle, out NativeRect bounds);
        return new WindowInfo(handle.ToInt64(), (int)processId, processName, text.ToString(),
            new PixelRect(bounds.Left, bounds.Top, Math.Max(0, bounds.Right - bounds.Left),
                Math.Max(0, bounds.Bottom - bounds.Top)), IsWindowVisible(handle));
    }

    private void Add(ActivityEvent item)
    {
        _events.Enqueue(item);
        while (_events.Count > 128 && _events.TryDequeue(out _)) { }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            int kind = message.ToInt32();
            if (kind is ButtonDown or RightButtonDown or MiddleButtonDown or MouseWheel or MouseHorizontalWheel)
            {
                MouseData sample = Marshal.PtrToStructure<MouseData>(data);
                Add(new ActivityEvent(DateTimeOffset.UtcNow,
                    kind is MouseWheel or MouseHorizontalWheel ? "mouse_wheel" : "mouse_click",
                    sample.Point.X, sample.Point.Y));
            }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && message.ToInt32() is KeyDown or SysKeyDown &&
            DateTimeOffset.UtcNow - _lastKeyEvent >= TimeSpan.FromMilliseconds(250))
        {
            _lastKeyEvent = DateTimeOffset.UtcNow;
            Add(new ActivityEvent(_lastKeyEvent, "keyboard_activity", -1, -1));
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private void OnWindow(nint hook, uint eventType, nint handle, int objectId,
        int childId, uint threadId, uint time)
    {
        if (handle == 0 || objectId != 0 || childId != 0) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (eventType == FocusEvent && now - _lastFocusEvent < TimeSpan.FromMilliseconds(400)) return;
        if (eventType == FocusEvent) _lastFocusEvent = now;
        Add(new ActivityEvent(now, eventType == ForegroundEvent ? "window_switch" : "focus_change", -1, -1));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
        if (_focusHook != 0) UnhookWinEvent(_focusHook);
    }

    private delegate nint HookProc(int code, nint message, nint data);
    private delegate void WinEventProc(nint hook, uint eventType, nint handle,
        int objectId, int childId, uint threadId, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    {
        public NativePoint Point;
        public uint MouseDataValue, Flags, Time;
        public nint ExtraInfo;
    }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module,
        WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}

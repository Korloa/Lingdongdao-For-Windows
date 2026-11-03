using System.Runtime.InteropServices;

namespace LingDongDao.Perception;

public static class DesktopWindowTool
{
    public static bool Activate(WindowInfo target)
    {
        nint handle = new(target.Handle);
        if (handle == nint.Zero || !IsWindow(handle)) return false;
        GetWindowThreadProcessId(handle, out uint processId);
        if (processId != target.ProcessId) return false;
        ShowWindow(handle, 9); // SW_RESTORE
        if (GetForegroundWindow() == handle) return true;
        SetForegroundWindow(handle);
        if (GetForegroundWindow() == handle) return true;
        uint current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (foreground != 0 && foreground != current &&
            AttachThreadInput(current, foreground, true))
        {
            try { SetForegroundWindow(handle); }
            finally { AttachThreadInput(current, foreground, false); }
        }
        if (GetForegroundWindow() == handle) return true;
        SwitchToThisWindow(handle, true);
        return GetForegroundWindow() == handle;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint handle, bool altTab);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool state);
}

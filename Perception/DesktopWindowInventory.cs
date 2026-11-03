using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LingDongDao.Perception;

public static class DesktopWindowInventory
{
    public static IReadOnlyList<WindowInfo> VisibleWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, 4) != nint.Zero) return true;
            GetWindowRect(handle, out NativeRect rect);
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 30 || height < 30) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            string name;
            try { using Process process = Process.GetProcessById((int)pid); name = process.ProcessName; }
            catch (Exception) { return true; }
            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            windows.Add(new WindowInfo(handle.ToInt64(), (int)pid, name, title.ToString(),
                new PixelRect(rect.Left, rect.Top, width, height), true));
            return true;
        }, nint.Zero);
        return windows;
    }

    private delegate bool WindowCallback(nint handle, nint parameter);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
}

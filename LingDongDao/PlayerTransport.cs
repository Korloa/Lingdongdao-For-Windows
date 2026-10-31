using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LingDongDao;

internal static class PlayerTransport
{
    internal static string LastStatus { get; private set; } = "未发送";
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr handle, out ClientRect rect);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect
    {
        public int Left, Top, Right, Bottom;
    }

    public static bool TrySend(string source, MusicControl control)
    {
        LastStatus = "准备发送";
        string name = source.Contains("kugou", StringComparison.OrdinalIgnoreCase) ||
                      source.Contains("kgmusic", StringComparison.OrdinalIgnoreCase)
            ? "KuGou" : "cloudmusic";
        Process[] processes = Process.GetProcessesByName(name);
        try
        {
            if (name == "KuGou")
            {
                IntPtr kugouWindow = processes.Select(process => process.MainWindowHandle)
                    .FirstOrDefault(handle => handle != IntPtr.Zero);
                if (kugouWindow == IntPtr.Zero)
                    kugouWindow = processes.Select(process =>
                        PlayerWindowTitles.FindKugouPlayerHandle(process.Id))
                        .FirstOrDefault(handle => handle != IntPtr.Zero);
                if (kugouWindow == IntPtr.Zero) { LastStatus = "找不到酷狗主窗口"; return false; }
                bool wasVisible = IsWindowVisible(kugouWindow);
                bool wasIconic = IsIconic(kugouWindow);
                GetClientRect(kugouWindow, out var rect);
                bool restore = !wasVisible || wasIconic ||
                    rect.Right - rect.Left <= 400 || rect.Bottom - rect.Top <= 300;
                if (restore)
                {
                    ShowWindow(kugouWindow, 4); // SW_SHOWNOACTIVATE
                    Thread.Sleep(200);
                }
                try
                {
                    if (!GetClientRect(kugouWindow, out rect) ||
                        rect.Right - rect.Left <= 400 || rect.Bottom - rect.Top <= 300)
                    {
                        LastStatus = "酷狗播放窗口尺寸无效";
                        return false;
                    }
                    // KuGou's bottom control positions follow its client width.
                    // Mapping reference: https://github.com/Enkianssus/AwooMusicBot/tree/main/experiments/kugou-control-poc
                    int x = (rect.Right - rect.Left) / 2 + (control switch
                    {
                        MusicControl.Previous => -55,
                        MusicControl.Next => 51,
                        _ => 0
                    });
                    int y = rect.Bottom - rect.Top - 37;
                    IntPtr point = new((y << 16) | (x & 0xFFFF));
                    bool sent = PostMessage(kugouWindow, 0x0200, IntPtr.Zero, point) &&
                                PostMessage(kugouWindow, 0x0201, new IntPtr(1), point) &&
                                PostMessage(kugouWindow, 0x0202, IntPtr.Zero, point);
                    Thread.Sleep(200);
                    LastStatus = sent ? "窗口控件已接收操作" : "酷狗窗口消息投递失败";
                    return sent;
                }
                finally
                {
                    if (restore) ShowWindow(kugouWindow, wasVisible ? 6 : 0);
                }
            }
            IntPtr player = processes.Select(process => process.MainWindowHandle)
                .FirstOrDefault(handle => handle != IntPtr.Zero);
            if (player == IntPtr.Zero) return false;
            IntPtr before = GetForegroundWindow();
            try
            {
                if (!SetForegroundWindow(player)) return false;
                Thread.Sleep(250);
                if (GetForegroundWindow() != player) return false;
                if (control == MusicControl.Toggle && name == "cloudmusic") Tap(0x20);
                else
                {
                    KeyDown(0x11); // Ctrl
                    KeyDown(0x12); // Alt
                    try
                    {
                        Tap(control switch
                        {
                            MusicControl.Previous => 0x25,
                            MusicControl.Next => 0x27,
                            _ => 0x26
                        });
                    }
                    finally
                    {
                        KeyUp(0x12);
                        KeyUp(0x11);
                    }
                }
                Thread.Sleep(150);
                return true;
            }
            finally
            {
                if (before != IntPtr.Zero && before != player) SetForegroundWindow(before);
            }
        }
        catch (Exception exception) { LastStatus = exception.Message; return false; }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private static void Tap(byte key)
    {
        KeyDown(key);
        KeyUp(key);
    }

    private static void KeyDown(byte key) => keybd_event(key, 0, 0, UIntPtr.Zero);
    private static void KeyUp(byte key) => keybd_event(key, 0, 2, UIntPtr.Zero);

}

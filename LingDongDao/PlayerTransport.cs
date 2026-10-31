using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LingDongDao;

internal static class PlayerTransport
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public static bool TrySend(string source, MusicControl control)
    {
        string name = source.Contains("kugou", StringComparison.OrdinalIgnoreCase) ||
                      source.Contains("kgmusic", StringComparison.OrdinalIgnoreCase)
            ? "KuGou" : "cloudmusic";
        Process[] processes = Process.GetProcessesByName(name);
        try
        {
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
        catch (Exception) { return false; }
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

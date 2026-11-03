using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LingDongDao.Perception;

public static class AgentBrowserTool
{
    public static HashSet<nint> SnapshotWindows(string browser)
    {
        string processName = browser == "edge" ? "msedge" : "chrome";
        var windows = new HashSet<nint>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, 4) != nint.Zero)
                return true;
            GetWindowThreadProcessId(handle, out uint processId);
            try
            {
                using Process process = Process.GetProcessById((int)processId);
                if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    windows.Add(handle);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, nint.Zero);
        return windows;
    }

    public static void FocusNewWindow(string browser, IReadOnlySet<nint> previous)
    {
        HashSet<nint> available = SnapshotWindows(browser);
        nint target = available.FirstOrDefault(handle => !previous.Contains(handle));
        if (target == nint.Zero) target = available.FirstOrDefault();
        if (target == nint.Zero) return;
        ShowWindow(target, 9); // SW_RESTORE
        if (SetForegroundWindow(target)) return;
        // Attach only while requesting focus; never send clicks to a stale window.
        uint current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (foreground == 0 || foreground == current ||
            !AttachThreadInput(current, foreground, true)) return;
        try { SetForegroundWindow(target); }
        finally { AttachThreadInput(current, foreground, false); }
    }
    public static bool TryNormalizeUrl(string? input, out string url)
    {
        url = "";
        string value = (input ?? "").Trim();
        if (value.Length is < 4 or > 2048 || value.Any(char.IsControl) ||
            value.Any(char.IsWhiteSpace)) return false;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || !uri.Host.Contains('.') ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback ||
            (IPAddress.TryParse(uri.Host, out IPAddress? address) && IsPrivate(address)))
            return false;
        url = uri.AbsoluteUri;
        return true;
    }

    public static bool TryLaunch(string browser, out string reason)
    {
        string? path = FindExecutable(browser);
        if (path is null) { reason = "未找到 Chrome 或 Edge 浏览器"; return false; }
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add("about:blank");
            using Process? process = Process.Start(start);
            if (process is null) { reason = "浏览器未能启动"; return false; }
            reason = "浏览器启动请求已发出";
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                           InvalidOperationException or IOException)
        { reason = "浏览器启动失败：" + exception.GetType().Name; return false; }
    }

    public static string? FindExecutable(string browser)
    {
        string file = browser switch
        {
            "chrome" => "chrome.exe",
            "edge" => "msedge.exe",
            _ => ""
        };
        if (file.Length == 0) return null;
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + file);
                if (key?.GetValue(null) is string value && File.Exists(value)) return value;
            }
            catch (Exception) { }
        }
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ];
        string suffix = browser == "chrome" ? @"Google\Chrome\Application\chrome.exe" :
            @"Microsoft\Edge\Application\msedge.exe";
        return roots.Select(root => Path.Combine(root, suffix)).FirstOrDefault(File.Exists);
    }

    private static bool IsPrivate(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                bytes[0] is 0xFC or 0xFD;
        return bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 0 ||
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 169 && bytes[1] == 254;
    }

    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachState);
}

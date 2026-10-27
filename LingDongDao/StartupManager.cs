using Microsoft.Win32;

namespace LingDongDao;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "LingDongDao";

    private static string CurrentCommand => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabledForCurrentExecutable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return string.Equals(key?.GetValue(EntryName) as string, CurrentCommand,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                ?? throw new InvalidOperationException("无法打开当前用户启动项。 ");
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(Environment.ProcessPath))
                    throw new InvalidOperationException("无法确定程序路径。");
                key.SetValue(EntryName, CurrentCommand, RegistryValueKind.String);
            }
            else if (key.GetValue(EntryName) is not null)
                key.DeleteValue(EntryName, false);
            return true;
        }
        catch (Exception exception)
        {
            error = $"设置开机启动失败：{exception.Message}";
            return false;
        }
    }
}

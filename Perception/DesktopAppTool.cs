using System.Diagnostics;

namespace LingDongDao.Perception;

public static class DesktopAppTool
{
    public static bool TryLaunch(string name, out string reason)
    {
        string requested = name.Trim();
        if (requested.Length is < 2 or > 80 || requested.Any(char.IsControl) ||
            requested.IndexOfAny(['\\', '/', ':', '"', '<', '>', '|']) >= 0)
        { reason = "应用名称无效"; return false; }
        string? executable = requested.ToLowerInvariant() switch
        {
            "资源管理器" or "文件资源管理器" or "explorer" or "file explorer" =>
                Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\explorer.exe",
            "记事本" or "notepad" =>
                Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\notepad.exe",
            "chrome" or "google chrome" or "谷歌浏览器" =>
                AgentBrowserTool.FindExecutable("chrome"),
            "edge" or "microsoft edge" => AgentBrowserTool.FindExecutable("edge"),
            "vs code" or "vscode" or "visual studio code" or "code" or "代码编辑器" =>
                FindCodeExecutable() ?? FindStartMenuShortcut("Visual Studio Code"),
            _ => FindStartMenuShortcut(requested)
        };
        if (executable is null || !File.Exists(executable))
        { reason = "开始菜单中未找到该应用"; return false; }
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(executable)
            { UseShellExecute = true });
            reason = "应用启动请求已发出";
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception)
        { reason = "应用启动失败：" + exception.GetType().Name; return false; }
    }

    public static string? FindCodeExecutable()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft VS Code", "Code.exe")
        ];
        string? installed = candidates.FirstOrDefault(File.Exists);
        if (installed is not null) return installed;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (!File.Exists(Path.Combine(folder, "code.cmd"))) continue;
                string candidate = Path.GetFullPath(Path.Combine(folder, "..", "Code.exe"));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or
                UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string? FindStartMenuShortcut(string name)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        ];
        var candidates = new List<string>();
        foreach (string root in roots.Where(Directory.Exists))
        {
            try { candidates.AddRange(Directory.EnumerateFiles(root, "*.lnk",
                SearchOption.AllDirectories)); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return candidates.OrderBy(path => path.Length)
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                .Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

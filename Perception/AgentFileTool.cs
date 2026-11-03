using System.Diagnostics;
using System.Text;

namespace LingDongDao.Perception;

public sealed record AgentFileRequest(string Path, string? Content = null,
    string? App = null);

// Create-only file operations for tasks the user explicitly asked the desktop agent to perform.
// Existing files are never overwritten; the service tracks which paths this run created.
public static class AgentFileTool
{
    private static readonly string[] TextExtensions =
        [".html", ".htm", ".css", ".js", ".json", ".txt", ".md"];

    public static string WorkspaceHints()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return $"桌面：{desktop}；文档：{documents}；下载：{downloads}";
    }

    public static bool TryNormalize(string? requested, out string fullPath, out string reason)
    {
        fullPath = "";
        reason = "路径无效";
        if (string.IsNullOrWhiteSpace(requested) || requested.Length > 500 ||
            requested.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(requested)) return false;
        try { fullPath = Path.GetFullPath(requested.Trim()); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
            PathTooLongException) { return false; }
        if (fullPath.Length > 500 || Path.GetFileName(fullPath).Length == 0 ||
            fullPath.Split(Path.DirectorySeparatorChar).Any(part =>
                part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".ssh", StringComparison.OrdinalIgnoreCase)))
            return false;
        string temp = Path.GetFullPath(Path.GetTempPath());
        string[] protectedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ];
        string normalizedPath = fullPath;
        if (!IsUnder(normalizedPath, temp) && protectedRoots.Where(root => root.Length > 0)
            .Any(root => IsUnder(normalizedPath, root)))
        { reason = "不能在系统或应用配置目录中创建文件"; return false; }
        reason = "";
        return true;
    }

    public static bool TryCreateFolder(string path, out string fullPath, out string reason)
    {
        if (!TryNormalize(path, out fullPath, out reason)) return false;
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        { reason = "目标已存在；未修改现有内容"; return false; }
        try
        {
            Directory.CreateDirectory(fullPath);
            reason = $"文件夹已创建：{fullPath}";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        { reason = "创建文件夹失败：" + exception.GetType().Name; return false; }
    }

    public static bool TryCreateTextFile(string path, string content,
        out string fullPath, out string reason)
    {
        if (!TryNormalize(path, out fullPath, out reason)) return false;
        if (!TextExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
        { reason = "只支持创建 HTML、CSS、JS、JSON、TXT、Markdown 文本文件"; return false; }
        if (content.Length is < 1 or > 20000 || content.Any(character =>
            char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        { reason = "文本长度或控制字符无效"; return false; }
        if (PrivacyFilter.ContainsSecret(content))
        { reason = "内容疑似包含密钥，未写入"; return false; }
        if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
        { reason = "父文件夹不存在；请先创建文件夹"; return false; }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        { reason = "目标已存在；未覆盖现有文件"; return false; }
        try
        {
            using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
            writer.Flush();
            reason = $"文件已创建：{fullPath}，{stream.Length} 字节";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        { reason = "创建文件失败：" + exception.GetType().Name; return false; }
    }

    public static bool TryOpenCreated(string path, string app,
        IReadOnlySet<string> created, out string reason)
    {
        if (!TryNormalize(path, out string fullPath, out reason)) return false;
        if (!created.Contains(fullPath))
        { reason = "本轮 Agent 未创建此路径，不会直接打开"; return false; }
        bool isFile = File.Exists(fullPath), isDirectory = Directory.Exists(fullPath);
        if (!isFile && !isDirectory)
        { reason = "路径已不存在"; return false; }
        string? executable = app switch
        {
            "vscode" when isFile => DesktopAppTool.FindCodeExecutable(),
            "chrome" or "edge" when isFile &&
                Path.GetExtension(fullPath).ToLowerInvariant() is ".html" or ".htm"
                => AgentBrowserTool.FindExecutable(app),
            "explorer" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            _ => null
        };
        if (executable is null || !File.Exists(executable))
        { reason = "未找到目标程序，或文件类型不受支持"; return false; }
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            if (app is "chrome" or "edge") start.ArgumentList.Add("--new-window");
            if (app == "explorer" && isFile) start.ArgumentList.Add("/select," + fullPath);
            else start.ArgumentList.Add(fullPath);
            using Process? process = Process.Start(start);
            if (process is null) { reason = "程序没有启动"; return false; }
            reason = $"已用 {app} 打开：{fullPath}；等待新截图核对";
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        { reason = "打开文件失败：" + exception.GetType().Name; return false; }
    }

    private static bool IsUnder(string path, string root)
    {
        string normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalized + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

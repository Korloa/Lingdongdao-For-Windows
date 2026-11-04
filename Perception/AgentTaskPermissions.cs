using System.Text.RegularExpressions;

namespace LingDongDao.Perception;

public static partial class AgentTaskPermissions
{
    [GeneratedRegex(@"回复|发送|发给|发消息|回消息|告诉|给.{1,30}说|reply|send|message",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitMessageRequest();

    [GeneratedRegex(@"发送|回复|^send$|^reply$", RegexOptions.IgnoreCase)]
    private static partial Regex SendControl();

    [GeneratedRegex(@"(?:创建|新建|写入|编写|保存|生成|制作|做一个|create|write|save|build).{0,28}(?:文件|文件夹|目录|网页|网站|页面|代码|folder|file|web ?page|code|[\p{L}\p{N}_-]+\.html?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitFileCreation();

    [GeneratedRegex(@"(?:不要|不需要|无需|禁止|别)[^，,。；;\r\n]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex NegativeClause();

    private static string PositiveGoal(string goal) => NegativeClause().Replace(goal, "");

    public static bool MayCreateFiles(string goal) =>
        ExplicitFileCreation().IsMatch(PositiveGoal(goal));

    [GeneratedRegex(@"(?:用|在|通过).{0,12}(?:浏览器|chrome|edge|browser)|(?:浏览器|chrome|edge|browser).{0,12}(?:打开|预览|显示|open|preview)",
        RegexOptions.IgnoreCase)]
    private static partial Regex BrowserPreview();

    public static bool RequiresBrowserPreview(string goal) =>
        MayCreateFiles(goal) && BrowserPreview().IsMatch(PositiveGoal(goal)) &&
        PositiveGoal(goal).Contains("html", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"VS\s*Code|Visual Studio Code", RegexOptions.IgnoreCase)]
    private static partial Regex VsCodeRequest();

    [GeneratedRegex(@"资源管理器|文件管理器|file explorer|explorer", RegexOptions.IgnoreCase)]
    private static partial Regex ExplorerRequest();

    public static bool RequiresVsCodeFile(string goal) =>
        MayCreateFiles(goal) && VsCodeRequest().IsMatch(PositiveGoal(goal));

    public static bool RequiresExplorerFile(string goal) =>
        MayCreateFiles(goal) && ExplorerRequest().IsMatch(PositiveGoal(goal));

    [GeneratedRegex(@"可视化|动画(?:展示|演示)|(?:展示|演示).{0,30}动画|visualiz|animated? demo",
        RegexOptions.IgnoreCase)]
    private static partial Regex VisualizationRequest();

    public static bool RequiresVisualization(string goal) =>
        VisualizationRequest().IsMatch(goal);

    [GeneratedRegex(@"(?<![\p{L}\p{N}_-])[\p{L}\p{N}_-]+\.html?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamedHtmlFile();

    public static IReadOnlySet<string> RequestedHtmlFileNames(string goal) =>
        NamedHtmlFile().Matches(goal).Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool MaySendMessage(string goal) => ExplicitMessageRequest().IsMatch(goal);

    public static bool IsMessagingApp(WindowInfo window)
    {
        string name = window.ProcessName;
        return name.Contains("WeChat", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Weixin", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("QQ", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WXWork", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Telegram", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Discord", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSendControl(AgentLoopFrame frame, AgentToolDecision action)
    {
        if (action.Kind is not (AgentToolKind.Click or AgentToolKind.DoubleClick)) return false;
        return frame.Package.Gui.Any(item => item.Bounds.Contains(action.X, action.Y) &&
            SendControl().IsMatch(item.Name)) ||
            frame.Package.Ocr.Any(item => item.Bounds.Contains(action.X, action.Y) &&
                SendControl().IsMatch(item.Text));
    }
}

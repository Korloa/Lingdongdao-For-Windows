using System.Text.RegularExpressions;

namespace LingDongDao.Perception;

public static partial class AgentTaskPermissions
{
    [GeneratedRegex(@"回复|发送|发给|发消息|回消息|告诉|给.{1,30}说|reply|send|message",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitMessageRequest();

    [GeneratedRegex(@"发送|回复|^send$|^reply$", RegexOptions.IgnoreCase)]
    private static partial Regex SendControl();

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

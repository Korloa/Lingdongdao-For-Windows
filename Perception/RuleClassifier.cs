using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LingDongDao.Perception;

// Rule scores are scheduling heuristics, not trained or calibrated probabilities.
public sealed class RuleClassifier : ILocalClassifier
{
    private static readonly Regex Error = new(
        @"\b(error|exception|traceback|failed|fatal|cannot find|not found)\b|错误|异常|失败|找不到|未找到",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Terminal = new(
        @"terminal|powershell|cmd(?:\.exe)?|console|终端|命令提示符",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LocalAssessment Classify(DesktopObservation observation, StateMemory memory)
    {
        string app = observation.Window?.ProcessName ?? "unknown";
        string title = observation.Window?.Title ?? "";
        string activity = Activity(app, title);
        OcrText? error = observation.Ocr.Where(item => item.Confidence >= 0.55)
            .FirstOrDefault(item => Error.IsMatch(item.Text));
        bool terminalContext = Terminal.IsMatch(app + " " + title) ||
            error is not null && observation.GuiElements.Any(item =>
                Terminal.IsMatch(item.Name) && item.Bounds.Intersects(error.Bounds) &&
                item.Type is "Pane" or "Group" or "TabItem");
        bool recentInteraction = observation.InteractionEvents.Any(item =>
            item.Kind is "mouse_click" or "keyboard_activity");
        bool visualChange = !observation.Change.Reasons.Contains("first_frame") &&
            (observation.Change.GlobalDifference >= 0.015 ||
             observation.Change.Regions.Count > 0);

        if (error is not null && terminalContext && (visualChange || recentInteraction))
        {
            string normalized = Regex.Replace(error.Text.ToLowerInvariant(), @"\d+", "#").Trim();
            string key = Hash(app + "|" + normalized);
            int repeats = memory.RecentCount(key, observation.Time, TimeSpan.FromMinutes(3));
            double importance = repeats > 0 ? 0.88 : 0.82;
            double intervention = repeats > 0 ? 0.86 : recentInteraction ? 0.83 : 0.80;
            if (memory.RecentlyHelped(key, observation.Time, TimeSpan.FromMinutes(15)))
                intervention = 0.1;
            return new LocalAssessment(activity, "terminal_error", error.Text,
                importance, intervention, Math.Min(0.95, error.Confidence),
                $"OCR text in terminal context; change={visualChange}; interaction={recentInteraction}; recent_count={repeats}",
                key);
        }
        CandidateEvent? clicked = observation.Candidates.FirstOrDefault(item => item.Type == "control_click");
        if (clicked is not null)
            return new LocalAssessment(activity, "control_click", clicked.Summary,
                0.23, 0.08, 0.8, clicked.Evidence, Hash(clicked.Summary));
        if (observation.Change.Reasons.Contains("window_switch"))
            return new LocalAssessment(activity, "window_switch", $"切换到 {app}",
                0.25, 0.08, 1, "Windows foreground window changed", Hash(app));
        return new LocalAssessment(activity, "none", "无重要事件", 0.05, 0.02,
            observation.MissingModules.Contains("ocr") ? 0.35 : 0.7,
            "No supported event evidence", "none");
    }

    private static string Activity(string app, string title)
    {
        string value = (app + " " + title).ToLowerInvariant();
        if (value.Contains("code") || value.Contains("visual studio") || value.Contains("rider")) return "coding";
        if (value.Contains("chrome") || value.Contains("edge") || value.Contains("firefox")) return "browsing";
        if (value.Contains("word") || value.Contains("pdf")) return "reading_or_writing";
        if (value.Contains("powershell") || value.Contains("cmd")) return "terminal";
        return "other";
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
}

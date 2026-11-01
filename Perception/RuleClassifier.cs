using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LingDongDao.Perception;

// Scores are explainable scheduling heuristics, not calibrated probabilities.
public sealed class RuleClassifier : ILocalClassifier
{
    private static readonly Regex Error = new(
        @"\berror\s*[:：]|\bexception\b|\btraceback\b|\bfatal\b|\bbuild failed\b|\btests? failed\b|\bmodule not found\b|\bcannot find\b|\bfile not found\b|\bfailed to\b|错误|异常|失败|找不到|未找到|无法访问此网站",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Terminal = new(
        @"terminal|powershell|cmd(?:\.exe)?|console|终端|命令提示符",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BrowserProblem = new(
        @"\b(?:404|500|502|503|this site can.t be reached|connection refused|access denied)\b|无法访问此网站|无法连接|网页无法打开",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DialogTitle = new(
        @"\b(?:error|warning|failure)\b|错误|异常|失败|警告",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> DialogTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Window", "Dialog", "Alert" };

    public LocalAssessment Classify(DesktopObservation observation, StateMemory memory)
    {
        string app = observation.Window?.ProcessName ?? "unknown";
        string title = observation.Window?.Title ?? "";
        string activity = Activity(app, title);
        bool initial = observation.Change.Reasons.Contains("first_frame");
        bool visualChange = !initial &&
            (observation.Change.Reasons.Contains("local_change") ||
             observation.Change.Reasons.Contains("global_change") ||
             observation.Change.Reasons.Contains("window_switch"));
        bool periodic = observation.Change.Reasons.Contains("periodic");
        bool clicked = observation.InteractionEvents.Any(item => item.Kind == "mouse_click");
        bool typing = observation.InteractionEvents.Any(item => item.Kind == "keyboard_activity");
        OcrText? error = observation.Ocr.Where(item => item.Confidence >= 0.6)
            .FirstOrDefault(item => Error.IsMatch(item.Text) ||
                activity == "browsing" && BrowserProblem.IsMatch(item.Text));
        bool terminalContext = Terminal.IsMatch(app + " " + title) ||
            error is not null && observation.GuiElements.Any(item =>
                Terminal.IsMatch(item.Name) && item.Bounds.Intersects(error.Bounds) &&
                item.Type is "Pane" or "Group" or "TabItem");
        bool dialogContext = error is not null && observation.GuiElements.Any(item =>
            DialogTypes.Contains(item.Type) && Error.IsMatch(item.Name) &&
            item.Bounds.Intersects(error.Bounds));

        GuiElement? dialog = observation.GuiElements.FirstOrDefault(item =>
            !item.IsPassword && DialogTypes.Contains(item.Type) &&
            DialogTitle.IsMatch(item.Name) &&
            (item.Type != "Window" || observation.Window is not { } foreground ||
             item.Bounds.Width < foreground.Bounds.Width * 0.85 &&
             item.Bounds.Height < foreground.Bounds.Height * 0.85));
        if (error is null && dialog is not null && !initial && (visualChange || clicked))
        {
            string key = Hash(app + "|dialog|" + dialog.Name.ToLowerInvariant());
            int repeats = memory.RecentCount(key, observation.Time, TimeSpan.FromMinutes(3));
            double intervention = repeats > 0 ? 0.88 : 0.82;
            if (memory.RecentlyHelped(key, observation.Time, TimeSpan.FromMinutes(15)))
                intervention = 0.08;
            return new LocalAssessment(activity, "dialog_error", dialog.Name,
                0.86, intervention, 0.9,
                $"uia_dialog={dialog.Type}; changed={visualChange}; previous={repeats}", key);
        }

        if (error is not null && !initial && (visualChange || clicked || periodic))
        {
            string normalized = Regex.Replace(error.Text.ToLowerInvariant(), @"\d+", "#").Trim();
            string key = Hash(app + "|error|" + normalized);
            int repeats = memory.RecentCount(key, observation.Time, TimeSpan.FromMinutes(3));
            TimeSpan age = memory.EpisodeAge(key, observation.Time);
            string kind = terminalContext ? "terminal_error" : dialogContext ? "dialog_error" :
                activity == "browsing" && BrowserProblem.IsMatch(error.Text) ? "browser_error" :
                activity == "coding" ? "application_error" : "possible_error";
            double baseScore = kind switch
            {
                "terminal_error" => 0.86,
                "dialog_error" => 0.83,
                "browser_error" => 0.70,
                "application_error" => 0.67,
                _ => 0.42
            };
            double repeatBonus = repeats >= 4 ? 0.20 : repeats >= 2 ? 0.15 :
                repeats >= 1 ? 0.07 : 0;
            double durationBonus = age >= TimeSpan.FromSeconds(20) ? 0.07 : 0;
            double typingPenalty = typing ? kind == "application_error" ? 0.23 : 0.03 : 0;
            double intervention = Math.Clamp(baseScore + repeatBonus + durationBonus - typingPenalty, 0, 0.96);
            if (memory.RecentlyHelped(key, observation.Time, TimeSpan.FromMinutes(15)))
                intervention = 0.08;
            double importance = Math.Clamp(baseScore + repeatBonus * 0.5, 0, 0.95);
            double confidence = Math.Clamp(error.Confidence * (terminalContext || dialogContext ? 1 : 0.82), 0, 0.95);
            return new LocalAssessment(activity, kind, error.Text, importance,
                intervention, confidence,
                $"ocr={error.Confidence:0.00}; context={kind}; changed={visualChange}; " +
                $"click={clicked}; typing={typing}; previous={repeats}; age_s={age.TotalSeconds:0}", key);
        }

        CandidateEvent? control = observation.Candidates.FirstOrDefault(item => item.Type == "control_click");
        if (control is not null)
        {
            string key = Hash("click|" + app + "|" + control.Summary);
            int repeats = memory.RecentCount(key, observation.Time, TimeSpan.FromMinutes(3));
            double score = repeats >= 4 ? 0.82 : repeats >= 2 ? 0.64 :
                repeats >= 1 ? 0.42 : 0.18;
            if (memory.RecentlyHelped(key, observation.Time, TimeSpan.FromMinutes(15)))
                score = 0.08;
            return new LocalAssessment(activity, repeats >= 2 ? "repeated_action" : "control_click",
                control.Summary, Math.Clamp(score + 0.08, 0, 0.9), score, 0.8,
                control.Evidence + $"; previous_clicks={repeats}", key);
        }
        if (observation.Change.Reasons.Contains("window_switch"))
            return new LocalAssessment(activity, "window_switch", $"切换到 {app}",
                0.22, 0.10, 1, "Windows foreground window changed", Hash("switch|" + app));
        return new LocalAssessment(activity, "none", "无值得介入的事件", 0.06, 0.04,
            observation.MissingModules.Contains("ocr") ? 0.35 : 0.7,
            "No supported problem evidence", "none");
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

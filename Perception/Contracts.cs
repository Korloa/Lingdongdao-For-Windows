namespace LingDongDao.Perception;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
    public bool Intersects(PixelRect other) => X < other.X + other.Width && other.X < X + Width &&
        Y < other.Y + other.Height && other.Y < Y + Height;
    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        int right = Math.Min(X + Width, other.X + other.Width);
        int bottom = Math.Min(Y + Height, other.Y + other.Height);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

public sealed record DisplayInfo(string Id, PixelRect Bounds, bool Primary);
public sealed record WindowInfo(long Handle, int ProcessId, string ProcessName, string Title,
    PixelRect Bounds, bool Visible);
public sealed record ActivityEvent(DateTimeOffset Time, string Kind, int X, int Y);
public sealed record ChangeRegion(PixelRect Bounds, double MeanDifference);
public sealed record ScreenChange(double GlobalDifference, IReadOnlyList<ChangeRegion> Regions,
    IReadOnlyList<string> Reasons, bool ShouldAnalyze);
public sealed record GuiElement(string Name, string Type, PixelRect Bounds, bool IsEnabled,
    bool IsPassword, string Source, double? Confidence);
public sealed record OcrText(string Text, PixelRect Bounds, double Confidence, DateTimeOffset Time,
    string? GuiElementName);
public sealed record CandidateEvent(string Type, string Summary, string Evidence, bool IsInference);

public sealed record DesktopObservation(
    DateTimeOffset Time,
    Guid ScreenshotId,
    DisplayInfo Display,
    WindowInfo? Window,
    PixelRect AnalysisBounds,
    IReadOnlyList<GuiElement> GuiElements,
    IReadOnlyList<OcrText> Ocr,
    IReadOnlyList<ActivityEvent> InteractionEvents,
    (int X, int Y) Mouse,
    ScreenChange Change,
    IReadOnlyList<CandidateEvent> Candidates,
    IReadOnlyList<string> MissingModules,
    IReadOnlyDictionary<string, double> ModuleConfidence);

public sealed record LocalAssessment(string Activity, string EventType, string EventSummary,
    double ImportanceScore, double InterventionScore, double Confidence,
    string Reason, string EventKey, bool IsCalibrated = false);

public sealed record CloudDecision(bool ShouldIntervene, string Reason, string Suggestion,
    string? RecommendedAction);

public sealed record PerceptionLog(DateTimeOffset Time, string Level, string Module,
    string Event, string Summary);

public enum ResourceMode { Low, Standard, Enhanced }

public sealed class PerceptionOptions
{
    public bool Enabled { get; set; }
    public bool Paused { get; set; }
    public bool CloudEnabled { get; set; }
    public bool UploadConsent { get; set; }
    public bool GuiDetectorEnabled { get; set; }
    public ResourceMode Mode { get; set; } = ResourceMode.Low;
    public double SampleSeconds { get; set; } = 2;
    public int FrameRetentionSeconds { get; set; } = 30;
    public int FrameMemoryLimitMb { get; set; } = 64;
    public double EscalationThreshold { get; set; } = 0.8;
    public int CloudCallsPerHour { get; set; } = 3;
    public double DailyCloudBudgetUsd { get; set; } = 0.25;
    public double EstimatedCostPerCallUsd { get; set; } = 0.02;
    public int CooldownSeconds { get; set; } = 300;
    public string[] SensitiveProcesses { get; set; } = ["KeePass", "Bitwarden", "1Password", "WindowsSecurityHealthUI"];
    public string[] SensitiveTitleWords { get; set; } = ["密码管理", "password vault", "银行账户"];

    public PerceptionOptions Clone()
    {
        var copy = (PerceptionOptions)MemberwiseClone();
        copy.SensitiveProcesses = (string[])SensitiveProcesses.Clone();
        copy.SensitiveTitleWords = (string[])SensitiveTitleWords.Clone();
        return copy;
    }

    public void Normalize()
    {
        SampleSeconds = Math.Clamp(SampleSeconds, 0.5, 30);
        FrameRetentionSeconds = Math.Clamp(FrameRetentionSeconds, 5, 180);
        FrameMemoryLimitMb = Math.Clamp(FrameMemoryLimitMb, 8, 512);
        EscalationThreshold = Math.Clamp(EscalationThreshold, 0.1, 1);
        CloudCallsPerHour = Math.Clamp(CloudCallsPerHour, 0, 60);
        DailyCloudBudgetUsd = Math.Clamp(DailyCloudBudgetUsd, 0, 100);
        EstimatedCostPerCallUsd = Math.Clamp(EstimatedCostPerCallUsd, 0.001, 10);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 86400);
    }
}

public interface IOcrEngine
{
    bool Available { get; }
    Task<IReadOnlyList<OcrText>> RecognizeAsync(byte[] image, PixelRect screenBounds,
        DateTimeOffset time, CancellationToken cancellationToken);
}

public interface IGuiDetector
{
    bool Available { get; }
    Task<IReadOnlyList<GuiElement>> DetectAsync(byte[] image, PixelRect screenBounds,
        CancellationToken cancellationToken);
}

public interface ILocalClassifier
{
    LocalAssessment Classify(DesktopObservation observation, StateMemory memory);
}

public interface ICloudAgent
{
    Task<CloudDecision?> DecideAsync(CloudEventPackage package, CancellationToken cancellationToken);
}

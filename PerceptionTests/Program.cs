using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Reflection;
using LingDongDao.Perception;
using ActivityEvent = LingDongDao.Perception.ActivityEvent;
using System.Windows.Threading;

static void Check(bool condition, string caseName)
{
    if (!condition) throw new Exception("FAIL: " + caseName);
    Console.WriteLine("PASS " + caseName);
}

static CapturedFrame Frame(DateTimeOffset time, WindowInfo window, string id = "test",
    Action<Graphics>? draw = null)
{
    using var bitmap = new Bitmap(800, 600);
    using (Graphics graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.White);
        draw?.Invoke(graphics);
    }
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Jpeg);
    return new CapturedFrame(Guid.NewGuid(), time,
        new DisplayInfo(id, new PixelRect(0, 0, 800, 600), true),
        window, stream.ToArray(), 800, 600);
}

static DesktopObservation Observation(CapturedFrame frame, IReadOnlyList<OcrText>? ocr = null,
    IReadOnlyList<GuiElement>? gui = null, IReadOnlyList<ActivityEvent>? events = null,
    IReadOnlyList<string>? reasons = null)
{
    var change = new ScreenChange(0.08, [new ChangeRegion(new PixelRect(20, 30, 300, 100), 0.1)],
        reasons ?? ["local_change"], true);
    return ObservationFusion.Fuse(frame, frame.Foreground!.Bounds, gui ?? [], ocr ?? [],
        events ?? [], (0, 0), change, []);
}

var now = DateTimeOffset.Now;
var browser = new WindowInfo(1, 100, "chrome", "A normal web page",
    new PixelRect(0, 0, 800, 600), true);
var vscode = new WindowInfo(2, 101, "Code", "project - Visual Studio Code",
    new PixelRect(0, 0, 800, 600), true);
var terminal = new WindowInfo(3, 102, "powershell", "Terminal - Visual Studio Code",
    new PixelRect(0, 0, 800, 600), true);
var classifier = new RuleClassifier();
var memory = new StateMemory();
var options = new PerceptionOptions { Enabled = true, CloudEnabled = true,
    UploadConsent = true, EscalationThreshold = 0.8, CooldownSeconds = 0 };

DesktopObservation browsing = Observation(Frame(now, browser));
LocalAssessment normal = classifier.Classify(browsing, memory);
Check(normal.EventType == "none" && normal.InterventionScore < 0.8 &&
    !new CloudEscalationGate().TryReserve(browsing, normal, options, now, out _),
    "1 normal browsing does not escalate");

var detector = new ChangeDetector();
CapturedFrame chromeFrame = Frame(now, browser);
CapturedFrame codeFrame = Frame(now.AddSeconds(2), vscode);
detector.Analyze(chromeFrame, [], false);
ScreenChange switched = detector.Analyze(codeFrame, [], false);
Check(switched.Reasons.Contains("window_switch"), "2 Chrome to VS Code switch");

var lineDetector = new ChangeDetector();
lineDetector.Analyze(Frame(now, terminal), [], false);
ScreenChange oneLine = lineDetector.Analyze(Frame(now.AddSeconds(2), terminal, draw: graphics =>
    graphics.FillRectangle(Brushes.Black, 40, 400, 300, 12)), [], false);
Check(oneLine.ShouldAnalyze && oneLine.Reasons.Contains("local_change") &&
    oneLine.GlobalDifference < 0.025, "2b one changed line triggers local analysis");

var errorText = new OcrText("Error: module not found", new PixelRect(40, 70, 260, 30),
    0.93, now, null);
DesktopObservation editorFirstFrame = Observation(Frame(now, vscode), [errorText],
    reasons: ["first_frame"]);
Check(classifier.Classify(editorFirstFrame, memory).EventType != "terminal_error",
    "2c VS Code initial frame is not a terminal error");
DesktopObservation errorObservation = Observation(Frame(now, terminal), [errorText], [],
    [new ActivityEvent(now, "keyboard_activity", -1, -1)]);
LocalAssessment firstError = classifier.Classify(errorObservation, memory);
Check(firstError.EventType == "terminal_error" && firstError.InterventionScore >= 0.8,
    "3 new terminal error is a candidate");

var editorError = Observation(Frame(now.AddSeconds(2), vscode), [errorText]);
LocalAssessment editorAssessment = classifier.Classify(editorError, new StateMemory());
Check(editorAssessment.EventType == "application_error" &&
    editorAssessment.InterventionScore >= 0.6,
    "3b changed editor error receives meaningful score");
var typingEditor = Observation(Frame(now.AddSeconds(2), vscode), [errorText], events:
    [new ActivityEvent(now, "keyboard_activity", -1, -1)]);
Check(classifier.Classify(typingEditor, new StateMemory()).InterventionScore < 0.5,
    "3c typing reduces uncertain editor interruption");
var browserError = Observation(Frame(now.AddSeconds(2), browser),
    [new OcrText("404 Not Found", new PixelRect(40, 70, 220, 30), 0.91, now, null)]);
Check(classifier.Classify(browserError, new StateMemory()).EventType == "browser_error" &&
    classifier.Classify(browserError, new StateMemory()).InterventionScore >= 0.65,
    "3d browser failure gets a useful candidate score");
var dialogError = Observation(Frame(now.AddSeconds(2), vscode), [errorText],
    [new GuiElement("Error: build failed", "Dialog", new PixelRect(10, 20, 500, 300),
        true, false, "uia", null)]);
Check(classifier.Classify(dialogError, new StateMemory()).EventType == "dialog_error",
    "3e UIA error dialog is recognized");
var uiaOnlyDialog = Observation(Frame(now.AddSeconds(2), vscode), gui:
    [new GuiElement("Error", "Dialog", new PixelRect(30, 40, 400, 220),
        true, false, "uia", null)]);
Check(classifier.Classify(uiaOnlyDialog, new StateMemory()).EventType == "dialog_error" &&
    classifier.Classify(uiaOnlyDialog, new StateMemory()).InterventionScore >= 0.8,
    "3e2 UIA error dialog works when OCR misses text");
var ordinaryErrorWord = Observation(Frame(now.AddSeconds(2), vscode),
    [new OcrText("let error = null", new PixelRect(40, 70, 220, 30), 0.95, now, null)]);
Check(classifier.Classify(ordinaryErrorWord, new StateMemory()).EventType == "none",
    "3f an error variable name is not a failure");
var numericCode = Observation(Frame(now.AddSeconds(2), vscode),
    [new OcrText("const port = 404;", new PixelRect(40, 70, 220, 30), 0.95, now, null)]);
Check(classifier.Classify(numericCode, new StateMemory()).EventType == "none",
    "3g HTTP status digits in source code are not a browser failure");

for (int index = 0; index < 10; index++)
    memory.Update(errorObservation with { Time = now.AddSeconds(index) }, firstError);
Check(memory.RecentCount(firstError.EventKey, now.AddSeconds(10), TimeSpan.FromMinutes(3)) == 10 &&
    memory.RecentSummaries(now.AddSeconds(10)).Count == 1,
    "4 repeated error is one episode with a count");

var scrollDetector = new ChangeDetector();
scrollDetector.Analyze(chromeFrame, [], false);
ScreenChange scroll = scrollDetector.Analyze(Frame(now.AddSeconds(2), browser),
    [new ActivityEvent(now.AddSeconds(2), "mouse_wheel", 300, 300)], false);
Check(!scroll.ShouldAnalyze, "5 wheel activity alone does not re-run OCR");

var button = new GuiElement("", "Button", new PixelRect(90, 120, 120, 42),
    true, false, "uia", null);
var buttonText = new OcrText("运行", new PixelRect(110, 130, 50, 22), 0.97, now, null);
DesktopObservation click = Observation(Frame(now, vscode), [buttonText], [button],
    [new ActivityEvent(now, "mouse_click", 130, 140)]);
Check(click.Candidates.Any(item => item.Type == "control_click" &&
    item.Summary.Contains("运行") && item.IsInference) &&
    click.Ocr[0].GuiElementName == "Button", "6 click bound to GUI and OCR text");
var clickMemory = new StateMemory();
LocalAssessment firstClick = classifier.Classify(click, clickMemory);
for (int index = 0; index < 5; index++)
    clickMemory.Update(click with { Time = now.AddSeconds(index) }, firstClick);
LocalAssessment repeatedClick = classifier.Classify(click with { Time = now.AddSeconds(6) }, clickMemory);
Check(firstClick.InterventionScore < 0.5 && repeatedClick.EventType == "repeated_action" &&
    repeatedClick.InterventionScore >= 0.8,
    "6b repeated GUI action raises score without flagging first click");
clickMemory.MarkHelped(repeatedClick.EventKey, now.AddSeconds(6));
Check(classifier.Classify(click with { Time = now.AddSeconds(7) }, clickMemory)
    .InterventionScore < 0.2, "6c recent help suppresses repeated action");

string credentialTestPath = Path.Combine(Path.GetTempPath(),
    "lingdongdao-dpapi-test-" + Guid.NewGuid().ToString("N"));
try
{
    AgentCredentialStore.Save("test-only-secret", credentialTestPath);
    Check(AgentCredentialStore.Load(credentialTestPath) == "test-only-secret" &&
        !Encoding.UTF8.GetString(File.ReadAllBytes(credentialTestPath)).Contains("test-only-secret"),
        "6d API key store encrypts for current Windows user");
    AgentCredentialStore.Save("replacement-only", credentialTestPath);
    Check(AgentCredentialStore.Load(credentialTestPath) == "replacement-only",
        "6e API key can be rotated without changing settings JSON");
}
finally { AgentCredentialStore.Delete(credentialTestPath); }

var frames = new FrameRingBuffer();
CapturedFrame before = Frame(now.AddSeconds(-3), terminal);
CapturedFrame current = Frame(now, terminal);
frames.Add(before, 30, 8);
frames.Add(current, 30, 8);
DesktopObservation packaged = Observation(current, [errorText]);
var gate = new CloudEscalationGate();
Check(gate.CanEscalate(packaged, firstError, options, now, out _) &&
    gate.CallsToday == 0, "7a dry-run gate does not spend budget");
Check(gate.TryReserve(packaged, firstError, options, now, out _),
    "7a2 local gate accepts a qualified event");
string ledgerTestPath = Path.Combine(Path.GetTempPath(),
    "lingdongdao-ledger-test-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var diskGate = new CloudEscalationGate(ledgerTestPath);
    Check(diskGate.TryReserve(packaged, firstError, options, now, out _),
        "7a3 disk gate records a cloud reservation");
    Check(diskGate.MarkDecision(now, firstError.EventKey, false),
        "7a3b a cloud decision is saved in the usage ledger");
    var restoredGate = new CloudEscalationGate(ledgerTestPath);
    var anotherEvent = firstError with { EventKey = "different-event" };
    var tinyBudget = options.Clone();
    tinyBudget.DailyCloudBudgetUsd = options.EstimatedCostPerCallUsd;
    Check(restoredGate.CallsToday == 1 &&
        !restoredGate.TryReserve(packaged, firstError, options, now.AddSeconds(1), out _) &&
        !restoredGate.TryReserve(packaged, anotherEvent, tinyBudget, now.AddSeconds(1), out _),
        "7a4 restart preserves duplicate and daily budget limits");
    Check(!restoredGate.CanEscalate(packaged, firstError, options,
            now.AddMinutes(20), out string declinedReason) &&
        declinedReason == "cloud_declined_recently",
        "7a4a cloud rejection suppresses repeat auto calls after restart");
    var manualEvent = firstError with { EventType = "manual_request", EventKey = "manual-test",
        InterventionScore = 1 };
    var hourlyLimited = options.Clone();
    hourlyLimited.CloudCallsPerHour = 1;
    Check(!restoredGate.CanEscalate(packaged, manualEvent, hourlyLimited,
            now.AddSeconds(2), out _) &&
        restoredGate.TryReserve(packaged, manualEvent, hourlyLimited,
            now.AddSeconds(2), out _, userInitiated: true) &&
        !restoredGate.TryReserve(packaged, manualEvent with { EventKey = "manual-next" },
            tinyBudget, now.AddSeconds(3), out _, userInitiated: true),
        "7a4b manual request bypasses automatic rate limits but keeps daily budget");
    File.WriteAllText(ledgerTestPath, "invalid ledger");
    Check(!new CloudEscalationGate(ledgerTestPath).CanEscalate(
        packaged, anotherEvent, options, now.AddSeconds(2), out string ledgerReason) &&
        ledgerReason == "ledger_unavailable", "7a5 damaged budget ledger blocks cloud calls");
}
finally { if (File.Exists(ledgerTestPath)) File.Delete(ledgerTestPath); }
CloudEventPackage? cloudPackage = PrivacyFilter.Build(packaged, firstError, frames, memory, options, []);
var mock = new MockCloudAgent();
if (cloudPackage is not null) await mock.DecideAsync(cloudPackage, CancellationToken.None);
Check(cloudPackage is { CurrentJpeg.Length: > 0, BeforeJpeg.Length: > 0 } &&
    mock.CallCount == 1 && cloudPackage.Ocr.Count > 0,
    "7b cloud package retains screenshots and context");
string historyTestFolder = Path.Combine(Path.GetTempPath(),
    "lingdongdao-history-test-" + Guid.NewGuid().ToString("N"));
try
{
    var history = new CloudCallHistory(historyTestFolder);
    var call = new CloudCallRecord(Guid.NewGuid().ToString("N"), now, true,
        "test-app", "test-window", "manual_request", "manual-test", 1, "pending", null,
        "", "", null, "", "", cloudPackage!.CurrentJpeg, null);
    history.Save(call);
    history.Save(call with { Status = "success", ShouldIntervene = false,
        Reason = "synthetic-model-reply", RawResponse = "{\"reason\":\"synthetic-model-reply\"}" });
    CloudCallRecord restored = history.List()[0];
    string encryptedPath = Path.Combine(historyTestFolder, call.Id + ".bin");
    Check(restored.CurrentJpeg!.SequenceEqual(cloudPackage.CurrentJpeg) &&
        restored.Reason == "synthetic-model-reply" &&
        !Encoding.UTF8.GetString(File.ReadAllBytes(encryptedPath)).Contains("synthetic-model-reply") &&
        history.List([new CloudUsageRecord(now.AddMinutes(-1), "old", 0.02)]).Count == 2 &&
        history.List([new CloudUsageRecord(now.AddSeconds(-1), "manual-test", 0.02)]).Count == 1,
        "7b1 encrypted call history keeps screenshot and full reply; legacy calls remain visible");
    history.Clear();
    Check(history.List([new CloudUsageRecord(now.AddMinutes(-1), "old", 0.02)]).Count == 0 &&
        !File.Exists(encryptedPath), "7b1b clearing history removes encrypted screenshots");
}
finally
{
    if (Directory.Exists(historyTestFolder))
    {
        foreach (string path in Directory.GetFiles(historyTestFolder)) File.Delete(path);
        Directory.Delete(historyTestFolder);
    }
}
Check(PrivacyFilter.Build(packaged, firstError, frames, memory, options)?.BeforeJpeg is null &&
    PrivacyFilter.Build(packaged, firstError, frames, memory, options,
        [new OcrText("token=abcdefghijklmnop", new PixelRect(10, 10, 120, 20),
            0.9, now, null)])?.BeforeJpeg is null,
    "7b2 prior screenshot needs a separate privacy scan");
var handler = new MockApiHandler();
using (var cloudClient = new ConfigurableCloudAgent(handler,
           "https://mock.invalid/v1/chat/completions", "mock-vision", "test-only"))
{
    CloudDecision? decision = await cloudClient.DecideAsync(cloudPackage!, CancellationToken.None);
    Check(decision is { ShouldIntervene: true, Suggestion: "Check the terminal error" } &&
        handler.SawImage && handler.SawContext && handler.SawBearer,
        "7c mock HTTP API receives image and structured context");
}

var noConsent = options.Clone();
noConsent.UploadConsent = false;
Check(!new CloudEscalationGate().TryReserve(packaged, firstError, noConsent, now, out _) &&
    PrivacyFilter.Build(packaged, firstError, frames, memory, noConsent) is null,
    "8a upload without consent blocked");
var sensitive = packaged with { Window = terminal with { ProcessName = "KeePass" } };
Check(!new CloudEscalationGate().TryReserve(sensitive, firstError, options, now, out _) &&
    PrivacyFilter.Build(sensitive, firstError, frames, memory, options) is null,
    "8b sensitive window blocked");
var noBudget = options.Clone();
noBudget.DailyCloudBudgetUsd = 0.001;
Check(!new CloudEscalationGate().TryReserve(packaged, firstError, noBudget, now, out _),
    "8c daily budget blocks call");
var secret = packaged with { Ocr = [new OcrText("api_key=sk-12345678901234567890",
    new PixelRect(50, 50, 120, 20), 0.9, now, null)] };
Check(PrivacyFilter.Build(secret, firstError, frames, memory, options) is null,
    "8d detected key blocks upload");
var noUia = packaged with { MissingModules = ["uia"] };
Check(PrivacyFilter.Build(noUia, firstError, frames, memory, options) is null,
    "8e unknown password regions block upload");
var passwordGui = packaged with { GuiElements = [new GuiElement("secret", "Edit",
    new PixelRect(20, 20, 100, 30), true, true, "uia", null)] };
Check(PrivacyFilter.Build(passwordGui, firstError, frames, memory, options)?.BeforeJpeg is null,
    "8f previous screenshot omitted when password field exists");

var agentFrame = new AgentLoopFrame(terminal, cloudPackage!.ScreenshotBounds,
    cloudPackage.CurrentJpeg, cloudPackage, packaged);
var clickAction = new AgentToolDecision("call-1", AgentToolKind.Click, 120, 140,
    "测试点击", false, "", "{}");
Check(AgentMouseTool.IsAllowed(agentFrame, clickAction, terminal, out _) &&
    !AgentMouseTool.IsAllowed(agentFrame, clickAction with { X = -100 }, terminal, out _) &&
    !AgentMouseTool.IsAllowed(agentFrame, clickAction,
        terminal with { Handle = terminal.Handle + 1 }, out _),
    "8g mouse tool rejects coordinates outside target and stale foreground windows");
var passwordAgentFrame = agentFrame with { Package = cloudPackage with { Gui =
    [new GuiElement("[password]", "Edit", new PixelRect(110, 130, 40, 30),
        true, true, "uia", null)] } };
Check(!AgentMouseTool.IsAllowed(passwordAgentFrame,
    clickAction with { Kind = AgentToolKind.RightClick }, terminal, out _),
    "8g1 right click cannot target a password control");
Type mouseInputType = typeof(AgentMouseTool).GetNestedType("Input",
    BindingFlags.NonPublic)!;
Check(System.Runtime.InteropServices.Marshal.SizeOf(mouseInputType) ==
    (IntPtr.Size == 8 ? 40 : 28), "8g2 SendInput structure matches Windows layout");
var agentEnvironment = new FakeAgentEnvironment(agentFrame, finishAfterActions: 1);
AgentLoopOutcome agentOutcome = await new AgentLoopEngine().RunAsync(agentEnvironment,
    4, null, CancellationToken.None);
Check(agentOutcome.Success && agentOutcome.Actions == 1 &&
    agentOutcome.CloudCalls == 2 && agentEnvironment.Captures == 2,
    "8h agent executes one tool, returns a new screenshot, then verifies success");
AgentLoopOutcome limitedOutcome = await new AgentLoopEngine().RunAsync(
    new FakeAgentEnvironment(agentFrame, finishAfterActions: 99), 2, null,
    CancellationToken.None);
Check(!limitedOutcome.Success && limitedOutcome.Actions == 2 &&
    limitedOutcome.CloudCalls == 3 && limitedOutcome.Status.Contains("上限"),
    "8i agent stops at the operation limit without an infinite loop");
using (var disabledAgent = new PerceptionService(() => new PerceptionOptions()))
    Check(!disabledAgent.TryStartAgent("执行一个测试任务", out _),
        "8i1 agent cannot start without cloud screenshot consent");
var agentHandler = new MockAgentToolHandler();
using (var agentClient = new ConfigurableCloudAgent(agentHandler,
           "https://mock.invalid/v1/chat/completions", "mock-vision", "test-only"))
{
    var conversation = new AgentConversation();
    AgentToolDecision firstTool = await agentClient.NextAgentToolAsync(conversation,
        agentFrame, "点击测试按钮", [], CancellationToken.None);
    var feedback = new AgentActionFeedback(firstTool, true, "已点击；等待截图验证");
    conversation.AddToolResult(firstTool, feedback);
    AgentToolDecision secondTool = await agentClient.NextAgentToolAsync(conversation,
        agentFrame, "点击测试按钮", [feedback], CancellationToken.None);
    Check(firstTool.Kind == AgentToolKind.Click && firstTool.X == 120 &&
        secondTool.Kind == AgentToolKind.Finish && secondTool.Success &&
        agentHandler.SawToolDefinition && agentHandler.SawSecondScreenshotAndToolResult,
        "8j cloud tool protocol sends action result plus new screenshot before finish");
}

try { throw new HttpRequestException("mock cloud unavailable"); }
catch (HttpRequestException)
{
    LocalAssessment afterFailure = classifier.Classify(errorObservation, memory);
    Check(afterFailure.EventType == "terminal_error", "9 cloud outage leaves local classification working");
}

using Process process = Process.GetCurrentProcess();
Console.WriteLine($"PERFORMANCE tests_process_mb={process.WorkingSet64 / 1048576.0:0.0} " +
                  $"cpu_seconds={process.TotalProcessorTime.TotalSeconds:0.00} frames_mb={frames.Bytes / 1048576.0:0.00}");

if (args.Contains("--deepseek-smoke"))
{
    using var deepSeek = new ConfigurableCloudAgent();
    Check(deepSeek.Available && deepSeek.Model == "deepseek-flash",
        "10 DeepSeek V4.1 Flash credential and model configured");
    CloudDecision? realDecision = await deepSeek.DecideAsync(cloudPackage!, CancellationToken.None);
    Check(realDecision is { Reason.Length: > 0 },
        "11 real DeepSeek multimodal request returns structured JSON");
    Console.WriteLine($"DEEPSEEK_SMOKE model={deepSeek.Model} " +
        $"intervene={realDecision!.ShouldIntervene} suggestion_length={realDecision.Suggestion.Length}");
}

if (args.Contains("--agent-tool-smoke"))
{
    using var toolClient = new ConfigurableCloudAgent();
    AgentToolDecision tool = await toolClient.NextAgentToolAsync(new AgentConversation(),
        agentFrame, "这是合成测试截图。不要点击，调用 finish 表示测试完成。", [], CancellationToken.None);
    Check(!string.IsNullOrWhiteSpace(tool.CallId) &&
        Enum.IsDefined(tool.Kind), "11b real DeepSeek vision tool call parses safely");
    Console.WriteLine($"AGENT_TOOL_SMOKE kind={tool.Kind} label_length={tool.Label.Length}");
}

if (args.Contains("--settings-smoke"))
{
    Exception? uiFailure = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            _ = new System.Windows.Application
            { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            Assembly assembly = typeof(LingDongDao.MainWindow).Assembly;
            Type settingsType = assembly.GetType("LingDongDao.AppSettings", true)!;
            Type panelType = assembly.GetType("LingDongDao.SettingsWindow", true)!;
            object settings = Activator.CreateInstance(settingsType, nonPublic: true)!;
            using var service = new PerceptionService(() => new PerceptionOptions());
            var constructor = panelType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(item => item.GetParameters().Length == 5);
            var panel = (System.Windows.Window)constructor.Invoke(
                [settings, null, null, null, service]);
            panelType.GetMethod("RefreshPerceptionStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, null);
            var status = (System.Windows.Controls.TextBlock)panel.FindName("CloudCredentialStatusText")!;
            Check(panel.FindName("DeepSeekKeyBox") is System.Windows.Controls.PasswordBox &&
                panel.FindName("PerceptionLogBox") is System.Windows.Controls.TextBox &&
                panel.FindName("DebugOverlayCheck") is System.Windows.Controls.CheckBox &&
                panel.FindName("WeatherCurrentText") is System.Windows.Controls.TextBlock &&
                panel.FindName("ManualAnalysisHotkeyBox") is System.Windows.Controls.TextBox &&
                panel.FindName("AgentGoalBox") is System.Windows.Controls.TextBox &&
                panel.FindName("AgentStartHotkeyBox") is System.Windows.Controls.TextBox &&
                status.Text.Contains("deepseek-flash"),
                "12 WPF settings loads DeepSeek key controls and cloud status");
            panelType.GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, ["PerceptionPage"]);
            var perceptionPage = (System.Windows.FrameworkElement)panel.FindName("PerceptionPage")!;
            var modePage = (System.Windows.FrameworkElement)panel.FindName("ModePage")!;
            Check(perceptionPage.Visibility == System.Windows.Visibility.Visible &&
                modePage.Visibility == System.Windows.Visibility.Collapsed &&
                ((System.Windows.Controls.TextBlock)panel.FindName("PageTitleText")!).Text == "桌面感知",
                "12b sidebar switches separate settings pages");
            Type weatherType = assembly.GetType("LingDongDao.CurrentWeather", true)!;
            string currentWeather = (string)weatherType.GetMethod("Format")!.Invoke(null,
                ["{\"current\":{\"time\":\"2026-11-01T20:00\",\"temperature_2m\":15.7,\"weather_code\":3,\"is_day\":0}}"] )!;
            Check(currentWeather.Contains("15.7°C") && currentWeather.Contains("阴") &&
                currentWeather.Contains("20:00"), "12c current weather shows temperature, condition and update time");
            Type historyType = assembly.GetType("LingDongDao.CloudHistoryWindow", true)!;
            var historyPanel = (System.Windows.Window)Activator.CreateInstance(historyType,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [service], null)!;
            Check(historyPanel.FindName("CallsList") is System.Windows.Controls.ListBox &&
                historyPanel.FindName("CurrentScreenshot") is System.Windows.Controls.Image &&
                historyPanel.FindName("RawResponseText") is System.Windows.Controls.TextBox,
                "13 WPF call history loads screenshots and complete response controls");
            historyPanel.Close();
            Type overlayType = assembly.GetType("LingDongDao.AgentActionOverlay", true)!;
            var overlay = (System.Windows.Window)Activator.CreateInstance(overlayType,
                nonPublic: true)!;
            overlayType.GetMethod("ShowAction")!.Invoke(overlay,
                [new AgentActionVisual(true, AgentToolKind.RightClick, 300, 220, "测试坐标")]);
            var coordinate = (System.Windows.Controls.TextBlock)overlayType
                .GetField("_coordinateText", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay)!;
            nint handle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            nint exStyle = (nint)overlayType.GetMethod("GetWindowLongPtr",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [handle, -20])!;
            Check(overlay.IsVisible && !overlay.ShowActivated &&
                coordinate.Text.Contains("X 300") && coordinate.Text.Contains("Y 220") &&
                (exStyle.ToInt64() & 0x20) != 0,
                "14 click marker shows action and physical coordinates without activation");
            overlay.Close();
            Type debugType = assembly.GetType("LingDongDao.PerceptionDebugOverlay", true)!;
            var debug = (System.Windows.Window)Activator.CreateInstance(debugType, nonPublic: true)!;
            debugType.GetMethod("ShowObservation")!.Invoke(debug, [click]);
            var canvas = (System.Windows.Controls.Canvas)debugType.GetField("_canvas",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(debug)!;
            bool excluded = (bool)debugType.GetProperty("CaptureExcluded")!.GetValue(debug)!;
            Check(debug.IsVisible && canvas.Children.Count >= 2 && excluded,
                "15 OCR and GUI debug bounds render in a capture-excluded overlay");
            debug.Close();
            if (args.Contains("--settings-snapshot"))
            {
                panel.Show();
                panelType.GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(panel, ["PerceptionPage"]);
                panel.UpdateLayout();
                var render = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                render.Render(panel);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(render));
                string snapshot = Path.Combine(Path.GetTempPath(), "lingdongdao-settings-preview.png");
                using var output = File.Create(snapshot);
                encoder.Save(output);
                Console.WriteLine("SETTINGS_SNAPSHOT " + snapshot);
                panel.Close();
            }
        }
        catch (Exception exception) { uiFailure = exception; }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (uiFailure is not null) throw uiFailure;
}

if (args.Contains("--live-smoke"))
{
    var capture = new ScreenCapture();
    var liveWindow = new WindowsActivity();
    WindowInfo? foreground = liveWindow.Foreground();
    var watch = Stopwatch.StartNew();
    IReadOnlyList<CapturedFrame> live = capture.Capture(foreground);
    ScreenChange liveChange = new ChangeDetector().Analyze(live[0], [], false);
    Console.WriteLine($"LIVE displays={live.Count} capture_change_ms={watch.ElapsedMilliseconds} " +
                      $"foreground={foreground?.ProcessName} first_reason={string.Join(',', liveChange.Reasons)}");
}

if (args.Contains("--live-service") || args.Contains("--live-service-enhanced"))
{
    bool enhanced = args.Contains("--live-service-enhanced");
    bool debug = args.Contains("--live-debug");
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var liveOptions = new PerceptionOptions { Enabled = true, CloudEnabled = false,
                Mode = enhanced ? ResourceMode.Enhanced : ResourceMode.Standard,
                GuiDetectorEnabled = enhanced, DebugOverlayEnabled = debug, SampleSeconds = 2 };
            using var service = new PerceptionService(() => liveOptions);
            int debugFrames = 0, debugOcr = 0, debugGui = 0;
            service.DebugObservation += observation =>
            {
                if (observation is null) return;
                debugFrames++;
                debugOcr = Math.Max(debugOcr, observation.Ocr.Count);
                debugGui = Math.Max(debugGui, observation.GuiElements.Count);
            };
            service.Logged += entry => Console.WriteLine($"SERVICE {entry.Module}/{entry.Event} {entry.Summary}");
            service.Refresh();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                service.Stop();
                var (mb, cpu) = service.ResourceUsage();
                Console.WriteLine($"LIVE_SERVICE frames={service.FrameCount} buffer_mb={service.FrameBytes / 1048576.0:0.0} " +
                                  $"process_mb={mb:0.0} workers_mb={service.WorkerMb:0.0} " +
                                  $"total_mb={mb + service.WorkerMb:0.0} cpu_percent={cpu:0.0} " +
                                  $"ocr={service.OcrStatus} gui={service.GuiStatus}");
                if (debug)
                    Check(debugFrames > 0 && debugOcr > 0 && debugGui > 0,
                        $"LIVE_DEBUG fused OCR={debugOcr} GUI={debugGui} frames={debugFrames}");
                Dispatcher.ExitAllFrames();
            };
            timer.Start();
            Dispatcher.Run();
        }
        catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(25))) throw new Exception("Live service timed out");
    if (failure is not null) throw failure;
}

sealed class MockApiHandler : HttpMessageHandler
{
    public bool SawImage { get; private set; }
    public bool SawContext { get; private set; }
    public bool SawBearer { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SawBearer = request.Headers.Authorization?.Scheme == "Bearer" &&
            request.Headers.Authorization.Parameter == "test-only";
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement content = json.RootElement.GetProperty("messages")[1].GetProperty("content");
        SawImage = content.EnumerateArray().Any(item =>
            item.GetProperty("type").GetString() == "image_url" &&
            item.GetProperty("image_url").GetProperty("url").GetString()!
                .StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal));
        SawContext = content[0].GetProperty("text").GetString()!.Contains("terminal_error");
        const string reply = "{\"choices\":[{\"message\":{\"content\":\"{\\\"should_intervene\\\":true,\\\"reason\\\":\\\"mock\\\",\\\"suggestion\\\":\\\"Check the terminal error\\\",\\\"recommended_action\\\":null}\"}}]}";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
    }
}

sealed class FakeAgentEnvironment(AgentLoopFrame frame, int finishAfterActions) : IAgentLoopEnvironment
{
    public int Captures { get; private set; }
    public Task<AgentLoopFrame> CaptureAsync(CancellationToken cancellationToken)
    {
        Captures++;
        return Task.FromResult(frame);
    }
    public Task<AgentToolDecision> DecideAsync(AgentLoopFrame current,
        IReadOnlyList<AgentActionFeedback> history, CancellationToken cancellationToken)
    {
        AgentToolDecision decision = history.Count >= finishAfterActions
            ? new("finish", AgentToolKind.Finish, 0, 0, "完成", true, "截图已确认成功", "{}")
            : new("click", AgentToolKind.Click, 120, 140, "测试点击", false, "", "{}");
        return Task.FromResult(decision);
    }
    public Task<AgentActionFeedback> ExecuteAsync(AgentLoopFrame current,
        AgentToolDecision decision, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentActionFeedback(decision, true, "mock click completed"));
}

sealed class MockAgentToolHandler : HttpMessageHandler
{
    private int _calls;
    public bool SawToolDefinition { get; private set; }
    public bool SawSecondScreenshotAndToolResult { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using JsonDocument body = JsonDocument.Parse(await request.Content!
            .ReadAsStringAsync(cancellationToken));
        JsonElement root = body.RootElement;
        SawToolDefinition = root.GetProperty("tool_choice").GetString() == "required" &&
            root.GetProperty("tools").GetArrayLength() == 3;
        JsonElement messages = root.GetProperty("messages");
        if (++_calls == 2)
            SawSecondScreenshotAndToolResult = messages.EnumerateArray().Any(item =>
                item.GetProperty("role").GetString() == "tool") &&
                messages[messages.GetArrayLength() - 1].GetProperty("content")[1]
                    .GetProperty("type").GetString() == "image_url";
        string tool = _calls == 1
            ? "{\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"click\",\"arguments\":\"{\\\"x\\\":120,\\\"y\\\":140,\\\"label\\\":\\\"测试点击\\\"}\"}}"
            : "{\"id\":\"call-2\",\"type\":\"function\",\"function\":{\"name\":\"finish\",\"arguments\":\"{\\\"success\\\":true,\\\"summary\\\":\\\"已完成\\\"}\"}}";
        string reply = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[" + tool + "]}}]}";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
    }
}

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

var frames = new FrameRingBuffer();
CapturedFrame before = Frame(now.AddSeconds(-3), terminal);
CapturedFrame current = Frame(now, terminal);
frames.Add(before, 30, 8);
frames.Add(current, 30, 8);
DesktopObservation packaged = Observation(current, [errorText]);
var gate = new CloudEscalationGate();
Check(gate.TryReserve(packaged, firstError, options, now, out _),
    "7a local gate accepts a qualified event");
CloudEventPackage? cloudPackage = PrivacyFilter.Build(packaged, firstError, frames, memory, options);
var mock = new MockCloudAgent();
if (cloudPackage is not null) await mock.DecideAsync(cloudPackage, CancellationToken.None);
Check(cloudPackage is { CurrentJpeg.Length: > 0, BeforeJpeg.Length: > 0 } &&
    mock.CallCount == 1 && cloudPackage.Ocr.Count > 0,
    "7b cloud package retains screenshots and context");
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

try { throw new HttpRequestException("mock cloud unavailable"); }
catch (HttpRequestException)
{
    LocalAssessment afterFailure = classifier.Classify(errorObservation, memory);
    Check(afterFailure.EventType == "terminal_error", "9 cloud outage leaves local classification working");
}

using Process process = Process.GetCurrentProcess();
Console.WriteLine($"PERFORMANCE tests_process_mb={process.WorkingSet64 / 1048576.0:0.0} " +
                  $"cpu_seconds={process.TotalProcessorTime.TotalSeconds:0.00} frames_mb={frames.Bytes / 1048576.0:0.00}");

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
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var liveOptions = new PerceptionOptions { Enabled = true, CloudEnabled = false,
                Mode = enhanced ? ResourceMode.Enhanced : ResourceMode.Standard,
                GuiDetectorEnabled = enhanced, SampleSeconds = 2 };
            using var service = new PerceptionService(() => liveOptions);
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

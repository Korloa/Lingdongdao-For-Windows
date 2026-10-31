using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace LingDongDao.Perception;

public sealed class PerceptionService : IDisposable
{
    private readonly Func<PerceptionOptions> _options;
    private readonly ScreenCapture _capture = new();
    private readonly ChangeDetector _change = new();
    private readonly UiAutomationReader _uia = new();
    private readonly PythonOcrBridge _ocr = new();
    private readonly PythonGuiDetector _gui = new();
    private readonly RuleClassifier _classifier = new();
    private readonly ConfigurableCloudAgent _cloud = new();
    private readonly CloudEscalationGate _gate = new();
    private readonly FrameRingBuffer _frames = new();
    private readonly StateMemory _memory = new();
    private readonly Queue<PerceptionLog> _logs = new();
    private WindowsActivity? _activity;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private Task<IReadOnlyList<GuiElement>>? _uiaPending;
    private long _uiaHandle;
    private DateTimeOffset _lastEvent = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFullScan = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOcr = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGui = DateTimeOffset.MinValue;
    private double _cpuPercent;
    private TimeSpan _previousCpu;
    private DateTimeOffset _previousCpuTime;
    private bool _disposed;
    private bool _restartQueued;
    private bool _stopping;
    private readonly object _logLock = new();

    public event Action<PerceptionLog>? Logged;
    public event Action<LocalAssessment>? Assessed;
    public event Action<CloudDecision>? Suggested;
    public string CurrentProcess { get; private set; } = "—";
    public LocalAssessment? LastAssessment { get; private set; }
    public int CloudCallsToday => _gate.CallsToday;
    public long FrameBytes => _frames.Bytes;
    public int FrameCount => _frames.Count;
    public bool IsRunning => _loop is { IsCompleted: false };
    public string OcrStatus => _ocr.Status;
    public string GuiStatus => _gui.Status;
    public bool CloudConfigured => _cloud.Available;
    public double WorkerMb => _ocr.WorkerMb + _gui.WorkerMb;

    public PerceptionService(Func<PerceptionOptions> options) => _options = options;

    // Called on the WPF UI thread so the native hooks have a message pump.
    public void Refresh()
    {
        if (_disposed) return;
        PerceptionOptions options = _options();
        options.Normalize();
        if (!options.Enabled || options.Paused)
        {
            Stop();
            return;
        }
        if (IsRunning)
        {
            if (_stopping && !_restartQueued && _loop is not null)
            {
                _restartQueued = true;
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                _ = _loop.ContinueWith(_ => dispatcher.BeginInvoke(() =>
                {
                    _restartQueued = false;
                    Refresh();
                }), TaskScheduler.Default);
            }
            return;
        }
        _activity = new WindowsActivity();
        try { _activity.Start(); }
        catch (Exception exception)
        {
            _activity.Dispose();
            _activity = null;
            Log("error", "windows", "hook_failed", exception.Message);
            return;
        }
        _stop = new CancellationTokenSource();
        _stopping = false;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        ResourceUsage();
        Log("info", "perception", "started", "Local capture started; cloud upload remains separately gated.");
    }

    public void Stop()
    {
        CancellationTokenSource? stopping = _stop;
        stopping?.Cancel();
        if (stopping is not null) _stopping = true;
        _activity?.Dispose();
        _activity = null;
        _stop = null;
        if (stopping is not null)
        {
            if (_loop is { IsCompleted: false } loop)
                _ = loop.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default);
            else stopping.Dispose();
        }
    }

    public void ClearFrames()
    {
        _frames.Clear();
        _change.Clear();
        Log("info", "capture", "cache_cleared", "In-memory screenshots cleared.");
    }

    public IReadOnlyList<PerceptionLog> RecentLogs()
    {
        lock (_logLock) return _logs.ToArray();
    }

    public (double ProcessMb, double CpuPercent) ResourceUsage()
    {
        using Process process = Process.GetCurrentProcess();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan cpu = process.TotalProcessorTime + _ocr.WorkerCpu + _gui.WorkerCpu;
        if (_previousCpuTime != DateTimeOffset.MinValue)
        {
            double elapsed = (now - _previousCpuTime).TotalSeconds;
            if (elapsed > 0) _cpuPercent = Math.Clamp((cpu - _previousCpu).TotalSeconds /
                (elapsed * Environment.ProcessorCount) * 100, 0, 100);
        }
        _previousCpu = cpu;
        _previousCpuTime = now;
        return (process.WorkingSet64 / 1048576.0, _cpuPercent);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await SampleAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            { Log("error", "perception", "sample_failed", exception.GetType().Name + ": " + exception.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(_options().SampleSeconds), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        PerceptionOptions options = _options();
        WindowInfo? window = _activity?.Foreground();
        if (window?.ProcessId == Environment.ProcessId) return;
        CurrentProcess = window?.ProcessName ?? "—";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<ActivityEvent> events = _activity?.Drain(_lastEvent) ?? [];
        _lastEvent = now;
        (int X, int Y) mouse = _activity?.MousePosition() ?? (0, 0);
        IReadOnlyList<CapturedFrame> frames = await Task.Run(() => _capture.Capture(window), cancellationToken);
        bool periodic = now - _lastFullScan >= TimeSpan.FromSeconds(30);
        foreach (CapturedFrame frame in frames)
        {
            _frames.Add(frame, options.FrameRetentionSeconds, options.FrameMemoryLimitMb);
            ScreenChange change = _change.Analyze(frame, events, periodic);
            if (!change.ShouldAnalyze) continue;
            if (window is null || !window.Bounds.Intersects(frame.Display.Bounds)) continue;
            PixelRect analysis = SelectAnalysisBounds(frame, window, change, periodic);
            byte[] region = CropToJpeg(frame, analysis);
            var missing = new List<string>();
            IReadOnlyList<GuiElement> controls = await ReadUiAsync(window, cancellationToken);
            if (controls.Count == 0) missing.Add("uia");
            IReadOnlyList<OcrText> text = [];
            bool runOcr = options.Mode != ResourceMode.Low || periodic ||
                change.Reasons.Contains("window_switch") ||
                now - _lastOcr >= TimeSpan.FromSeconds(8);
            if (_ocr.Available && runOcr)
            {
                text = await _ocr.RecognizeAsync(region, analysis, frame.Time, cancellationToken);
                _lastOcr = now;
            }
            if (!_ocr.Available || !runOcr) missing.Add("ocr");
            if (options.Mode == ResourceMode.Enhanced && options.GuiDetectorEnabled &&
                _gui.Available && now - _lastGui >= TimeSpan.FromSeconds(5))
            {
                IReadOnlyList<GuiElement> found = await _gui.DetectAsync(region, analysis, cancellationToken);
                controls = controls.Concat(found).ToArray();
                _lastGui = now;
            }
            else if (options.Mode == ResourceMode.Enhanced && options.GuiDetectorEnabled && !_gui.Available)
                missing.Add("gui_detector");
            DesktopObservation observation = ObservationFusion.Fuse(frame, analysis, controls,
                text, events, mouse, change, missing);
            LocalAssessment assessment = _classifier.Classify(observation, _memory);
            _memory.Update(observation, assessment);
            LastAssessment = assessment;
            Assessed?.Invoke(assessment);
            Log("info", "local", assessment.EventType,
                $"app={window.ProcessName} score={assessment.InterventionScore:0.00} reason={string.Join(',', change.Reasons)}");
            if (!options.CloudEnabled || !options.UploadConsent || !_cloud.Available)
                continue;
            if (assessment.EventType == "none" ||
                assessment.InterventionScore < options.EscalationThreshold) continue;
            CloudEventPackage? package = PrivacyFilter.Build(observation, assessment,
                _frames, _memory, options);
            if (package is null) { Log("warn", "privacy", "blocked", "Package not safe to build"); continue; }
            if (!_gate.TryReserve(observation, assessment, options, now, out string reason))
            {
                if (assessment.InterventionScore >= options.EscalationThreshold)
                    Log("info", "gate", "blocked", reason);
                continue;
            }
            try
            {
                CloudDecision? decision = await _cloud.DecideAsync(package, cancellationToken);
                if (decision is null) { Log("warn", "cloud", "unavailable", "API not configured or no response"); continue; }
                Log("info", "cloud", "decision", decision.ShouldIntervene ? "suggest" : "do_not_interrupt");
                if (decision.ShouldIntervene && !string.IsNullOrWhiteSpace(decision.Suggestion))
                {
                    _memory.MarkHelped(assessment.EventKey, now);
                    Suggested?.Invoke(decision);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { Log("warn", "cloud", "request_failed", exception.GetType().Name); }
        }
        if (periodic) _lastFullScan = now;
    }

    private async Task<IReadOnlyList<GuiElement>> ReadUiAsync(WindowInfo window,
        CancellationToken cancellationToken)
    {
        if (_uiaPending is null || _uiaPending.IsCompleted)
        {
            _uiaHandle = window.Handle;
            _uiaPending = Task.Run(() => _uia.Read(window), cancellationToken);
        }
        if (_uiaHandle != window.Handle) return [];
        Task done = await Task.WhenAny(_uiaPending, Task.Delay(900, cancellationToken));
        return done == _uiaPending && _uiaPending.IsCompletedSuccessfully ? _uiaPending.Result : [];
    }

    private static PixelRect SelectAnalysisBounds(CapturedFrame frame, WindowInfo window,
        ScreenChange change, bool periodic)
    {
        PixelRect visible = window.Bounds.Intersect(frame.Display.Bounds);
        if (periodic || change.Reasons.Contains("window_switch") || change.Regions.Count == 0)
            return visible;
        var relevant = change.Regions.Where(region => region.Bounds.Intersects(visible))
            .OrderByDescending(region => region.MeanDifference).Take(4).ToArray();
        if (relevant.Length == 0) return visible;
        int left = relevant.Min(item => item.Bounds.X), top = relevant.Min(item => item.Bounds.Y);
        int right = relevant.Max(item => item.Bounds.X + item.Bounds.Width);
        int bottom = relevant.Max(item => item.Bounds.Y + item.Bounds.Height);
        return new PixelRect(left - 16, top - 16, right - left + 32, bottom - top + 32)
            .Intersect(visible);
    }

    private static byte[] CropToJpeg(CapturedFrame frame, PixelRect bounds)
    {
        using var stream = new MemoryStream(frame.Jpeg);
        using var source = new Bitmap(stream);
        using var output = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(output))
            graphics.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height),
                new Rectangle(bounds.X - frame.Display.Bounds.X,
                    bounds.Y - frame.Display.Bounds.Y, bounds.Width, bounds.Height), GraphicsUnit.Pixel);
        using var encoded = new MemoryStream();
        output.Save(encoded, ImageFormat.Jpeg);
        return encoded.ToArray();
    }

    private void Log(string level, string module, string name, string summary)
    {
        var entry = new PerceptionLog(DateTimeOffset.Now, level, module, name, summary);
        lock (_logLock)
        {
            _logs.Enqueue(entry);
            while (_logs.Count > 120) _logs.Dequeue();
        }
        Logged?.Invoke(entry);
    }

    public void Dispose()
    {
        _disposed = true;
        Task? active = _loop;
        Stop();
        void Release()
        {
            _ocr.Dispose();
            _gui.Dispose();
            _cloud.Dispose();
            _frames.Clear();
        }
        if (active is { IsCompleted: false })
            _ = active.ContinueWith(_ => Release(), TaskScheduler.Default);
        else Release();
    }
}

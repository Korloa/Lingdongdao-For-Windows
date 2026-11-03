using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;

namespace LingDongDao.Perception;

public sealed partial class PerceptionService : IDisposable
{
    private readonly Func<PerceptionOptions> _options;
    private readonly ScreenCapture _capture = new();
    private readonly ChangeDetector _change = new();
    private readonly UiAutomationReader _uia = new();
    private readonly PythonOcrBridge _ocr = new();
    private readonly PythonGuiDetector _gui = new();
    private readonly RuleClassifier _classifier = new();
    private readonly ConfigurableCloudAgent _cloud = new();
    private readonly CloudEscalationGate _gate = new(CloudEscalationGate.DefaultLedgerPath);
    private readonly CloudCallHistory _history = new();
    private readonly AgentSkillStore _skills = new();
    private readonly AgentWakeScheduler _wakes;
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
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
    public event Action<DesktopObservation?>? DebugObservation;
    public event Action<CloudDecision>? Suggested;
    public event Action? CloudHistoryChanged;
    public string CurrentProcess { get; private set; } = "—";
    public LocalAssessment? LastAssessment { get; private set; }
    public LocalAssessment? LastMeaningfulAssessment { get; private set; }
    public DateTimeOffset LastMeaningfulTime { get; private set; }
    public int CloudCallsToday => _gate.CallsToday;
    public double EstimatedCloudSpendToday => _gate.EstimatedSpentToday;
    public long FrameBytes => _frames.Bytes;
    public int FrameCount => _frames.Count;
    public bool IsRunning => _loop is { IsCompleted: false };
    public string OcrStatus => _ocr.Status;
    public string GuiStatus => _options().Mode != ResourceMode.Enhanced
        ? "当前资源模式未运行 GUI" : !_options().GuiDetectorEnabled
            ? "GUI 检测已关闭" : _gui.Status;
    public bool CloudConfigured => _cloud.Available;
    public string CloudModel => _cloud.Model;
    public string CloudLastStatus { get; private set; } = "未验证";
    public double WorkerMb => _ocr.WorkerMb + _gui.WorkerMb;
    public IReadOnlyList<CloudCallRecord> CloudHistory() => _history.List(_gate.UsageRecords);
    public void ClearCloudHistory() { _history.Clear(); CloudHistoryChanged?.Invoke(); }

    public PerceptionService(Func<PerceptionOptions> options)
    {
        _options = options;
        _wakes = new AgentWakeScheduler(OnAgentWake, error: exception =>
            Log("warn", "agent", "wake_error", exception.GetType().Name));
    }

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
        DebugObservation?.Invoke(null);
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
            try
            {
                await _analysisLock.WaitAsync(cancellationToken);
                try { await SampleAsync(cancellationToken); }
                finally { _analysisLock.Release(); }
            }
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
        if (window?.ProcessId == Environment.ProcessId)
        {
            DebugObservation?.Invoke(null);
            return;
        }
        if (options.DebugOverlayEnabled && PrivacyFilter.IsSensitive(window, options))
            DebugObservation?.Invoke(null);
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
            if (options.DebugOverlayEnabled)
                DebugObservation?.Invoke(PrivacyFilter.IsSensitive(observation, options)
                    ? null : observation);
            LocalAssessment assessment = _classifier.Classify(observation, _memory);
            _memory.Update(observation, assessment);
            LastAssessment = assessment;
            if (assessment.EventType != "none" && assessment.InterventionScore >= 0.3)
            {
                LastMeaningfulAssessment = assessment;
                LastMeaningfulTime = now;
            }
            Assessed?.Invoke(assessment);
            Log("info", "local", assessment.EventType,
                $"app={window.ProcessName} score={assessment.InterventionScore:0.00} " +
                $"reason={assessment.Reason} trigger={string.Join(',', change.Reasons)}");
            if (!options.CloudEnabled || !options.UploadConsent || !_cloud.Available)
                continue;
            if (assessment.EventType == "none" ||
                assessment.InterventionScore < options.EscalationThreshold) continue;
            if (!_gate.CanEscalate(observation, assessment, options, now, out string initialReason))
            {
                Log("info", "gate", "blocked", initialReason);
                continue;
            }
            // Incremental OCR may miss a secret elsewhere in the visible window.
            if (!_ocr.Available)
            {
                Log("warn", "privacy", "blocked", "Full-window OCR is unavailable");
                continue;
            }
            PixelRect fullWindow = window.Bounds.Intersect(frame.Display.Bounds);
            if (fullWindow.Width < 10 || fullWindow.Height < 10) continue;
            IReadOnlyList<OcrText> fullText = await _ocr.RecognizeAsync(
                CropToJpeg(frame, fullWindow), fullWindow, frame.Time, cancellationToken);
            if (!_ocr.Available)
            {
                Log("warn", "privacy", "blocked", "Full-window OCR failed");
                continue;
            }
            IReadOnlyList<OcrText>? beforeText = null;
            CapturedFrame? priorFrame = _frames.Before(frame.Time, frame.Display.Id,
                TimeSpan.FromSeconds(12));
            if (priorFrame?.Foreground?.Handle == window.Handle &&
                priorFrame.Foreground.Bounds == window.Bounds &&
                priorFrame.Display.Bounds == frame.Display.Bounds &&
                !controls.Any(item => item.IsPassword))
            {
                beforeText = await _ocr.RecognizeAsync(CropToJpeg(priorFrame, fullWindow),
                    fullWindow, priorFrame.Time, cancellationToken);
                if (!_ocr.Available) beforeText = null;
            }
            DesktopObservation uploadObservation = observation with { Ocr = fullText };
            CloudEventPackage? package = PrivacyFilter.Build(uploadObservation, assessment,
                _frames, _memory, options, beforeText);
            if (package is null) { Log("warn", "privacy", "blocked", "Package not safe to build"); continue; }
            if (!_gate.TryReserve(uploadObservation, assessment, options, now, out string reason))
            {
                if (assessment.InterventionScore >= options.EscalationThreshold)
                    Log("info", "gate", "blocked", reason);
                continue;
            }
            ManualAnalysisResult result = await CallCloudAsync(package, false, now, cancellationToken);
            if (result.Decision is { ShouldIntervene: true } decision &&
                !string.IsNullOrWhiteSpace(decision.Suggestion))
            {
                _memory.MarkHelped(assessment.EventKey, now);
                Suggested?.Invoke(decision);
            }
        }
        if (periodic) _lastFullScan = now;
    }

    public async Task<ManualAnalysisResult> AnalyzeNowAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return new(null, "感知服务已关闭");
        PerceptionOptions options = _options();
        options.Normalize();
        if (!options.CloudEnabled || !options.UploadConsent)
            return new(null, "请先在控制面板授权云端截图上传");
        if (!_cloud.Available) return new(null, "请先保存 DeepSeek API 密钥");
        if (!_ocr.Available) return new(null, "离线 OCR 不可用，无法完成上传前隐私检查");
        await _analysisLock.WaitAsync(cancellationToken);
        try
        {
            using var activity = new WindowsActivity();
            WindowInfo? window = activity.Foreground();
            if (window is null || window.ProcessId == Environment.ProcessId)
                return new(null, "请切换到需要分析的窗口，再按主动分析快捷键");
            if (PrivacyFilter.IsSensitive(window, options))
                return new(null, "当前窗口已被隐私规则排除");
            IReadOnlyList<CapturedFrame> captured = await Task.Run(() =>
                _capture.Capture(window), cancellationToken);
            CapturedFrame? frame = captured.Where(item => item.Display.Bounds.Intersects(window.Bounds))
                .OrderByDescending(item => item.Display.Bounds.Intersect(window.Bounds).Width *
                    (long)item.Display.Bounds.Intersect(window.Bounds).Height).FirstOrDefault();
            if (frame is null) return new(null, "无法截取当前窗口");
            _frames.Add(frame, options.FrameRetentionSeconds, options.FrameMemoryLimitMb);
            PixelRect bounds = frame.Display.Bounds.Intersect(window.Bounds);
            IReadOnlyList<GuiElement> controls = await ReadUiAsync(window, cancellationToken);
            if (controls.Count == 0) return new(null, "当前窗口无法完成控件隐私检查");
            IReadOnlyList<OcrText> text = await _ocr.RecognizeAsync(
                CropToJpeg(frame, bounds), bounds, frame.Time, cancellationToken);
            if (!_ocr.Available) return new(null, "OCR 隐私检查失败");
            var change = new ScreenChange(0, [], ["manual_hotkey"], true);
            DesktopObservation observation = ObservationFusion.Fuse(frame, bounds, controls,
                text, [], activity.MousePosition(), change, []);
            var assessment = new LocalAssessment("manual", "manual_request", "用户主动要求分析当前窗口",
                1, 1, 1, "user_hotkey", "manual:" + Guid.NewGuid().ToString("N"));
            DateTimeOffset now = frame.Time;
            if (!_gate.CanEscalate(observation, assessment, options, now, out string gateReason, true))
                return new(null, GateMessage(gateReason));
            CloudEventPackage? package = PrivacyFilter.Build(observation, assessment,
                _frames, _memory, options);
            if (package is null) return new(null, "当前窗口未通过隐私检查，未上传截图");
            if (!_gate.TryReserve(observation, assessment, options, now, out gateReason, true))
                return new(null, GateMessage(gateReason));
            return await CallCloudAsync(package, true, now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(null, "分析已取消"); }
        catch (Exception exception)
        {
            Log("warn", "manual", "failed", exception.GetType().Name);
            return new(null, "主动分析失败：" + exception.GetType().Name);
        }
        finally { _analysisLock.Release(); }
    }

    private async Task<ManualAnalysisResult> CallCloudAsync(CloudEventPackage package,
        bool userInitiated, DateTimeOffset reservedAt, CancellationToken cancellationToken)
    {
        var record = new CloudCallRecord(Guid.NewGuid().ToString("N"), package.Time,
            userInitiated, package.ProcessName, package.WindowTitle,
            package.Assessment.EventSummary, package.Assessment.EventKey,
            package.Assessment.InterventionScore,
            "pending", null, "", "", null, "", "", package.CurrentJpeg, package.BeforeJpeg);
        try { _history.Save(record); CloudHistoryChanged?.Invoke(); }
        catch (Exception exception)
        {
            Log("warn", "history", "save_failed", exception.GetType().Name);
            return new(null, "无法保存调用记录，本次未发送截图");
        }
        CloudDecision? decision;
        try { decision = await _cloud.DecideAsync(package, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CloudLastStatus = exception is HttpRequestException http && http.StatusCode.HasValue
                ? $"HTTP {(int)http.StatusCode.Value}" : exception.GetType().Name;
            record = record with { Status = "error", Error = CloudLastStatus };
            try { _history.Save(record); CloudHistoryChanged?.Invoke(); }
            catch (Exception saveException) { Log("warn", "history", "save_failed", saveException.GetType().Name); }
            Log("warn", "cloud", "request_failed", CloudLastStatus);
            return new(null, "云端请求失败：" + CloudLastStatus);
        }
        if (decision is null)
        {
            CloudLastStatus = "响应为空";
            record = record with { Status = "empty", Error = "模型未返回可解析的内容" };
            Log("warn", "cloud", "empty", "Model returned no structured decision");
        }
        else
        {
            CloudLastStatus = "连接成功";
            if (!_gate.MarkDecision(reservedAt, package.Assessment.EventKey,
                decision.ShouldIntervene))
                Log("warn", "gate", "decision_not_saved", "Cloud result could not update budget ledger");
            record = record with { Status = "success", ShouldIntervene = decision.ShouldIntervene,
                Reason = decision.Reason, Suggestion = decision.Suggestion,
                RecommendedAction = decision.RecommendedAction,
                RawResponse = decision.RawJson ?? "" };
            Log("info", "cloud", "decision", decision.ShouldIntervene ? "suggest" : "do_not_interrupt");
        }
        try { _history.Save(record); CloudHistoryChanged?.Invoke(); }
        catch (Exception exception)
        { Log("warn", "history", "save_failed", exception.GetType().Name); }
        return new(decision, decision is null ? "模型没有返回可用的分析结果" : "分析完成");
    }

    private static string GateMessage(string reason) => reason switch
    {
        "daily_budget" => "今日云端费用预算已用尽，可在设置中调整预算",
        "ledger_unavailable" => "调用额度记录不可用，已停止云端请求",
        "sensitive_window" => "当前窗口已被隐私规则排除",
        "upload_not_authorized" => "请先授权云端截图上传",
        "ledger_write_failed" => "调用额度记录保存失败，本次未上传",
        _ => "云端调用被限制：" + reason
    };

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
        _wakes.Dispose();
        Task? active = _loop;
        Task? agent = _agentTask;
        StopAgent();
        Stop();
        void Release()
        {
            _ocr.Dispose();
            _gui.Dispose();
            _cloud.Dispose();
            _frames.Clear();
        }
        var running = new[] { active, agent }.Where(task => task is { IsCompleted: false })
            .Cast<Task>().ToArray();
        if (running.Length > 0)
            _ = Task.WhenAll(running).ContinueWith(_ => Release(), TaskScheduler.Default);
        else Release();
    }
}

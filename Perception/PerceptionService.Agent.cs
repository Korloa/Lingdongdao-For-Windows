using System.Net.Http;

namespace LingDongDao.Perception;

public sealed partial class PerceptionService
{
    private CancellationTokenSource? _agentStop;
    private Task? _agentTask;
    private string _agentStatus = "未运行";
    private int _agentActions, _agentCalls;

    public event Action<AgentActionVisual>? AgentVisual;
    public event Action? AgentBeforeCapture;
    public event Action? AgentAfterCapture;
    public event Action<AgentLoopOutcome>? AgentCompleted;
    public bool AgentRunning => _agentTask is { IsCompleted: false };
    public string AgentStatus => _agentStatus;
    public int AgentActions => _agentActions;
    public int AgentCalls => _agentCalls;

    public bool TryStartAgent(string goal, out string message, bool quickMode = false)
    {
        goal = goal.Trim();
        if (_disposed) { message = "服务已关闭"; return false; }
        if (AgentRunning) { message = "Agent 已在运行"; return false; }
        if (goal.Length is < 3 or > 300)
        { message = "请输入 3～300 字的任务目标"; return false; }
        PerceptionOptions options = _options();
        options.Normalize();
        if (!options.CloudEnabled || !options.UploadConsent)
        { message = "请先授权云端截图上传"; return false; }
        if (!_cloud.Available) { message = "请先配置云端密钥"; return false; }
        if (!_ocr.Available) { message = "离线 OCR 不可用，无法做上传前隐私检查"; return false; }
        if (_gate.EstimatedSpentToday + options.EstimatedCostPerCallUsd > options.DailyCloudBudgetUsd)
        { message = "今日云端预算不足，请在控制面板调整预算"; return false; }
        _agentStop?.Dispose();
        _agentStop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _agentActions = 0;
        _agentCalls = 0;
        ReportAgent("正在启动");
        CancellationToken token = _agentStop.Token;
        _agentTask = Task.Run(() => RunAgentAsync(goal,
            quickMode ? options.QuickAgentMaxActions : options.AgentMaxActions,
            quickMode, token));
        message = "已启动；可随时按“停止 Agent”快捷键中止";
        return true;
    }

    public void StopAgent() => _agentStop?.Cancel();

    private async Task RunAgentAsync(string goal, int maxActions, bool quickMode,
        CancellationToken cancellationToken)
    {
        AgentLoopOutcome outcome;
        bool locked = false;
        try
        {
            await _analysisLock.WaitAsync(cancellationToken);
            locked = true;
            var environment = new LiveAgentEnvironment(this, goal, quickMode);
            outcome = await new AgentLoopEngine().RunAsync(environment, maxActions,
                ReportAgent, cancellationToken);
        }
        catch (OperationCanceledException)
        { outcome = new(false, _agentActions, _agentCalls, "已停止", "用户停止或运行超过 5 分钟"); }
        catch (Exception exception)
        {
            Log("warn", "agent", "stopped", exception.GetType().Name + ": " + exception.Message);
            outcome = new(false, _agentActions, _agentCalls, "无法继续", exception.Message);
        }
        finally
        {
            AgentVisual?.Invoke(new AgentActionVisual(false, AgentToolKind.Click, 0, 0, ""));
            if (locked) _analysisLock.Release();
        }
        ReportAgent($"{outcome.Status} · {outcome.Actions} 步 · {outcome.CloudCalls} 次云端判断");
        AgentCompleted?.Invoke(outcome);
    }

    private void ReportAgent(string status)
    {
        _agentStatus = status;
        Log("info", "agent", "state", status);
    }

    private sealed class LiveAgentEnvironment : IAgentLoopEnvironment
    {
        private readonly PerceptionService _service;
        private readonly string _goal;
        private readonly bool _quickMode;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly AgentConversation _conversation;
        private int _processId;
        private int _round;
        private string? _expectedProcessName;
        private HashSet<nint> _browserWindowsBeforeLaunch = [];
        private bool _typedInMessageApp;

        public LiveAgentEnvironment(PerceptionService service, string goal, bool quickMode)
        { _service = service; _goal = goal; _quickMode = quickMode;
            _conversation = new AgentConversation(quickMode); }

        public async Task<AgentLoopFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            _service.AgentBeforeCapture?.Invoke();
            await Task.Delay(420, cancellationToken);
            using var activity = new WindowsActivity();
            WindowInfo? window = activity.Foreground();
            if (_expectedProcessName is { } expected)
            {
                for (int attempt = 0; attempt < 32 &&
                     !string.Equals(window?.ProcessName, expected, StringComparison.OrdinalIgnoreCase);
                     attempt++)
                {
                    AgentBrowserTool.FocusNewWindow(expected == "msedge" ? "edge" : "chrome",
                        _browserWindowsBeforeLaunch);
                    await Task.Delay(250, cancellationToken);
                    window = activity.Foreground();
                }
                if (!string.Equals(window?.ProcessName, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("浏览器没有切换到前台，Agent 已停止");
                _processId = window!.ProcessId;
                _expectedProcessName = null;
            }
            if (window is null || window.ProcessId == Environment.ProcessId)
                throw new InvalidOperationException("请将目标软件保持在前台");
            if (_processId == 0) _processId = window.ProcessId;
            else if (!_quickMode && window.ProcessId != _processId)
                throw new InvalidOperationException("前台已切换到其他软件，Agent 已停止");
            PerceptionOptions options = _service._options();
            if (!options.CloudEnabled || !options.UploadConsent ||
                PrivacyFilter.IsSensitive(window, options))
                throw new InvalidOperationException("当前窗口未获上传授权或被隐私规则排除");
            IReadOnlyList<WindowInfo> windowsBefore = _quickMode
                ? DesktopWindowInventory.VisibleWindows() : [window];
            CapturedFrame? frame;
            if (_quickMode)
                frame = await Task.Run(() => _service._capture.CaptureDesktop(window),
                    cancellationToken);
            else
            {
                IReadOnlyList<CapturedFrame> captured = await Task.Run(() =>
                    _service._capture.Capture(window), cancellationToken);
                frame = captured.Where(item => item.Display.Bounds.Intersects(window.Bounds))
                    .OrderByDescending(item => item.Display.Bounds.Intersect(window.Bounds).Width *
                        (long)item.Display.Bounds.Intersect(window.Bounds).Height).FirstOrDefault();
            }
            IReadOnlyList<WindowInfo> windows = _quickMode
                ? windowsBefore.Concat(DesktopWindowInventory.VisibleWindows())
                    .GroupBy(item => item.Handle).Select(group => group.First()).ToArray()
                : [window];
            _service.AgentAfterCapture?.Invoke();
            if (frame is null) throw new InvalidOperationException("无法截取桌面");
            PixelRect bounds = _quickMode ? frame.Display.Bounds :
                frame.Display.Bounds.Intersect(window.Bounds);
            _service._frames.Add(frame, options.FrameRetentionSeconds, options.FrameMemoryLimitMb);
            IReadOnlyList<GuiElement> controls = await _service.ReadUiAsync(window, cancellationToken);
            if (controls.Count == 0 && !_quickMode)
                throw new InvalidOperationException("控件隐私检查不可用");
            IReadOnlyList<OcrText> ocr = await _service._ocr.RecognizeAsync(
                _quickMode ? frame.Jpeg : CropToJpeg(frame, bounds), bounds, frame.Time,
                cancellationToken);
            if (!_service._ocr.Available) throw new InvalidOperationException("OCR 隐私检查失败");
            var change = new ScreenChange(0, [], ["agent_step"], true);
            var observation = ObservationFusion.Fuse(frame, bounds, controls, ocr, [],
                activity.MousePosition(), change, controls.Count == 0 ? ["uia"] : []);
            var assessment = new LocalAssessment("agent", "agent_step", _goal,
                1, 1, 1, "user_started_agent", $"agent:{_sessionId}:{_round++}");
            CloudEventPackage package = (_quickMode
                ? PrivacyFilter.BuildDesktop(observation, assessment, _service._frames,
                    _service._memory, options, windows)
                : PrivacyFilter.Build(observation, assessment,
                    _service._frames, _service._memory, options))
                ?? throw new InvalidOperationException("当前截图未通过隐私检查，Agent 已停止");
            IReadOnlyList<WindowInfo> safeWindows = windows.Where(item =>
                item.ProcessId != Environment.ProcessId &&
                !PrivacyFilter.IsSensitive(item, options) &&
                !PrivacyFilter.ContainsSecret(item.Title)).Take(30).ToArray();
            return new AgentLoopFrame(window, bounds, package.CurrentJpeg, package,
                observation, safeWindows);
        }

        public async Task<AgentToolDecision> DecideAsync(AgentLoopFrame frame,
            IReadOnlyList<AgentActionFeedback> history, CancellationToken cancellationToken)
        {
            PerceptionOptions options = _service._options();
            DateTimeOffset reservedAt = DateTimeOffset.UtcNow;
            if (!_service._gate.TryReserve(frame.Observation, frame.Package.Assessment,
                options, reservedAt, out string reason, userInitiated: true))
                throw new InvalidOperationException(GateMessage(reason));
            _service._agentCalls++;
            var record = new CloudCallRecord(Guid.NewGuid().ToString("N"), reservedAt, true,
                frame.Window.ProcessName, frame.Window.Title, _goal,
                frame.Package.Assessment.EventKey, 1, "pending", null, "", "", null,
                "", "", frame.Jpeg, null);
            try { _service._history.Save(record); _service.CloudHistoryChanged?.Invoke(); }
            catch (Exception exception)
                { throw new IOException("无法保存 Agent 调用记录，未发送截图", exception); }
            try
            {
                AgentToolDecision decision = await _service._cloud.NextAgentToolAsync(
                    _conversation, frame, _goal, history, cancellationToken, _quickMode);
                record = record with { Status = decision.Kind == AgentToolKind.Finish
                    ? "agent_finished" : "agent_action", Reason = decision.Kind == AgentToolKind.Finish
                    ? decision.Summary : decision.Label, RawResponse = decision.RawResponse,
                    ShouldIntervene = decision.Kind != AgentToolKind.Finish || decision.Success };
                _service._gate.MarkDecision(reservedAt, frame.Package.Assessment.EventKey, true);
                _service._history.Save(record);
                _service.CloudHistoryChanged?.Invoke();
                return decision;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                record = record with { Status = "error", Error = exception.GetType().Name };
                try { _service._history.Save(record); _service.CloudHistoryChanged?.Invoke(); }
                catch (Exception) { }
                throw;
            }
        }

        public async Task<AgentActionFeedback> ExecuteAsync(AgentLoopFrame frame,
            AgentToolDecision decision, CancellationToken cancellationToken)
        {
            if (decision.Kind == AgentToolKind.OpenBrowser)
            {
                using var beforeLaunch = new WindowsActivity();
                WindowInfo? foreground = beforeLaunch.Foreground();
                bool sameWindow = foreground?.Handle == frame.Window.Handle &&
                    foreground.ProcessId == frame.Window.ProcessId;
                string browserName = decision.Value == "edge" ? "edge" : "chrome";
                _browserWindowsBeforeLaunch = AgentBrowserTool.SnapshotWindows(browserName);
                bool executed = _quickMode && sameWindow && decision.Value is { } browser &&
                    AgentBrowserTool.TryLaunch(browser, out _);
                if (executed)
                {
                    _expectedProcessName = browserName == "edge" ? "msedge" : "chrome";
                    _service._agentActions++;
                }
                var result = new AgentActionFeedback(decision, executed,
                    executed ? "浏览器已启动；等待新窗口截图验证" :
                    !sameWindow ? "前台窗口已变化，未启动浏览器" : "浏览器未能启动");
                _conversation.AddToolResult(decision, result);
                if (executed) await Task.Delay(1000, cancellationToken);
                return result;
            }
            if (_quickMode && decision.Kind is AgentToolKind.OpenApp or AgentToolKind.ActivateWindow)
            {
                using var beforeSwitch = new WindowsActivity();
                WindowInfo? foreground = beforeSwitch.Foreground();
                bool sameWindow = foreground?.Handle == frame.Window.Handle &&
                    foreground.ProcessId == frame.Window.ProcessId;
                WindowInfo? target = decision.Kind == AgentToolKind.ActivateWindow &&
                    long.TryParse(decision.Value, out long handle)
                    ? frame.VisibleWindows?.FirstOrDefault(item => item.Handle == handle) : null;
                bool executed = false;
                string detail;
                if (!sameWindow) detail = "桌面前台已变化，未切换软件";
                else if (decision.Kind == AgentToolKind.ActivateWindow && target is null)
                    detail = "窗口不在上一帧可见窗口清单中";
                else
                {
                    if (target is not null)
                    {
                        _service.AgentVisual?.Invoke(new AgentActionVisual(true,
                            AgentToolKind.ActivateWindow,
                            target.Bounds.X + Math.Min(100, target.Bounds.Width / 2),
                            target.Bounds.Y + Math.Min(24, target.Bounds.Height / 2),
                            decision.Label));
                        await Task.Delay(680, cancellationToken);
                        executed = DesktopWindowTool.Activate(target);
                    }
                    else executed = DesktopAppTool.TryLaunch(decision.Value ?? "", out _);
                    detail = executed ? "软件切换请求已发出；等待新截图验证" :
                        "未能打开或激活目标软件";
                }
                if (executed) _service._agentActions++;
                var result = new AgentActionFeedback(decision, executed, detail);
                _conversation.AddToolResult(decision, result);
                if (executed) await Task.Delay(900, cancellationToken);
                return result;
            }
            if (_quickMode && decision.Kind == AgentToolKind.Wait)
            {
                int milliseconds = int.Parse(decision.Value!);
                await Task.Delay(milliseconds, cancellationToken);
                _service._agentActions++;
                var result = new AgentActionFeedback(decision, true,
                    $"已等待 {milliseconds} ms；下一帧截图将显示最新桌面");
                _conversation.AddToolResult(decision, result);
                return result;
            }
            using var activity = new WindowsActivity();
            bool browserWindow = frame.Window.ProcessName.Equals("chrome",
                StringComparison.OrdinalIgnoreCase) ||
                frame.Window.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase);
            if (decision.Kind == AgentToolKind.Navigate && (!_quickMode || !browserWindow))
            {
                var blocked = new AgentActionFeedback(decision, false,
                    "仅快捷 Agent 可在 Chrome 或 Edge 地址栏访问网页");
                _conversation.AddToolResult(decision, blocked);
                return blocked;
            }
            if (_quickMode && decision.Kind is AgentToolKind.TypeText or AgentToolKind.PressKeys)
            {
                bool sameForeground = activity.Foreground() is { } current &&
                    current.Handle == frame.Window.Handle &&
                    current.ProcessId == frame.Window.ProcessId;
                bool password = UiAutomationReader.FocusedIsPassword();
                bool secret = decision.Kind == AgentToolKind.TypeText &&
                    PrivacyFilter.ContainsSecret(decision.Value ?? "");
                bool unauthorizedSend = AgentTaskPermissions.IsMessagingApp(frame.Window) &&
                    !AgentTaskPermissions.MaySendMessage(_goal) && _typedInMessageApp &&
                    decision.Kind == AgentToolKind.PressKeys &&
                    decision.Value is "ENTER" or "CTRL+ENTER";
                bool executed = sameForeground && !password && !secret &&
                    !unauthorizedSend &&
                    (decision.Kind == AgentToolKind.TypeText
                        ? AgentKeyboardTool.TypeText(decision.Value ?? "")
                        : AgentKeyboardTool.PressKeys(decision.Value ?? ""));
                if (executed) _service._agentActions++;
                if (executed && AgentTaskPermissions.IsMessagingApp(frame.Window) &&
                    (decision.Kind == AgentToolKind.TypeText ||
                     decision.Kind == AgentToolKind.PressKeys && decision.Value == "CTRL+V"))
                    _typedInMessageApp = true;
                var result = new AgentActionFeedback(decision, executed,
                    executed ? "键盘动作已发出；等待新截图验证" :
                    !sameForeground ? "前台窗口已改变，未输入" :
                    password ? "当前焦点是密码输入控件" :
                    unauthorizedSend ? "任务没有明确要求发送消息，已拦截回车" :
                    secret ? "文本包含敏感凭据，未输入" : "Windows 未接受键盘动作");
                _conversation.AddToolResult(decision, result);
                if (executed) await Task.Delay(450, cancellationToken);
                return result;
            }
            if (_quickMode && AgentTaskPermissions.IsMessagingApp(frame.Window) &&
                !AgentTaskPermissions.MaySendMessage(_goal) &&
                AgentTaskPermissions.IsSendControl(frame, decision))
            {
                var blocked = new AgentActionFeedback(decision, false,
                    "任务没有明确要求发送消息，已拦截发送按钮");
                _conversation.AddToolResult(decision, blocked);
                return blocked;
            }
            bool allowed = _quickMode
                ? AgentMouseTool.IsAllowedDesktop(frame, decision, activity.Foreground(),
                    out string reason)
                : AgentMouseTool.IsAllowed(frame, decision, activity.Foreground(),
                    out reason);
            if (!allowed)
            {
                var blocked = new AgentActionFeedback(decision, false, reason);
                _conversation.AddToolResult(decision, blocked);
                return blocked;
            }
            _service.AgentVisual?.Invoke(new AgentActionVisual(true, decision.Kind,
                decision.X, decision.Y, decision.Label));
            try
            {
                await Task.Delay(_quickMode ? 680 : 180, cancellationToken);
                using var freshActivity = new WindowsActivity();
                bool stillAllowed = _quickMode
                    ? AgentMouseTool.IsAllowedDesktop(frame, decision,
                        freshActivity.Foreground(), out string latestReason)
                    : AgentMouseTool.IsAllowed(frame, decision,
                        freshActivity.Foreground(), out latestReason);
                bool clicked = false;
                if (stillAllowed)
                {
                    if (decision.Kind == AgentToolKind.Drag)
                        _service.AgentVisual?.Invoke(new AgentActionVisual(true,
                            AgentToolKind.Drag, decision.TargetX, decision.TargetY,
                            decision.Label));
                    clicked = decision.Kind switch
                    {
                        AgentToolKind.Scroll => AgentMouseTool.ExecuteScroll(decision.X,
                            decision.Y, int.Parse(decision.Value!), _quickMode),
                        AgentToolKind.Drag => AgentMouseTool.ExecuteDrag(decision.X,
                            decision.Y, decision.TargetX, decision.TargetY, _quickMode,
                            cancellationToken),
                        _ => AgentMouseTool.Execute(
                            decision.Kind == AgentToolKind.Navigate
                                ? decision with { Kind = AgentToolKind.Click } : decision,
                            preserveUserCursor: _quickMode)
                    };
                }
                bool executed = clicked;
                if (clicked && decision.Kind == AgentToolKind.Navigate)
                {
                    using var beforeTyping = new WindowsActivity();
                    WindowInfo? focused = beforeTyping.Foreground();
                    executed = focused?.Handle == frame.Window.Handle &&
                        focused.ProcessId == frame.Window.ProcessId &&
                        AgentKeyboardTool.Navigate(decision.Value ?? "");
                }
                if (executed) _service._agentActions++;
                var result = new AgentActionFeedback(decision, executed,
                    executed ? (decision.Kind == AgentToolKind.Navigate
                        ? "已在浏览器地址栏输入网址；等待新截图验证页面" :
                        "鼠标动作已发出；结果待下一帧截图验证") :
                    !stillAllowed ? latestReason : "Windows 未接受鼠标或键盘动作");
                _conversation.AddToolResult(decision, result);
                await Task.Delay(decision.Kind == AgentToolKind.Navigate ? 1600 : 650,
                    cancellationToken);
                return result;
            }
            finally
            {
                if (!_quickMode)
                {
                    _service.AgentVisual?.Invoke(new AgentActionVisual(false, decision.Kind,
                        decision.X, decision.Y, decision.Label));
                    await Task.Delay(300, CancellationToken.None);
                }
            }
        }
    }
}

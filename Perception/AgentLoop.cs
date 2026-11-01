namespace LingDongDao.Perception;

public enum AgentToolKind { Click, RightClick, Finish }

public sealed record AgentToolDecision(string CallId, AgentToolKind Kind, int X, int Y,
    string Label, bool Success, string Summary, string RawResponse);

public sealed record AgentLoopFrame(WindowInfo Window, PixelRect Bounds, byte[] Jpeg,
    CloudEventPackage Package, DesktopObservation Observation);

public sealed record AgentActionVisual(bool Visible, AgentToolKind Kind, int X, int Y,
    string Label);

public sealed record AgentActionFeedback(AgentToolDecision Decision, bool Executed,
    string Detail);

public sealed record AgentLoopOutcome(bool Success, int Actions, int CloudCalls,
    string Status, string Summary);

public interface IAgentLoopEnvironment
{
    Task<AgentLoopFrame> CaptureAsync(CancellationToken cancellationToken);
    Task<AgentToolDecision> DecideAsync(AgentLoopFrame frame,
        IReadOnlyList<AgentActionFeedback> history, CancellationToken cancellationToken);
    Task<AgentActionFeedback> ExecuteAsync(AgentLoopFrame frame, AgentToolDecision decision,
        CancellationToken cancellationToken);
}

public sealed class AgentLoopEngine
{
    public async Task<AgentLoopOutcome> RunAsync(IAgentLoopEnvironment environment,
        int maxActions, Action<string>? status, CancellationToken cancellationToken)
    {
        if (maxActions is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxActions));
        var history = new List<AgentActionFeedback>();
        for (int round = 0; round <= maxActions; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status?.Invoke($"正在截取第 {round + 1} 轮画面");
            AgentLoopFrame frame = await environment.CaptureAsync(cancellationToken);
            status?.Invoke($"正在让云端判断第 {round + 1} 轮");
            AgentToolDecision decision = await environment.DecideAsync(frame, history, cancellationToken);
            if (decision.Kind == AgentToolKind.Finish)
                return new AgentLoopOutcome(decision.Success, history.Count, round + 1,
                    decision.Success ? "任务完成" : "模型无法继续", decision.Summary);
            if (round == maxActions)
                return new AgentLoopOutcome(false, history.Count, round + 1,
                    "达到操作步数上限", "最后一次截图已交给云端判断，但任务仍未完成");
            status?.Invoke($"第 {round + 1} 步：{decision.Label} ({decision.X}, {decision.Y})");
            AgentActionFeedback feedback = await environment.ExecuteAsync(frame, decision,
                cancellationToken);
            history.Add(feedback);
            // The next iteration captures the result of this exact action and returns it to the model.
        }
        throw new InvalidOperationException("Unreachable agent loop state");
    }
}

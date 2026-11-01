using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed class AgentConversation
{
    private readonly List<object> _messages =
    [
        new { role = "system", content =
            "You operate one Windows application for the user's explicit task. " +
            "Call exactly one tool each turn: click, right_click, or finish. " +
            "Coordinates must be absolute physical screen pixels inside the supplied screenshot bounds. " +
            "The image is the current screen after the previous tool result. Verify success visually before finish(success=true). " +
            "If the task cannot be completed with these mouse tools, call finish(success=false). " +
            "Do not guess hidden controls, click outside the task window, or claim success without visual evidence. " +
            "Use short Chinese labels and summaries." }
    ];

    public IReadOnlyList<object> Messages => _messages;

    public void AddScreenshot(string goal, AgentLoopFrame frame,
        IReadOnlyList<AgentActionFeedback> history)
    {
        string previous = history.Count == 0 ? "No action yet." :
            string.Join("; ", history.TakeLast(4).Select(item =>
                $"{item.Decision.Kind}({item.Decision.X},{item.Decision.Y}): {item.Detail}"));
        string prompt = $"用户任务：{goal}\n当前窗口：{frame.Window.ProcessName} / {frame.Window.Title}\n" +
            $"截图对应的屏幕范围：x={frame.Bounds.X}, y={frame.Bounds.Y}, " +
            $"width={frame.Bounds.Width}, height={frame.Bounds.Height}。\n上一轮工具结果：{previous}\n" +
            $"OCR：{JsonSerializer.Serialize(frame.Package.Ocr.Take(80))}\n" +
            $"控件：{JsonSerializer.Serialize(frame.Package.Gui.Take(80))}\n" +
            "请根据新截图判断任务是否已完成；若未完成，仅调用一个鼠标工具。";
        _messages.Add(new { role = "user", content = new object[]
        {
            new { type = "text", text = prompt },
            new { type = "image_url", image_url = new { url =
                "data:image/jpeg;base64," + Convert.ToBase64String(frame.Jpeg) } }
        } });
    }

    public void AddToolResult(AgentToolDecision decision, AgentActionFeedback feedback)
    {
        using JsonDocument assistant = JsonDocument.Parse(decision.RawResponse);
        _messages.Add(assistant.RootElement.Clone());
        _messages.Add(new { role = "tool", tool_call_id = decision.CallId,
            content = JsonSerializer.Serialize(new { executed = feedback.Executed,
                detail = feedback.Detail, verification = "See the next user screenshot" }) });
    }
}

public sealed partial class ConfigurableCloudAgent
{
    private static readonly object[] AgentTools =
    [
        new { type = "function", function = new { name = "click",
            description = "Left click at an absolute screen coordinate inside the task window.",
            parameters = new { type = "object", properties = new
            {
                x = new { type = "integer" }, y = new { type = "integer" },
                label = new { type = "string", description = "Short Chinese text shown by the click marker" }
            }, required = new[] { "x", "y", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "right_click",
            description = "Right click at an absolute screen coordinate inside the task window.",
            parameters = new { type = "object", properties = new
            {
                x = new { type = "integer" }, y = new { type = "integer" },
                label = new { type = "string", description = "Short Chinese text shown by the click marker" }
            }, required = new[] { "x", "y", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "finish",
            description = "Stop the loop after verifying success, or when no safe next mouse action exists.",
            parameters = new { type = "object", properties = new
            {
                success = new { type = "boolean" },
                summary = new { type = "string", description = "Short Chinese explanation of observed result" }
            }, required = new[] { "success", "summary" }, additionalProperties = false } } }
    ];

    public async Task<AgentToolDecision> NextAgentToolAsync(AgentConversation conversation,
        AgentLoopFrame frame, string goal, IReadOnlyList<AgentActionFeedback> history,
        CancellationToken cancellationToken)
    {
        string? apiKey = ResolveKey();
        if (!Available || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Cloud agent is not configured");
        conversation.AddScreenshot(goal, frame, history);
        var body = new Dictionary<string, object>
        {
            ["model"] = _model!, ["messages"] = conversation.Messages,
            ["tools"] = AgentTools, ["tool_choice"] = "required", ["max_tokens"] = 450
        };
        if (Uri.TryCreate(_endpoint, UriKind.Absolute, out Uri? uri) &&
            uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new { type = "disabled" };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return ParseAgentToolResponse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public static AgentToolDecision ParseAgentToolResponse(string responseJson)
    {
        using JsonDocument envelope = JsonDocument.Parse(responseJson);
        JsonElement message = envelope.RootElement.GetProperty("choices")[0].GetProperty("message");
        if (!message.TryGetProperty("tool_calls", out JsonElement calls) ||
            calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() != 1)
            throw new InvalidDataException("Model must call exactly one agent tool");
        JsonElement call = calls[0];
        string id = call.GetProperty("id").GetString() ?? "";
        string name = call.GetProperty("function").GetProperty("name").GetString() ?? "";
        string arguments = call.GetProperty("function").GetProperty("arguments").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Tool call ID is missing");
        using JsonDocument parsed = JsonDocument.Parse(arguments);
        JsonElement input = parsed.RootElement;
        string raw = message.GetRawText();
        if (name == "finish")
        {
            bool success = input.GetProperty("success").GetBoolean();
            string summary = (input.GetProperty("summary").GetString() ?? "").Trim();
            if (summary.Length > 400) summary = summary[..400];
            return new AgentToolDecision(id, AgentToolKind.Finish, 0, 0,
                "完成", success, summary, raw);
        }
        AgentToolKind kind = name switch
        {
            "click" => AgentToolKind.Click,
            "right_click" => AgentToolKind.RightClick,
            _ => throw new InvalidDataException("Unknown agent tool: " + name)
        };
        int x = input.GetProperty("x").GetInt32();
        int y = input.GetProperty("y").GetInt32();
        string label = (input.GetProperty("label").GetString() ?? "").Trim();
        if (label.Length > 48) label = label[..48];
        if (string.IsNullOrWhiteSpace(label)) label = kind == AgentToolKind.Click ? "左键点击" : "右键点击";
        return new AgentToolDecision(id, kind, x, y, label, false, "", raw);
    }
}

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed class AgentConversation
{
    private readonly List<object> _messages;
    private readonly bool _quickMode;

    public AgentConversation(bool quickMode = false)
    {
        _quickMode = quickMode;
        string tools = quickMode
            ? "You may use open_browser (chrome or edge) to open a browser, then navigate to an HTTP(S) URL using the visible address bar. You may also click or right_click. "
            : "You operate one Windows application. Call only click, right_click, or finish. ";
        _messages = [new { role = "system", content =
            "You are a visible desktop agent carrying out the user's explicit task. " + tools +
            "Call exactly one tool each turn. Coordinates are absolute physical screen pixels inside the current screenshot bounds. " +
            "After every action you receive a new screenshot. Verify the requested result in that screenshot before finish(success=true). " +
            "If the task cannot be completed with available tools, call finish(success=false). " +
            "Do not guess hidden controls, click password fields, or claim success without visual evidence. " +
            "Use short Chinese labels and summaries." }];
    }

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
            (_quickMode ? "请根据新截图判断任务是否已完成；如需打开浏览器可调用 open_browser，再调用 navigate 访问网址。" :
                "请根据新截图判断任务是否已完成；若未完成，仅调用一个鼠标工具。");
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
    private static readonly object[] QuickAgentTools =
    [
        ..AgentTools,
        new { type = "function", function = new { name = "open_browser",
            description = "Open a new Chrome or Edge browser window. Use only for a user-requested browser task.",
            parameters = new { type = "object", properties = new
            {
                browser = new { type = "string", @enum = new[] { "chrome", "edge" } },
                label = new { type = "string", description = "Short Chinese action label" }
            }, required = new[] { "browser", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "navigate",
            description = "In the foreground Chrome or Edge window, move the visible agent cursor to the address bar, click, then navigate to an HTTP(S) URL.",
            parameters = new { type = "object", properties = new
            {
                url = new { type = "string", description = "Full HTTP(S) URL requested by the user" },
                x = new { type = "integer", description = "Address bar screen X coordinate" },
                y = new { type = "integer", description = "Address bar screen Y coordinate" },
                label = new { type = "string", description = "Short Chinese action label" }
            }, required = new[] { "url", "x", "y", "label" }, additionalProperties = false } } }
    ];

    public async Task<AgentToolDecision> NextAgentToolAsync(AgentConversation conversation,
        AgentLoopFrame frame, string goal, IReadOnlyList<AgentActionFeedback> history,
        CancellationToken cancellationToken, bool quickMode = false)
    {
        string? apiKey = ResolveKey();
        if (!Available || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Cloud agent is not configured");
        conversation.AddScreenshot(goal, frame, history);
        var body = new Dictionary<string, object>
        {
            ["model"] = _model!, ["messages"] = conversation.Messages,
            ["tools"] = quickMode ? QuickAgentTools : AgentTools,
            ["tool_choice"] = "required", ["max_tokens"] = 450
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
        if (name == "open_browser")
        {
            string browser = (input.GetProperty("browser").GetString() ?? "").Trim().ToLowerInvariant();
            if (browser is not ("chrome" or "edge"))
                throw new InvalidDataException("Unsupported browser");
            string browserLabel = (input.GetProperty("label").GetString() ?? "打开浏览器").Trim();
            if (browserLabel.Length > 48) browserLabel = browserLabel[..48];
            return new AgentToolDecision(id, AgentToolKind.OpenBrowser, 0, 0, browserLabel,
                false, "", raw, browser);
        }
        if (name == "navigate")
        {
            string requestedUrl = input.GetProperty("url").GetString() ?? "";
            if (!AgentBrowserTool.TryNormalizeUrl(requestedUrl, out string url))
                throw new InvalidDataException("Unsupported browser URL");
            int navigateX = input.GetProperty("x").GetInt32();
            int navigateY = input.GetProperty("y").GetInt32();
            string navigateLabel = (input.GetProperty("label").GetString() ?? "访问网页").Trim();
            if (navigateLabel.Length > 48) navigateLabel = navigateLabel[..48];
            return new AgentToolDecision(id, AgentToolKind.Navigate, navigateX, navigateY,
                navigateLabel,
                false, "", raw, url);
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

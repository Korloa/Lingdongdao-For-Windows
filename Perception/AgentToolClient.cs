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
            ? "You operate the entire Windows desktop, including taskbar, Start menu, all monitors, and any app. " +
              "Use mouse, keyboard, scroll, drag, wait, and browser tools as needed. A window may change after each action. " +
              "Prefer open_app for installed programs and activate_window for an already visible window. " +
              "Use WIN, type_text, and ENTER for apps not found by open_app. " +
              "Only send messages or submit forms when the user's task explicitly asks you to do so. "
            : "You operate one Windows application. Call only click, right_click, or finish. ";
        _messages = [new { role = "system", content =
            "You are a visible desktop agent carrying out the user's explicit task. " + tools +
            "Call exactly one tool each turn. Coordinates are absolute physical screen pixels inside the current screenshot bounds. " +
            "After every action you receive a new screenshot. Verify the requested result in that screenshot before finish(success=true). " +
            "If the task cannot be completed with available tools, call finish(success=false). " +
            "Do not guess hidden controls, click password fields, type secrets, or claim success without visual evidence. " +
            "Use short Chinese labels and summaries." }];
    }

    public IReadOnlyList<object> Messages => _messages;

    public void AddScreenshot(string goal, AgentLoopFrame frame,
        IReadOnlyList<AgentActionFeedback> history)
    {
        // Keep the latest two action cycles; older outcomes remain in the compact history line.
        while (_messages.Count > 7) _messages.RemoveRange(1, 3);
        string previous = history.Count == 0 ? "No action yet." :
            string.Join("; ", history.TakeLast(4).Select(item =>
                $"{item.Decision.Kind}({item.Decision.X},{item.Decision.Y}): {item.Detail}"));
        string windows = frame.VisibleWindows is null ? "" :
            "可见窗口：" + JsonSerializer.Serialize(frame.VisibleWindows.Take(25).Select(item => new
            { item.Handle, item.ProcessName, item.Title, item.Bounds })) + "\n";
        string prompt = $"用户任务：{goal}\n当前窗口：{frame.Window.ProcessName} / {frame.Window.Title}\n" +
            windows +
            $"截图对应的屏幕范围：x={frame.Bounds.X}, y={frame.Bounds.Y}, " +
            $"width={frame.Bounds.Width}, height={frame.Bounds.Height}。\n上一轮工具结果：{previous}\n" +
            $"OCR：{JsonSerializer.Serialize(frame.Package.Ocr.Take(80))}\n" +
            $"控件：{JsonSerializer.Serialize(frame.Package.Gui.Take(80))}\n" +
            (_quickMode ? "这是整个虚拟桌面的截图（可能包含多个显示器），坐标为真实屏幕像素；请根据新截图判断任务是否已完成。" :
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
            }, required = new[] { "url", "x", "y", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "double_click",
            description = "Double left click anywhere on the visible desktop.",
            parameters = new { type = "object", properties = new
            { x = new { type = "integer" }, y = new { type = "integer" },
              label = new { type = "string" } },
              required = new[] { "x", "y", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "scroll",
            description = "Scroll at a desktop coordinate. Positive delta scrolls up, negative scrolls down. Use wheel units, normally 120 or -120.",
            parameters = new { type = "object", properties = new
            { x = new { type = "integer" }, y = new { type = "integer" },
              delta = new { type = "integer" }, label = new { type = "string" } },
              required = new[] { "x", "y", "delta", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "drag",
            description = "Drag with the left mouse button from one desktop point to another.",
            parameters = new { type = "object", properties = new
            { x = new { type = "integer" }, y = new { type = "integer" },
              target_x = new { type = "integer" }, target_y = new { type = "integer" },
              label = new { type = "string" } },
              required = new[] { "x", "y", "target_x", "target_y", "label" },
              additionalProperties = false } } },
        new { type = "function", function = new { name = "type_text",
            description = "Type Unicode text into the currently focused desktop control. Use only after focusing and checking the target. Never type passwords or secrets.",
            parameters = new { type = "object", properties = new
            { text = new { type = "string" }, label = new { type = "string" } },
              required = new[] { "text", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "press_keys",
            description = "Press one approved Windows key or shortcut. Examples WIN, ENTER, ESC, TAB, ALT+TAB, CTRL+A, CTRL+L, WIN+E.",
            parameters = new { type = "object", properties = new
            { keys = new { type = "string" }, label = new { type = "string" } },
              required = new[] { "keys", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "wait",
            description = "Wait 200 to 5000 milliseconds for an app or page to load, then inspect a new screenshot.",
            parameters = new { type = "object", properties = new
            { milliseconds = new { type = "integer" }, label = new { type = "string" } },
              required = new[] { "milliseconds", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "open_app",
            description = "Launch an installed Windows app by its Start menu shortcut name, such as QQ, 微信, 记事本, or 资源管理器. No shell commands or file paths.",
            parameters = new { type = "object", properties = new
            { name = new { type = "string" }, label = new { type = "string" } },
              required = new[] { "name", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "activate_window",
            description = "Bring an already visible desktop window to the foreground using its exact handle from the visible-window list.",
            parameters = new { type = "object", properties = new
            { handle = new { type = "integer" }, label = new { type = "string" } },
              required = new[] { "handle", "label" }, additionalProperties = false } } }
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
            ["tool_choice"] = "required", ["max_tokens"] = quickMode ? 1200 : 450
        };
        if (Uri.TryCreate(_endpoint, UriKind.Absolute, out Uri? uri) &&
            uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new { type = "disabled" };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return ParseAgentToolResponse(await response.Content.ReadAsStringAsync(cancellationToken), quickMode);
    }

    public static AgentToolDecision ParseAgentToolResponse(string responseJson, bool quickMode = false)
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
        if (!quickMode && name is not ("click" or "right_click" or "finish"))
            throw new InvalidDataException("Tool is unavailable in the single-window agent");
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
        if (name is "open_app" or "activate_window")
        {
            string value = name == "open_app" ?
                (input.GetProperty("name").GetString() ?? "").Trim() :
                input.GetProperty("handle").GetInt64().ToString();
            if (name == "open_app" && (value.Length is < 2 or > 80 ||
                value.Any(char.IsControl)))
                throw new InvalidDataException("Invalid app name");
            if (name == "activate_window" &&
                (!long.TryParse(value, out long handle) || handle <= 0))
                throw new InvalidDataException("Invalid window handle");
            string caption = (input.GetProperty("label").GetString() ?? name).Trim();
            if (caption.Length > 48) caption = caption[..48];
            return new AgentToolDecision(id, name == "open_app" ? AgentToolKind.OpenApp :
                AgentToolKind.ActivateWindow, 0, 0, caption, false, "", raw, value);
        }
        if (name is "type_text" or "press_keys" or "wait")
        {
            AgentToolKind toolKind = name switch
            { "type_text" => AgentToolKind.TypeText, "press_keys" => AgentToolKind.PressKeys,
              _ => AgentToolKind.Wait };
            string value = name switch
            {
                "type_text" => input.GetProperty("text").GetString() ?? "",
                "press_keys" => input.GetProperty("keys").GetString() ?? "",
                _ => input.GetProperty("milliseconds").GetInt32().ToString()
            };
            if (name == "type_text" && (value.Length is < 1 or > 1000 ||
                value.Any(character => char.IsControl(character))))
                throw new InvalidDataException("Invalid text length or control character");
            if (name == "press_keys" && !AgentKeyboardTool.IsApprovedShortcut(value))
                throw new InvalidDataException("Unsupported keyboard shortcut");
            if (name == "wait" && (!int.TryParse(value, out int delay) || delay is < 200 or > 5000))
                throw new InvalidDataException("Invalid wait duration");
            string caption = (input.GetProperty("label").GetString() ?? name).Trim();
            if (caption.Length > 48) caption = caption[..48];
            return new AgentToolDecision(id, toolKind, 0, 0, caption, false, "", raw, value);
        }
        AgentToolKind kind = name switch
        {
            "click" => AgentToolKind.Click,
            "right_click" => AgentToolKind.RightClick,
            "double_click" => AgentToolKind.DoubleClick,
            "scroll" => AgentToolKind.Scroll,
            "drag" => AgentToolKind.Drag,
            _ => throw new InvalidDataException("Unknown agent tool: " + name)
        };
        int x = input.GetProperty("x").GetInt32();
        int y = input.GetProperty("y").GetInt32();
        string label = (input.GetProperty("label").GetString() ?? "").Trim();
        if (label.Length > 48) label = label[..48];
        if (string.IsNullOrWhiteSpace(label)) label = kind == AgentToolKind.Click ? "左键点击" : "右键点击";
        string? valueForAction = null;
        int targetX = 0, targetY = 0;
        if (kind == AgentToolKind.Scroll)
        {
            int delta = input.GetProperty("delta").GetInt32();
            if (delta is < -1200 or > 1200 || delta == 0)
                throw new InvalidDataException("Invalid scroll amount");
            valueForAction = delta.ToString();
        }
        if (kind == AgentToolKind.Drag)
        {
            targetX = input.GetProperty("target_x").GetInt32();
            targetY = input.GetProperty("target_y").GetInt32();
        }
        return new AgentToolDecision(id, kind, x, y, label, false, "", raw,
            valueForAction, targetX, targetY);
    }
}

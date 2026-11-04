using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed class AgentConversation
{
    private readonly List<object> _messages;
    private readonly bool _quickMode;
    private readonly string _context;

    public AgentConversation(bool quickMode = false, string? context = null)
    {
        _quickMode = quickMode;
        _context = context ?? "";
        string tools = quickMode
            ? "You operate the entire Windows desktop, including taskbar, Start menu, all monitors, and any app. " +
              "Use mouse, keyboard, scroll, drag, wait, and browser tools as needed. A window may change after each action. " +
              "Prefer open_app for installed programs and activate_window for an already visible window. " +
              "Use WIN, type_text, and ENTER for apps not found by open_app. " +
              "For a requested folder or source file, prefer create_folder and create_text_file with an absolute path. " +
              "Create the parent folder BEFORE create_text_file. If the user named index.html or another filename, keep that exact filename. " +
              "Use open_created_file to show a file you created in VS Code, Chrome, Edge, or Explorer; do not use navigate for file:// URLs. " +
              "VS Code app names include 'VS Code' and 'Visual Studio Code'. " +
              "If the user asked to work in VS Code, open VS Code first, create_text_file, then open_created_file with app=vscode before opening the HTML in a browser. " +
              "If the user asked to work in File Explorer, create the file/folder then use open_created_file with app=explorer so the result is visible there. " +
              "For an HTML browser task, finish(success=true) only after open_created_file with app=chrome or edge succeeded for the NEW file, and a fresh screenshot confirms it. " +
              "Do not create the same file twice after a prior create_text_file success; use its existing path with open_created_file. " +
              "For GUI-only creation, Ctrl+N, Ctrl+Shift+S and Ctrl+Shift+N are supported; type_text accepts multiline code. " +
              "If no destination was specified, use the user's Desktop folder and say where you saved the result. " +
              "When a visual explanation or animated demonstration would help the user's task, use show_visualization with an HTML fragment, CSS, and JavaScript. " +
              "The visualization appears in a centered desktop panel; after showing it you receive a fresh screenshot to verify. " +
              "If the user explicitly asks for visualization or animation, do not finish successfully before showing it. " +
              "Use the built-in visual.animate, visual.stagger, visual.count, and visual.sleep helpers if useful. " +
              "Keep visualization self-contained, readable, and focused on the user's request. No external libraries, URLs, forms, or secrets. " +
              "The panel stays visible until the user closes it or you call close_visualization. " +
              "Only send messages or submit forms when the user's task explicitly asks you to do so. " +
              "For a delay longer than 5 seconds, use schedule_wake once and end this run; the app will resume you at the due time with a fresh screenshot. "
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
        string prompt = $"用户任务：{goal}\n" +
            (_quickMode ? $"可用文件位置：{AgentFileTool.WorkspaceHints()}\n" : "") +
            (string.IsNullOrEmpty(_context) ? "" : $"已验证的本地经验或唤醒上下文：{_context}\n经验仅供参考，实际操作前仍需查看当前截图。\n") +
            $"当前窗口：{frame.Window.ProcessName} / {frame.Window.Title}\n" +
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
            description = "Type Unicode text, including CR/LF and tabs, into the focused desktop control. For a new code file prefer create_text_file. Never type passwords or secrets.",
            parameters = new { type = "object", properties = new
            { text = new { type = "string" }, label = new { type = "string" } },
              required = new[] { "text", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "press_keys",
            description = "Press one shortcut from: WIN, WIN+E, WIN+D, ENTER, ESC, TAB, SHIFT+TAB, ALT+TAB, ALT+F4, ALT+D, ALT+LEFT, ALT+RIGHT, CTRL+A/L/C/V/S/Z/F/W/N/O/P/T/TAB/ENTER, CTRL+SHIFT+S/N/P, BACKSPACE, DELETE, arrows, SPACE, F2, F5. Do not invent shortcuts.",
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
               required = new[] { "handle", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "schedule_wake",
            description = "Suspend this task and resume after 10 to 86400 seconds. Use for watching a video or other long waits; do not poll screenshots. The current run ends after scheduling.",
            parameters = new { type = "object", properties = new
            { seconds = new { type = "integer" },
              context = new { type = "string", description = "What to inspect when the timer fires, max 400 chars. Never include secrets." },
              label = new { type = "string" } },
              required = new[] { "seconds", "context", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "create_folder",
            description = "Create a new folder at an absolute Windows path, only when the user requested file creation. Never modifies an existing folder. Use Desktop if the location is unspecified.",
            parameters = new { type = "object", properties = new
            { path = new { type = "string" }, label = new { type = "string" } },
              required = new[] { "path", "label" }, additionalProperties = false } } },
        new { type = "function", function = new { name = "create_text_file",
            description = "Create a NEW UTF-8 .html/.htm/.css/.js/.json/.txt/.md file with complete content at an absolute Windows path. Parent folder must exist. Existing files are never overwritten. Prefer this for HTML/CSS/JS code over typing in an editor.",
            parameters = new { type = "object", properties = new
            { path = new { type = "string" }, content = new { type = "string" },
              label = new { type = "string" } },
              required = new[] { "path", "content", "label" },
              additionalProperties = false } } },
        new { type = "function", function = new { name = "open_created_file",
            description = "Open a file or folder created earlier in THIS agent run. Use vscode for source preview, chrome/edge for local HTML, explorer to show a folder/file. This cannot open arbitrary preexisting paths.",
            parameters = new { type = "object", properties = new
            { path = new { type = "string" },
              app = new { type = "string", @enum = new[] { "vscode", "chrome", "edge", "explorer" } },
              label = new { type = "string" } },
              required = new[] { "path", "app", "label" },
              additionalProperties = false } } },
        new { type = "function", function = new { name = "show_visualization",
            description = "Display a self-contained HTML/CSS/JS animation in a centered borderless desktop panel. Use for a visual explanation or demonstration. No external assets or network access. The next screenshot shows the panel.",
            parameters = new { type = "object", properties = new
            {
                title = new { type = "string", description = "Short Chinese panel title" },
                html = new { type = "string", description = "HTML body fragment only; no script/style/link/iframe" },
                css = new { type = "string", description = "Inline CSS and keyframe animations" },
                javascript = new { type = "string", description = "Inline JavaScript animation; visual helpers available" },
                width = new { type = "integer", description = "Panel width in DIP, 480-1200; default 820" },
                height = new { type = "integer", description = "Panel height in DIP, 320-760; default 520" },
                duration_seconds = new { type = "integer", description = "Auto close after 1-300 seconds, or 0 to keep open" },
                label = new { type = "string", description = "Short Chinese action label" }
            }, required = new[] { "title", "html", "css", "javascript", "label" },
               additionalProperties = false } } },
        new { type = "function", function = new { name = "close_visualization",
            description = "Close the currently displayed animation panel, when the demonstration is finished.",
            parameters = new { type = "object", properties = new
            { label = new { type = "string" } }, required = new[] { "label" },
            additionalProperties = false } } }
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
            ["tool_choice"] = "required", ["max_tokens"] = quickMode ? 8192 : 450
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
        string raw = message.GetRawText();
        try
        {
        using JsonDocument parsed = JsonDocument.Parse(arguments);
        JsonElement input = parsed.RootElement;
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
        if (name == "schedule_wake")
        {
            int seconds = input.GetProperty("seconds").GetInt32();
            string context = (input.GetProperty("context").GetString() ?? "").Trim();
            if (seconds is < 10 or > 86400 || context.Length is < 3 or > 400 ||
                PrivacyFilter.ContainsSecret(context))
                throw new InvalidDataException("Invalid wake request");
            string wakeLabel = (input.GetProperty("label").GetString() ?? "定时唤醒").Trim();
            if (wakeLabel.Length > 48) wakeLabel = wakeLabel[..48];
            return new AgentToolDecision(id, AgentToolKind.ScheduleWake, 0, 0,
                wakeLabel, false, "", raw, $"{seconds}|{context}");
        }
        if (name == "show_visualization")
        {
            var request = new AgentVisualizationRequest(
                (input.GetProperty("title").GetString() ?? "").Trim(),
                input.GetProperty("html").GetString() ?? "",
                input.GetProperty("css").GetString() ?? "",
                input.GetProperty("javascript").GetString() ?? "",
                input.TryGetProperty("width", out JsonElement width) ? width.GetInt32() : 820,
                input.TryGetProperty("height", out JsonElement height) ? height.GetInt32() : 520,
                input.TryGetProperty("duration_seconds", out JsonElement duration) ?
                    duration.GetInt32() : 0);
            if (!AgentVisualizationDocument.TryValidate(request, out string reason))
                throw new InvalidDataException(reason);
            string visualLabel = (input.GetProperty("label").GetString() ?? "展示动画").Trim();
            if (visualLabel.Length > 48) visualLabel = visualLabel[..48];
            return new AgentToolDecision(id, AgentToolKind.ShowVisualization, 0, 0,
                visualLabel, false, "", raw, JsonSerializer.Serialize(request));
        }
        if (name == "close_visualization")
        {
            string closeLabel = (input.GetProperty("label").GetString() ?? "关闭动画").Trim();
            if (closeLabel.Length > 48) closeLabel = closeLabel[..48];
            return new AgentToolDecision(id, AgentToolKind.CloseVisualization, 0, 0,
                closeLabel, false, "", raw);
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
            JsonElement handleInput = name == "activate_window" ?
                input.GetProperty("handle") : default;
            string value = name == "open_app" ?
                (input.GetProperty("name").GetString() ?? "").Trim() :
                handleInput.ValueKind == JsonValueKind.String ?
                    handleInput.GetString() ?? "" : handleInput.GetInt64().ToString();
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
        if (name is "create_folder" or "create_text_file" or "open_created_file")
        {
            string path = (input.GetProperty("path").GetString() ?? "").Trim();
            if (!AgentFileTool.TryNormalize(path, out string fullPath, out string error))
                throw new InvalidDataException(error);
            string? content = name == "create_text_file" ?
                input.GetProperty("content").GetString() : null;
            string? app = name == "open_created_file" ?
                (input.GetProperty("app").GetString() ?? "").Trim().ToLowerInvariant() : null;
            if (name == "create_text_file" &&
                (content is null || content.Length is < 1 or > 20000))
                throw new InvalidDataException("Invalid file content length");
            if (name == "open_created_file" &&
                app is not ("vscode" or "chrome" or "edge" or "explorer"))
                throw new InvalidDataException("Unsupported file application");
            AgentToolKind fileKind = name switch
            {
                "create_folder" => AgentToolKind.CreateFolder,
                "create_text_file" => AgentToolKind.CreateTextFile,
                _ => AgentToolKind.OpenCreatedFile
            };
            string caption = (input.GetProperty("label").GetString() ?? name).Trim();
            if (caption.Length > 48) caption = caption[..48];
            return new AgentToolDecision(id, fileKind, 0, 0, caption, false, "", raw,
                JsonSerializer.Serialize(new AgentFileRequest(fullPath, content, app)));
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
            if (name == "type_text" && (value.Length is < 1 or > 20000 ||
                value.Any(character => char.IsControl(character) &&
                    character is not ('\r' or '\n' or '\t'))))
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
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or
            InvalidOperationException or FormatException or ArgumentOutOfRangeException or JsonException)
        {
            string detail = exception is InvalidDataException ? exception.Message :
                "Tool arguments are missing or invalid";
            return new AgentToolDecision(id, AgentToolKind.Rejected, 0, 0,
                "工具参数不支持", false, "", raw, detail);
        }
    }
}

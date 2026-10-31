using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingDongDao.Perception;

public sealed record CloudEventPackage(DateTimeOffset Time, string ProcessName, string WindowTitle,
    PixelRect ScreenshotBounds, byte[] CurrentJpeg, byte[]? BeforeJpeg,
    IReadOnlyList<OcrText> Ocr, IReadOnlyList<GuiElement> Gui,
    IReadOnlyList<ActivityEvent> Interactions, IReadOnlyList<string> Memory,
    LocalAssessment Assessment, IReadOnlyList<string> TriggerReasons);

public static class PrivacyFilter
{
    private static readonly Regex Secret = new(
        @"\b(?:sk-[A-Za-z0-9_-]{12,}|AKIA[A-Z0-9]{16}|Bearer\s+[A-Za-z0-9._~-]{12,}|(?:api[_ -]?key|secret|token)\s*[:=]\s*\S{12,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSensitive(WindowInfo? window, PerceptionOptions options) =>
        window is null || options.SensitiveProcesses.Any(name =>
            window.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)) ||
        options.SensitiveTitleWords.Any(word =>
            window.Title.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static bool IsSensitive(DesktopObservation observation, PerceptionOptions options) =>
        IsSensitive(observation.Window, options) ||
        Secret.IsMatch(observation.Window?.Title ?? "") ||
        observation.Ocr.Any(item => Secret.IsMatch(item.Text)) ||
        observation.GuiElements.Any(item => Secret.IsMatch(item.Name));

    public static CloudEventPackage? Build(DesktopObservation observation,
        LocalAssessment assessment, FrameRingBuffer frames, StateMemory memory,
        PerceptionOptions options)
    {
        if (!options.UploadConsent || !options.CloudEnabled || IsSensitive(observation, options) ||
            observation.MissingModules.Contains("uia") ||
            memory.RecentSummaries(observation.Time).Any(Secret.IsMatch))
            return null;
        CapturedFrame? current = frames.Find(observation.ScreenshotId);
        if (current is null || observation.Window is null) return null;
        PixelRect crop = observation.Window.Bounds.Intersect(current.Display.Bounds);
        if (crop.Width < 10 || crop.Height < 10) return null;
        var sensitive = observation.GuiElements.Where(item => item.IsPassword)
            .Select(item => item.Bounds).ToArray();
        byte[] screenshot = CropAndMask(current, crop, sensitive);
        CapturedFrame? before = frames.Before(observation.Time, current.Display.Id,
            TimeSpan.FromSeconds(12));
        byte[]? beforeJpeg = before is null || before.Foreground?.Handle != observation.Window.Handle ||
            sensitive.Length > 0 ? null : CropAndMask(before, crop, sensitive);
        var safeOcr = observation.Ocr.Where(item => !sensitive.Any(rect => rect.Intersects(item.Bounds)))
            .ToArray();
        var safeGui = observation.GuiElements.Select(item => item.IsPassword
            ? item with { Name = "[password]" } : item).ToArray();
        return new CloudEventPackage(observation.Time, observation.Window.ProcessName,
            observation.Window.Title, crop, screenshot, beforeJpeg, safeOcr, safeGui,
            observation.InteractionEvents, memory.RecentSummaries(observation.Time), assessment,
            observation.Change.Reasons);
    }

    private static byte[] CropAndMask(CapturedFrame frame, PixelRect crop,
        IReadOnlyList<PixelRect> sensitive)
    {
        using var sourceStream = new MemoryStream(frame.Jpeg);
        using var source = new Bitmap(sourceStream);
        using var bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImage(source, new Rectangle(0, 0, crop.Width, crop.Height),
                new Rectangle(crop.X - frame.Display.Bounds.X, crop.Y - frame.Display.Bounds.Y,
                    crop.Width, crop.Height), GraphicsUnit.Pixel);
            foreach (PixelRect rect in sensitive)
            {
                PixelRect masked = rect.Intersect(crop);
                if (masked.Width > 0 && masked.Height > 0)
                    graphics.FillRectangle(Brushes.Black,
                        masked.X - crop.X, masked.Y - crop.Y, masked.Width, masked.Height);
            }
        }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Jpeg);
        return output.ToArray();
    }
}

public sealed class MockCloudAgent : ICloudAgent
{
    public int CallCount { get; private set; }
    public CloudEventPackage? LastPackage { get; private set; }
    public Task<CloudDecision?> DecideAsync(CloudEventPackage package, CancellationToken cancellationToken)
    {
        CallCount++;
        LastPackage = package;
        return Task.FromResult<CloudDecision?>(new CloudDecision(false,
            "mock response", "", null));
    }
}

public sealed class ConfigurableCloudAgent : ICloudAgent, IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _endpoint;
    private readonly string? _model;
    private readonly string? _key;
    public ConfigurableCloudAgent(HttpMessageHandler? handler = null, string? endpoint = null,
        string? model = null, string? key = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(35);
        _endpoint = endpoint ?? Environment.GetEnvironmentVariable("LINGDONGDAO_AGENT_BASE_URL");
        _model = model ?? Environment.GetEnvironmentVariable("LINGDONGDAO_AGENT_MODEL");
        _key = key ?? Environment.GetEnvironmentVariable("LINGDONGDAO_AGENT_API_KEY");
    }
    public bool Available => Uri.TryCreate(_endpoint, UriKind.Absolute, out Uri? endpoint) &&
        (endpoint.Scheme == Uri.UriSchemeHttps ||
         endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback) &&
        !string.IsNullOrWhiteSpace(_model) && !string.IsNullOrWhiteSpace(_key);

    public async Task<CloudDecision?> DecideAsync(CloudEventPackage package,
        CancellationToken cancellationToken)
    {
        if (!Available) return null;
        var context = new
        {
            package.Time, package.ProcessName, package.WindowTitle,
            package.ScreenshotBounds, package.Ocr, package.Gui, package.Interactions,
            package.Memory, package.Assessment, package.TriggerReasons
        };
        var content = new List<object>
        {
            new { type = "text", text = JsonSerializer.Serialize(context) },
            new { type = "image_url", image_url = new { url =
                "data:image/jpeg;base64," + Convert.ToBase64String(package.CurrentJpeg) } }
        };
        if (package.BeforeJpeg is { Length: > 0 } before)
            content.Add(new { type = "image_url", image_url = new { url =
                "data:image/jpeg;base64," + Convert.ToBase64String(before) } });
        var body = new
        {
            model = _model,
            response_format = new { type = "json_object" },
            max_tokens = 350,
            messages = new object[]
            {
                new { role = "system", content =
                    "Analyze desktop evidence. Return JSON with should_intervene, reason, suggestion, recommended_action. " +
                    "Only suggest help for a clear user problem. Do not claim an inferred event is observed fact. " +
                    "Never request computer control or execute actions." },
                new { role = "user", content }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        string? json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using JsonDocument decision = JsonDocument.Parse(json);
        JsonElement root = decision.RootElement;
        bool intervene = root.TryGetProperty("should_intervene", out var property) &&
            property.ValueKind == JsonValueKind.True;
        return new CloudDecision(intervene,
            Read(root, "reason"), Read(root, "suggestion"), Read(root, "recommended_action"));
    }

    private static string Read(JsonElement root, string key) => root.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public void Dispose() => _http.Dispose();
}

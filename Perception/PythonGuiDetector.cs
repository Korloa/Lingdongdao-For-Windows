using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed class PythonGuiDetector : IGuiDetector, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _nextId;
    private int _workerPid;
    private bool _failed;
    public string Status { get; private set; } = "not started";
    public double WorkerMb
    {
        get
        {
            try
            {
                if (_workerPid == 0) return 0;
                using Process worker = Process.GetProcessById(_workerPid);
                return worker.WorkingSet64 / 1048576.0 +
                    (_process is { HasExited: false } wrapper && wrapper.Id != _workerPid
                        ? wrapper.WorkingSet64 / 1048576.0 : 0);
            }
            catch (Exception) { return 0; }
        }
    }
    public TimeSpan WorkerCpu
    {
        get
        {
            try
            {
                if (_workerPid == 0) return TimeSpan.Zero;
                using Process worker = Process.GetProcessById(_workerPid);
                return worker.TotalProcessorTime +
                    (_process is { HasExited: false } wrapper && wrapper.Id != _workerPid
                        ? wrapper.TotalProcessorTime : TimeSpan.Zero);
            }
            catch (Exception) { return TimeSpan.Zero; }
        }
    }

    private static string PythonPath => Environment.GetEnvironmentVariable("LINGDONGDAO_PYTHON") ??
        (File.Exists(Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe"))
            ? Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe") : "python");
    private static string WorkerPath => Path.Combine(AppContext.BaseDirectory,
        "perception_py", "gui_worker.py");
    private static string ModelPath => Environment.GetEnvironmentVariable("LINGDONGDAO_GUI_MODEL") ??
        Path.Combine(AppContext.BaseDirectory, "models", "gpa", "model.onnx");
    public bool Available => !_failed && File.Exists(WorkerPath) && File.Exists(ModelPath);

    public async Task<IReadOnlyList<GuiElement>> DetectAsync(byte[] image, PixelRect screenBounds,
        CancellationToken cancellationToken)
    {
        if (!Available) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            if (_process is null || _process.HasExited) return [];
            int id = Interlocked.Increment(ref _nextId);
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                { id, jpeg = Convert.ToBase64String(image) }).AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new IOException("GUI worker closed output");
            using JsonDocument result = JsonDocument.Parse(line);
            if (result.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString());
            if (result.RootElement.GetProperty("id").GetInt32() != id)
                throw new InvalidOperationException("GUI worker response mismatch");
            var elements = new List<GuiElement>();
            foreach (JsonElement item in result.RootElement.GetProperty("elements").EnumerateArray())
            {
                int[] box = item.GetProperty("box").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                if (box.Length != 4) continue;
                elements.Add(new GuiElement("", item.GetProperty("class").GetString() ?? "interactive_region",
                    new PixelRect(screenBounds.X + box[0], screenBounds.Y + box[1], box[2], box[3]),
                    true, false, "gpa_onnx", item.GetProperty("score").GetDouble()));
            }
            Status = $"ready; {elements.Count} regions";
            return elements;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.GetType().Name + ": " + exception.Message;
            StopWorker();
            _failed = true;
            return [];
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        var info = new ProcessStartInfo(PythonPath)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(WorkerPath);
        info.Environment["LINGDONGDAO_GUI_MODEL"] = ModelPath;
        info.Environment["OMP_NUM_THREADS"] = "2";
        _process = Process.Start(info) ?? throw new IOException("Could not start GUI worker");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
        if (line is null) throw new IOException("GUI worker failed to initialize");
        using JsonDocument ready = JsonDocument.Parse(line);
        if (!ready.RootElement.GetProperty("ready").GetBoolean())
            throw new IOException(ready.RootElement.GetProperty("error").GetString());
        _workerPid = ready.RootElement.GetProperty("pid").GetInt32();
        Status = "ready";
    }

    private void StopWorker()
    {
        if (_process is null) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception) { }
        _process.Dispose();
        _process = null;
        _workerPid = 0;
    }

    public void Dispose() { StopWorker(); _gate.Dispose(); }
}

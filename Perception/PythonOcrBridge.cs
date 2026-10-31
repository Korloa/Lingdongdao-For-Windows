using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed class PythonOcrBridge : IOcrEngine, IDisposable
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
            ? Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe")
            : "python");

    private static string WorkerPath => Path.Combine(AppContext.BaseDirectory,
        "perception_py", "ocr_worker.py");
    public bool Available => !_failed && File.Exists(WorkerPath);

    public async Task<IReadOnlyList<OcrText>> RecognizeAsync(byte[] image, PixelRect screenBounds,
        DateTimeOffset time, CancellationToken cancellationToken)
    {
        if (!Available) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            if (_process is null || _process.HasExited) return [];
            int id = Interlocked.Increment(ref _nextId);
            string request = JsonSerializer.Serialize(new { id, jpeg = Convert.ToBase64String(image) });
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new IOException("OCR worker closed output");
            using JsonDocument result = JsonDocument.Parse(line);
            if (result.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString());
            if (result.RootElement.GetProperty("id").GetInt32() != id)
                throw new InvalidOperationException("OCR worker response mismatch");
            var words = new List<OcrText>();
            foreach (JsonElement word in result.RootElement.GetProperty("words").EnumerateArray())
            {
                var points = word.GetProperty("points").EnumerateArray()
                    .Select(point => (X: point[0].GetInt32(), Y: point[1].GetInt32())).ToArray();
                if (points.Length != 4) continue;
                int x = points.Min(point => point.X), y = points.Min(point => point.Y);
                int right = points.Max(point => point.X), bottom = points.Max(point => point.Y);
                words.Add(new OcrText(word.GetProperty("text").GetString() ?? "",
                    new PixelRect(screenBounds.X + x, screenBounds.Y + y,
                        Math.Max(1, right - x), Math.Max(1, bottom - y)),
                    word.GetProperty("score").GetDouble(), time, null));
            }
            Status = $"ready; {words.Count} text regions";
            return words;
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
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(WorkerPath);
        info.Environment["OMP_NUM_THREADS"] = "2";
        _process = Process.Start(info) ?? throw new IOException("Could not start OCR worker");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
        if (line is null) throw new IOException("OCR worker failed to initialize");
        using JsonDocument ready = JsonDocument.Parse(line);
        if (!ready.RootElement.GetProperty("ready").GetBoolean())
            throw new IOException("OCR worker did not become ready");
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

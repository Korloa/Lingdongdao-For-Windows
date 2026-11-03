using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed class PythonOcrBridge : IOcrEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _pythonPath;
    private readonly string _workerPath;
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _firstResponseTimeout;
    private readonly TimeSpan _retryBase;
    private Process? _process;
    private int _nextId;
    private int _workerPid;
    private int _failures;
    private long _retryAtUtcTicks;
    private bool _freshWorker;
    public string Status { get; private set; } = "not started";

    public PythonOcrBridge(string? pythonPath = null, string? workerPath = null,
        TimeSpan? responseTimeout = null, TimeSpan? retryBase = null,
        TimeSpan? firstResponseTimeout = null)
    {
        _pythonPath = pythonPath ?? PythonRuntime.PythonPath;
        _workerPath = workerPath ?? Path.Combine(AppContext.BaseDirectory, "perception_py", "ocr_worker.py");
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(25);
        _firstResponseTimeout = firstResponseTimeout ?? TimeSpan.FromSeconds(60);
        _retryBase = retryBase ?? TimeSpan.FromSeconds(10);
    }
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

    public bool Available => File.Exists(_workerPath) &&
        DateTime.UtcNow.Ticks >= Interlocked.Read(ref _retryAtUtcTicks);

    public async Task<IReadOnlyList<OcrText>> RecognizeAsync(byte[] image, PixelRect screenBounds,
        DateTimeOffset time, CancellationToken cancellationToken)
    {
        if (!Available) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Available) return [];
            await EnsureStartedAsync(cancellationToken);
            if (_process is null || _process.HasExited) return [];
            int id = Interlocked.Increment(ref _nextId);
            string request = JsonSerializer.Serialize(new { id, jpeg = Convert.ToBase64String(image) });
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            TimeSpan responseLimit = _freshWorker ? _firstResponseTimeout : _responseTimeout;
            timeout.CancelAfter(responseLimit);
            string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new IOException("OCR worker closed output");
            using JsonDocument result = JsonDocument.Parse(line);
            if (result.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString());
            int responseId = result.RootElement.GetProperty("id").GetInt32();
            if (responseId != id)
                throw new InvalidOperationException(
                    $"OCR worker response mismatch (expected {id}, got {responseId})");
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
            _freshWorker = false;
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Exchange(ref _retryAtUtcTicks, 0);
            return words;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan limit = _freshWorker ? _firstResponseTimeout : _responseTimeout;
            StopWorker();
            MarkFailure($"OCR worker timed out after {limit.TotalSeconds:0.#}s");
            return [];
        }
        catch (OperationCanceledException)
        {
            StopWorker();
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StopWorker();
            MarkFailure(exception.GetType().Name + ": " + exception.Message);
            return [];
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        var info = new ProcessStartInfo(_pythonPath)
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
        info.ArgumentList.Add(_workerPath);
        info.Environment["OMP_NUM_THREADS"] = "2";
        _process = Process.Start(info) ?? throw new IOException("Could not start OCR worker");
        _freshWorker = true;
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
        _freshWorker = true;
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
        _freshWorker = false;
    }

    private void MarkFailure(string reason)
    {
        int failures = Math.Min(Interlocked.Increment(ref _failures), 6);
        double delayMs = Math.Min(_retryBase.TotalMilliseconds * Math.Pow(2, failures - 1), 120_000);
        Interlocked.Exchange(ref _retryAtUtcTicks, DateTime.UtcNow.AddMilliseconds(delayMs).Ticks);
        Status = $"{reason}; retry in {delayMs / 1000:0.#}s";
    }

    public void Dispose() { StopWorker(); _gate.Dispose(); }
}

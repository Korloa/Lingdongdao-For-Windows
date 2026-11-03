using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed record AgentWakeJob(Guid Id, string Goal, string Context, DateTimeOffset DueAt);

// One timer for the next due job. No screenshot or cloud polling while a task sleeps.
public sealed class AgentWakeScheduler : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LingDongDao.AgentWake.v1");
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<AgentWakeJob> _wake;
    private readonly Action<Exception>? _error;
    private readonly object _sync = new();
    private readonly System.Threading.Timer _timer;
    private List<AgentWakeJob> _jobs;
    private bool _disposed;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingDongDao", "agent-wakes.dpapi");

    public AgentWakeScheduler(Action<AgentWakeJob> wake, string? path = null,
        Func<DateTimeOffset>? now = null, Action<Exception>? error = null)
    {
        _wake = wake;
        _error = error;
        _path = path ?? DefaultPath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _jobs = Read();
        _timer = new System.Threading.Timer(_ =>
        {
            try { Fire(); }
            catch (Exception exception) { _error?.Invoke(exception); }
        }, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_sync) Arm();
    }

    public IReadOnlyList<AgentWakeJob> Pending
    {
        get { lock (_sync) return _jobs.OrderBy(item => item.DueAt).ToArray(); }
    }

    public AgentWakeJob Schedule(string goal, string context, TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(10) || delay > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(delay));
        if (goal.Length is < 3 or > 300 || context.Length > 400)
            throw new ArgumentException("Invalid wake context");
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AgentWakeScheduler));
            if (_jobs.Count >= 5) throw new InvalidOperationException("最多保留 5 个定时任务");
            var job = new AgentWakeJob(Guid.NewGuid(), goal, context, _now() + delay);
            _jobs.Add(job);
            Write();
            Arm();
            return job;
        }
    }

    public void Clear()
    {
        lock (_sync) { _jobs.Clear(); Write(); Arm(); }
    }

    public void CheckDue() => Fire();

    private void Fire()
    {
        AgentWakeJob[] due;
        lock (_sync)
        {
            if (_disposed) return;
            due = _jobs.Where(item => item.DueAt <= _now()).OrderBy(item => item.DueAt).ToArray();
            if (due.Length == 0) { Arm(); return; }
            _jobs.RemoveAll(item => due.Any(d => d.Id == item.Id));
            Write();
            Arm();
        }
        foreach (AgentWakeJob job in due) _wake(job);
    }

    private void Arm()
    {
        if (_disposed) return;
        if (_jobs.Count == 0) { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        TimeSpan delay = _jobs.Min(item => item.DueAt) - _now();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private List<AgentWakeJob> Read()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy,
                DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<List<AgentWakeJob>>(plain) ?? []; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException) { return []; }
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_jobs);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp = _path + ".tmp";
        try { File.WriteAllBytes(temp, encrypted); File.Move(temp, _path, true); }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _timer.Dispose(); }
    }
}

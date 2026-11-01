using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed record CloudCallRecord(
    string Id, DateTimeOffset Time, bool UserInitiated, string ProcessName,
    string WindowTitle, string EventSummary, string EventKey, double LocalScore, string Status,
    bool? ShouldIntervene, string Reason, string Suggestion,
    string? RecommendedAction, string RawResponse, string Error,
    byte[]? CurrentJpeg, byte[]? BeforeJpeg)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusLabel => Status switch
    {
        "pending" => "等待模型回复",
        "success" => ShouldIntervene == true
            ? (string.IsNullOrWhiteSpace(Suggestion) ? "模型未提供可显示建议" : "建议提醒")
            : "模型决定不提醒",
        "empty" => "模型回复为空",
        "error" => "调用失败",
        "legacy" => "旧版未保存内容",
        _ => Status
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceLabel => UserInitiated ? "手动" : "自动";
}

// Each call is an encrypted, local-only record. A failed response still keeps its sent screenshot.
public sealed class CloudCallHistory
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LingDongDao.CloudHistory.v1");
    private readonly string _folder;
    private readonly object _sync = new();
    private string ClearMarker => Path.Combine(_folder, "cleared-before.txt");

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingDongDao", "cloud-history");

    public CloudCallHistory(string? folder = null) => _folder = folder ?? DefaultFolder;

    public void Save(CloudCallRecord record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _))
            throw new ArgumentException("Invalid call ID", nameof(record));
        lock (_sync)
        {
            Directory.CreateDirectory(_folder);
            string target = Path.Combine(_folder, record.Id + ".bin");
            string temporary = target + ".tmp";
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(record);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, target, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            // Keep recent records bounded because each includes a screenshot.
            foreach (string old in Directory.GetFiles(_folder, "*.bin")
                .OrderByDescending(File.GetLastWriteTimeUtc).Skip(100))
                File.Delete(old);
        }
    }

    public IReadOnlyList<CloudCallRecord> List(IReadOnlyList<CloudUsageRecord>? ledger = null)
    {
        lock (_sync)
        {
            var records = new List<CloudCallRecord>();
            if (Directory.Exists(_folder))
                foreach (string file in Directory.GetFiles(_folder, "*.bin")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
                {
                    try
                    {
                        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy,
                            DataProtectionScope.CurrentUser);
                        try
                        {
                            CloudCallRecord? item = JsonSerializer.Deserialize<CloudCallRecord>(plain);
                            if (item is not null) records.Add(item);
                        }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or CryptographicException or JsonException) { }
                }
            DateTimeOffset clearedBefore = DateTimeOffset.MinValue;
            try
            {
                if (File.Exists(ClearMarker) && DateTimeOffset.TryParse(
                    File.ReadAllText(ClearMarker), out DateTimeOffset parsed)) clearedBefore = parsed;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (ledger is not null)
                foreach (CloudUsageRecord usage in ledger)
                    if (usage.Time > clearedBefore && !records.Any(item =>
                        item.EventKey == usage.EventKey &&
                        Math.Abs((item.Time - usage.Time).TotalSeconds) < 10))
                        records.Add(new CloudCallRecord("legacy-" + usage.Time.UtcTicks,
                            usage.Time, false, "—", "", "旧版调用", usage.EventKey, 0,
                            "legacy", null, "旧版未保存模型回复", "", null, "",
                            "旧版未保存截图和回复，无法补回。", null, null));
            return records.OrderByDescending(item => item.Time).ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_folder);
            foreach (string file in Directory.GetFiles(_folder, "*.bin")) File.Delete(file);
            File.WriteAllText(ClearMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}

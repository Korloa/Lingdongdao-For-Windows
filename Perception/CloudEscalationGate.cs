using System.Text.Json;

namespace LingDongDao.Perception;

public sealed record CloudUsageRecord(DateTimeOffset Time, string EventKey, double EstimatedUsd);

public sealed class CloudEscalationGate
{
    private readonly string? _ledgerPath;
    private readonly List<CloudUsageRecord> _calls = new();
    private bool _ledgerValid = true;

    public static string DefaultLedgerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingDongDao", "cloud-usage.json");

    // Pass no path for an isolated in-memory gate, such as offline replay tests.
    public CloudEscalationGate(string? ledgerPath = null)
    {
        _ledgerPath = ledgerPath;
        if (ledgerPath is null || !File.Exists(ledgerPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<CloudUsageRecord>>(
                File.ReadAllText(ledgerPath));
            if (loaded is null || loaded.Any(item => item.EstimatedUsd < 0 ||
                string.IsNullOrWhiteSpace(item.EventKey))) throw new JsonException("Invalid usage ledger");
            _calls.AddRange(loaded);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException) { _ledgerValid = false; }
    }

    public int CallsToday => _calls.Count(item =>
        DateOnly.FromDateTime(item.Time.LocalDateTime) == DateOnly.FromDateTime(DateTime.Now));
    public double EstimatedSpentToday => _calls.Where(item =>
        DateOnly.FromDateTime(item.Time.LocalDateTime) == DateOnly.FromDateTime(DateTime.Now))
        .Sum(item => item.EstimatedUsd);

    public bool CanEscalate(DesktopObservation observation, LocalAssessment assessment,
        PerceptionOptions options, DateTimeOffset now, out string reason)
    {
        options.Normalize();
        if (!_ledgerValid) { reason = "ledger_unavailable"; return false; }
        if (!options.Enabled || options.Paused) { reason = "perception_off"; return false; }
        if (!options.CloudEnabled || !options.UploadConsent) { reason = "upload_not_authorized"; return false; }
        if (assessment.EventType == "none" || assessment.InterventionScore < options.EscalationThreshold)
        { reason = "below_threshold"; return false; }
        if (assessment.EventKey == "none" || _calls.Any(item =>
            item.EventKey == assessment.EventKey && now - item.Time < TimeSpan.FromMinutes(15)))
        { reason = "duplicate"; return false; }
        if (PrivacyFilter.IsSensitive(observation, options))
        { reason = "sensitive_window"; return false; }
        if (_calls.Count > 0 && now - _calls[^1].Time < TimeSpan.FromSeconds(options.CooldownSeconds))
        { reason = "cooldown"; return false; }
        if (_calls.Count(item => now - item.Time < TimeSpan.FromHours(1)) >= options.CloudCallsPerHour)
        { reason = "hourly_limit"; return false; }
        DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
        double spent = _calls.Where(item => DateOnly.FromDateTime(item.Time.LocalDateTime) == today)
            .Sum(item => item.EstimatedUsd);
        if (spent + options.EstimatedCostPerCallUsd > options.DailyCloudBudgetUsd)
        { reason = "daily_budget"; return false; }
        reason = "allowed";
        return true;
    }

    public bool TryReserve(DesktopObservation observation, LocalAssessment assessment,
        PerceptionOptions options, DateTimeOffset now, out string reason)
    {
        if (!CanEscalate(observation, assessment, options, now, out reason)) return false;
        var next = _calls.Where(item => now - item.Time < TimeSpan.FromDays(2)).ToList();
        next.Add(new CloudUsageRecord(now, assessment.EventKey, options.EstimatedCostPerCallUsd));
        if (_ledgerPath is not null)
        {
            string temporary = _ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(next));
                File.Move(temporary, _ledgerPath, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                reason = "ledger_write_failed";
                return false;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        _calls.Clear();
        _calls.AddRange(next);
        reason = "allowed";
        return true;
    }
}

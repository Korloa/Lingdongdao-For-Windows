namespace LingDongDao.Perception;

public sealed class CloudEscalationGate
{
    private readonly List<DateTimeOffset> _calls = new();
    private readonly Dictionary<string, DateTimeOffset> _recentKeys = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private DateOnly _budgetDay;
    private double _spent;

    public int CallsToday { get; private set; }
    public double EstimatedSpentToday => _spent;

    public bool TryReserve(DesktopObservation observation, LocalAssessment assessment,
        PerceptionOptions options, DateTimeOffset now, out string reason)
    {
        options.Normalize();
        if (!options.Enabled || options.Paused) { reason = "perception_off"; return false; }
        if (!options.CloudEnabled || !options.UploadConsent) { reason = "upload_not_authorized"; return false; }
        if (assessment.EventType == "none" || assessment.InterventionScore < options.EscalationThreshold)
        { reason = "below_threshold"; return false; }
        if (assessment.EventKey == "none" ||
            _recentKeys.TryGetValue(assessment.EventKey, out DateTimeOffset previous) &&
            now - previous < TimeSpan.FromMinutes(15))
        { reason = "duplicate"; return false; }
        if (PrivacyFilter.IsSensitive(observation, options))
        { reason = "sensitive_window"; return false; }
        if (now - _lastCall < TimeSpan.FromSeconds(options.CooldownSeconds))
        { reason = "cooldown"; return false; }
        _calls.RemoveAll(time => now - time >= TimeSpan.FromHours(1));
        if (_calls.Count >= options.CloudCallsPerHour)
        { reason = "hourly_limit"; return false; }
        DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
        if (today != _budgetDay) { _budgetDay = today; _spent = 0; CallsToday = 0; }
        if (_spent + options.EstimatedCostPerCallUsd > options.DailyCloudBudgetUsd)
        { reason = "daily_budget"; return false; }
        _lastCall = now;
        _recentKeys[assessment.EventKey] = now;
        _calls.Add(now);
        _spent += options.EstimatedCostPerCallUsd;
        CallsToday++;
        reason = "allowed";
        return true;
    }
}

namespace LingDongDao.Perception;

public sealed class StateMemory
{
    private sealed record Episode(DateTimeOffset First, DateTimeOffset Last, string Type, int Count);
    private readonly Dictionary<string, Episode> _events = new(StringComparer.Ordinal);
    private readonly Queue<(DateTimeOffset Time, string Activity)> _activities = new();
    private readonly Dictionary<string, DateTimeOffset> _helped = new(StringComparer.Ordinal);
    public string CurrentTask { get; private set; } = "unknown";

    public int RecentCount(string key, DateTimeOffset now, TimeSpan period)
    {
        Prune(now);
        return _events.TryGetValue(key, out Episode? episode) &&
            now - episode.Last <= period ? episode.Count : 0;
    }

    public bool RecentlyHelped(string key, DateTimeOffset now, TimeSpan period) =>
        _helped.TryGetValue(key, out DateTimeOffset time) && now - time <= period;

    public void MarkHelped(string key, DateTimeOffset time) => _helped[key] = time;

    public void Update(DesktopObservation observation, LocalAssessment assessment)
    {
        Prune(observation.Time);
        CurrentTask = assessment.Activity;
        _activities.Enqueue((observation.Time, assessment.Activity));
        if (assessment.EventType != "none")
        {
            if (_events.TryGetValue(assessment.EventKey, out Episode? old) &&
                observation.Time - old.Last <= TimeSpan.FromMinutes(3))
                _events[assessment.EventKey] = old with
                { Last = observation.Time, Count = old.Count + 1 };
            else
                _events[assessment.EventKey] = new Episode(observation.Time,
                    observation.Time, assessment.EventType, 1);
        }
    }

    public IReadOnlyList<string> RecentSummaries(DateTimeOffset now) => _events
        .Where(item => now - item.Value.Last <= TimeSpan.FromMinutes(3))
        .OrderBy(item => item.Value.Last).TakeLast(12)
        .Select(item => $"{item.Value.First:HH:mm:ss}-{item.Value.Last:HH:mm:ss} " +
                        $"{item.Value.Type} {item.Key} count={item.Value.Count}").ToArray();

    private void Prune(DateTimeOffset now)
    {
        foreach (string key in _events.Where(item => now - item.Value.Last > TimeSpan.FromMinutes(5))
                     .Select(item => item.Key).ToArray()) _events.Remove(key);
        while (_activities.Count > 0 && now - _activities.Peek().Time > TimeSpan.FromMinutes(5))
            _activities.Dequeue();
        foreach (string key in _helped.Where(item => now - item.Value > TimeSpan.FromHours(1))
                     .Select(item => item.Key).ToArray()) _helped.Remove(key);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LingDongDao.Perception;

public sealed record AgentSkillStep(string Tool, string Label, string? SafeValue);
public sealed record AgentSkill(string Goal, DateTimeOffset LastSuccess, int SuccessCount,
    IReadOnlyList<AgentSkillStep> Steps);

// Stores only successful, reusable action outlines. Coordinates and typed content never enter the store.
public sealed class AgentSkillStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LingDongDao.AgentSkills.v1");
    private readonly string _path;
    private readonly object _sync = new();
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingDongDao", "agent-skills.dpapi");

    public AgentSkillStore(string? path = null) => _path = path ?? DefaultPath;

    public IReadOnlyList<AgentSkill> List()
    {
        lock (_sync) return Read();
    }

    public AgentSkill? Find(string goal)
    {
        string key = Normalize(goal);
        lock (_sync) return Read().FirstOrDefault(item => Normalize(item.Goal) == key);
    }

    public void Remember(string goal, IEnumerable<AgentActionFeedback> actions)
    {
        string normalized = Normalize(goal);
        if (normalized.Length is < 3 or > 300 || PrivacyFilter.ContainsSecret(goal)) return;
        AgentSkillStep[] steps = actions.Where(item => item.Executed)
            .Select(item => ToStep(item.Decision)).Where(item => item is not null)
            .Cast<AgentSkillStep>().Take(20).ToArray();
        if (steps.Length == 0) return;
        lock (_sync)
        {
            var entries = Read().ToList();
            int index = entries.FindIndex(item => Normalize(item.Goal) == normalized);
            int count = index >= 0 ? entries[index].SuccessCount + 1 : 1;
            if (index >= 0) entries.RemoveAt(index);
            entries.Insert(0, new AgentSkill(goal.Trim(), DateTimeOffset.UtcNow, count, steps));
            Write(entries.OrderByDescending(item => item.LastSuccess).Take(100).ToArray());
        }
    }

    public void Clear()
    {
        lock (_sync) if (File.Exists(_path)) File.Delete(_path);
    }

    private static AgentSkillStep? ToStep(AgentToolDecision decision)
    {
        string? value = decision.Kind switch
        {
            AgentToolKind.OpenApp or AgentToolKind.OpenBrowser or AgentToolKind.PressKeys
                => decision.Value,
            AgentToolKind.Navigate when Uri.TryCreate(decision.Value, UriKind.Absolute, out Uri? uri)
                => uri.GetLeftPart(UriPartial.Authority),
            _ => null
        };
        if (value is { Length: > 100 }) value = value[..100];
        // Labels can reflect private message contents; keep only the tool kind for those actions.
        string label = decision.Kind is AgentToolKind.TypeText or AgentToolKind.Click or
            AgentToolKind.RightClick or AgentToolKind.DoubleClick or AgentToolKind.Drag
            ? decision.Kind.ToString() : decision.Label[..Math.Min(48, decision.Label.Length)];
        return decision.Kind is AgentToolKind.Wait or AgentToolKind.Finish or AgentToolKind.ScheduleWake
            ? null : new AgentSkillStep(decision.Kind.ToString(), label, value);
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private List<AgentSkill> Read()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy,
                DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<List<AgentSkill>>(plain) ?? []; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException) { return []; }
    }

    private void Write(IReadOnlyList<AgentSkill> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entries);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp = _path + ".tmp";
        try { File.WriteAllBytes(temp, encrypted); File.Move(temp, _path, true); }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (File.Exists(temp)) File.Delete(temp); }
    }
}

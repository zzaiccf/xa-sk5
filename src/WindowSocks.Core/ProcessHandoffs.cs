namespace WindowSocks.Core;

public sealed class ProcessHandoffGrace
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, DateTimeOffset> empty = new(StringComparer.Ordinal);
    public bool Waiting(string key, bool hasLiveMembers, DateTimeOffset now)
    {
        if (hasLiveMembers) { empty.Remove(key); return false; }
        if (!empty.TryGetValue(key, out var since)) empty[key] = since = now;
        return now - since < Timeout;
    }
    public void Retain(IEnumerable<string> keys)
    {
        var retained = keys.ToHashSet(StringComparer.Ordinal);
        foreach (string key in empty.Keys.Where(k => !retained.Contains(k)).ToArray()) empty.Remove(key);
    }
}

public static class ProcessHandoffPolicy
{
    public static AutoProxyRule Automatic(AutoProxyRule rule) => rule.Kind == "Process" ? rule with { IncludeChildren = true } : rule;
}

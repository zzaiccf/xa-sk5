namespace WindowSocks.Core;

public sealed record ProxySessionState(string TargetKey, Guid ProxyId, ProcessIdentity[] Processes, bool Active);

public sealed class ProxySessionTracker
{
    private readonly Dictionary<string, (Guid Proxy, ProcessIdentity[] Processes, DateTimeOffset Since)> sessions = [];
    public void Synchronize(IEnumerable<ProxySessionState> states, DateTimeOffset now)
    {
        var active = states.Where(s => s.Active && s.Processes.Length > 0).ToDictionary(s => s.TargetKey, StringComparer.Ordinal);
        foreach (var key in sessions.Keys.Where(k => !active.ContainsKey(k)).ToArray()) sessions.Remove(key);
        foreach (var (key, state) in active) {
            var members = state.Processes.Distinct().OrderBy(p => p.Pid).ThenBy(p => p.CreatedFileTime).ToArray();
            if (!sessions.TryGetValue(key, out var previous) || previous.Proxy != state.ProxyId || !previous.Processes.SequenceEqual(members))
                sessions[key] = (state.ProxyId, members, now);
        }
    }
    public DateTimeOffset? ConnectedAt(string key) => sessions.TryGetValue(key, out var session) ? session.Since : null;
    public static string Duration(DateTimeOffset? since, DateTimeOffset now)
    {
        if (since is null) return "—";
        var elapsed = now > since ? now - since.Value : TimeSpan.Zero;
        return elapsed.Days > 0 ? $"{elapsed.Days} 天 {elapsed:hh\\:mm\\:ss}" : elapsed.ToString(@"hh\:mm\:ss");
    }
}

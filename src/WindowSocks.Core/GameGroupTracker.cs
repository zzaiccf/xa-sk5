namespace WindowSocks.Core;

public sealed record GameProcessObservation(ProcessIdentity Identity, string Path, int ParentPid);
public sealed record GameGroupMember(ProcessIdentity Identity, string Path, long ExitedFileTime = 0);
public sealed record GameLaunchGroup
{
    public ProcessIdentity Root { get; init; }
    public string LauncherPath { get; init; } = "";
    public string ClientPath { get; init; } = "";
    public GameGroupMember[] Members { get; init; } = [];
    public long EmptySinceFileTime { get; init; }
    public string Key => $"game:{Root.Pid}:{Root.CreatedFileTime}";
    public TargetInstance Target() => new(Key, $"{System.IO.Path.GetFileNameWithoutExtension(LauncherPath)} 遊戲組 · {Root.Pid}",
        "GameGroup", LauncherPath, -1, Members.Any(m => m.ExitedFileTime == 0),
        Members.Where(m => m.ExitedFileTime == 0).Select(m => m.Identity).ToArray(),
        string.Join("、", Members.Where(m => m.ExitedFileTime == 0).Select(m => $"{System.IO.Path.GetFileName(m.Path)} · PID {m.Identity.Pid}")));
}
public sealed record GameGroupScan(GameLaunchGroup[] Groups, GameProcessObservation[] Unmatched);

// Only the two explicit images participate. Parent PID alone never proves lineage after exit/reuse.
public static class GameGroupTracker
{
    public static GameGroupScan Update(IEnumerable<GameLaunchGroup> previous, AutoProxyRule rule,
        GameProcessObservation[] observations, IReadOnlyDictionary<ProcessIdentity, long> exitTimes, long now)
    {
        var live = observations.Select(p => p.Identity).ToHashSet();
        var groups = previous.Select(g => g with { Members = g.Members.Select(m => live.Contains(m.Identity) ? m with { ExitedFileTime = 0 } :
            m with { ExitedFileTime = m.ExitedFileTime > 0 ? m.ExitedFileTime : exitTimes.GetValueOrDefault(m.Identity, -1) }).ToArray() }).ToList();
        var unmatched = new List<GameProcessObservation>();
        foreach (var p in observations.OrderBy(p => p.Identity.CreatedFileTime).ThenBy(p => p.Identity.Pid)) {
            if (groups.Any(g => g.Members.Any(m => m.Identity == p.Identity))) continue;
            var parents = groups.Where(g => Allowed(g, p.Path) && g.Members.Any(m => m.Identity.Pid == p.ParentPid &&
                m.Identity.CreatedFileTime <= p.Identity.CreatedFileTime &&
                (live.Contains(m.Identity) || m.ExitedFileTime > 0 && p.Identity.CreatedFileTime <= m.ExitedFileTime))).ToArray();
            if (parents.Length == 1) {
                var g = parents[0]; int i = groups.IndexOf(g);
                if (g.Members.Length < 64) groups[i] = g with { Members = [.. g.Members, new(p.Identity, p.Path)], EmptySinceFileTime = 0 };
                else unmatched.Add(p);
            } else if (parents.Length == 0 && rule.Enabled && rule.Kind == "GameGroup" && Same(p.Path, rule.Pattern)) {
                groups.Add(new() { Root = p.Identity, LauncherPath = rule.Pattern, ClientPath = rule.ClientPath, Members = [new(p.Identity, p.Path)] });
            } else unmatched.Add(p);
        }
        for (int i = groups.Count - 1; i >= 0; i--) {
            var g = groups[i]; bool empty = g.Members.All(m => m.ExitedFileTime != 0);
            groups[i] = g with { EmptySinceFileTime = !empty ? 0 : g.EmptySinceFileTime == 0 ? now : g.EmptySinceFileTime };
            // Preserve the assigned slot across launcher-to-client transitions; release after every member ends.
            if (empty && now - groups[i].EmptySinceFileTime >= TimeSpan.FromSeconds(10).Ticks) groups.RemoveAt(i);
        }
        return new(groups.ToArray(), unmatched.ToArray());
    }
    private static bool Allowed(GameLaunchGroup g, string path) => Same(path, g.LauncherPath) || Same(path, g.ClientPath);
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

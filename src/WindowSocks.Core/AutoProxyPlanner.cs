namespace WindowSocks.Core;

public static class AutoProxyPlanner
{
    public static void Validate(AutoProxyRule rule)
    {
        if (rule.PerProxy is < 1 or > 10) throw new InvalidOperationException("每個 IP 可分配 1–10 個目標。");
        if (rule.Kind is not ("LDPlayer" or "Emulator" or "EmulatorSequence" or "Process" or "GameGroup")) throw new InvalidOperationException("請選擇模擬器、一般程序或遊戲組。");
        if (rule.IncludeChildren && rule.Kind != "Process") throw new InvalidOperationException("子程序代理僅適用一般程序規則。");
        if (rule.Kind == "Emulator") { FixedEmulatorPlanner.Validate(rule.EmulatorAssignments, allowEmpty: !rule.Enabled); return; }
        if (rule.Kind == "EmulatorSequence") {
            if (!EmulatorCatalog.IsEmulator(rule.EmulatorKind)) throw new InvalidOperationException("請選擇雷電或 MuMu 模擬器。");
            EmulatorCatalog.ValidateConsole(rule.EmulatorKind, rule.ConsolePath, requireFile: false);
            if (rule.EmulatorAssignments.Length != 0) throw new InvalidOperationException("依啟動順序模式不能同時指定固定編號配置。");
            return;
        }
        if (rule.Kind == "LDPlayer") return;
        if (rule.Kind == "GameGroup") {
            Validate(rule with { Kind = "Process", Match = TargetMatch.ExecutablePath });
            Validate(rule with { Kind = "Process", Match = TargetMatch.ExecutablePath, Pattern = rule.ClientPath });
            if (rule.Pattern.Equals(rule.ClientPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("登入器與遊戲程序請填寫不同的完整路徑。");
            return;
        }
        if (rule.Match is not (TargetMatch.ExecutableName or TargetMatch.ExecutablePath)) throw new InvalidOperationException("自動匹配支援完整檔名或完整路徑。");
        if (string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Length > 512 || rule.Pattern.IndexOfAny(['*', '?', '\r', '\n']) >= 0)
            throw new InvalidOperationException("請填寫確切的 .exe／.bin 檔名或完整路徑，不能使用萬用字元。");
        if (!new[] { ".exe", ".bin" }.Contains(Path.GetExtension(rule.Pattern), StringComparer.OrdinalIgnoreCase) ||
            (rule.Match == TargetMatch.ExecutableName && Path.GetFileName(rule.Pattern) != rule.Pattern) ||
            (rule.Match == TargetMatch.ExecutablePath && !Path.IsPathFullyQualified(rule.Pattern)))
            throw new InvalidOperationException("程序匹配格式不正確，請使用 game.exe、遊戲 .bin，或完整程式路徑。");
    }

    public static bool Matches(TargetInstance target, AutoProxyRule rule) => rule.Kind == "EmulatorSequence" ? SequentialEmulatorPlanner.Matches(target, rule) : rule.Kind == "Emulator" ? rule.EmulatorAssignments.Any(a => a.TargetKey == target.Key) : target.Kind == rule.Kind &&
        (rule.Kind == "LDPlayer" ? target.Path.Equals(rule.ConsolePath, StringComparison.OrdinalIgnoreCase) : rule.Kind == "GameGroup" ? target.Path.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase) :
        (rule.Match == TargetMatch.ExecutableName ? Path.GetFileName(target.Path) : target.Path).Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase));

    // Match roots only, including when a child happens to have the same executable name.
    public static TargetInstance[] Roots(IEnumerable<TargetInstance> candidates, IReadOnlyDictionary<int, int> parents,
        Func<int, ProcessIdentity?> identity)
    {
        var list = candidates.ToArray(); var selected = list.SelectMany(t => t.Processes).ToHashSet();
        return list.Where(t => !t.Processes.Any(p => {
            var visited = new HashSet<int>(); var child = p;
            while (parents.TryGetValue(child.Pid, out int parent) && visited.Add(parent)) {
                var ancestor = identity(parent);
                if (ancestor is null || ancestor.Value.CreatedFileTime > child.CreatedFileTime) break;
                if (selected.Contains(ancestor.Value)) return true;
                child = ancestor.Value;
            }
            return false;
        })).ToArray();
    }

    public static AssignmentItem[] Additions(IEnumerable<TargetInstance> candidates, IEnumerable<InstanceBinding> current,
        IEnumerable<ProxyProfile> profiles, int perProxy, IEnumerable<string> excluded, bool launchOrder = false)
    {
        if (perProxy is < 1 or > 10) throw new InvalidOperationException("每個 IP 可分配 1–10 個目標。");
        var bindings = current.ToArray(); var skip = excluded.Concat(bindings.Select(b => b.TargetKey)).ToHashSet(StringComparer.Ordinal);
        var usedProcesses = bindings.SelectMany(b => b.Processes.Concat(b.IncludeChildren ? b.Descendants : [])).ToHashSet();
        var counts = bindings.GroupBy(b => b.ProxyId).ToDictionary(g => g.Key, g => g.Count());
        var exclusive = bindings.Where(b => !b.SharedProxy).Select(b => b.ProxyId).ToHashSet();
        var pool = profiles.Select(p => p.Id).Distinct().Where(id => !exclusive.Contains(id)).ToArray();
        var items = new List<AssignmentItem>();
        var ordered = launchOrder
            ? candidates.Where(t => t.Running && t.Processes.Length > 0).OrderBy(t => t.Processes.Min(p => p.CreatedFileTime)).ThenBy(t => t.Key, StringComparer.Ordinal)
            : candidates.OrderBy(t => t.Kind).ThenBy(t => t.Index).ThenBy(t => t.Processes.FirstOrDefault().CreatedFileTime).ThenBy(t => t.Key, StringComparer.Ordinal);
        foreach (var target in ordered) {
            if (bindings.Length + items.Count >= 10) break;
            if (skip.Contains(target.Key) || target.Processes.Any(usedProcesses.Contains)) continue;
            var proxy = pool.FirstOrDefault(id => counts.GetValueOrDefault(id) < perProxy);
            if (proxy == Guid.Empty) break;
            items.Add(new(target, proxy)); counts[proxy] = counts.GetValueOrDefault(proxy) + 1;
            skip.Add(target.Key); foreach (var p in target.Processes) usedProcesses.Add(p);
        }
        return items.ToArray();
    }
}

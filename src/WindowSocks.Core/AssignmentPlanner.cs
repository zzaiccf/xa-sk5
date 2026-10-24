namespace WindowSocks.Core;

public enum TargetMatch { Any, ExecutableName, ExecutablePath, WindowTitle }

public static class AssignmentPlanner
{
    public static bool Matches(TargetInstance target, string query, TargetMatch mode)
    {
        query = query.Trim(); if (query.Length == 0) return true;
        string text = mode switch {
            TargetMatch.ExecutableName => Path.GetFileName(target.Path),
            TargetMatch.ExecutablePath => target.Path,
            TargetMatch.WindowTitle => target.Name,
            _ => target.Name + " " + target.Path + " " + target.Detail
        };
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    public static AssignmentItem[] Distribute(TargetInstance[] targets, Guid[] proxies, int perProxy)
    {
        if (perProxy is < 1 or > 10) throw new InvalidOperationException("每條代理可分配 1–10 個目標。");
        if (targets.Length is < 1 or > 10) throw new InvalidOperationException("請選擇 1–10 個目標。");
        if (proxies.Length == 0 || proxies.Distinct().Count() != proxies.Length) throw new InvalidOperationException("請選擇不重複的代理。");
        if (targets.Length > (long)proxies.Length * perProxy) throw new InvalidOperationException("選取的目標超過代理容量，請增加每條代理的窗口數或選取更多代理。");
        if (targets.Select(t => t.Key).Distinct().Count() != targets.Length) throw new InvalidOperationException("目標不可重複。");
        return targets.Select((t, i) => new AssignmentItem(t, proxies[i / perProxy])).ToArray();
    }
    // Pure validation before any native rule change; existing bindings outside the batch are preserved.
    public static List<InstanceBinding> Prepare(IEnumerable<InstanceBinding> current, IEnumerable<ProxyProfile> profiles, BatchBindRequest request)
    {
        if (request.Items.Length is < 1 or > 10) throw new InvalidOperationException("每次可分配 1–10 個目標。");
        var keys = request.Items.Select(i => i.Target.Key).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != request.Items.Length || keys.Contains("")) throw new InvalidOperationException("目標識別不可空白或重複。");
        var proxyIds = profiles.Select(p => p.Id).ToHashSet();
        var existing = current.ToArray();
        var result = existing.Where(b => !keys.Contains(b.TargetKey)).Select(b => b with { }).ToList();
        foreach (var item in request.Items) {
            if (!proxyIds.Contains(item.ProxyId)) throw new InvalidOperationException("找不到指定代理。");
            var t = item.Target;
            if (t.Kind is not ("Process" or "LDPlayer" or "MuMu" or "GameGroup") || t.Kind == "Process" && (t.Processes.Length != 1 || !t.Running) || t.Kind == "GameGroup" && (t.Processes.Length is < 1 or > 64 || !t.Running)) throw new InvalidOperationException("請選取正在執行的程序或已辨識的模擬器／遊戲組。");
            if (item.IncludeChildren && t.Kind != "Process") throw new InvalidOperationException("子程序代理僅適用一般程序；雷電與遊戲組使用各自的成員規則。");
            var previous = existing.FirstOrDefault(b => b.TargetKey == t.Key);
            result.Add(new() { TargetKey = t.Key, ProxyId = item.ProxyId, SharedProxy = item.SharedProxy, IncludeChildren = item.IncludeChildren,
                Descendants = item.IncludeChildren && previous?.IncludeChildren == true && previous.Processes.SequenceEqual(t.Processes) ? previous.Descendants.ToArray() : [],
                Name = t.Name, Kind = t.Kind, Path = t.Path, Index = t.Index, Processes = t.Processes.ToArray(), Enabled = false,
                EmulatorProcessPath = t.EmulatorProcessPath, EmulatorCommandHash = t.EmulatorCommandHash });
        }
        if (result.Count > 10) throw new InvalidOperationException("目前最多同時管理 10 個目標；請先解除不使用的綁定。");
        foreach (var group in result.GroupBy(b => b.ProxyId))
            if (group.Count() > 1 && group.Any(b => !b.SharedProxy)) throw new InvalidOperationException("此代理仍有獨占綁定，請將相關目標一起選入共用分配，或先解除該綁定。");
        if (result.SelectMany(b => b.Processes.Select(p => (p, b.TargetKey))).GroupBy(x => x.p).Any(g => g.Select(x => x.TargetKey).Distinct().Count() > 1))
            throw new InvalidOperationException("同一程序的多個窗口只需分配一次；重複程序不能建立不同綁定。");
        return result;
    }
    public static bool HasDuplicateExit(Guid id, IEnumerable<InstanceBinding> bindings, IReadOnlyDictionary<Guid, ProbeResult> probes)
    {
        if (!probes.TryGetValue(id, out var p)) return false;
        // Sharing the same profile is intentional. Different profiles with identical exits still fail the independence check.
        return bindings.Where(b => b.ProxyId != id).Any(b => probes.TryGetValue(b.ProxyId, out var q) &&
            new[] { p.TcpExit, p.UdpExit }.Where(x => x is not null).Intersect(new[] { q.TcpExit, q.UdpExit }.Where(x => x is not null)).Any());
    }
}

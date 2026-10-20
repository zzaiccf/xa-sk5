namespace WindowSocks.Core;

public static class FixedEmulatorPlanner
{
    public static void Validate(EmulatorAssignment[] assignments, bool allowEmpty = false)
    {
        if (assignments.Length > 10 || !allowEmpty && assignments.Length == 0) throw new InvalidOperationException("請指定 1–10 個模擬器窗口的代理。");
        foreach (var item in assignments) {
            if (!EmulatorCatalog.IsEmulator(item.Kind) || item.Index < 0 || item.ProxyId == Guid.Empty) throw new InvalidOperationException("窗口類型、編號或代理不正確。");
            EmulatorCatalog.ValidateConsole(item.Kind, item.ConsolePath, requireFile: false);
        }
        if (assignments.Select(a => a.TargetKey).Distinct(StringComparer.Ordinal).Count() != assignments.Length)
            throw new InvalidOperationException("同一模擬器窗口只能指定一條代理。");
    }
    public static List<InstanceBinding> Prepare(AutoProxyRule rule, AutoProxyRule previous, IEnumerable<InstanceBinding> current,
        IEnumerable<ProxyProfile> profiles, IEnumerable<TargetInstance> targets)
    {
        Validate(rule.EmulatorAssignments, allowEmpty: !rule.Enabled);
        var bindings = current.ToArray(); var known = targets.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var priorKeys = previous.Kind == "Emulator" ? previous.EmulatorAssignments.Select(a => a.TargetKey).ToHashSet(StringComparer.Ordinal) : [];
        var retained = bindings.Where(b => !(b.AutoManaged && priorKeys.Contains(b.TargetKey)))
            .Select(b => previous.Kind != "Emulator" && b.AutoManaged ? b with { AutoManaged = false, Enabled = false, AutoPaused = true } : b).ToArray();
        var items = rule.EmulatorAssignments.Select(a => {
            var old = bindings.FirstOrDefault(b => b.TargetKey == a.TargetKey);
            var target = known.GetValueOrDefault(a.TargetKey) ?? new(a.TargetKey, a.Name, a.Kind, a.ConsolePath, a.Index, false, []) {
                EmulatorProcessPath = old?.EmulatorProcessPath ?? "", EmulatorCommandHash = old?.EmulatorCommandHash ?? ""
            };
            return new AssignmentItem(target, a.ProxyId, SharedProxy: true);
        }).ToArray();
        var next = items.Length == 0 ? retained.Select(b => b with { }).ToList() : AssignmentPlanner.Prepare(retained, profiles, new(items));
        foreach (var binding in next.Where(b => rule.EmulatorAssignments.Any(a => a.TargetKey == b.TargetKey))) {
            var old = bindings.FirstOrDefault(b => b.TargetKey == binding.TargetKey && b.ProxyId == binding.ProxyId);
            binding.AutoManaged = true; binding.AutoPaused = old?.AutoPaused ?? false;
            binding.Enabled = rule.Enabled && !binding.AutoPaused;
        }
        return next;
    }
}

namespace WindowSocks.Core;

public static class SequentialEmulatorPlanner
{
    public static bool Matches(TargetInstance target, AutoProxyRule rule) => target.Kind == rule.EmulatorKind &&
        target.Path.Equals(rule.ConsolePath, StringComparison.OrdinalIgnoreCase);

    public static TargetInstance[] Candidates(IEnumerable<TargetInstance> targets, AutoProxyRule rule) =>
        targets.Where(t => Matches(t, rule) && t.Running && t.Processes.Length > 0).ToArray();

    public static bool IsLive(InstanceBinding binding, Func<int, ProcessIdentity?> identity) =>
        binding.Processes.Any(p => identity(p.Pid) == p);

    // Existing live assignments keep their IP; stopped windows reserve no slot in launch-order mode.
    public static List<InstanceBinding> Prepare(AutoProxyRule rule, IEnumerable<InstanceBinding> current,
        Func<int, ProcessIdentity?> identity)
    {
        var result = new List<InstanceBinding>();
        foreach (var binding in current) {
            if (!binding.AutoManaged) { result.Add(binding with { }); continue; }
            bool inScope = binding.Kind == rule.EmulatorKind && binding.Path.Equals(rule.ConsolePath, StringComparison.OrdinalIgnoreCase);
            if (!inScope) { result.Add(binding with { AutoManaged = false, Enabled = false, AutoPaused = true }); continue; }
            if (IsLive(binding, identity)) result.Add(binding with { Enabled = rule.Enabled && !binding.AutoPaused });
        }
        if (result.Where(b => b.AutoManaged).GroupBy(b => b.ProxyId).Any(g => g.Count() > rule.PerProxy))
            throw new InvalidOperationException("已有 IP 的分配數超過新上限，請先解除多餘綁定；不會自動替窗口換 IP。");
        return result;
    }
}

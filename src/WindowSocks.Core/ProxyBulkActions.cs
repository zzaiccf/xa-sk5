namespace WindowSocks.Core;

public sealed record ClearProxyPoolResult(Guid[] RemovedIds);
public sealed record ProxyProcessScope(string Key, string Name, ProcessIdentity[] Processes);
public sealed record CloseProxyProcessesPlan(Guid Token, ProxyProcessScope[] Targets, int ProcessCount, DateTimeOffset ExpiresAt);
public sealed record CloseProxyProcessesRequest(Guid Token);
public sealed record ProcessCloseOutcome(ProcessIdentity Identity, string State, string Message);
public sealed record CloseProxyProcessesResult(ProcessCloseOutcome[] Processes)
{
    public string StateUpdateWarning { get; init; } = "";
    public int Closed => Processes.Count(p => p.State == "closed");
    public int AlreadyExited => Processes.Count(p => p.State == "exited");
    public int Unclosed => Processes.Count(p => p.State is "failed" or "skipped");
    public string Message => $"已關閉 {Closed} 個程序" + (AlreadyExited > 0 ? $"，{AlreadyExited} 個已結束" : "") +
        (Unclosed > 0 ? $"，{Unclosed} 個未關閉（請查看原因）" : "") + "；全局代理保持原設定。" + StateUpdateWarning;
}

public static class ProxyPoolUsage
{
    // Validate the complete pool before removing anything, including saved assignments to stopped VMs.
    public static void RequireUnused(SavedConfig config, IEnumerable<VmwareRuntime>? runtime = null)
    {
        var ids = config.Proxies.Select(p => p.Id).ToHashSet();
        if (ids.Count == 0) return;
        var reasons = new List<string>();
        if (config.GlobalProxy.Enabled && ids.Contains(config.GlobalProxy.ProxyId)) reasons.Add("全局代理正在使用節點");
        if (config.Bindings.Any(b => ids.Contains(b.ProxyId))) reasons.Add("仍有窗口綁定節點（包含暫停或未啟動的窗口）");
        if (config.AutoProxy.EmulatorAssignments.Any(a => ids.Contains(a.ProxyId)) ||
            config.PendingEmulatorRule?.EmulatorAssignments.Any(a => ids.Contains(a.ProxyId)) == true)
            reasons.Add("固定模擬器規則仍使用節點");
        if (!config.Vmware.RuleDeleted && config.Vmware.Machines.Any(m => !m.UseLocalNetwork && ids.Contains(m.ProxyId)) ||
            (runtime ?? []).Any(m => !m.UseLocalNetwork && ids.Contains(m.ProxyId))) reasons.Add("VMware 分配仍使用節點");
        if (reasons.Count > 0) throw new InvalidOperationException("無法清除所有 IP：" + string.Join("；", reasons) + "。請先解除相關分配／綁定，或停用全局代理；本次未刪除任何節點。");
    }
}

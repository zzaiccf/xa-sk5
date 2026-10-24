using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed partial class Controller
{
    // The complete per-process report must fit within the 1 MiB pipe envelope.
    internal const int MaximumCloseProcesses = 1024;
    private CloseProxyProcessesPlan? preparedProcessClose;

    private ClearProxyPoolResult ClearProxyPool()
    {
        ProxyPoolUsage.RequireUnused(config, vmwareRuntime);
        Guid[] ids = config.Proxies.Select(p => p.Id).ToArray();
        var next = config with { Proxies = [], LastProbes = [], ServiceProxyExpirations = [] };
        store.Save(next); // A failed save leaves the current pool intact.
        config.Proxies = next.Proxies; config.LastProbes = next.LastProbes; config.ServiceProxyExpirations = next.ServiceProxyExpirations;
        foreach (Guid id in ids) {
            epochs[id] = ++epoch; probes.Remove(id); probeViews.Remove(id); retries.Remove(id); lastReady.Remove(id);
            activeChecks.Remove((id, false)); activeChecks.Remove((id, true)); supplementalChecks.Remove(id);
        }
        Log($"已清除全部 {ids.Length} 個 IP 節點；沒有關閉程式。");
        return new(ids);
    }
    private ProxyProcessScope[] IndividualProcessScopes()
    {
        var result = new List<ProxyProcessScope>();
        foreach (var binding in config.Bindings.Where(b => b.ProxyId != Guid.Empty)) {
            if (ManagedProcessTerminator.ProtectedImage(binding.Path)) continue;
            var group = config.GameGroups.FirstOrDefault(g => g.Key == binding.TargetKey);
            var members = binding.Processes.Concat(binding.Descendants).Concat(group?.Members.Select(m => m.Identity) ?? []).Distinct()
                .Where(p => LdPlayer.Identity(p.Pid) == p && IsOwned(p.Pid)).ToArray();
            if (members.Length > 0) result.Add(new(binding.TargetKey, binding.Name, members));
        }
        if (!config.Vmware.RuleDeleted) foreach (var vm in vmwareRuntime.Where(v => v.ProxyId != Guid.Empty && !v.UseLocalNetwork && v.Process.HasValue)) {
            var mapping = config.Vmware.Machines.FirstOrDefault(m => m.VmxPath.Equals(vm.VmxPath, StringComparison.OrdinalIgnoreCase));
            if (mapping is null || vm.Process is not { } process || LdPlayer.Identity(process.Pid) != process || !IsOwned(process.Pid)) continue;
            result.Add(new("vmware:" + Path.GetFullPath(vm.VmxPath).ToUpperInvariant(), mapping.Name.Length > 0 ? mapping.Name : Path.GetFileNameWithoutExtension(vm.VmxPath), [process]));
        }
        return result.ToArray();
    }
    private CloseProxyProcessesPlan PrepareCloseProxyProcesses()
    {
        RefreshBindingChildren();
        var scopes = IndividualProcessScopes();
        var members = ProcessCatalog.WithChildren(scopes.SelectMany(s => s.Processes).Distinct()).Where(p => p.Pid != Environment.ProcessId && IsOwned(p.Pid) && !ManagedProcessTerminator.ProtectedImage(ProcessCatalog.ImagePath(p.Pid))).ToArray();
        if (members.Length > MaximumCloseProcesses) throw new InvalidOperationException("程序超過 1024 個，請先分批關閉窗口。");
        // Only explicitly managed scopes are approved. Global routing never enumerates a kill list.
        preparedProcessClose = new(Guid.NewGuid(), scopes, members.Length, DateTimeOffset.UtcNow.AddMinutes(2));
        return preparedProcessClose;
    }
    private async Task<CloseProxyProcessesResult> CloseProxyProcesses(CloseProxyProcessesRequest request)
    {
        var plan = preparedProcessClose;
        if (plan is null || plan.Token != request.Token || plan.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("關閉清單已失效，請重新按「關閉所有代理進程」。");
        preparedProcessClose = null; // Confirmation is one-shot, including partial failures.
        RefreshBindingChildren();
        var current = IndividualProcessScopes().ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var roots = plan.Targets.SelectMany(scope => current.TryGetValue(scope.Key, out var live)
            ? scope.Processes.Intersect(live.Processes) : []).Distinct().ToArray();
        // Include children created since confirmation, but never a new root or a reused PID.
        var members = ProcessCatalog.WithChildren(roots);
        if (members.Length > MaximumCloseProcesses) throw new InvalidOperationException("程序超過 1024 個，請先分批關閉窗口。");
        var result = new CloseProxyProcessesResult(await ManagedProcessTerminator.CloseAsync(members, OwnerSid));
        try { RefreshBindingChildren(); ApplyRules(); store.Save(config); }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // Process termination cannot be rolled back. Always report what actually closed.
            Log("程序關閉後更新狀態失敗：" + ex.GetType().Name);
            result = result with { StateUpdateWarning = "窗口狀態更新未完成，請重新整理。" };
        }
        Log(result.Message); return result;
    }
}

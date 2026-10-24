namespace WindowSocks.Core;

public sealed record WindowListItem(TargetInstance Target, Guid ProxyId, bool UseLocalNetwork, string Rule, string State, string Detail,
    DateTimeOffset? ConnectedAt = null, long? Sent = null, long? Received = null, uint? TcpConnections = null, uint? UdpAssociations = null)
{
    public string InstanceDetail => (Target.Kind switch { "VMware" => "VMware", "LDPlayer" => "雷電", "MuMu" => "MuMu", "GameGroup" => "遊戲組", _ => "程序" }) +
        (Target.Processes.Length == 0 ? " · 未啟動" : " · PID " + string.Join(", ", Target.Processes.Select(p => p.Pid)));
}

public static class WindowInventory
{
    public static WindowListItem[] List(Snapshot snapshot, IEnumerable<VmwareDiscovered>? discovered = null)
    {
        var result = new List<WindowListItem>();
        foreach (var target in snapshot.Targets.Where(t => t.Kind != "VMware")) {
            var binding = snapshot.Bindings.FirstOrDefault(b => b.TargetKey == target.Key);
            if (binding is null && target.Kind == "Process" && target.Processes.Any(p => snapshot.Bindings.Any(b => b.IncludeChildren && b.Descendants.Contains(p)))) continue;
            var status = snapshot.Statuses.FirstOrDefault(s => s.TargetKey == target.Key);
            var visible = target;
            if (target.Kind == "Process" && binding?.IncludeChildren == true) {
                var live = binding.Processes.Concat(binding.Descendants).Distinct().Where(p => LdPlayer.Identity(p.Pid) == p).ToArray();
                string name = target.Name;
                foreach (var identity in live.OrderByDescending(p => p.CreatedFileTime)) {
                    try {
                        using var process = System.Diagnostics.Process.GetProcessById(identity.Pid);
                        if (process.MainWindowHandle != 0 && !string.IsNullOrWhiteSpace(process.MainWindowTitle)) { name = process.MainWindowTitle; break; }
                    } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
                visible = target with { Name = name, Processes = live, Running = live.Length > 0 || status?.State.StartsWith("接續遊戲中", StringComparison.Ordinal) == true,
                    Detail = target.Detail + (live.Length == 0 ? "\n正在接續遊戲，保留原 IP。" : "\n目前成員 PID：" + string.Join(", ", live.Select(p => p.Pid))) };
            }
            result.Add(new(visible, binding?.ProxyId ?? Guid.Empty, false, binding is null ? "未設定個別規則" : binding.AutoManaged ? "自動代理" : "手動分配",
                status?.State ?? (target.Kind is "GameGroup" or "GameOrphan" ? "未分配 · 已封鎖" : snapshot.GlobalProxy.Enabled ? "依全局代理規則" : target.Running ? "執行中 · 未代理" : "未啟動"), visible.Detail,
                status?.ConnectedAt, status?.Sent, status?.Received, status?.TcpConnections, status?.UdpAssociations));
        }
        var observed = (discovered ?? []).GroupBy(v => v.Machine.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var vmware = snapshot.Vmware;
        var mappings = vmware is not null && !vmware.Settings.RuleDeleted ? vmware.Settings.Machines : [];
        foreach (string path in mappings.Select(m => m.VmxPath).Concat(observed.Keys).Distinct(StringComparer.OrdinalIgnoreCase)) {
            var mapping = mappings.FirstOrDefault(m => m.VmxPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            var runtime = mapping is null ? null : vmware?.Machines.FirstOrDefault(m => m.VmxPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            var seen = observed.GetValueOrDefault(path);
            var process = runtime is null ? seen?.Process : runtime.Process;
            string name = mapping?.Name is { Length: > 0 } savedName ? savedName : seen?.Machine.Name ?? Path.GetFileNameWithoutExtension(path);
            var target = new TargetInstance("vmware:" + Path.GetFullPath(path).ToUpperInvariant(), name, "VMware", path, -1, process.HasValue, process is { } running ? [running] : []);
            bool local = runtime?.UseLocalNetwork ?? mapping?.UseLocalNetwork ?? false;
            Guid proxy = local ? Guid.Empty : runtime?.ProxyId ?? (vmware?.Settings.ByStartup == false ? mapping?.ProxyId ?? Guid.Empty : Guid.Empty);
            string state = !snapshot.Diagnostics.ServiceOnline ? "服務未連線 · 狀態待確認" : mapping is null ? process.HasValue ? "執行中 · 未設定規則" : "未啟動" :
                vmware!.Settings.PendingPreparation ? vmware.Settings.PreparationError.Length > 0 ? "準備未完成 · 未代理" : "等待網路準備 · 未代理" :
                !process.HasValue ? "未啟動" : !vmware.Prepared ? "未準備 · 未代理" : !vmware.Settings.Enabled ? "已暫停 · 已封鎖" :
                !vmware.Running ? "閘道未就緒 · 未代理" : runtime?.State ?? "等待程序辨識 · 已封鎖";
            string detail = path + (mapping?.GuestIp is { Length: > 0 } ip ? "\n客體 IP：" + ip : "") + (mapping is null ? "\n尚未設定 VMware 規則" : "\n" + vmware!.Detail);
            if (process is { } instance) detail += "\nPID：" + instance.Pid + " · 啟動：" + DateTime.FromFileTimeUtc(instance.CreatedFileTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            bool metrics = snapshot.Diagnostics.ServiceOnline && process.HasValue && runtime?.StatsAvailable == true;
            result.Add(new(target, proxy, local, mapping is null ? "未設定規則" : "VMware 虛擬機代理", state, detail,
                process.HasValue && vmware?.Prepared == true && vmware.Running && vmware.Settings.Enabled && !vmware.Settings.PendingPreparation ? runtime?.ConnectedAt : null,
                metrics ? runtime!.Sent : null, metrics ? runtime!.Received : null, metrics ? runtime!.TcpConnections : null, metrics ? runtime!.UdpAssociations : null));
        }
        return result.OrderByDescending(r => r.Target.Running).ThenBy(r => r.Target.Processes.FirstOrDefault().CreatedFileTime).ThenBy(r => r.Target.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

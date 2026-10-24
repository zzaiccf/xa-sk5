using System.Management;
using System.Net;
using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed partial class Controller
{
    private VmwareRuntime[] vmwareRuntime = [];
    private string vmwareError = "尚未準備專用網路";
    private bool? vmwareForwarding;
    private bool vmwareDiscoveryHealthy;
    private bool vmwareSetupFailed;
    private string VmwareJournal => Path.Combine(directory, "vmware-network-backup.json");
    private VmwareDiscovered[] ListVmware()
    {
        var found = new List<VmwareDiscovered>();
        using var search = new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='vmware-vmx.exe'");
        using var processes = search.Get();
        foreach (ManagementObject row in processes) using (row) {
            int pid = checked((int)(uint)row["ProcessId"]);
            if (!IsOwned(pid) || LdPlayer.Identity(pid) is not { } identity || !Path.GetFileName((string?)row["ExecutablePath"] ?? "").Equals("vmware-vmx.exe", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string path in VmwareVmx.CommandArguments((string?)row["CommandLine"] ?? "").Where(a => Path.IsPathFullyQualified(a) && a.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase))) {
                var vm = VmwareVmx.Read(path);
                found.Add(new(vm, identity, "執行中 · PID " + pid + " · " + DateTime.FromFileTimeUtc(identity.CreatedFileTime).ToLocalTime().ToString("MM/dd HH:mm:ss")));
            }
        }
        using var profile = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + OwnerSid);
        string? root = profile?.GetValue("ProfileImagePath") as string;
        if (root is not null) found.AddRange(VmwareDiscovery.Registered(Path.Combine(Environment.ExpandEnvironmentVariables(root), "AppData", "Roaming", "VMware", "inventory.vmls")));
        return found.DistinctBy(v => v.Machine.Path, StringComparer.OrdinalIgnoreCase).OrderBy(v => v.Process?.CreatedFileTime ?? long.MaxValue).ToArray();
    }
    private Task<bool> SetupVmware(VmwareSettings request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireVmwareLocalSupport(request);
        if (VmwarePrepared) {
            var existing = config.Vmware;
            if (!request.Machines.Select(m => VmwareVmx.NormalizePath(m.VmxPath)).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(existing.Machines.Select(m => m.VmxPath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("新增或移除 VM 前，請在進階設定回復原網路，再重新選取並儲存。");
            SetVmware(existing with { Enabled = true, RuleDeleted = false, ByStartup = request.ByStartup, PerProxy = request.PerProxy, OverflowToLocal = request.OverflowToLocal, Machines = existing.Machines.Select(m => { var route = request.Machines.Single(r => r.VmxPath.Equals(m.VmxPath, StringComparison.OrdinalIgnoreCase)); return m with { ProxyId = route.ProxyId, UseLocalNetwork = route.UseLocalNetwork }; }).ToArray() });
            return Task.FromResult(true);
        }
        if (!VmwareNetwork.CanPrepare(VmwareJournal)) throw new InvalidOperationException("上次網路變更未完成，請在進階設定回復後再儲存。");
        if (request.Machines.Length is < 1 or > 10) throw new InvalidOperationException("請選取 1–10 台虛擬機。");
        var machines = request.Machines.Select(m => VmwareVmx.Read(m.VmxPath)).ToArray();
        var draft = request with { Enabled = true, RuleDeleted = false, PendingPreparation = true, NeedsNetworkPlan = true, PreparationError = "",
            Machines = machines.Select((m, i) => new VmwareMapping(m.Path, "", request.Machines[i].ProxyId) { Uuid = m.Uuid, Mac = m.Mac, Name = m.Name, UseLocalNetwork = request.Machines[i].UseLocalNetwork }).ToArray() };
        VmwarePlanner.Validate(draft, config.Proxies);
        var original = config.Vmware; config.Vmware = draft;
        try { store.Save(config); } catch { config.Vmware = original; throw; }
        vmwareRuntime = []; vmwareSetupFailed = false;
        vmwareError = "規則已儲存；等待背景檢查網路準備狀態，目前尚未代理。";
        return Task.FromResult(true);
    }
    private VmwareSettings PlanVmwareRequest(VmwareSettings request)
    {
        var machines = request.Machines.Select(m => VmwareVmx.Read(m.VmxPath)).ToArray();
        var known = ListVmware();
        var candidates = VmwareNetwork.Adapters().Where(a => request.AdapterId.Length == 0 || a.Id == request.AdapterId).ToArray();
        VmwareSettings? selected = null; string reason = "找不到可用的 VMware Host-only 網路，請在進階設定檢查。";
        foreach (var adapter in candidates) {
            try {
                string vnet = System.Text.RegularExpressions.Regex.Match(adapter.Name, @"VMnet\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
                if (known.Any(v => !machines.Any(m => m.Path.Equals(v.Machine.Path, StringComparison.OrdinalIgnoreCase)) &&
                    (v.Machine.Vnet.Equals(vnet, StringComparison.OrdinalIgnoreCase) || v.Machine.Connection.Equals("hostonly", StringComparison.OrdinalIgnoreCase) && vnet.Equals("VMnet1", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException(vnet + " 已由未選取的虛擬機使用，請另選專用網路。");
                string[] macs = machines.Select(m => { _ = VmwareVmx.Configure(VmwareVmx.ReadText(m.Path), vnet, out string mac); return mac; }).ToArray();
                var ips = VmwareDhcp.AvailableAddresses(File.ReadAllText(VmwareNetwork.DhcpPath), adapter.Address, macs);
                var candidate = request with { Enabled = true, RuleDeleted = false, PendingPreparation = true, NeedsNetworkPlan = false, PreparationError = "", AdapterId = adapter.Id, Machines = machines.Select((m, i) => new VmwareMapping(m.Path, ips[i], request.Machines[i].ProxyId) { Uuid = m.Uuid, Mac = m.Mac, Name = m.Name, UseLocalNetwork = request.Machines[i].UseLocalNetwork }).ToArray() };
                VmwarePlanner.Validate(candidate, config.Proxies);
                _ = VmwareNetwork.Plan(candidate, VmwareJournal, requirePoweredOff: false);
                selected = candidate; break;
            } catch (Exception ex) when (ex is IOException or InvalidOperationException) { reason = ex.Message; }
        }
        if (selected is null) throw new InvalidOperationException(reason);
        return selected;
    }
    private async Task CompleteVmwareSetup(CancellationToken ct)
    {
        var pending = config.Vmware;
        if (!pending.PendingPreparation || vmwareSetupFailed || VmwarePrepared) return;
        try {
            if (!VmwareSetupReadiness.CanPrepare(pending, out string detail)) { vmwareError = detail; return; }
            if (pending.NeedsNetworkPlan) {
                var planned = PlanVmwareRequest(pending); config.Vmware = planned;
                try { store.Save(config); } catch { config.Vmware = pending; throw; }
                pending = planned;
            }
            vmwareError = "正在自動備份並設定代理網路，請稍候再啟動 VM。";
            await PrepareVmware(pending with { PendingPreparation = false }, ct);
            SetVmware(config.Vmware with { Enabled = true });
            vmwareError = "已儲存並啟用；下次啟動所選虛擬機時自動分配代理。";
        } catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) {
            vmwareSetupFailed = true; vmwareError = "規則已儲存；自動準備未完成：" + ex.Message + " 請處理後再按儲存重試。";
            config.Vmware = config.Vmware with { PreparationError = ex.Message };
            try { store.Save(config); } catch (IOException error) { Log("準備失敗原因未能寫入設定：" + error.Message); }
        }
    }
    private void PauseVmware()
    {
        if (VmwarePrepared) SetVmware(config.Vmware with { Enabled = false });
        else { config.Vmware = config.Vmware with { Enabled = false, PendingPreparation = false }; store.Save(config); vmwareRuntime = []; }
        vmwareError = "已暫停代理；尚未準備的網路不會變更。";
    }
    private bool VmwarePrepared { get { try { return VmwareNetwork.Prepared(VmwareJournal); } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { vmwareError = "專用網路備份異常，請保留備份並回復：" + ex.Message; return false; } } }
    private async Task<bool> PrepareVmware(VmwareSettings settings, CancellationToken ct)
    {
        RequireVmwareLocalSupport(settings);
        VmwarePlanner.Validate(settings, config.Proxies, requirePlanned: true);
        if (!VmwareNetwork.CanPrepare(VmwareJournal)) throw new InvalidOperationException("已有專用網路備份，請先關機並回復原網路。");
        if (settings.Machines.Length == 0) throw new InvalidOperationException("請先加入 VMware 虛擬機。");
        if (!NativeEngine.SupportsVmwareGateway) throw new InvalidOperationException("請先套用支援 VMware 的原生核心。");
        foreach (var mapping in settings.Machines) VmwareNetwork.RequirePoweredOff(mapping.VmxPath);
        var adapter = VmwareNetwork.Adapter(settings.AdapterId);
        var plan = await Task.Run(() => VmwareNetwork.Plan(settings, VmwareJournal), ct);
        ct.ThrowIfCancellationRequested();
        EnsureEngine();
        if (NativeEngine.Core_ReplaceGatewayRules([], 0) == 0 || NativeEngine.Core_StartGateway(Ip(VmwareDhcp.Address(VmwareDhcp.Number(adapter.Address) & 0xffffff00)), Ip(adapter.Mask), Ip(adapter.Address), (uint)adapter.Index) == 0)
            throw new IOException("專用網路攔截尚未就緒，沒有變更 VMware 網路。");
        try { await Task.Run(() => VmwareNetwork.Apply(plan, VmwareJournal), ct); }
        catch {
            try { VmwareNetwork.SetForwarding(settings.AdapterId, false); vmwareForwarding = false; NativeEngine.Core_StopGateway(); } catch { }
            throw;
        }
        var previous = config.Vmware;
        config.Vmware = settings with { Enabled = false, Machines = settings.Machines.Select(m => { var vm = VmwareVmx.Read(m.VmxPath); return m with { Mac = vm.Mac, Uuid = vm.Uuid }; }).ToArray() };
        try { store.Save(config); } catch { config.Vmware = previous; VmwareNetwork.Restore(VmwareJournal); NativeEngine.Core_StopGateway(); throw; }
        vmwareRuntime = []; vmwareError = "專用網路已準備；請啟用分配後啟動虛擬機。"; ApplyVmwareRules(); return true;
    }
    private void SetVmware(VmwareSettings settings)
    {
        RequireVmwareLocalSupport(settings);
        VmwarePlanner.Validate(settings, config.Proxies, requirePlanned: true);
        if (!VmwarePrepared) throw new InvalidOperationException("請先準備專用代理網路。");
        var original = config.Vmware;
        if (original.ByStartup && settings.ByStartup && !settings.OverflowToLocal && vmwareRuntime.Where(r => r.Process.HasValue && r.ProxyId != Guid.Empty).GroupBy(r => r.ProxyId).Any(g => g.Count() > settings.PerProxy))
            throw new InvalidOperationException("目前使用中虛擬機超過新名額，請先關閉部分虛擬機再減少數量。");
        if (settings.AdapterId != original.AdapterId || !settings.Machines.Select(m => (Path.GetFullPath(m.VmxPath).ToUpperInvariant(), m.GuestIp)).Order().SequenceEqual(original.Machines.Select(m => (Path.GetFullPath(m.VmxPath).ToUpperInvariant(), m.GuestIp)).Order()))
            throw new InvalidOperationException("變更虛擬機或客體 IP 前，請先關機並回復原網路，再重新準備。");
        config.Vmware = settings with { Machines = settings.Machines.Select(m => { var old = original.Machines.Single(p => p.VmxPath.Equals(m.VmxPath, StringComparison.OrdinalIgnoreCase)); return m with { Mac = old.Mac, Uuid = old.Uuid }; }).ToArray() };
        if (original.ByStartup != settings.ByStartup) vmwareRuntime = [];
        try { DiscoverVmware(); ApplyVmwareRules(); store.Save(config); }
        catch { config.Vmware = original; ApplyVmwareRules(); throw; }
    }
    private void RestoreVmware()
    {
        foreach (var mapping in config.Vmware.Machines) VmwareNetwork.RequirePoweredOff(mapping.VmxPath);
        VmwareNetwork.Restore(VmwareJournal);
        config.Vmware = config.Vmware with { Enabled = false, PendingPreparation = false }; store.Save(config);
        if (NativeEngine.SupportsVmwareGateway) { NativeEngine.Core_ReplaceGatewayRules([], 0); NativeEngine.Core_StopGateway(); }
        vmwareRuntime = []; vmwareError = "已回復原 VMware 網路。";
    }
    private static uint Ip(string ip) => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes());
    private void DiscoverVmware()
    {
        try { DiscoverVmwareChecked(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // Preserve reservations across a transient WMI failure; still revoke all forwarding.
            vmwareDiscoveryHealthy = false;
            vmwareRuntime = vmwareRuntime.Select(r => r with { State = "程序辨識未完成 · 已封鎖", ConnectedAt = null }).ToArray(); vmwareError = "VMware 程序辨識未完成：" + ex.Message;
            if (NativeEngine.SupportsVmwareGateway) NativeEngine.Core_ReplaceGatewayRules([], 0);
        }
    }
    private void DiscoverVmwareChecked()
    {
        if (config.Vmware.RuleDeleted) {
            vmwareRuntime = []; vmwareDiscoveryHealthy = false;
            if (config.VmwareSessions.Length > 0) { config.VmwareSessions = []; childrenDirty = true; }
            return;
        }
        if (config.Vmware.Machines.Length == 0) return;
        var live = config.Vmware.Machines.ToDictionary(m => m.VmxPath, _ => (ProcessIdentity?)null, StringComparer.OrdinalIgnoreCase);
        var duplicate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var search = new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='vmware-vmx.exe'");
        using var rows = search.Get();
        foreach (ManagementObject row in rows) using (row) {
            int pid = checked((int)(uint)row["ProcessId"]);
            if (!IsOwned(pid) || LdPlayer.Identity(pid) is not { } identity || !Path.GetFileName((string?)row["ExecutablePath"] ?? "").Equals("vmware-vmx.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var paths = VmwareVmx.CommandArguments((string?)row["CommandLine"] ?? "").Where(a => a.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(a)).Select(Path.GetFullPath).ToArray();
            foreach (string path in paths.Where(live.ContainsKey)) { if (live[path] is not null) duplicate.Add(path); else live[path] = identity; }
        }
        foreach (string path in duplicate) live[path] = null;
        var next = VmwarePlanner.Assign(config.Vmware, config.Proxies, live, vmwareRuntime);
        if (!next.Select(r => (r.VmxPath, r.Process, r.ProxyId, r.UseLocalNetwork)).SequenceEqual(vmwareRuntime.Select(r => (r.VmxPath, r.Process, r.ProxyId, r.UseLocalNetwork)))) { config.VmwareSessions = next; childrenDirty = true; }
        vmwareRuntime = next;
        vmwareDiscoveryHealthy = true;
        if (!config.Vmware.PendingPreparation) vmwareError = "";
    }
    private void ApplyVmwareRules()
    {
        try { ApplyVmwareRulesChecked(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (NativeEngine.SupportsVmwareGateway) NativeEngine.Core_ReplaceGatewayRules([], 0);
            try { SuspendVmwareNetwork(); } catch { }
            vmwareRuntime = vmwareRuntime.Select(r => r with { State = "網路異常 · 已封鎖", ConnectedAt = null }).ToArray(); vmwareError = ex.Message;
        }
    }
    private void ApplyVmwareRulesChecked()
    {
        if (!NativeEngine.SupportsVmwareGateway) return;
        if (config.Vmware.Machines.Length == 0 || !VmwarePrepared) {
            NativeEngine.Core_ReplaceGatewayRules([], 0);
            vmwareRuntime = vmwareRuntime.Select(r => r with { State = "未準備 · 未代理", ConnectedAt = null }).ToArray(); return;
        }
        var adapter = VmwareNetwork.Adapter(config.Vmware.AdapterId);
        var rules = new List<NativeEngine.GatewayRule>();
        foreach (var mapping in config.Vmware.Machines) {
            var runtime = vmwareRuntime.FirstOrDefault(r => r.VmxPath.Equals(mapping.VmxPath, StringComparison.OrdinalIgnoreCase));
            bool identity = false;
            try { var vm = VmwareVmx.Read(mapping.VmxPath); identity = vm.Uuid == mapping.Uuid && vm.Mac == mapping.Mac && vm.Connection.Equals("custom", StringComparison.OrdinalIgnoreCase) && adapter.Name.EndsWith(vm.Vnet, StringComparison.OrdinalIgnoreCase) && vm.Vnet.Length > 0; } catch (IOException) { }
            bool duplicateExit = runtime is not null && (DuplicateExit(runtime.ProxyId) || VmwarePlanner.HasDuplicateExit(runtime.ProxyId, vmwareRuntime, probes));
            bool local = runtime?.UseLocalNetwork == true;
            bool ready = !config.Vmware.RuleDeleted && vmwareDiscoveryHealthy && config.Vmware.Enabled && identity && runtime?.Process is { } process && LdPlayer.Identity(process.Pid) == process && (local ? NativeEngine.SupportsVmwareLocalNetwork : runtime.ProxyId != Guid.Empty && Healthy(runtime.ProxyId) && !duplicateExit);
            rules.Add(new() { Source = Ip(mapping.GuestIp), Pid = (uint)(runtime?.Process?.Pid ?? 0), Created = (ulong)(runtime?.Process?.CreatedFileTime ?? 0), Proxy = local ? 0u : nativeIds.GetValueOrDefault(runtime?.ProxyId ?? Guid.Empty), Blocked = ready ? local ? 2u : 0u : 1u });
            if (runtime is not null) {
                int i = Array.IndexOf(vmwareRuntime, runtime);
                vmwareRuntime[i] = runtime with { State = !identity ? "網卡身分變更 · 已封鎖" : !config.Vmware.Enabled ? "已暫停 · 已封鎖" : runtime.Process is null ? "等待啟動" : local ? ready ? "本地網路已套用" : "本地網路未就緒 · 已封鎖" : runtime.ProxyId == Guid.Empty ? "容量不足 · 已封鎖" : duplicateExit ? "實際 IP 重複 · 已封鎖" : !ready ? "等待檢測 · 已封鎖" : "代理規則已套用", ConnectedAt = ready ? runtime.ConnectedAt ?? DateTimeOffset.UtcNow : null };
            }
        }
        if (NativeEngine.Core_ReplaceGatewayRules(rules.ToArray(), (uint)rules.Count) == 0) throw new IOException("核心拒絕 VMware 分流規則。");
        if (NativeEngine.Core_IsRunning() != 0 && NativeEngine.Core_StartGateway(Ip(VmwareDhcp.Address(VmwareDhcp.Number(adapter.Address) & 0xffffff00)), Ip(adapter.Mask), Ip(adapter.Address), (uint)adapter.Index) == 0) throw new IOException("無法啟動專用 VMware 閘道攔截。");
        bool forwarding = !config.Vmware.RuleDeleted && vmwareDiscoveryHealthy && config.Vmware.Enabled && NativeEngine.Core_GatewayRunning() != 0;
        if (vmwareForwarding != forwarding) { VmwareNetwork.SetForwarding(config.Vmware.AdapterId, forwarding); vmwareForwarding = forwarding; }
    }
    private void SuspendVmwareNetwork()
    {
        if (config.Vmware.AdapterId.Length > 0 && VmwarePrepared) { VmwareNetwork.SetForwarding(config.Vmware.AdapterId, false); vmwareForwarding = false; }
    }
    private static void RequireVmwareLocalSupport(VmwareSettings settings)
    {
        if ((settings.OverflowToLocal || settings.Machines.Any(m => m.UseLocalNetwork)) && !NativeEngine.SupportsVmwareLocalNetwork)
            throw new InvalidOperationException("本地網路需要新版代理元件，請安裝新版私人測試安裝包。");
    }
    private VmwareState VmwareSnapshot()
    {
        bool prepared = VmwarePrepared;
        bool pending; try { pending = !VmwareNetwork.CanPrepare(VmwareJournal); } catch { pending = true; }
        var visible = vmwareRuntime.Select(r => {
            if (!NativeEngine.Present || r.Process is not { } process || LdPlayer.Identity(process.Pid) != process) return r with { StatsAvailable = false };
            NativeEngine.Core_GetProcessStats((uint)process.Pid, (ulong)process.CreatedFileTime, out var traffic, out uint tcp, out uint udp);
            return r with { Sent = (long)traffic.Sent, Received = (long)traffic.Received, TcpConnections = tcp, UdpAssociations = udp, StatsAvailable = true };
        }).ToArray();
        return new(config.Vmware, visible, prepared, NativeEngine.SupportsVmwareGateway && NativeEngine.Core_GatewayRunning() != 0,
            pending && !prepared ? "網路準備未完成，請將虛擬機關機後在進階設定回復原網路。" : config.Vmware.PendingPreparation && config.Vmware.PreparationError.Length > 0 ? "規則已儲存；準備未完成：" + config.Vmware.PreparationError : vmwareError.Length > 0 ? vmwareError : prepared ? "代理已設定；開機時自動分配。客體實際出口仍需另行核對。" : "勾選虛擬機並按儲存，程式會自動完成網路設定。") { HasBackup = pending };
    }
}

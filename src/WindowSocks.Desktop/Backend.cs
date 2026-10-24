using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public class Backend
{
    private readonly ConfigStore store;
    private readonly SavedConfig config;
    private readonly Func<Command, CancellationToken, Task<Reply>>? serviceTransport;
    private readonly Dictionary<Guid, ProbeResult> probes = new();
    private bool supportsTcpFirst;
    private readonly SemaphoreSlim supplementalGate = new(ProbeTiming.MaxSupplementalProbes, ProbeTiming.MaxSupplementalProbes);
    private CancellationTokenSource supplementalLifetime = new();
    public bool Online { get; protected set; }
    public bool HasLocalProxies => config.Proxies.Count > 0;
    public string ConsolePath { get => config.LdConsole; set { config.LdConsole = value; store.Save(config); } }
    public string MuMuConsolePath { get => config.MuMuConsole; set { config.MuMuConsole = value; store.Save(config); } }
    public Backend() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks")) { }
    public Backend(string configurationDirectory, Func<Command, CancellationToken, Task<Reply>>? serviceTransport = null)
    {
        this.serviceTransport = serviceTransport;
        store = new(configurationDirectory, DataProtectionScope.CurrentUser);
        config = store.Load(); foreach (var b in config.Bindings) b.Enabled = false; foreach (var p in config.LastProbes) probes[p.ProxyId] = p;
    }
    private async Task<T> Call<T>(Command command, CancellationToken ct = default)
    {
        if (serviceTransport is null) return await PipeTransport.CallAsync<T>(command, ct);
        var reply = await serviceTransport(command, ct);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error);
        return Json.Read<T>(reply.Data);
    }
    public virtual async Task<Snapshot> Snapshot(TargetInstance[] targets)
    {
        try
        {
            var snapshot = await Call<Snapshot>(Command.Create("snapshot", new { }));
            if (!Online) StopProbes();
            Online = true; supportsTcpFirst = snapshot.SupportsTcpFirstProbes;
            await Call<bool>(Command.Create("syncTargets", snapshot.SupportsFixedEmulators ? targets : targets.Where(t => t.Kind != "MuMu").ToArray()));
            if (snapshot.Vmware is { } vmware && System.Text.Json.JsonSerializer.Serialize(config.Vmware, Json.Options) != System.Text.Json.JsonSerializer.Serialize(vmware.Settings, Json.Options)) {
                config.Vmware = vmware.Settings; store.Save(config);
            }
            return snapshot with { Proxies = ProxyExpirationMetadata.Apply(snapshot.Proxies, config.ServiceProxyExpirations), Targets = targets.Concat(snapshot.Targets).DistinctBy(t => t.Key).ToArray(),
                AutoProxy = PendingForService(snapshot.Proxies) ?? snapshot.AutoProxy,
                HasPendingEmulatorConfiguration = config.PendingEmulatorRule is not null,
                AutoProxyStatus = config.PendingEmulatorRule is null ? snapshot.AutoProxyStatus : "模擬器配置已儲存在本機 · 更新背景服務後請開啟自動代理套用" };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { Online = false; }
        var savedTargets = config.Bindings.Select(b => new TargetInstance(b.TargetKey, b.Name, b.Kind, b.Path, b.Index, b.Processes.Any(p => LdPlayer.Identity(p.Pid) == p), b.Processes));
        return new(config.Proxies.Select(p => p.Redacted()).ToArray(), config.Bindings.ToArray(), targets.Concat(savedTargets).DistinctBy(t => t.Key).ToArray(), probes.Values.ToArray(), config.Bindings.Select(b => new RuntimeStatus(b.TargetKey, "待服務套用 · 未代理", b.IncludeChildren ? "僅儲存分配；啟用後涵蓋子程序" : "僅儲存分配，不含子程序", 0, 0)).ToArray(),
            new(false, NativeEngine.DriverPresent, NativeEngine.Present, false, AppReleasePackage.CurrentVersion + " 預覽版", "離線設定模式：可管理與檢測代理，尚未攔截任何窗口流量。", [])) {
                AutoProxy = config.PendingEmulatorRule ?? config.AutoProxy,
                Vmware = config.Vmware.Machines.Length == 0 ? null : new(config.Vmware, [], false, false, "背景服務未連線；已儲存規則保留，執行狀態待確認。"),
                HasPendingEmulatorConfiguration = config.PendingEmulatorRule is not null,
                AutoProxyStatus = config.PendingEmulatorRule is null ? "離線模式 · 尚未啟用自動代理" : "模擬器配置已儲存在本機 · 背景服務連線後請套用"
            };
    }
    public virtual async Task<Guid[]> Import(string text)
    {
        var imported = ProxyImport.Parse(text);
        if (Online) return await ImportToService(text, imported);
        else {
            var ids = ProxyImport.ApplyTo(config.Proxies, imported); store.Save(config); return ids;
        }
    }
    private async Task<Guid[]> ImportToService(string text, IReadOnlyCollection<ProxyProfile> imported)
    {
        var before = await Call<Snapshot>(Command.Create("snapshot", new { }));
        if (!before.SupportsProxyExpiration) ProxyExpirationMetadata.ValidateLegacyImport(before.Proxies, imported);
        await Call<int>(Command.Create("import", new ImportRequest(before.SupportsProxyExpiration ? text : ProxyImport.ExportForServiceTransfer(imported, includeExpiration: false))));
        var after = await Call<Snapshot>(Command.Create("snapshot", new { }));
        if (!after.SupportsProxyExpiration) {
            ProxyExpirationMetadata.Remember(before.Proxies, after.Proxies, imported, config.ServiceProxyExpirations);
            store.Save(config);
        }
        return after.Proxies.Where(p => imported.Any(i => ProxyImport.SameEndpointAccount(i, p))).Select(p => p.Id).ToArray();
    }
    public virtual async Task<ProbeResult> Probe(Guid id, CancellationToken ct = default)
    {
        if (Online) return await Call<ProbeResult>(Command.Create(supportsTcpFirst ? "probeTcp" : "probe", new ProxyRequest(id)), ct);
        var profile = config.Proxies.First(p => p.Id == id) with { };
        var result = await ProxyProbe.RunTcpAsync(profile, ct: ct); ct.ThrowIfCancellationRequested();
        if (!config.Proxies.Any(p => p.Id == id && ProxyImport.SameProxy(p, profile))) throw new InvalidOperationException("節點已變更，請重新檢測。");
        probes[id] = result; config.LastProbes = probes.Values.ToList(); store.Save(config);
        if (result.UdpPending) _ = SupplementLocal(profile, result, supplementalLifetime.Token);
        return result;
    }
    public void StopProbes()
    {
        supplementalLifetime.Cancel(); supplementalLifetime.Dispose(); supplementalLifetime = new();
    }
    private bool CurrentLocal(ProxyProfile profile, ProbeResult tcp) =>
        probes.GetValueOrDefault(profile.Id)?.CheckId == tcp.CheckId && config.Proxies.Any(p => p.Id == profile.Id && ProxyImport.SameProxy(p, profile));
    private async Task SupplementLocal(ProxyProfile profile, ProbeResult tcp, CancellationToken ct)
    {
        bool entered = false;
        try {
            await Task.Yield(); await supplementalGate.WaitAsync(ct); entered = true;
            if (!CurrentLocal(profile, tcp)) return;
            var result = await ProxyProbe.SupplementAsync(profile, tcp, ct: ct); ct.ThrowIfCancellationRequested();
            if (CurrentLocal(profile, tcp) && DateTimeOffset.UtcNow - tcp.At < ProbeTiming.HealthValidity) {
                probes[profile.Id] = result; config.LastProbes = probes.Values.ToList(); store.Save(config);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (CurrentLocal(profile, tcp)) probes[profile.Id] = tcp with { UdpPending = false, At = DateTimeOffset.UtcNow, Message = "UDP／所在地補測未完成，可手動重試。" };
        } finally { if (entered) supplementalGate.Release(); }
    }
    public async Task<string> Export()
    {
        if (!Online) return ProxyImport.Export(config.Proxies);
        var state = await Call<Snapshot>(Command.Create("snapshot", new { }));
        return ProxyImport.Export(ProxyExpirationMetadata.Apply(state.Proxies, config.ServiceProxyExpirations));
    }
    public async Task<byte[]> Backup() => Online ? ConfigStore.WithProxyExpirations(Convert.FromBase64String(await Call<string>(Command.Create("backup", new { }))), config.ServiceProxyExpirations) : store.Backup(config);
    public async Task<DiagnosticBundle> Diagnose()
    {
        if (!Online) throw new InvalidOperationException("請先連線背景服務，再擷取程序與連線診斷。");
        return await Call<DiagnosticBundle>(Command.Create("diagnose", new { }));
    }
    public async Task Delete(Guid id)
    {
        if (Online) {
            await Call<bool>(Command.Create("deleteProxy", new ProxyRequest(id)));
            if (config.ServiceProxyExpirations.Remove(id)) store.Save(config);
        }
        else {
            if (config.Bindings.Any(b => b.ProxyId == id)) throw new InvalidOperationException("請先解除使用此代理的分配。");
            config.Proxies.RemoveAll(p => p.Id == id); probes.Remove(id); config.LastProbes = probes.Values.ToList(); store.Save(config);
        }
    }
    public virtual async Task<ClearProxyPoolResult> ClearProxyPool()
    {
        if (Online) {
            await RequireBulkActions();
            var result = await Call<ClearProxyPoolResult>(Command.Create("clearProxyPool", new { }));
            foreach (Guid id in result.RemovedIds) config.ServiceProxyExpirations.Remove(id);
            store.Save(config); return result;
        }
        ProxyPoolUsage.RequireUnused(config);
        Guid[] ids = config.Proxies.Select(p => p.Id).ToArray();
        var next = config with { Proxies = [], LastProbes = [] };
        store.Save(next); StopProbes();
        config.Proxies = next.Proxies; config.LastProbes = next.LastProbes; probes.Clear();
        return new(ids);
    }
    private async Task RequireBulkActions()
    {
        if (!Online) throw new InvalidOperationException("背景服務未連線，無法關閉代理進程。");
        if (!(await Call<Snapshot>(Command.Create("snapshot", new { }))).SupportsBulkActions)
            throw new InvalidOperationException("請先套用 0.1.22 以上背景服務，才能使用批次清除及關閉進程。");
    }
    public virtual async Task<CloseProxyProcessesPlan> PrepareCloseProxyProcesses()
    {
        await RequireBulkActions();
        return await Call<CloseProxyProcessesPlan>(Command.Create("prepareCloseProxyProcesses", new { }));
    }
    public virtual async Task<CloseProxyProcessesResult> CloseProxyProcesses(Guid token)
    {
        if (!Online) throw new InvalidOperationException("背景服務未連線，無法關閉代理進程。");
        return await Call<CloseProxyProcessesResult>(Command.Create("closeProxyProcesses", new CloseProxyProcessesRequest(token)));
    }
    public async Task Assign(BatchBindRequest request)
    {
        if (Online) {
            if (request.Items.Any(i => i.Target.Kind == "MuMu") && !(await Call<Snapshot>(Command.Create("snapshot", new { }))).SupportsFixedEmulators)
                throw new InvalidOperationException("MuMu 代理需要 0.1.10 以上背景服務，請先更新；固定配置可先儲存在本機。");
            await Act("bindBatch", request); return;
        }
        foreach (var item in request.Items)
            if (item.Target.Processes.Any(p => LdPlayer.Identity(p.Pid) != p)) throw new InvalidOperationException("選取的程序已結束，請重新選取。");
        config.Bindings = AssignmentPlanner.Prepare(config.Bindings, config.Proxies, request); store.Save(config);
    }
    public virtual async Task<bool> Edit(EditProxyRequest request)
    {
        if (Online) {
            var state = await Call<Snapshot>(Command.Create("snapshot", new { }));
            if (!state.SupportsProxyEditing) throw new InvalidOperationException("目前背景服務尚未支援修改節點，請套用新版背景服務後再試。");
            bool changed = await Call<bool>(Command.Create("editProxy", request));
            config.ServiceProxyExpirations.Remove(request.ProxyId); store.Save(config); return changed;
        }
        var next = ProxyEditing.Prepare(config.Proxies, request);
        int index = config.Proxies.FindIndex(p => p.Id == request.ProxyId);
        bool connectionChanged = !ProxyImport.SameProxy(config.Proxies[index], next);
        config.Proxies[index] = next;
        if (connectionChanged) probes.Remove(next.Id);
        config.LastProbes = probes.Values.ToList(); store.Save(config); return connectionChanged;
    }
    public async Task<bool> ConfigureAutomatic(AutoProxyRule rule)
    {
        rule = ProcessHandoffPolicy.Automatic(rule) with { Configured = true };
        AutoProxyPlanner.Validate(rule);
        if (rule.Kind is "Emulator" or "EmulatorSequence") {
            var state = Online ? await Call<Snapshot>(Command.Create("snapshot", new { })) : null;
            if (state is null || (rule.Kind == "EmulatorSequence" ? !state.SupportsSequentialEmulators : !state.SupportsFixedEmulators)) {
                config.PendingEmulatorRule = rule; store.Save(config); return false;
            }
        }
        await Act("setAutoProxy", rule);
        config.PendingEmulatorRule = null; store.Save(config); return true;
    }
    public async Task DeleteRule(string key)
    {
        if (Online) {
            var state = await Call<Snapshot>(Command.Create("snapshot", new { }));
            if (!state.SupportsRuleManagement) throw new InvalidOperationException("請先安裝新版私人測試版，才能刪除保存的代理規則。");
            await Call<bool>(Command.Create("deleteRule", new DeleteRuleRequest(key)));
            if (key == "automatic") { config.PendingEmulatorRule = null; store.Save(config); }
            return;
        }
        var next = ProxyRules.Delete(config, key);
        config.AutoProxy = next.AutoProxy; config.PendingEmulatorRule = next.PendingEmulatorRule; config.Bindings = next.Bindings;
        config.GameGroups = next.GameGroups; config.AutoExcludedTargets = next.AutoExcludedTargets; config.AutoExcludedDescendants = next.AutoExcludedDescendants;
        config.Vmware = next.Vmware; config.VmwareSessions = next.VmwareSessions; store.Save(config);
    }
    private AutoProxyRule? PendingForService(ProxyProfile[] serviceProfiles)
    {
        var pending = config.PendingEmulatorRule;
        if (pending is null) return null;
        bool changed = false;
        var assignments = pending.EmulatorAssignments.Select(item => {
            if (serviceProfiles.Any(p => p.Id == item.ProxyId)) return item;
            var local = config.Proxies.FirstOrDefault(p => p.Id == item.ProxyId);
            if (local is null) return item;
            var matches = serviceProfiles.Where(p => ProxyImport.SameEndpointAccount(local, p)).ToArray();
            if (matches.Length != 1) return item;
            changed = true; return item with { ProxyId = matches[0].Id };
        }).ToArray();
        if (changed) { config.PendingEmulatorRule = pending with { EmulatorAssignments = assignments }; store.Save(config); }
        return config.PendingEmulatorRule;
    }
    public async Task Act<T>(string action, T data)
    {
        if (!Online) {
            if (action == "unbind" && data is TargetRequest target) { config.Bindings.RemoveAll(b => b.TargetKey == target.TargetKey); store.Save(config); return; }
            throw new InvalidOperationException("背景服務未連線。分配可以先儲存；啟用代理需先安裝背景服務及官方驅動。");
        }
        if (action == "setGlobalProxy" && data is GlobalProxySettings { Enabled: true, BypassRemoteControl: true } &&
            !(await Call<Snapshot>(Command.Create("snapshot", new { }))).SupportsRemoteControlBypass)
            throw new InvalidOperationException("遠端控制直連需要 0.1.11 背景服務與原生核心。請先更新元件，再啟用全局代理，以保留遠端連線。");
        await Call<bool>(Command.Create(action, data));
    }
    public Task<VmwareAdapter[]> VmwareAdapters(CancellationToken ct = default) => Call<VmwareAdapter[]>(Command.Create("vmwareAdapters", new { }), ct);
    public Task RequestServiceStop(CancellationToken ct) => Call<bool>(Command.Create("stopService", new { }), ct);
    public Task<VmwareDiscovered[]> DiscoverVmware(CancellationToken ct = default) => Call<VmwareDiscovered[]>(Command.Create("discoverVmware", new { }), ct);
    public async Task SetupVmware(VmwareSettings settings)
    {
        if ((settings.OverflowToLocal || settings.Machines.Any(m => m.UseLocalNetwork)) && !(await Call<Snapshot>(Command.Create("snapshot", new { }))).SupportsVmwareLocalNetwork)
            throw new InvalidOperationException("本地網路需要新版代理元件，請安裝新版私人測試安裝包。");
        await Act("setupVmware", settings);
    }
    public Task PauseVmware() => Act("pauseVmware", new { });
    public Task SaveVmware(VmwareSettings settings, bool prepare) => Act(prepare ? "prepareVmware" : "setVmware", settings);
    public Task RestoreVmware() => Act("restoreVmware", new { });
    public async Task Migrate()
    {
        if (config.Bindings.Any(b => b.IncludeChildren)) {
            var service = await Call<Snapshot>(Command.Create("snapshot", new { }));
            if (!Version.TryParse(service.Diagnostics.Version.Split(' ')[0], out var version) || version < new Version(0, 1, 9))
                throw new InvalidOperationException("涵蓋子程序的離線分配需要 0.1.9 以上背景服務，請先安裝新版再移交。");
        }
        if (config.Proxies.Count == 0) throw new InvalidOperationException("沒有離線代理可移交。");
        await ImportToService(ProxyImport.ExportForServiceTransfer(config.Proxies), config.Proxies);
        if (config.Bindings.Count > 0) {
            var state = await Call<Snapshot>(Command.Create("snapshot", new { }));
            var items = config.Bindings.Select(b => {
                var local = config.Proxies.First(p => p.Id == b.ProxyId);
                var matches = state.Proxies.Where(p => p.Host.Equals(local.Host, StringComparison.OrdinalIgnoreCase) && p.Port == local.Port && p.Username == local.Username).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("服務中存在相同端點與帳號的多條代理，請手動分配以避免選錯。");
                return new AssignmentItem(new(b.TargetKey, b.Name, b.Kind, b.Path, b.Index, b.Processes.Length > 0, b.Processes), matches[0].Id, b.SharedProxy, b.IncludeChildren);
            }).ToArray();
            await Act("bindBatch", new BatchBindRequest(items)); config.Bindings.Clear(); store.Save(config);
        }
    }
}

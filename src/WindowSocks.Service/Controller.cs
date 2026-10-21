using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed partial class Controller : IDisposable
{
    public readonly SemaphoreSlim Gate = new(1, 1);
    public string OwnerSid { get; }
    private readonly string directory;
    private readonly ConfigStore store;
    private readonly SavedConfig config;
    private readonly Dictionary<string, TargetInstance> targets = [];
    private readonly Dictionary<Guid, ProbeResult> probes = [];
    // TCP previews are for display; routing continues to use fully validated health.
    private readonly Dictionary<Guid, ProbeResult> probeViews = [];
    private readonly Dictionary<Guid, uint> nativeIds = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, int Failures)> retries = [];
    private readonly Dictionary<Guid, DateTimeOffset> lastReady = [];
    private readonly Queue<string> logs = new();
    private readonly HashSet<string> conflicts = [];
    private readonly HashSet<string> automaticUnassigned = [];
    private bool started;
    private string engineError = "尚未啟動";
    private string autoStatus = "尚未啟用自動分配";
    private readonly GameGroupObserver gameObserver;
    private string gameWarning = "";
    private ulong lastEventSequence;
    private bool childrenDirty;
    private readonly ProxySessionTracker sessions = new();
    private readonly RemoteDesktopListener remoteDesktop = new();
    private readonly SemaphoreSlim probeGate = new(ProbeTiming.MaxConcurrentProbes, ProbeTiming.MaxConcurrentProbes);
    private readonly Dictionary<Guid, Task<ProbeResult>> activeProbes = [];
    private readonly Func<ProxyProfile, bool, CancellationToken, Task<ProbeResult>> measureProxy;
    private readonly Dictionary<Guid, Task<ProbeResult>> tcpProbes = [];
    private readonly HashSet<Task> supplementalTasks = [];
    private readonly Dictionary<Guid, long> epochs = [];
    private readonly Dictionary<Guid, long> supplementalChecks = [];
    private long epoch;
    private readonly CancellationTokenSource supplementalLifetime = new();
    private readonly SemaphoreSlim supplementalGate = new(ProbeTiming.MaxSupplementalProbes, ProbeTiming.MaxSupplementalProbes);
    private readonly Func<ProxyProfile, bool, CancellationToken, Task<ProbeResult>> measureTcp;
    private readonly Func<ProxyProfile, ProbeResult, bool, CancellationToken, Task<ProbeResult>> supplementProxy;

    public Controller() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowSocks")) { }
    public Controller(string configurationDirectory, Func<ProxyProfile, bool, CancellationToken, Task<ProbeResult>>? measureProxy = null,
        Func<ProxyProfile, bool, CancellationToken, Task<ProbeResult>>? measureTcp = null,
        Func<ProxyProfile, ProbeResult, bool, CancellationToken, Task<ProbeResult>>? supplementProxy = null)
    {
        this.measureProxy = measureProxy ?? MeasureProxy;
        this.measureTcp = measureTcp ?? ((profile, remote, ct) => ProxyProbe.RunTcpAsync(profile, new(RemoteStunDns: remote), ct));
        this.supplementProxy = supplementProxy ?? ((profile, result, remote, ct) => ProxyProbe.SupplementAsync(profile, result, new(RemoteStunDns: remote), ct));
        directory = configurationDirectory;
        var sidFile = Path.Combine(directory, "owner.sid");
        if (!File.Exists(sidFile)) throw new InvalidOperationException("尚未安裝服務，缺少 owner.sid。");
        OwnerSid = File.ReadAllText(sidFile).Trim(); _ = new SecurityIdentifier(OwnerSid);
        store = new(directory); config = store.Load();
        if (NativeEngine.Present && NativeEngine.Core_SetOwner(OwnerSid) == 0) throw new InvalidDataException("安裝使用者識別無效。");
        // Restart is deliberately blocked until targets and upstreams are revalidated.
        ConfigureProxies(); ApplyRules();
        gameObserver = new(config.AutoProxy, config.GameGroups, IsOwned);
        if ((config.GlobalProxy.Enabled || config.AutoProxy.Enabled || config.Bindings.Count > 0) && NativeEngine.DriverPresent) {
            try { EnsureEngine(); } catch (Exception ex) { engineError = ex.Message; }
        }
        Log("背景服務啟動；等待程序身分及代理檢測。");
    }
    private void Log(string text) { logs.Enqueue($"{DateTimeOffset.Now:HH:mm:ss}  {text}"); while (logs.Count > 100) logs.Dequeue(); }
    private void ConfigureProxies()
    {
        foreach (var p in config.Proxies)
        {
            if (!nativeIds.TryGetValue(p.Id, out uint id)) nativeIds[p.Id] = id = (uint)nativeIds.Count + 1;
            string host = config.GlobalProxy.Enabled && p.Id == config.GlobalProxy.ProxyId ? config.GlobalProxy.ResolvedHost : p.Host;
            if (NativeEngine.Present && NativeEngine.Core_SetProxy(id, host, checked((ushort)p.Port), p.Username, p.Password) == 0) throw new InvalidDataException("代理設定被核心拒絕。");
        }
    }
    public async Task<Reply> Dispatch(Command cmd, CancellationToken ct)
    {
        // Network checks must not hold the configuration lock: imports and snapshots
        // remain available even when an upstream takes the full timeout to respond.
        if (cmd.Action is "probe" or "probeTcp") {
            try { return Reply.Success(await ProbeInBackground(Json.Read<ProxyRequest>(cmd.Data).ProxyId, ct, tcpFirst: cmd.Action == "probeTcp")); }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                return Reply.Failure(ex is OperationCanceledException ? "操作逾時或已取消。" : ex.Message);
            }
        }
        await Gate.WaitAsync(ct);
        try
        {
            switch (cmd.Action)
            {
                case "snapshot": return Reply.Success(GetSnapshot());
                case "diagnose": return Reply.Success(GetDiagnosticBundle());
                case "probeTarget":
                    var targetProbe = Json.Read<TargetProbeRequest>(cmd.Data);
                    return Reply.Success(await TargetConnectivityProbe.RunAsync(FindProxy(targetProbe.ProxyId), targetProbe, ct));
                case "import":
                    var imported = ProxyImport.Parse(Json.Read<ImportRequest>(cmd.Data).Text);
                    ProxyImport.ApplyTo(config.Proxies, imported);
                    ConfigureProxies(); store.Save(config); Log($"已處理 {imported.Count} 筆代理匯入。"); return Reply.Success(imported.Count);
                case "export": return Reply.Success(ProxyImport.Export(config.Proxies));
                case "backup": return Reply.Success(Convert.ToBase64String(store.Backup(config)));
                case "deleteProxy":
                    var delId = Json.Read<ProxyRequest>(cmd.Data).ProxyId;
                    if (config.Bindings.Any(x => x.ProxyId == delId)) throw new InvalidOperationException("請先解除使用此代理的綁定。");
                    if (config.AutoProxy.EmulatorAssignments.Any(x => x.ProxyId == delId)) throw new InvalidOperationException("請先在固定窗口配置中移除此代理。");
                    if (config.GlobalProxy.Enabled && config.GlobalProxy.ProxyId == delId) throw new InvalidOperationException("請先停用使用此節點的全局代理。");
                    config.Proxies.RemoveAll(x => x.Id == delId); probes.Remove(delId); probeViews.Remove(delId); store.Save(config); return Reply.Success(true);
                case "syncTargets":
                    var discovered = Json.Read<TargetInstance[]>(cmd.Data);
                    if (discovered.Length > 500) throw new InvalidDataException("實例數超出限制。");
                    bool targetsChanged = false;
                    foreach (var target in discovered)
                    {
                        ValidateTarget(target);
                        var captured = CaptureEmulatorIdentity(target);
                        targetsChanged |= !targets.TryGetValue(target.Key, out var previous) ||
                            !previous.Processes.SequenceEqual(captured.Processes) || (previous with { Processes = captured.Processes }) != captured;
                        targets[target.Key] = captured;
                    }
                    if (targetsChanged) ApplyRules(); return Reply.Success(true);
                case "bind":
                    var binding = Json.Read<BindRequest>(cmd.Data); Bind(binding.TargetKey, binding.ProxyId); return Reply.Success(true);
                case "bindBatch":
                    BindBatch(Json.Read<BatchBindRequest>(cmd.Data)); return Reply.Success(true);
                case "setAutoProxy":
                    SetAutoProxy(Json.Read<AutoProxyRule>(cmd.Data)); return Reply.Success(true);
                case "setGlobalProxy":
                    await SetGlobalProxy(Json.Read<GlobalProxySettings>(cmd.Data), ct); return Reply.Success(true);
                case "bindProcess":
                    var request = Json.Read<ProcessRequest>(cmd.Data); var process = ProcessCatalog.List().FirstOrDefault(t => t.Processes.Any(p => p.Pid == request.Pid)) ?? throw new InvalidOperationException("找不到程序。");
                    ValidateTarget(process); targets[process.Key] = process; BindBatch(new([new(process, request.ProxyId, false, request.IncludeChildren)]));
                    return Reply.Success(true);
                case "enable":
                    var enabled = GetBinding(cmd);
                    EnsureEngine();
                    // Quarantine before probing/launching; a failed test leaves this target blocked.
                    enabled.Enabled = false; ApplyRules();
                    var checkedProxy = await Probe(FindProxy(enabled.ProxyId), ct);
                    if (!checkedProxy.Ready) { store.Save(config); throw new InvalidOperationException("代理 TCP／UDP 尚未通過實測，實例保持封鎖。"); }
                    if (DuplicateExit(enabled.ProxyId)) { store.Save(config); throw new InvalidOperationException("與另一個綁定實例共用出口 IP，請改用不同代理。"); }
                    if (conflicts.Contains(enabled.TargetKey)) throw new InvalidOperationException("多個實例共用無法區分的程序，不支援獨立分流；目前保持封鎖。");
                    enabled.Enabled = true; enabled.AutoPaused = false; store.Save(config); ApplyRules(); return Reply.Success(true);
                case "pause": var paused = GetBinding(cmd); paused.Enabled = false; paused.AutoPaused = true; ApplyRules(); store.Save(config); Log("已暫停並保留實例封鎖。"); return Reply.Success(true);
                case "unbind":
                    var unbind = GetBinding(cmd); unbind.Enabled = false; ApplyRules();
                    if (unbind.AutoManaged) { if (!config.AutoExcludedTargets.Contains(unbind.TargetKey)) config.AutoExcludedTargets.Add(unbind.TargetKey); automaticUnassigned.Add(unbind.TargetKey); }
                    config.Bindings.Remove(unbind); ApplyRules(); store.Save(config); Log("已解除綁定，恢復原有網路；本目標不會被目前自動規則再次加入。"); return Reply.Success(true);
                case "setLdPath":
                    string path = Json.Read<string>(cmd.Data); ValidateConsole(path); config.LdConsole = Path.GetFullPath(path); store.Save(config); return Reply.Success(true);
                case "getLdPath": return Reply.Success(config.LdConsole);
                default: return Reply.Failure("不支援的服務命令。");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string message = ex is DllNotFoundException ? "缺少原生核心或依賴檔案。" : ex is FormatException ? "輸入格式錯誤，請檢查代理欄位。" : ex is OperationCanceledException ? "操作逾時或已取消。" : ex.Message;
            // Exceptions from upstream/network do not include the submitted credential text.
            Log(message); return Reply.Failure(message);
        }
        finally { Gate.Release(); }
    }
    private ProxyProfile FindProxy(Guid id) => config.Proxies.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("找不到代理。");
    private InstanceBinding GetBinding(Command cmd) => config.Bindings.FirstOrDefault(b => b.TargetKey == Json.Read<TargetRequest>(cmd.Data).TargetKey) ?? throw new InvalidOperationException("此目標尚未綁定代理。");
    private void Bind(string key, Guid proxy)
    {
        if (!targets.TryGetValue(key, out var target)) throw new InvalidOperationException("請先重新整理實例。");
        BindBatch(new([new(target, proxy, false)]));
    }
    private void BindBatch(BatchBindRequest request)
    {
        request = new(request.Items.Select(i => i with { Target = CaptureEmulatorIdentity(i.Target) }).ToArray());
        var next = AssignmentPlanner.Prepare(config.Bindings, config.Proxies, request);
        foreach (var item in request.Items) {
            ValidateTarget(item.Target);
            if (item.Target.Kind == "LDPlayer" && !LdPlayer.IsNat(item.Target.Path, item.Target.Index)) throw new InvalidOperationException("此實例不是可辨識的單網卡 NAT 模式。");
        }
        EnsureEngine();
        var old = config.Bindings; var previousTargets = new Dictionary<string, TargetInstance>(targets);
        try {
            foreach (var item in request.Items) targets[item.Target.Key] = item.Target;
            config.Bindings = next; ApplyRules(); store.Save(config);
        } catch {
            config.Bindings = old; targets.Clear(); foreach (var pair in previousTargets) targets.Add(pair.Key, pair.Value);
            ApplyRules(); throw;
        }
        Log($"已分配 {request.Items.Length} 個目標／遊戲組；僅指定成員，目前封鎖，啟用前需通過檢測。");
    }
    private void EnsureEngine()
    {
        if (started && NativeEngine.Core_IsRunning() != 0) return;
        if (!NativeEngine.Present) throw new InvalidOperationException("缺少 WindowSocksNative.dll，尚未啟用代理。");
        if (!NativeEngine.DriverPresent) throw new InvalidOperationException("缺少官方 WinDivert.dll／WinDivert64.sys，無法攔截流量。代理尚未啟用。");
        if (started) { NativeEngine.Core_Stop(); started = false; }
        if (NativeEngine.Core_Start(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll")) == 0) { engineError = $"驅動啟動失敗，Windows 錯誤 {NativeEngine.Core_LastError()}。"; throw new InvalidOperationException(engineError); }
        started = true; engineError = "核心執行中（實機相容性須驗收）";
    }
    private async Task SetGlobalProxy(GlobalProxySettings settings, CancellationToken ct)
    {
        if (!settings.Enabled) {
            config.GlobalProxy = config.GlobalProxy with { Enabled = false };
            ApplyRules(); store.Save(config); Log("已停用全局代理；指定程序綁定繼續依原規則執行。"); return;
        }
        settings = settings with { RemoteControlExecutables = RemoteControlBypass.NormalizeCustom(settings.RemoteControlExecutables) };
        if (settings.BypassRemoteControl && !NativeEngine.SupportsRemoteControlBypass) throw new InvalidOperationException("遠端控制直連需要 0.1.11 以上背景服務與原生核心，請先更新元件。");
        var profile = FindProxy(settings.ProxyId);
        // Resolve before interception, then retain this numeric address across service restarts.
        string resolved;
        if (RemoteControlBypass.PinnedAddressForPolicyChange(config.GlobalProxy, settings) is { } pinned) resolved = pinned;
        else {
            var addresses = await Dns.GetHostAddressesAsync(profile.Host, AddressFamily.InterNetwork, ct).WaitAsync(TimeSpan.FromSeconds(8), ct);
            resolved = addresses.FirstOrDefault()?.ToString() ?? throw new InvalidOperationException("此代理沒有可用的 IPv4 位址。");
        }
        EnsureEngine();
        var previous = config.GlobalProxy;
        probes.Remove(profile.Id); retries.Remove(profile.Id); lastReady.Remove(profile.Id);
        config.GlobalProxy = settings with { ResolvedHost = resolved };
        try { ConfigureProxies(); ApplyRules(); store.Save(config); }
        catch { config.GlobalProxy = previous; ConfigureProxies(); ApplyRules(); throw; }
        // Enabled but quarantined until a fresh check completes; a failed check leaves quarantine in place.
        try { await Probe(profile, ct); }
        finally { ApplyRules(); }
        Log(GlobalStatus());
    }
    private void SetAutoProxy(AutoProxyRule rule)
    {
        if (rule.Kind == "Emulator") { SetFixedEmulators(rule); return; }
        if (rule.Kind == "EmulatorSequence") { SetSequentialEmulators(rule); return; }
        if (!rule.Enabled) {
            foreach (var binding in config.Bindings.Where(b => b.AutoManaged)) binding.Enabled = false;
            config.AutoProxy = config.AutoProxy with { Enabled = false }; ApplyRules(); store.Save(config);
            gameObserver.SetRule(config.AutoProxy);
            autoStatus = "自動分配已暫停；原有自動綁定保持封鎖"; Log(autoStatus); return;
        }
        rule = rule with { Pattern = rule.Pattern.Trim(), ClientPath = rule.ClientPath.Trim() };
        AutoProxyPlanner.Validate(rule);
        if (rule.Kind == "LDPlayer") ValidateConsole(rule.ConsolePath);
        if (rule.Enabled) EnsureEngine();
        bool changedScope = rule.Kind != config.AutoProxy.Kind || rule.Match != config.AutoProxy.Match ||
            rule.IncludeChildren != config.AutoProxy.IncludeChildren ||
            !rule.Pattern.Equals(config.AutoProxy.Pattern, StringComparison.OrdinalIgnoreCase) ||
            !rule.ClientPath.Equals(config.AutoProxy.ClientPath, StringComparison.OrdinalIgnoreCase) ||
            !rule.ConsolePath.Equals(config.AutoProxy.ConsolePath, StringComparison.OrdinalIgnoreCase);
        if (rule.Enabled && !changedScope && config.Bindings.Where(b => b.AutoManaged).GroupBy(b => b.ProxyId).Any(g => g.Count() > rule.PerProxy))
            throw new InvalidOperationException("已有 IP 的分配數超過新上限，請先解除多餘綁定；不會自動替窗口換 IP。");
        foreach (var binding in config.Bindings.Where(b => b.AutoManaged)) {
            if (changedScope) { binding.AutoManaged = false; binding.Enabled = false; binding.AutoPaused = true; }
            else binding.Enabled = rule.Enabled && !binding.AutoPaused;
        }
        if (changedScope) { config.AutoExcludedTargets.Clear(); config.AutoExcludedDescendants.Clear(); }
        automaticUnassigned.Clear();
        config.AutoProxy = rule; ApplyRules(); store.Save(config);
        gameObserver.SetRule(rule);
        autoStatus = rule.Enabled ? "正在探索符合規則的目標…" : "自動分配已暫停；原有自動綁定保持封鎖";
        Log(autoStatus);
    }

    private void DiscoverAutomaticTargets()
    {
        var rule = config.AutoProxy;
        if (!rule.Enabled) return;
        if (rule.Kind == "Emulator") {
            foreach (var item in rule.EmulatorAssignments) if (!targets.ContainsKey(item.TargetKey)) {
                var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == item.TargetKey);
                targets[item.TargetKey] = new(item.TargetKey, item.Name, item.Kind, item.ConsolePath, item.Index, false, []) {
                    EmulatorProcessPath = binding?.EmulatorProcessPath ?? "", EmulatorCommandHash = binding?.EmulatorCommandHash ?? ""
                };
            }
            return;
        }
        if (rule.Kind == "LDPlayer" || rule.Kind == "EmulatorSequence" && rule.EmulatorKind == "LDPlayer") {
            // Discover installed instances without executing a user-owned console as LocalSystem.
            var folder = Path.Combine(Path.GetDirectoryName(rule.ConsolePath)!, "vms");
            if (Directory.Exists(folder)) foreach (var vm in Directory.EnumerateDirectories(folder, "leidian*")) {
                if (!int.TryParse(Path.GetFileName(vm).AsSpan(7), out int index) || index < 0 || index == 99999) continue;
                var key = LdPlayer.Key(rule.ConsolePath, index);
                if (!targets.ContainsKey(key)) targets[key] = new(key, $"雷電 {index}", "LDPlayer", rule.ConsolePath, index, false, []);
            }
        }
        else if (rule.Kind == "Process") {
            var matched = ProcessCatalog.List(Path.GetFileName(rule.Pattern)).Where(t => AutoProxyPlanner.Matches(t, rule) && t.Processes.All(p => IsOwned(p.Pid))).ToArray();
            matched = SelectAutomaticRoots(matched);
            foreach (var target in matched) { ValidateTarget(target); targets[target.Key] = target; }
        }
    }

    private void DiscoverGameGroups()
    {
        var (scan, error) = gameObserver.Read();
        if (error.Length > 0) throw new InvalidOperationException(error);
        gameWarning = scan.Unmatched.Length == 0 ? "" : $" · {scan.Unmatched.Length} 個程序來源未確認，保持封鎖；請從登入器重新啟動";
        var keys = scan.Groups.Select(g => g.Key).ToHashSet();
        var expired = config.Bindings.Where(b => b.Kind == "GameGroup" && !keys.Contains(b.TargetKey)).ToArray();
        foreach (var binding in expired) config.Bindings.Remove(binding);
        foreach (string key in targets.Where(x => x.Value.Kind == "GameGroup").Select(x => x.Key).ToArray()) targets.Remove(key);
        foreach (var group in scan.Groups) {
            var target = group.Target(); targets[target.Key] = target;
            foreach (var binding in config.Bindings.Where(b => b.TargetKey == target.Key)) binding.Processes = target.Processes;
        }
        foreach (var p in scan.Unmatched) targets[$"orphan:{p.Identity.Pid}:{p.Identity.CreatedFileTime}"] = new($"orphan:{p.Identity.Pid}:{p.Identity.CreatedFileTime}",
            $"{Path.GetFileName(p.Path)} · 來源未確認", "GameOrphan", p.Path, -1, true, [p.Identity], "無法證明登入器來源，保持封鎖；請重新從登入器啟動");
        foreach (string key in targets.Where(x => x.Value.Kind == "GameOrphan" && !scan.Unmatched.Any(p => x.Value.Processes.Contains(p.Identity))).Select(x => x.Key).ToArray()) targets.Remove(key);
        bool changed = expired.Length > 0 || JsonSerializer.Serialize(config.GameGroups, Json.Options) != JsonSerializer.Serialize(scan.Groups, Json.Options);
        config.GameGroups = scan.Groups.ToList();
        if (config.AutoExcludedTargets.RemoveAll(key => key.StartsWith("game:") && !keys.Contains(key)) > 0) changed = true;
        if (changed) store.Save(config);
    }

    private TargetInstance[] SelectAutomaticRoots(TargetInstance[] candidates)
    {
        // Remember observed descendants for their entire lifetime, even after the root exits or the service restarts.
        int removed = config.AutoExcludedDescendants.RemoveAll(p => LdPlayer.Identity(p.Pid) != p);
        var roots = AutoProxyPlanner.Roots(candidates, ProcessCatalog.Parents(), LdPlayer.Identity);
        var rootKeys = roots.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var known = config.AutoExcludedDescendants.ToHashSet();
        foreach (var child in candidates.Where(t => !rootKeys.Contains(t.Key)).SelectMany(t => t.Processes)) known.Add(child);
        bool changed = removed > 0 || known.Count != config.AutoExcludedDescendants.Count;
        config.AutoExcludedDescendants = known.ToList();
        if (changed) store.Save(config);
        automaticUnassigned.Clear();
        foreach (var candidate in candidates.Where(t => config.Bindings.All(b => b.TargetKey != t.Key))) automaticUnassigned.Add(candidate.Key);
        return roots.Where(t => !t.Processes.Any(known.Contains)).ToArray();
    }

    private void ReconcileAutomaticBindings()
    {
        var rule = config.AutoProxy;
        if (!rule.Enabled) return;
        if (rule.Kind == "Emulator") {
            autoStatus = $"固定窗口 {rule.EmulatorAssignments.Length} 個 · 使用 {rule.EmulatorAssignments.Select(a => a.ProxyId).Distinct().Count()} 個節點 · 不隨啟動順序變更";
            return;
        }
        if (rule.Kind == "EmulatorSequence") {
            if (config.Bindings.RemoveAll(b => b.AutoManaged && EmulatorCatalog.IsEmulator(b.Kind) && !SequentialEmulatorPlanner.IsLive(b, LdPlayer.Identity)) > 0)
                store.Save(config);
        }
        bool changed = config.Bindings.RemoveAll(b => b.AutoManaged && b.Kind == "Process" && !b.Processes.Concat(b.IncludeChildren ? b.Descendants : []).Any(p => LdPlayer.Identity(p.Pid) == p)) > 0;
        foreach (var key in targets.Where(x => x.Value.Kind == "Process" && !x.Value.Processes.Any(p => LdPlayer.Identity(p.Pid) == p) &&
            !config.Bindings.Any(b => b.TargetKey == x.Key && b.IncludeChildren && b.Descendants.Any(p => LdPlayer.Identity(p.Pid) == p))).Select(x => x.Key).ToArray()) targets.Remove(key);
        if (config.AutoExcludedTargets.RemoveAll(key => key.StartsWith("pid:") && !targets.ContainsKey(key)) > 0) changed = true;
        var candidates = targets.Values.Where(t => AutoProxyPlanner.Matches(t, rule)).ToArray();
        if (rule.Kind == "Process") candidates = SelectAutomaticRoots(candidates);
        else if (rule.Kind == "EmulatorSequence") candidates = SequentialEmulatorPlanner.Candidates(candidates, rule)
            .Where(t => t.Processes.All(p => LdPlayer.Identity(p.Pid) == p && IsOwned(p.Pid)) && (t.Kind != "LDPlayer" || LdPlayer.IsNat(t.Path, t.Index))).ToArray();
        else if (rule.Kind == "LDPlayer") candidates = candidates.Where(t => LdPlayer.IsNat(t.Path, t.Index)).ToArray();
        else candidates = candidates.Where(t => t.Processes.Length > 0 && config.GameGroups.Any(g => g.Key == t.Key && g.ClientPath.Equals(rule.ClientPath, StringComparison.OrdinalIgnoreCase))).ToArray();
        var additions = AutoProxyPlanner.Additions(candidates, config.Bindings, config.Proxies, rule.PerProxy, config.AutoExcludedTargets, launchOrder: rule.Kind == "EmulatorSequence");
        if (additions.Length > 0) {
            BindBatch(new(additions));
            foreach (var item in additions) {
                var binding = config.Bindings.Single(b => b.TargetKey == item.Target.Key);
                binding.AutoManaged = true; binding.IncludeChildren = rule.IncludeChildren; binding.Enabled = true; // ApplyRules blocks until fresh TCP and UDP checks pass.
            }
            changed = true;
        }
        if (changed) store.Save(config);
        int waiting = candidates.Count(t => config.Bindings.All(b => b.TargetKey != t.Key) && !config.AutoExcludedTargets.Contains(t.Key));
        autoStatus = config.Proxies.Count == 0 ? "等待代理匯入；目前未代理" :
            (rule.Kind == "EmulatorSequence" ? "依啟動順序 · " : "") + $"每個 IP 最多 {rule.PerProxy} 個{(rule.Kind == "GameGroup" ? "遊戲組" : "目標")} · 自動管理 {config.Bindings.Count(b => b.AutoManaged)} 個 · 容量不足／規則衝突未分配 {waiting} 個" + gameWarning;
    }
    private async Task<ProbeResult> Probe(ProxyProfile p, CancellationToken ct)
    {
        epochs[p.Id] = ++epoch;
        var endpoint = config.GlobalProxy.Enabled && p.Id == config.GlobalProxy.ProxyId ? p with { Host = config.GlobalProxy.ResolvedHost } : p;
        var result = await measureProxy(endpoint, config.GlobalProxy.Enabled, ct);
        RecordProbe(p, result); return result;
    }
    private static async Task<ProbeResult> MeasureProxy(ProxyProfile endpoint, bool remoteStunDns, CancellationToken ct)
    {
        var result = await ProxyProbe.RunAsync(endpoint, new(RemoteStunDns: remoteStunDns), ct);
        return await IpLocationLookup.EnrichAsync(endpoint, result, ct);
    }
    private async Task<ProbeResult> ProbeInBackground(Guid id, CancellationToken ct, bool onlyIfDue = false, bool tcpFirst = false)
    {
        Task<ProbeResult> work; bool created = false;
        await Gate.WaitAsync(ct);
        try {
            var profile = FindProxy(id) with { };
            if (onlyIfDue && retries.TryGetValue(id, out var retry) && retry.At > DateTimeOffset.UtcNow && probes.TryGetValue(id, out var fresh)) return fresh;
            var active = tcpFirst ? tcpProbes : activeProbes;
            if (!active.TryGetValue(id, out work!)) {
                created = true;
                long check = ++epoch; epochs[id] = check;
                work = MeasureInBackground(profile, config.GlobalProxy, check, tcpFirst, ct);
                active.Add(id, work);
            }
        } finally { Gate.Release(); }
        // A duplicate request shares the running measurement. Cancelling a joiner
        // cannot cancel the original caller's measurement.
        return created ? await work : await work.WaitAsync(ct);
    }
    private async Task<ProbeResult> MeasureInBackground(ProxyProfile profile, GlobalProxySettings policy, long check, bool tcpFirst, CancellationToken ct)
    {
        bool entered = false, removed = false;
        try {
            await probeGate.WaitAsync(ct); entered = true;
            var endpoint = policy.Enabled && profile.Id == policy.ProxyId ? profile with { Host = policy.ResolvedHost } : profile;
            var result = await (tcpFirst ? measureTcp : measureProxy)(endpoint, policy.Enabled, ct);
            await Gate.WaitAsync(CancellationToken.None);
            try {
                ct.ThrowIfCancellationRequested();
                // A deleted/replaced node or changed global route invalidates this check.
                var current = config.Proxies.FirstOrDefault(p => p.Id == profile.Id);
                if (current is null || !CurrentCheck(profile, policy, check))
                    throw new InvalidOperationException("節點或全局代理設定已變更，請重新檢測。");
                if (result.UdpPending) {
                    probeViews[profile.Id] = result;
                    supplementalChecks[profile.Id] = check;
                    supplementalTasks.RemoveWhere(task => task.IsCompleted);
                    supplementalTasks.Add(Task.Run(() => SupplementInBackground(profile, endpoint, policy, check, result)));
                } else { RecordProbe(current, result); ApplyProbeRules(profile.Id); }
                return result;
            } finally {
                (tcpFirst ? tcpProbes : activeProbes).Remove(profile.Id); removed = true;
                Gate.Release();
            }
        } finally {
            if (entered) probeGate.Release();
            // This method starts while Gate is held by ProbeInBackground, so even
            // synchronous completion cannot remove the entry before it is added.
            if (!removed) {
                await Gate.WaitAsync(CancellationToken.None);
                try { (tcpFirst ? tcpProbes : activeProbes).Remove(profile.Id); } finally { Gate.Release(); }
            }
        }
    }
    private bool CurrentCheck(ProxyProfile profile, GlobalProxySettings policy, long check) =>
        epochs.GetValueOrDefault(profile.Id) == check && policy == config.GlobalProxy &&
        config.Proxies.Any(p => p.Id == profile.Id && ProxyImport.SameProxy(p, profile));
    private async Task SupplementInBackground(ProxyProfile profile, ProxyProfile endpoint, GlobalProxySettings policy, long check, ProbeResult tcp)
    {
        bool entered = false; var ct = supplementalLifetime.Token;
        try {
            // Separate capacity keeps slow UDP/location work out of the TCP queue.
            await supplementalGate.WaitAsync(ct); entered = true;
            await Gate.WaitAsync(ct);
            try { if (!CurrentCheck(profile, policy, check)) return; } finally { Gate.Release(); }
            var result = await supplementProxy(endpoint, tcp, policy.Enabled, ct);
            await Gate.WaitAsync(ct);
            try {
                if (!CurrentCheck(profile, policy, check) || DateTimeOffset.UtcNow - tcp.At >= ProbeTiming.HealthValidity) return;
                RecordProbe(profile, result); ApplyProbeRules(profile.Id);
            } finally { Gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            await Gate.WaitAsync(CancellationToken.None);
            try {
                if (CurrentCheck(profile, policy, check)) {
                    probeViews[profile.Id] = tcp with { UdpPending = false, At = DateTimeOffset.UtcNow, Message = "UDP／所在地補測未完成，可手動重試。" };
                    retries[profile.Id] = (DateTimeOffset.UtcNow.Add(ProbeTiming.RetryInterval), retries.GetValueOrDefault(profile.Id).Failures);
                }
            } finally { Gate.Release(); }
        }
        finally {
            if (entered) supplementalGate.Release();
            await Gate.WaitAsync(CancellationToken.None);
            try {
                if (supplementalChecks.GetValueOrDefault(profile.Id) == check) supplementalChecks.Remove(profile.Id);
                if (!ct.IsCancellationRequested && probeViews.TryGetValue(profile.Id, out var pending) && pending.CheckId == tcp.CheckId && pending.UdpPending) {
                    probeViews[profile.Id] = pending with { UdpPending = false, At = DateTimeOffset.UtcNow, Message = "補測已取消或設定變更，請重新檢測。" };
                    retries[profile.Id] = (DateTimeOffset.UtcNow.Add(ProbeTiming.RetryInterval), retries.GetValueOrDefault(profile.Id).Failures);
                }
            } finally { Gate.Release(); }
        }
    }
    private void RecordProbe(ProxyProfile p, ProbeResult result)
    {
        probes[p.Id] = result;
        probeViews.Remove(p.Id);
        if (result.Ready) lastReady[p.Id] = result.At;
        int failures = result.Ready ? 0 : retries.GetValueOrDefault(p.Id).Failures + 1;
        retries[p.Id] = (DateTimeOffset.UtcNow.Add(ProbeTiming.NextInterval(result.Ready)), failures);
        Log($"代理「{p.Name}」檢測：TCP {result.TcpLabel}，UDP {result.UdpLabel}。");
    }
    private void ApplyProbeRules(Guid id)
    {
        // An unused pool node cannot change live process/global routing. Rebuilding
        // all emulator rules here would hold up unrelated TCP result delivery.
        if (config.Bindings.Any(b => b.ProxyId == id) || config.GlobalProxy.Enabled && config.GlobalProxy.ProxyId == id) ApplyRules();
    }
    private bool DuplicateExit(Guid id)
    {
        return AssignmentPlanner.HasDuplicateExit(id, config.Bindings, probes);
    }
    private readonly Dictionary<string, bool> bindingHealth = [];
    // A single failed probe - often only its UDP leg, while the game talks TCP - must not tear down a live
    // session. Keep routing until two consecutive checks fail; until then traffic still goes to the proxy
    // only, so nothing falls back to a direct connection.
    private bool Healthy(Guid proxyId)
    {
        return ProxyHealth.CanRoute(probes.GetValueOrDefault(proxyId), lastReady.TryGetValue(proxyId, out var success) ? success : null,
            retries.GetValueOrDefault(proxyId).Failures, DateTimeOffset.UtcNow);
    }
    private void ReportHealth(string targetKey, bool ready, Guid proxyId)
    {
        bool known = bindingHealth.TryGetValue(targetKey, out var previous);
        bindingHealth[targetKey] = ready;
        if (known ? previous == ready : ready) return;
        Log(ready ? "代理檢測恢復通過；目標恢復轉送。"
            : $"代理檢測未通過（{(probes.TryGetValue(proxyId, out var p) ? p.Message : "尚無結果")}）；目標暫停轉送並維持封鎖，未改走直連。");
    }
    private void ApplyRules()
    {
        if (!NativeEngine.Present) { sessions.Synchronize([], DateTimeOffset.UtcNow); return; }
        // Build then atomically replace the quarantine set; releasing a binding must release its guard too.
        var guardPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool processStartupGuard = config.AutoProxy.Enabled && config.AutoProxy.Kind == "Process" && config.AutoProxy.Match == TargetMatch.ExecutablePath;
        if (processStartupGuard) guardPaths.Add(config.AutoProxy.Pattern);
        if (config.AutoProxy.Enabled && config.AutoProxy.Kind == "GameGroup") { guardPaths.Add(config.AutoProxy.Pattern); guardPaths.Add(config.AutoProxy.ClientPath); }
        foreach (var group in config.GameGroups.Where(g => config.Bindings.Any(b => b.TargetKey == g.Key))) { guardPaths.Add(group.LauncherPath); guardPaths.Add(group.ClientPath); }
        var guardedEmulators = config.Bindings.Where(b => b.Kind == "LDPlayer").Select(b => b.Path)
            .Concat(config.AutoProxy.Enabled && config.AutoProxy.Kind == "EmulatorSequence" && config.AutoProxy.EmulatorKind == "LDPlayer" ? [config.AutoProxy.ConsolePath] : []).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var console in guardedEmulators)
        {
            string root = Path.GetDirectoryName(console)!;
            foreach (var filename in new[] { "Ld9BoxHeadless.exe", "LdVBoxHeadless.exe" })
                foreach (string folder in new[] { root, Path.Combine(root, "LDVBox"), Path.Combine(root, "ld9box"), Path.Combine(root, "LDPlayer9"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ldplayer9box") })
                    if (File.Exists(Path.Combine(folder, filename))) guardPaths.Add(Path.Combine(folder, filename));
        }
        foreach (var b in config.Bindings.Where(b => b.Kind == "MuMu" && b.EmulatorProcessPath.Length > 0)) guardPaths.Add(b.EmulatorProcessPath);
        if (config.AutoProxy.Enabled && config.AutoProxy.Kind == "EmulatorSequence")
            foreach (var target in targets.Values.Where(t => t.Kind == "MuMu" && SequentialEmulatorPlanner.Matches(t, config.AutoProxy) && t.EmulatorProcessPath.Length > 0)) guardPaths.Add(target.EmulatorProcessPath);
        conflicts.Clear();
        var rows = new List<NativeEngine.Rule>(); var ownership = new Dictionary<ProcessIdentity, List<InstanceBinding>>();
        RefreshBindingChildren();
        foreach (var b in config.Bindings)
        {
            if (targets.TryGetValue(b.TargetKey, out var target)) {
                // Keep a stopped sequence assignment's original identity until reconciliation releases its slot.
                if (!(config.AutoProxy.Kind == "EmulatorSequence" && b.AutoManaged && !SequentialEmulatorPlanner.IsLive(b, LdPlayer.Identity))) b.Processes = target.Processes;
                if (target.Kind == "MuMu" && target.EmulatorCommandHash.Length > 0 &&
                    (b.EmulatorProcessPath != target.EmulatorProcessPath || b.EmulatorCommandHash != target.EmulatorCommandHash)) {
                    b.EmulatorProcessPath = target.EmulatorProcessPath; b.EmulatorCommandHash = target.EmulatorCommandHash; childrenDirty = true;
                }
            }
            var processes = b.Processes.Concat(b.IncludeChildren ? b.Descendants : []).Distinct().Where(p => LdPlayer.Identity(p.Pid) == p).ToArray();
            foreach (var p in processes.Where(p => IsOwned(p.Pid))) { if (!ownership.TryGetValue(p, out var list)) ownership[p] = list = []; list.Add(b); }
        }
        foreach (var (process, bindings) in ownership)
        {
            var b = bindings[0]; bool shared = bindings.Select(x => x.TargetKey).Distinct().Count() > 1;
            if (shared) foreach (var binding in bindings) conflicts.Add(binding.TargetKey);
            bool ready = b.Enabled && !shared && Healthy(b.ProxyId) && !DuplicateExit(b.ProxyId);
            ReportHealth(b.TargetKey, ready, b.ProxyId);
            rows.Add(new() { Pid = (uint)process.Pid, Created = (ulong)process.CreatedFileTime, Proxy = nativeIds[b.ProxyId], Blocked = ready ? 0u : 1u, Reserved = b.IncludeChildren ? 1u : 0u });
        }
        // Known unbound instances bypass the startup guard. Unknown new instances stay quarantined until identified.
        foreach (var target in targets.Values.Where(t => EmulatorCatalog.IsEmulator(t.Kind) && config.Bindings.All(b => b.TargetKey != t.Key)))
            foreach (var p in ProcessCatalog.WithChildren(target.Processes).Where(p => IsOwned(p.Pid)))
                if (!rows.Any(r => r.Pid == p.Pid)) rows.Add(new() { Pid = (uint)p.Pid, Created = (ulong)p.CreatedFileTime,
                    Blocked = config.AutoProxy.Enabled && config.AutoProxy.Kind == "EmulatorSequence" && SequentialEmulatorPlanner.Matches(target, config.AutoProxy) && !config.AutoExcludedTargets.Contains(target.Key) ? 1u : 2u });
        if (processStartupGuard) {
            // Known overflow/unbound roots and excluded descendants keep the documented unproxied behavior.
            var bypass = targets.Values.Where(t => t.Kind == "Process" && automaticUnassigned.Contains(t.Key) && AutoProxyPlanner.Matches(t, config.AutoProxy) && config.Bindings.All(b => b.TargetKey != t.Key)).SelectMany(t => t.Processes)
                .Concat(config.AutoExcludedDescendants).Distinct();
            foreach (var p in bypass.Where(p => LdPlayer.Identity(p.Pid) == p && IsOwned(p.Pid)))
                if (!rows.Any(r => r.Pid == p.Pid)) rows.Add(new() { Pid = (uint)p.Pid, Created = (ulong)p.CreatedFileTime, Blocked = 2 });
        }
        // Explicitly unbound groups regain direct access. Capacity overflow and unknown clients stay blocked.
        foreach (var group in config.GameGroups.Where(g => config.AutoExcludedTargets.Contains(g.Key) && config.Bindings.All(b => b.TargetKey != g.Key)))
            foreach (var p in group.Target().Processes.Where(p => LdPlayer.Identity(p.Pid) == p && IsOwned(p.Pid)))
                if (!rows.Any(r => r.Pid == p.Pid)) rows.Add(new() { Pid = (uint)p.Pid, Created = (ulong)p.CreatedFileTime, Blocked = 2 });
        var global = config.GlobalProxy;
        uint globalId = global.Enabled ? nativeIds[global.ProxyId] : 0;
        uint globalState = !global.Enabled ? 0u : Healthy(global.ProxyId) ? 1u : 2u;
        int applied;
        string guards = string.Join('\0', guardPaths) + "\0\0";
        if (NativeEngine.SupportsRemoteControlBypass) {
            var rdp = global.Enabled && global.BypassRemoteControl ? remoteDesktop.Read() : default;
            applied = NativeEngine.Core_ReplacePolicyEx2(rows.ToArray(), (uint)rows.Count, guards, globalId, globalState, global.BypassLan ? 1u : 0u,
                global.BypassRemoteControl ? 1u : 0u, RemoteControlBypass.NativeImages(global.RemoteControlExecutables), rdp.Pid, rdp.Created, rdp.Port);
        } else applied = NativeEngine.Core_ReplacePolicyEx(rows.ToArray(), (uint)rows.Count, guards, globalId, globalState, global.BypassLan ? 1u : 0u);
        if (applied == 0) throw new InvalidOperationException("程序規則有衝突，核心拒絕更新。");
        UpdateSessions();
    }
    private void RefreshBindingChildren()
    {
        foreach (var b in config.Bindings.Where(b => b.IncludeChildren && b.Kind == "Process")) {
            var known = b.Processes.Concat(b.Descendants).ToHashSet();
            // The native engine may see the first child packet even when the parent exits between monitor ticks.
            if (NativeEngine.Present) foreach (var p in known.ToArray()) {
                var children = new NativeEngine.Rule[1024];
                uint count = NativeEngine.Core_GetDescendants((uint)p.Pid, (ulong)p.CreatedFileTime, children, (uint)children.Length);
                foreach (var child in children.Take(checked((int)count))) known.Add(new((int)child.Pid, (long)child.Created));
            }
            var live = ProcessCatalog.WithChildren(known).Where(p => IsOwned(p.Pid) && !b.Processes.Contains(p)).OrderBy(p => p.Pid).ToArray();
            if (live.Length > 512) throw new InvalidOperationException("單一程序樹超過 512 個成員，請改用全局代理。");
            if (!b.Descendants.SequenceEqual(live)) { b.Descendants = live; childrenDirty = true; }
        }
    }
    public async Task Tick(CancellationToken ct)
    {
        Guid due = Guid.Empty;
        await Gate.WaitAsync(ct);
        try
        {
            SaveNetworkEvents();
            DiscoverGameGroups();
            RefreshBindingChildren();
            PruneExitedProcesses();
            DiscoverAutomaticTargets();
            DiscoverRestarts();
            DiscoverMuMuRestarts();
            ReconcileAutomaticBindings();
            if ((config.GlobalProxy.Enabled || config.Bindings.Count > 0 || config.AutoProxy.Enabled) && NativeEngine.DriverPresent && (!started || NativeEngine.Core_IsRunning() == 0)) { try { EnsureEngine(); } catch (Exception ex) { engineError = ex.Message; } }
            ApplyRules();
            due = config.Bindings.Where(b => b.Enabled).Select(b => b.ProxyId).Concat(config.GlobalProxy.Enabled ? [config.GlobalProxy.ProxyId] : []).Distinct().FirstOrDefault(id => !tcpProbes.ContainsKey(id) && !supplementalChecks.ContainsKey(id) && (!retries.TryGetValue(id, out var r) || r.At <= DateTimeOffset.UtcNow));
            if (childrenDirty) { store.Save(config); childrenDirty = false; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { autoStatus = "自動分配／監測失敗：" + ex.Message; Log("監測失敗：" + ex.Message); }
        finally { Gate.Release(); }
        if (due != Guid.Empty) {
            try { await ProbeInBackground(due, ct, onlyIfDue: true); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) {
                await Gate.WaitAsync(ct);
                try { Log("背景檢測未完成：" + ex.Message); } finally { Gate.Release(); }
            }
        }
    }
    private void PruneExitedProcesses()
    {
        // A PID+birth binding cannot apply to a future process; retaining exited roots permanently consumes IP capacity.
        var expired = config.Bindings.Where(b => b.Kind == "Process" && !b.Processes.Concat(b.IncludeChildren ? b.Descendants : []).Any(p => LdPlayer.Identity(p.Pid) == p)).ToArray();
        foreach (var binding in expired) { config.Bindings.Remove(binding); targets.Remove(binding.TargetKey); }
        foreach (var key in targets.Where(t => t.Value.Kind == "Process" && !t.Value.Processes.Any(p => LdPlayer.Identity(p.Pid) == p) &&
            !config.Bindings.Any(b => b.TargetKey == t.Key && b.IncludeChildren && b.Descendants.Any(p => LdPlayer.Identity(p.Pid) == p))).Select(t => t.Key).ToArray()) targets.Remove(key);
        if (expired.Length > 0) { store.Save(config); Log($"已釋放 {expired.Length} 個已結束程序的分配名額。"); }
    }
    private void DiscoverRestarts()
    {
        var ldBindings = config.Bindings.Where(b => b.Kind == "LDPlayer").Concat(targets.Values.Where(t => t.Kind == "LDPlayer" && config.AutoProxy.Enabled &&
            (config.AutoProxy.Kind == "LDPlayer" || config.AutoProxy.Kind == "EmulatorSequence" && SequentialEmulatorPlanner.Matches(t, config.AutoProxy))).Select(t => new InstanceBinding { TargetKey = t.Key, Name = t.Name, Kind = t.Kind, Path = t.Path, Index = t.Index, Processes = t.Processes })).DistinctBy(b => b.TargetKey).ToArray(); if (ldBindings.Length == 0) return;
        // Do not execute a user's ldconsole.exe as LocalSystem. Read-only WMI and .vbox UUIDs support discovery while the UI is absent.
        using var searcher = EmulatorProcessesPresent(["Ld9BoxHeadless.exe", "LdVBoxHeadless.exe"])
            ? new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='Ld9BoxHeadless.exe' OR Name='LdVBoxHeadless.exe'") : null;
        using var collection = searcher?.Get();
        foreach (var b in ldBindings)
        {
            string vmFolder = Path.Combine(Path.GetDirectoryName(b.Path)!, "vms", $"leidian{b.Index}");
            var xmlFile = Directory.Exists(vmFolder) ? Directory.EnumerateFiles(vmFolder, "*.vbox").FirstOrDefault() : null;
            string uuid = xmlFile is null ? "" : System.Xml.Linq.XDocument.Load(xmlFile).Descendants().FirstOrDefault(x => x.Name.LocalName == "Machine")?.Attribute("uuid")?.Value.Trim('{', '}') ?? "";
            var found = new List<ProcessIdentity>();
            if (collection is not null) foreach (ManagementObject process in collection)
            {
                string path = (string?)process["ExecutablePath"] ?? "", command = (string?)process["CommandLine"] ?? "";
                string boxRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ldplayer9box") + Path.DirectorySeparatorChar;
                if (!path.StartsWith(Path.GetDirectoryName(b.Path)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(boxRoot, StringComparison.OrdinalIgnoreCase)) continue;
                bool match = System.Text.RegularExpressions.Regex.IsMatch(command, $@"(?i)(?<![a-z0-9])leidian{b.Index}(?![a-z0-9])") || uuid.Length > 0 && command.Contains(uuid, StringComparison.OrdinalIgnoreCase);
                int pid = checked((int)(uint)process["ProcessId"]);
                if (match && IsOwned(pid) && LdPlayer.Identity(pid) is { } identity) found.Add(identity);
            }
            if (found.Count == 1) targets[b.TargetKey] = new(b.TargetKey, b.Name, b.Kind, b.Path, b.Index, true, found.ToArray());
            else if (!b.Processes.Any(p => LdPlayer.Identity(p.Pid) == p)) targets[b.TargetKey] = new(b.TargetKey, b.Name, b.Kind, b.Path, b.Index, false, [], found.Count > 1 ? "無法唯一辨識實例" : "未執行或等待辨識");
        }
    }
    private static bool EmulatorProcessesPresent(IEnumerable<string> names)
    {
        bool present = false;
        foreach (string name in names) foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            using (process) { present = true; }
        return present;
    }
    private Snapshot GetSnapshot()
    {
        bool running = NativeEngine.Present && NativeEngine.Core_IsRunning() != 0;
        UpdateSessions();
        var statuses = config.Bindings.Select(b => {
            var pids = b.Processes.Concat(b.IncludeChildren ? b.Descendants : []).Distinct().Where(p => LdPlayer.Identity(p.Pid) == p).ToArray();
            var members = b.Kind == "GameGroup" ? config.GameGroups.FirstOrDefault(g => g.Key == b.TargetKey)?.Members.Select(m => m.Identity).ToArray() ?? pids : pids;
            long sent = 0, received = 0; uint tcp = 0, udp = 0;
            foreach (var p in members) if (NativeEngine.Present) { NativeEngine.Core_GetProcessStats((uint)p.Pid, (ulong)p.CreatedFileTime, out var t, out uint pt, out uint pu); sent += (long)t.Sent; received += (long)t.Received; tcp += pt; udp += pu; }
            string state = !running ? "核心未啟用" : conflicts.Contains(b.TargetKey) ? "共用程序，不支援分流" : !b.Enabled ? "已封鎖" : pids.Length == 0 ? "等待實例" : DuplicateExit(b.ProxyId) ? "出口重複，已封鎖" : !Healthy(b.ProxyId) ? "代理異常，已封鎖" : "代理執行中";
            return new RuntimeStatus(b.TargetKey, state, running ? "TCP／UDP 規則已下達；實際 IP 仍須從目標內驗證。連入時長為本次代理啟用時長；連線數為核心已建立的 TCP／UDP 關聯。" : engineError, sent, received) { TcpConnections = tcp, UdpAssociations = udp, ConnectedAt = sessions.ConnectedAt(b.TargetKey) };
        }).ToArray();
        var visibleTargets = targets.Values.Concat(config.Bindings.Select(b => new TargetInstance(b.TargetKey, b.Name, b.Kind, b.Path, b.Index,
            b.Processes.Concat(b.IncludeChildren ? b.Descendants : []).Any(p => LdPlayer.Identity(p.Pid) == p), b.Processes))).DistinctBy(t => t.Key).ToArray();
        var visibleProbes = probes.Values.Where(p => !probeViews.ContainsKey(p.ProxyId)).Concat(probeViews.Values).ToArray();
        return new(config.Proxies.Select(p => p.Redacted()).ToArray(), config.Bindings.ToArray(), visibleTargets, visibleProbes, statuses,
            new(true, NativeEngine.DriverPresent, NativeEngine.Present, running, AppReleasePackage.CurrentVersion + " 預覽版", NativeEngine.DriverPresent ? engineError : "缺少 WinDivert 驅動，未啟用攔截。", logs.ToArray()))
            { SupportsProxyExpiration = true, SupportsFixedEmulators = true, SupportsSequentialEmulators = true, SupportsRemoteControlBypass = NativeEngine.SupportsRemoteControlBypass, SupportsLowFrequencyProbes = true, SupportsTcpFirstProbes = true, AutoProxy = config.AutoProxy, AutoProxyStatus = autoStatus, GlobalProxy = config.GlobalProxy, GlobalProxyStatus = GlobalStatus(), GlobalProxyRouting = config.GlobalProxy.Enabled && running && Healthy(config.GlobalProxy.ProxyId), FlowEvents = ReadNetworkEvents().TakeLast(64).ToArray() };
    }
    private string GlobalStatus()
    {
        var g = config.GlobalProxy;
        if (!g.Enabled) return "全局代理未啟用";
        string name = config.Proxies.FirstOrDefault(p => p.Id == g.ProxyId)?.Name ?? "未知節點";
        string state = !started || NativeEngine.Core_IsRunning() == 0 ? "核心未啟用" : Healthy(g.ProxyId) ? "代理執行中" : "代理未通過檢測，外網保持封鎖";
        string remote = !g.BypassRemoteControl ? "" : NativeEngine.SupportsRemoteControlBypass ? "遠端控制直連 · 其餘" : "遠端直連待更新核心 · ";
        return $"全局：{name} · {state} · IPv4 TCP／UDP · {(g.BypassLan ? "區網直連" : "區網也走代理")} · {remote}外網 IPv6 封鎖";
    }
    private NetworkFlowEvent[] ReadNetworkEvents() => NativeEngine.FlowEvents().Select(e => new NetworkFlowEvent(e.Sequence, e.Flow,
        DateTimeOffset.FromFileTime(checked((long)e.At)), new(checked((int)e.Pid), checked((long)e.Created)),
        config.Proxies.FirstOrDefault(p => nativeIds.GetValueOrDefault(p.Id) == e.Proxy)?.Endpoint ?? "未指定", e.Protocol == 6 ? "TCP" : "UDP",
        new System.Net.IPAddress(BitConverter.GetBytes(e.Source)).ToString(), checked((int)e.SourcePort),
        new System.Net.IPAddress(BitConverter.GetBytes(e.Destination)).ToString(), checked((int)e.DestinationPort), e.Stage,
        NetworkFlowEvent.StageLabel(e.Stage), e.Error, e.Sent, e.Received)).ToArray();
    private void SaveNetworkEvents()
    {
        var rows = ReadNetworkEvents().Where(e => e.Sequence > lastEventSequence).ToArray();
        if (rows.Length == 0) return;
        try {
            string file = Path.Combine(directory, "network-events.jsonl");
            if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024) File.Move(file, Path.Combine(directory, "network-events.previous.jsonl"), true);
            File.AppendAllLines(file, rows.Select(e => JsonSerializer.Serialize(e, Json.Options)));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("無法保存連線事件，核心規則照常執行；記憶體中仍保留最近事件。"); }
        lastEventSequence = rows[^1].Sequence;
    }
    private DiagnosticBundle GetDiagnosticBundle()
    {
        var snapshot = GetSnapshot();
        var reports = config.Bindings.Select(b => {
            var group = config.GameGroups.FirstOrDefault(g => g.Key == b.TargetKey);
            var members = group?.Members ?? b.Processes.Select(p => new GameGroupMember(p, b.Path)).ToArray();
            var processes = members.Select(m => {
                bool alive = LdPlayer.Identity(m.Identity.Pid) == m.Identity;
                bool? responding = null; string title = ""; long window = 0;
                if (alive) try { using var p = Process.GetProcessById(m.Identity.Pid); responding = p.Responding; title = p.MainWindowTitle; window = p.MainWindowHandle.ToInt64(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
                var traffic = new NativeEngine.Traffic(); uint tcp = 0, udp = 0;
                if (NativeEngine.Present) NativeEngine.Core_GetProcessStats((uint)m.Identity.Pid, (ulong)m.Identity.CreatedFileTime, out traffic, out tcp, out udp);
                return new DiagnosticProcess(m.Identity, m.Path, alive, responding, title, window, (long)traffic.Sent, (long)traffic.Received, (long)traffic.Dropped, tcp, udp);
            }).ToArray();
            return new DiagnosticTarget(b.TargetKey, b.Name, FindProxy(b.ProxyId).Endpoint, snapshot.Statuses.FirstOrDefault(s => s.TargetKey == b.TargetKey), probes.GetValueOrDefault(b.ProxyId), processes);
        }).ToArray();
        SaveNetworkEvents();
        string dllVersion = "未找到", hash = "", signature = "未找到";
        string driverFile = Path.Combine(AppContext.BaseDirectory, "WinDivert64.sys"), driverDll = Path.Combine(AppContext.BaseDirectory, "WinDivert.dll");
        if (File.Exists(driverDll)) dllVersion = FileVersionInfo.GetVersionInfo(driverDll).FileVersion ?? "未標示";
        if (File.Exists(driverFile)) {
            try { hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(driverFile))); DriverTrust.VerifySignature(driverFile); signature = "驗證通過"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception) { signature = "無法確認，請使用元件診斷複查"; }
        }
        return new(DateTimeOffset.UtcNow, snapshot.Diagnostics.Version, snapshot.Diagnostics.EngineRunning, reports, ReadNetworkEvents(), logs.ToArray(),
            "只記錄程序、窗口與連線中繼資料，不含代理帳密、封包內容或遊戲帳密。上游 EOF／重設不能單獨證明是代理商或遊戲伺服器關閉；既有版本沒有事件的歷史無法補回。TCP 數不含已半關閉的連線。")
        { Compatibility = new("WinDivert 程序分流", NativeEngine.DriverPresent, snapshot.Diagnostics.EngineRunning, dllVersion, hash, signature,
            "本模式載入封包攔截驅動，不注入遊戲或修改遊戲檔案。Socket 關閉無法證明反作弊偵測；本模式不具備不可偵測保證。無主機驅動方向為遊戲原生代理或外部網關，需另行驗證多開分流。") };
    }
    private static void ValidateConsole(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !new[] { "ldconsole.exe", "dnconsole.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidOperationException("請選擇本機雷電 ldconsole.exe。");
    }
    private void ValidateTarget(TargetInstance target)
    {
        if (target.Kind == "LDPlayer") { ValidateConsole(target.Path); if (target.Index < 0 || target.Key != LdPlayer.Key(target.Path, target.Index)) throw new InvalidDataException("實例識別不符。"); }
        else if (target.Kind == "MuMu") {
            EmulatorCatalog.ValidateConsole("MuMu", target.Path);
            if (target.Index < 0 || target.Key != EmulatorCatalog.Key("MuMu", target.Path, target.Index) || target.Processes.Length > 1) throw new InvalidDataException("MuMu 實例識別不符。");
            string root = EmulatorCatalog.InstallRoot(target.Path) + Path.DirectorySeparatorChar;
            foreach (var process in target.Processes) if (!ProcessCatalog.ImagePath(process.Pid).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MuMu 虛擬機程序不在指定安裝位置，無法確認窗口。");
        }
        else if (target.Kind == "GameGroup") {
            var group = config.GameGroups.SingleOrDefault(g => g.Key == target.Key);
            if (group is null || !target.Path.Equals(group.LauncherPath, StringComparison.OrdinalIgnoreCase) || !target.Processes.ToHashSet().SetEquals(group.Target().Processes)) throw new InvalidDataException("遊戲組尚未由服務辨識，請重新整理。");
            foreach (var p in target.Processes) if (!ProcessCatalog.ImagePath(p.Pid).Equals(group.Members.Single(m => m.Identity == p).Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("遊戲組成員路徑已改變。");
        }
        else if (target.Kind != "Process") throw new InvalidDataException("不支援的目標種類。");
        foreach (var p in target.Processes) if (LdPlayer.Identity(p.Pid) != p || !IsOwned(p.Pid)) throw new InvalidOperationException("程序已結束、身分改變，或不屬於安裝使用者。");
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    private bool IsOwned(int pid)
    {
        nint process = OpenProcess(0x1000, false, pid); if (process == 0) return false;
        try { if (!OpenProcessToken(process, 8, out var token)) return false; try { using var identity = new WindowsIdentity(token); return identity.User?.Value == OwnerSid; } finally { CloseHandle(token); } }
        finally { CloseHandle(process); }
    }
    public void Dispose() {
        supplementalLifetime.Cancel(); Task.WhenAll(supplementalTasks).GetAwaiter().GetResult();
        gameObserver.Dispose(); if (NativeEngine.Present && started) NativeEngine.Core_Stop();
        supplementalLifetime.Dispose(); supplementalGate.Dispose(); probeGate.Dispose(); Gate.Dispose();
    }
}

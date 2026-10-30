using System.Security.Cryptography;
using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed partial class Controller
{
    private readonly ISandboxRuntime sandboxRuntime;
    private long sandboxMembershipRevision;
    private readonly Dictionary<Guid, (string Token, DateTimeOffset Expires)> sandboxTickets = [];
    private readonly Dictionary<Guid, string> sandboxErrors = [];
    private readonly HashSet<Guid> sandboxIdentityUnavailable = [];
    private readonly Dictionary<Guid, (int Count, DateTimeOffset Since)> sandboxQueryFailures = [];
    private readonly HashSet<ProcessIdentity> verifiedSandboxMembers = [];
    private readonly Dictionary<Guid, ProcessIdentity> sandboxBootstraps = [];
    private readonly Dictionary<Guid, string> sandboxLaunchErrors = [], sandboxStages = [];
    private readonly Dictionary<Guid, SandboxNetworkReport> sandboxNetworkChecks = [];
    private readonly SemaphoreSlim sandboxPoll = new(1,1);
    private bool sandboxStopping, sandboxRecoveryRequested;
    private readonly SemaphoreSlim sandboxStopGate = new(1,1);
    private DateTimeOffset sandboxNextDriverCheck;
    private string? sandboxDriverError;
    private bool sandboxFirewallReady=true;
    private Task sandboxInitialization=Task.CompletedTask;
    private async Task InitializeSandboxFirewallAsync(CancellationToken ct)
    {
        try {
            while(true){
                SandboxInstanceProfile[] profiles;long revision;
                await Gate.WaitAsync(ct);
                try{if(stopping||sandboxStopping)return;profiles=config.SandboxInstances.ToArray();revision=sandboxMembershipRevision;}
                finally{Gate.Release();}
                var images=await Task.Run(()=>discoverSandboxImages(profiles),ct);
                await Gate.WaitAsync(ct);
                try{
                    if(stopping||sandboxStopping)return;
                    if(revision!=sandboxMembershipRevision)continue;
                    ReplaceSandboxFirewallImages(images);sandboxFirewallReady=true;
                    ApplyRules();PublishSnapshot();return;
                }finally{Gate.Release();}
            }
        }catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch(Exception ex) when(ex is not OutOfMemoryException){
            await Gate.WaitAsync(CancellationToken.None);
            try{foreach(var p in config.SandboxInstances)sandboxErrors[p.Id]="持久封鎖初始化失敗，將自動重試："+ex.Message;PublishSnapshot();}
            finally{Gate.Release();}
        }
    }
    private async Task MutateSandboxOutsideGate(Command command,CancellationToken ct)
    {
        SandboxInstanceProfile profile;SandboxInstanceProfile[] next;long revision;
        bool saving=command.Action=="saveSandbox";
        await Gate.WaitAsync(ct);
        try{
            if(stopping||sandboxStopping)throw new InvalidOperationException("服務正在停止沙盒。");
            profile=saving?Json.Read<SandboxInstanceProfile>(command.Data):SandboxProfile(Json.Read<SandboxRequest>(command.Data).Id);
            if(config.SandboxInstances.FirstOrDefault(p=>p.Id==profile.Id)?.RemovalPending==true && command.Action!="finishSandboxRemoval")throw new InvalidOperationException("此實例正在清理，請從儲存空間頁重試完成。");
            if(saving&&profile.RemovalPending)throw new InvalidOperationException("不可由保存設定建立清理狀態。");
            if(command.Action=="finishSandboxRemoval") {
                if(!profile.RemovalPending)throw new InvalidOperationException("尚未保留清理工作。");
                if(Json.Read<SandboxRemovalRequest>(command.Data).DeleteFiles && (Directory.Exists(Path.GetDirectoryName(profile.WorkingDirectory)!) || File.Exists(Path.GetDirectoryName(profile.WorkingDirectory)!)))throw new IOException("仍有副本資料，保留清理紀錄以供重試。");
            }
            SandboxInstances.Validate(profile);revision=sandboxMembershipRevision;
            next=config.SandboxInstances.Where(p=>p.Id!=profile.Id).Concat(saving?[profile]:Array.Empty<SandboxInstanceProfile>()).ToArray();
        }finally{Gate.Release();}
        bool running=await Task.Run(()=>SandboxRuntime.Processes(profile.BoxName).Length>0,ct);
        if(running)throw new InvalidOperationException("請先停止此沙盒再修改或移除設定。");
        var images=sandboxFirewall is null?[]:await Task.Run(()=>discoverSandboxImages(next),ct);
        if(await Task.Run(()=>SandboxRuntime.Processes(profile.BoxName).Length>0,ct))throw new InvalidOperationException("掃描期間沙盒已啟動，未修改設定。");
        await Gate.WaitAsync(ct);
        try{
            if(stopping||sandboxStopping||revision!=sandboxMembershipRevision)throw new InvalidOperationException("沙盒設定已變更，請重試；未套用過期的映像清單。");
            if(saving)SaveSandbox(profile,images);else RemoveSandbox(profile.Id,images);
        }finally{try{PublishSnapshot();}finally{Gate.Release();}}
    }
    public async Task TickSandboxes(CancellationToken ct)
    {
        if(!await sandboxPoll.WaitAsync(0,ct))return;
        try {
            // The periodic control worker also runs while the packet path is paused.
            // Neither driver polling nor BFE RPC is performed by the packet worker.
            if(handoffClock()>=sandboxNextDriverCheck){
                bool check;await Gate.WaitAsync(ct);
                try {check=!stopping&&!sandboxStopping&&config.SandboxInstances.Count>0;} finally{Gate.Release();}
                if(check){
                    sandboxNextDriverCheck=handoffClock().AddSeconds(5);
                    try {await Task.Run(()=>SandboxRuntime.VerifyCompatibility(),ct);sandboxDriverError=null;}
                    catch(Exception ex) when(ex is not OutOfMemoryException && ex is not OperationCanceledException){sandboxDriverError=ex.Message;}
                    await Gate.WaitAsync(ct);
                    try{if(!sandboxFirewallReady&&sandboxInitialization.IsCompleted&&!stopping&&!sandboxStopping)sandboxInitialization=Task.Run(()=>InitializeSandboxFirewallAsync(supplementalLifetime.Token));}
                    finally{Gate.Release();}
                }
            }
            await Gate.WaitAsync(ct);
            try{if(!stopping&&!sandboxStopping&&!SandboxRuntime.IdentityAvailable)sandboxFirewall?.WithdrawPermit();}
            finally{Gate.Release();}
            var observations=await ObserveSandboxes(ct);if(observations.Length==0)return;
            await Gate.WaitAsync(ct);
            try {if(!stopping&&!sandboxStopping){
                string before=SandboxRoutingStamp();ReconcileSandboxes(observations);
                if(before!=SandboxRoutingStamp()){ApplyRules();PublishSnapshot();}
            }}
            finally{Gate.Release();}
        } finally{sandboxPoll.Release();}
    }
    private ISandboxRuntime SandboxRuntime => sandboxRuntime;
    private async Task<SandboxInstanceProfile> BeginSandboxRemoval(Guid id,CancellationToken ct)
    {
        SandboxInstanceProfile profile;long revision;
        await Gate.WaitAsync(ct);
        try {if(stopping||sandboxStopping)throw new InvalidOperationException("服務正在停止。");profile=SandboxProfile(id);revision=sandboxMembershipRevision;if(sandboxTickets.ContainsKey(id))throw new InvalidOperationException("請先停止正在啟動的沙盒。");}
        finally{Gate.Release();}
        if(await Task.Run(()=>SandboxRuntime.Processes(profile.BoxName).Length>0,ct))throw new InvalidOperationException("沙盒仍在執行，請先正常關閉遊戲並停止此實例，再移除。");
        await Gate.WaitAsync(ct);
        try {
            if(stopping||sandboxStopping||revision!=sandboxMembershipRevision||sandboxTickets.ContainsKey(id))throw new InvalidOperationException("實例狀態已變更，未開始清理。");
            var pending=profile with{Enabled=false,RemovalPending=true};
            var next=config with {SandboxInstances=config.SandboxInstances.Select(p=>p.Id==id?pending:p).ToList(),Bindings=config.Bindings.Select(b=>b.TargetKey==profile.TargetKey?b with{Enabled=false}:b).ToList()};
            store.Save(next);config.SandboxInstances=next.SandboxInstances;config.Bindings=next.Bindings;sandboxMembershipRevision++;
            ApplyRules();return pending;
        } finally {try{PublishSnapshot();}finally{Gate.Release();}}
    }
    private string SandboxRoutingStamp() => System.Text.Json.JsonSerializer.Serialize(new {
        Bindings=config.Bindings.Where(b=>b.Kind=="Sandbox"),Profiles=config.SandboxInstances,
        Tickets=sandboxTickets.Where(t=>t.Value.Expires>handoffClock()).Select(t=>t.Key),Errors=sandboxErrors,
        Members=verifiedSandboxMembers.OrderBy(p=>p.Pid).ThenBy(p=>p.CreatedFileTime),Unavailable=sandboxIdentityUnavailable
    },Json.Options);
    private async Task<SandboxLaunchTicket> ArmSandboxOutsideGate(Guid id,CancellationToken ct)
    {
        SandboxInstanceProfile profile;SandboxInstanceProfile[] profiles;long revision;
        await Gate.WaitAsync(ct);
        try { if(stopping||sandboxStopping)throw new InvalidOperationException("服務正在停止沙盒。");profile=SandboxProfile(id);if(profile.RemovalPending)throw new InvalidOperationException("此實例正在清理，請先完成移除。");if(!sandboxFirewallReady)throw new InvalidOperationException("正在背景建立 WFP 防護，完成後即可啟動。");profiles=config.SandboxInstances.ToArray();revision=sandboxMembershipRevision; }
        finally { Gate.Release(); }
        await Task.Run(()=>SandboxRuntime.ValidateSettings(profile),ct).WaitAsync(TimeSpan.FromSeconds(1),ct);
        await Task.Run(()=>SandboxRuntime.VerifyCompatibility(),ct).WaitAsync(TimeSpan.FromSeconds(1),ct);
        bool running=await Task.Run(()=>SandboxRuntime.Processes(profile.BoxName).Length>0,ct);
        var images=sandboxFirewall is null?[]:await Task.Run(()=>discoverSandboxImages(profiles),ct);
        await Gate.WaitAsync(ct);
        try {
            if(stopping||sandboxStopping||revision!=sandboxMembershipRevision||SandboxProfile(id)!=profile)throw new InvalidOperationException("沙盒設定已變更，請重新啟動。");
            ReplaceSandboxFirewallImages(images);
            return CommitSandboxArm(id,running);
        } finally {try {PublishSnapshot();}finally{Gate.Release();}}
    }
    private bool ShouldGuardSandbox(SandboxInstanceProfile profile) =>
        sandboxTickets.TryGetValue(profile.Id, out var ticket) && ticket.Expires > handoffClock() ||
        config.Bindings.Where(b => b.TargetKey == profile.TargetKey).SelectMany(b => b.Processes).Any(p => LdPlayer.Identity(p.Pid) == p);
    private SandboxInstanceProfile SandboxProfile(Guid id) => config.SandboxInstances.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("找不到沙盒實例。");
    private void SaveSandbox(SandboxInstanceProfile profile,string[] images)
    {
        SandboxInstances.Validate(profile);
        ProxyOwnershipLedger.From(config,vmwareRuntime).Require(profile.ProxyId,profile.TargetKey,"Sandbox");
        if (!config.Proxies.Any(p => p.Id == profile.ProxyId)) throw new InvalidOperationException("線路已移除，請重新選取。");
        var old = config.SandboxInstances.SingleOrDefault(p => p.Id == profile.Id);
        if (old is null && config.SandboxInstances.Count >= 10) throw new InvalidOperationException("最多建立十個沙盒實例。");
        if (old is not null && (old.BoxName != profile.BoxName || old.LauncherPath != profile.LauncherPath || old.WorkingDirectory != profile.WorkingDirectory || old.FileRoot != profile.FileRoot))
            throw new InvalidOperationException("已保存實例不可更換沙盒身分或遊戲位置，請另建實例。");
        if (sandboxTickets.ContainsKey(profile.Id)) throw new InvalidOperationException("請先停止此沙盒再修改設定。");
        if (config.SandboxInstances.Any(p => p.Id != profile.Id && (p.ProxyId == profile.ProxyId || p.BoxName == profile.BoxName || p.WorkingDirectory.Equals(profile.WorkingDirectory, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("每個沙盒需要獨立的線路與遊戲副本。");
        profile = profile with { Enabled = false };
        var profiles = config.SandboxInstances.Where(p => p.Id != profile.Id).Append(profile).ToList();
        var bindings = config.Bindings.Where(b => b.TargetKey != profile.TargetKey).ToList();
        if (bindings.Count >= 10) throw new InvalidOperationException("管理數量已達十個。");
        bindings.Add(new() { TargetKey = profile.TargetKey, Kind = "Sandbox", Name = profile.Name, Path = profile.LauncherPath, ProxyId = profile.ProxyId, Enabled = false });
        ReplaceSandboxFirewallImages(images);
        store.Save(config with { SandboxInstances = profiles, Bindings = bindings });
        sandboxMembershipRevision++;
        config.SandboxInstances = profiles; config.Bindings = bindings; targets.Remove(profile.TargetKey); ApplyRules();
        Log($"已保存沙盒「{profile.Name}」固定線路。");
    }
    private SandboxLaunchTicket ArmSandbox(Guid id)
    {
        var profile = SandboxProfile(id);
        SandboxRuntime.ValidateSettings(profile);
        SandboxRuntime.VerifyCompatibility();
        return CommitSandboxArm(id,SandboxRuntime.Processes(profile.BoxName).Length>0);
    }
    private SandboxLaunchTicket CommitSandboxArm(Guid id,bool running)
    {
        var profile = SandboxProfile(id); SandboxInstances.Validate(profile);
        if(profile.RemovalPending)throw new InvalidOperationException("此實例正在清理，不能啟動。");
        ProxyOwnershipLedger.From(config,vmwareRuntime).Require(profile.ProxyId,profile.TargetKey,"Sandbox");
        sandboxIdentityUnavailable.Remove(id);
        if (!NativeEngine.SupportsP0RoutingSafety) throw new InvalidOperationException("桌面沙盒需要 0.1.31 原生核心的封包身分保護，請安裝／修復元件。");
        if (running || sandboxTickets.TryGetValue(id, out var pending) && pending.Expires > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("此實例已執行或正在啟動，不會重複開啟。");
        try { _ = File.GetAttributes(profile.LauncherPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"服務無法讀取遊戲副本（{ex.GetType().Name} / {ex.HResult:X8}）：{ex.Message}"); }
        if (!Healthy(profile.ProxyId) || DuplicateExit(profile.ProxyId)) throw new InvalidOperationException("線路未通過檢測或出口重複，未啟動遊戲。");
        if (config.Bindings.Any(b => b.TargetKey != profile.TargetKey && b.ProxyId == profile.ProxyId)) throw new InvalidOperationException("此線路仍被其他窗口使用。");
        if (config.Bindings.Count(b => b.TargetKey != profile.TargetKey) >= 10) throw new InvalidOperationException("管理數量已達十個。");
        EnsureEngine(); profile = profile with { Enabled = true };
        sandboxMembershipRevision++;
        config.SandboxInstances = config.SandboxInstances.Select(p => p.Id == id ? profile : p).ToList();
        config.Bindings.RemoveAll(b => b.TargetKey == profile.TargetKey);
        config.Bindings.Add(new() { TargetKey = profile.TargetKey, Kind = "Sandbox", Name = profile.Name, Path = profile.LauncherPath,
            ProxyId = profile.ProxyId, Enabled = true, SharedProxy = false });
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sandboxTickets[id] = (token, DateTimeOffset.UtcNow.AddSeconds(60)); sandboxErrors.Remove(id);
        sandboxLaunchErrors.Remove(id); sandboxStages.Remove(id); sandboxBootstraps.Remove(id); sandboxNetworkChecks.Remove(id);
        ApplyRules(); store.Save(config); Log($"沙盒「{profile.Name}」已保留線路，等待啟動核驗。");
        return new(profile, token);
    }
    private SandboxLaunchPermission RegisterSandbox(SandboxRegistration request, int? callerPid)
    {
        var profile = SandboxProfile(request.Id);
        ProxyOwnershipLedger.From(config,vmwareRuntime).Require(profile.ProxyId,profile.TargetKey,"Sandbox");
        if (callerPid != request.Process.Pid || LdPlayer.Identity(request.Process.Pid) != request.Process)
            throw new InvalidOperationException("啟動程序 PID 或建立時間不符。");
        if (!sandboxTickets.TryGetValue(request.Id, out var ticket) || ticket.Expires < DateTimeOffset.UtcNow ||
            request.Token.Length != ticket.Token.Length || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(request.Token), System.Text.Encoding.ASCII.GetBytes(ticket.Token)))
            throw new InvalidOperationException("啟動核驗已失效，請重新啟動。");
        var observed = SandboxRuntime.Query(request.Process.Pid);
        if (observed is null || observed.BoxName != profile.BoxName || observed.OwnerSid != OwnerSid)
            throw new InvalidOperationException("程序不在指定沙盒中，未啟動遊戲。");
        if (!profile.Enabled || !Healthy(profile.ProxyId) || DuplicateExit(profile.ProxyId)) throw new InvalidOperationException("線路尚未就緒，未啟動遊戲。");
        sandboxTickets.Remove(request.Id); ReconcileSandboxes(); ApplyRules();
        var binding = config.Bindings.Single(b => b.TargetKey == profile.TargetKey);
        if (!binding.Processes.Contains(request.Process)) throw new InvalidOperationException("沙盒程序尚未完成路由。");
        Log($"沙盒「{profile.Name}」身分與代理核驗完成。");
        if (NativeEngine.Core_IsRunning() == 0 || conflicts.Contains(profile.TargetKey)) throw new InvalidOperationException("代理核心尚未就緒，未啟動遊戲。");
        sandboxBootstraps[request.Id] = request.Process;
        return new(profile.LauncherPath, profile.WorkingDirectory, profile.FileRoot);
    }
    private void ReportSandboxLaunch(SandboxLaunchReport report, int? callerPid)
    {
        var profile = SandboxProfile(report.Id);
        if (!sandboxBootstraps.TryGetValue(report.Id, out var helper) || callerPid != helper.Pid || LdPlayer.Identity(helper.Pid) != helper ||
            SandboxRuntime.Query(helper.Pid) is not { } member || member.BoxName != profile.BoxName || member.OwnerSid != OwnerSid)
            throw new InvalidOperationException("沙盒啟動回報身分不符。");
        if (report.Started) {
            if (!sandboxNetworkChecks.ContainsKey(report.Id)) throw new InvalidOperationException("尚未通過沙盒出口驗證。");
            sandboxStages[report.Id] = "啟動器已建立，請確認遊戲畫面";
        }
        else {
            sandboxLaunchErrors[report.Id] = "啟動器啟動失敗：" + report.Detail[..Math.Min(report.Detail.Length, 400)];
            PauseSandbox(report.Id);
        }
        Log($"沙盒「{profile.Name}」：{(report.Started ? sandboxStages[report.Id] : sandboxLaunchErrors[report.Id])}。");
    }
    private void ReportSandboxNetwork(SandboxNetworkReport report, int? callerPid)
    {
        var profile = SandboxProfile(report.Id);
        if (!sandboxBootstraps.TryGetValue(report.Id, out var helper) || callerPid != helper.Pid || LdPlayer.Identity(helper.Pid) != helper ||
            SandboxRuntime.Query(helper.Pid) is not { } member || member.BoxName != profile.BoxName || member.OwnerSid != OwnerSid)
            throw new InvalidOperationException("出口驗證程序身分不符。");
        var probe = probes.GetValueOrDefault(profile.ProxyId);
        if (!profile.Enabled || !Healthy(profile.ProxyId) || DuplicateExit(profile.ProxyId) || !SandboxInstances.ExitMatches(report, probe))
            throw new InvalidOperationException($"沙盒出口驗證未通過（TCP：{report.TcpExit ?? "未收到回覆"}，UDP：{report.UdpExit ?? "未收到回覆"}），未啟動遊戲。");
        sandboxNetworkChecks[report.Id] = report;
        Log($"沙盒「{profile.Name}」TCP／UDP 實測出口吻合；直接 DNS 檢測：{(report.DirectDns ? "通過" : "未通過")}。共享 Windows DNS 另行驗證。");
    }
    private sealed record SandboxObservation(SandboxInstanceProfile Profile, SandboxProcess[] Members, string? Error, long Revision);
    private async Task<SandboxObservation[]> ObserveSandboxes(CancellationToken ct)
    {
        SandboxInstanceProfile[] profiles; long revision;
        await Gate.WaitAsync(ct);
        try { if (stopping||sandboxStopping) return []; profiles = config.SandboxInstances.ToArray(); revision = sandboxMembershipRevision; }
        finally { Gate.Release(); }
        if (profiles.Length == 0) return [];
        return await Task.Run(() => profiles.Select(profile => {
            ct.ThrowIfCancellationRequested();
            try { return new SandboxObservation(profile, SandboxRuntime.Processes(profile.BoxName), null, revision); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return new SandboxObservation(profile, [], ex.Message, revision); }
        }).ToArray(), ct);
    }
    private void ReconcileSandboxes(SandboxObservation[]? observations = null)
    {
        if (observations is not null && observations.Any(o => o.Revision != sandboxMembershipRevision)) return;
        if (observations is null) sandboxMembershipRevision++;
        verifiedSandboxMembers.RemoveWhere(p => LdPlayer.Identity(p.Pid) != p);
        foreach (var profile in config.SandboxInstances) {
            try {
                var observed = observations?.FirstOrDefault(o => o.Profile.Id == profile.Id && o.Profile.BoxName == profile.BoxName);
                if (observations is not null && observed is null) throw new IOException("等待新沙盒身分核驗。");
                if (observed?.Error is { } error) throw new IOException(error);
                var members = SandboxInstances.VerifiedMembers(profile, observed?.Members ?? SandboxRuntime.Processes(profile.BoxName), OwnerSid, LdPlayer.Identity);
                var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == profile.TargetKey);
                foreach(var member in members.Except(binding?.Processes??[])) {
                    var age=DateTimeOffset.UtcNow-UtcBirth(member.CreatedFileTime);
                    Log($"沙盒 {profile.Name} PID {member.Pid} 納管延遲 {Math.Max(0,age.TotalMilliseconds):F0} ms。");
                }
                if(binding is not null) verifiedSandboxMembers.ExceptWith(binding.Processes);
                verifiedSandboxMembers.UnionWith(members);
                sandboxQueryFailures.Remove(profile.Id);
                if (binding is not null) { binding.Processes = members; binding.Enabled = sandboxFirewallReady && profile.Enabled && !sandboxLaunchErrors.ContainsKey(profile.Id); }
                targets[profile.TargetKey] = new(profile.TargetKey, profile.Name, "Sandbox", profile.LauncherPath, -1, members.Length > 0, members,
                    "沙盒：" + profile.BoxName + " · 固定線路");
                sandboxErrors.Remove(profile.Id);
                sandboxIdentityUnavailable.Remove(profile.Id);
                if (!SandboxRuntime.IdentityAvailable) {
                    sandboxIdentityUnavailable.Add(profile.Id);
                    sandboxErrors[profile.Id] = sandboxDriverError ?? "身分查詢不可用 · 已封鎖，每 5 秒自動重試。";
                    if (binding is not null) binding.Enabled = false;
                }
                if (sandboxTickets.TryGetValue(profile.Id, out var pending) && pending.Expires < DateTimeOffset.UtcNow) {
                    sandboxTickets.Remove(profile.Id); sandboxLaunchErrors[profile.Id] = "啟動核驗逾時，請查看沙盒啟動紀錄。";
                    if (binding is not null) binding.Enabled = false;
                }
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                sandboxErrors[profile.Id] = ex.Message;
                var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == profile.TargetKey);
                var previous=sandboxQueryFailures.GetValueOrDefault(profile.Id,(0,handoffClock()));
                var failures=(Count:previous.Item1+1,Since:previous.Item2);sandboxQueryFailures[profile.Id]=failures;
                if (binding is not null) {
                    binding.Processes=binding.Processes.Where(p=>verifiedSandboxMembers.Contains(p)&&LdPlayer.Identity(p.Pid)==p).ToArray();
                    if(failures.Count>=3||handoffClock()-failures.Since>=TimeSpan.FromSeconds(15)) binding.Enabled=false;
                }
            }
        }
    }
    private static DateTimeOffset UtcBirth(long fileTime)=>new(DateTime.FromFileTimeUtc(fileTime));
    private void PauseSandbox(Guid id)
    {
        sandboxMembershipRevision++;
        var profile = SandboxProfile(id);
        config.SandboxInstances = config.SandboxInstances.Select(p => p.Id == id ? p with { Enabled = false } : p).ToList();
        sandboxTickets.Remove(id);
        var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == profile.TargetKey); if (binding is not null) binding.Enabled = false;
        ApplyRules(); store.Save(config);
    }
    private void StopManagedSandboxes()
    {
        var errors=TerminateSandboxBatch(config.SandboxInstances.ToArray(),KnownSandboxMembers());
        if(errors.Count>0){foreach(var binding in config.Bindings.Where(b=>b.Kind=="Sandbox"))binding.Enabled=false;ApplyRules();throw new InvalidOperationException("沙盒仍在執行，拒絕停止代理服務："+string.Join("；",errors));}
    }
    private Dictionary<string,ProcessIdentity[]> KnownSandboxMembers() => config.Bindings.Where(b=>b.Kind=="Sandbox").ToDictionary(b=>b.TargetKey,b=>b.Processes.Where(verifiedSandboxMembers.Contains).ToArray());
    private List<string> TerminateSandboxBatch(SandboxInstanceProfile[] profiles,Dictionary<string,ProcessIdentity[]> knownMembers)
    {
        var errors=new List<string>();
        foreach (var profile in profiles) {
            try { SandboxRuntime.TerminateOwned(profile, OwnerSid); }
            catch(Exception ex) when(ex is not OutOfMemoryException) {
                var known=knownMembers.GetValueOrDefault(profile.TargetKey,[]);
                // CloseAsync pins handles and verifies both birth and SID before termination.
                var closed=ManagedProcessTerminator.CloseAsync(known,OwnerSid).GetAwaiter().GetResult();
                bool confirmed=false;
                try { confirmed=SandboxRuntime.Processes(profile.BoxName).Length==0; } catch(Exception check) when(check is not OutOfMemoryException){ }
                if(!confirmed||closed.Any(r=>r.State is not ("closed" or "exited"))) errors.Add(profile.Name+"："+ex.Message);
            }
        }
        return errors;
    }
    private async Task StopSandboxesOutsideGate(CancellationToken ct,bool stopService=false)
    {
        await sandboxStopGate.WaitAsync(ct);
        try {
            SandboxInstanceProfile[] profiles;Dictionary<string,ProcessIdentity[]> known;long revision;
            await Gate.WaitAsync(ct);
            try {
                if(stopping)return;
                sandboxStopping=true;revision=++sandboxMembershipRevision;
                profiles=config.SandboxInstances.ToArray();known=KnownSandboxMembers();sandboxTickets.Clear();
                foreach(var binding in config.Bindings.Where(b=>b.Kind=="Sandbox"))binding.Enabled=false;
                ApplyRules();PublishSnapshot();
            } finally {Gate.Release();}
            // Do not cancel after termination starts: settle the operation and generation
            // before allowing another launch to reuse a box or a proxy assignment.
            var errors=await Task.Run(()=>TerminateSandboxBatch(profiles,known));
            await Gate.WaitAsync(CancellationToken.None);
            try {
                if(revision!=sandboxMembershipRevision)throw new InvalidOperationException("沙盒世代已變更，未停止核心。");
                if(errors.Count>0){config.SandboxInstances=config.SandboxInstances.Select(p=>p with{Enabled=false}).ToList();store.Save(config);throw new InvalidOperationException("沙盒仍在執行，拒絕停止代理服務："+string.Join("；",errors));}
                if(stopService){sandboxFirewall?.WithdrawPermit();SuspendVmwareNetwork();supplementalLifetime.Cancel();if(NativeEngine.Present)NativeEngine.Core_Stop();started=false;stopping=true;}
            } finally {sandboxStopping=false;try{PublishSnapshot();}finally{Gate.Release();}}
        } finally {
            if(sandboxStopping){await Gate.WaitAsync(CancellationToken.None);try{sandboxStopping=false;}finally{Gate.Release();}}
            sandboxStopGate.Release();
        }
    }
    private async Task<bool> StopServiceOutsideGate(CancellationToken ct)
    {
        await StopSandboxesOutsideGate(ct,true);return true;
    }
    private void RemoveSandbox(Guid id,string[] images)
    {
        var profile = SandboxProfile(id);
        if(sandboxTickets.ContainsKey(id))throw new InvalidOperationException("請先停止正在啟動的沙盒。");
        ReplaceSandboxFirewallImages(images);
        sandboxMembershipRevision++;
        config.SandboxInstances.RemoveAll(p => p.Id == id); config.Bindings.RemoveAll(b => b.TargetKey == profile.TargetKey);
        targets.Remove(profile.TargetKey); sandboxTickets.Remove(id); sandboxErrors.Remove(id); sandboxLaunchErrors.Remove(id); sandboxStages.Remove(id); sandboxBootstraps.Remove(id); sandboxNetworkChecks.Remove(id); ApplyRules(); store.Save(config);
    }
    private void ReplaceSandboxFirewallImages(string[] images)
    {
        sandboxFirewall?.ReplaceImages(images);
        if(started&&engineLifecycle.Healthy)sandboxFirewall?.AllowWhileHealthy();
    }
    private SandboxInstanceStatus[] SandboxStatuses() => config.SandboxInstances.Select(p => {
        var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == p.TargetKey); var members = binding?.Processes ?? [];
        return new SandboxInstanceStatus(p.Id, p.RemovalPending ? "清理未完成 · 請重試移除" : !sandboxFirewallReady ? "正在建立 WFP 防護 · 沙盒暫停" : sandboxIdentityUnavailable.Contains(p.Id) ? "身分查詢不可用 · 已封鎖 · 自動重試" : members.Length > 0 && NativeEngine.Core_IsRunning() == 0 ? "代理核心未執行 · 請停止沙盒" : sandboxLaunchErrors.ContainsKey(p.Id) ? "啟動失敗 · 已封鎖" : sandboxErrors.ContainsKey(p.Id) ? binding?.Enabled==true ? "身分查詢重試 · 保留既有線路" : "沙盒身分查詢失敗 · 已封鎖" : sandboxTickets.ContainsKey(p.Id) ? "正在啟動" :
            members.Length == 0 ? "未啟動" : !p.Enabled ? "已暫停 · 已封鎖" : !Healthy(p.ProxyId) || DuplicateExit(p.ProxyId) ? "線路異常 · 已封鎖" : !sandboxNetworkChecks.ContainsKey(p.Id) ? "正在驗證出口" : "代理執行中",
            sandboxLaunchErrors.GetValueOrDefault(p.Id, sandboxErrors.GetValueOrDefault(p.Id, sandboxStages.GetValueOrDefault(p.Id, p.BoxName))), members) { Network = sandboxNetworkChecks.GetValueOrDefault(p.Id) };
    }).ToArray();
}

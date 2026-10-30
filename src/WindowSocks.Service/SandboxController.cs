using System.Security.Cryptography;
using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed partial class Controller
{
    private readonly SandboxieServiceRuntime sandboxRuntime = new();
    private long sandboxMembershipRevision;
    private readonly Dictionary<Guid, (string Token, DateTimeOffset Expires)> sandboxTickets = [];
    private readonly Dictionary<Guid, string> sandboxErrors = [];
    private readonly HashSet<ProcessIdentity> verifiedSandboxMembers = [];
    private readonly Dictionary<Guid, ProcessIdentity> sandboxBootstraps = [];
    private readonly Dictionary<Guid, string> sandboxLaunchErrors = [], sandboxStages = [];
    private readonly Dictionary<Guid, SandboxNetworkReport> sandboxNetworkChecks = [];
    private SandboxieServiceRuntime SandboxRuntime => sandboxRuntime;
    private SandboxInstanceProfile SandboxProfile(Guid id) => config.SandboxInstances.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("找不到沙盒實例。");
    private void SaveSandbox(SandboxInstanceProfile profile)
    {
        SandboxInstances.Validate(profile);
        if (!config.Proxies.Any(p => p.Id == profile.ProxyId)) throw new InvalidOperationException("線路已移除，請重新選取。");
        var old = config.SandboxInstances.SingleOrDefault(p => p.Id == profile.Id);
        if (old is null && config.SandboxInstances.Count >= 10) throw new InvalidOperationException("最多建立十個沙盒實例。");
        if (old is not null && (old.BoxName != profile.BoxName || old.LauncherPath != profile.LauncherPath || old.WorkingDirectory != profile.WorkingDirectory || old.FileRoot != profile.FileRoot))
            throw new InvalidOperationException("已保存實例不可更換沙盒身分或遊戲位置，請另建實例。");
        if (SandboxRuntime.Processes(profile.BoxName).Length > 0 || sandboxTickets.ContainsKey(profile.Id)) throw new InvalidOperationException("請先停止此沙盒再修改設定。");
        if (config.SandboxInstances.Any(p => p.Id != profile.Id && (p.ProxyId == profile.ProxyId || p.BoxName == profile.BoxName || p.WorkingDirectory.Equals(profile.WorkingDirectory, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("每個沙盒需要獨立的線路與遊戲副本。");
        profile = profile with { Enabled = false };
        var profiles = config.SandboxInstances.Where(p => p.Id != profile.Id).Append(profile).ToList();
        var bindings = config.Bindings.Where(b => b.TargetKey != profile.TargetKey).ToList();
        if (bindings.Count >= 10) throw new InvalidOperationException("管理數量已達十個。");
        bindings.Add(new() { TargetKey = profile.TargetKey, Kind = "Sandbox", Name = profile.Name, Path = profile.LauncherPath, ProxyId = profile.ProxyId, Enabled = false });
        store.Save(config with { SandboxInstances = profiles, Bindings = bindings });
        config.SandboxInstances = profiles; config.Bindings = bindings; targets.Remove(profile.TargetKey); ApplyRules();
        Log($"已保存沙盒「{profile.Name}」固定線路。");
    }
    private SandboxLaunchTicket ArmSandbox(Guid id)
    {
        var profile = SandboxProfile(id); SandboxInstances.Validate(profile);
        if (!NativeEngine.SupportsSandboxGuards) throw new InvalidOperationException("桌面沙盒需要 0.1.27 原生核心，請安裝／修復元件。");
        if (SandboxRuntime.Processes(profile.BoxName).Length > 0 || sandboxTickets.TryGetValue(id, out var pending) && pending.Expires > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("此實例已執行或正在啟動，不會重複開啟。");
        try { _ = File.GetAttributes(profile.LauncherPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"服務無法讀取遊戲副本（{ex.GetType().Name} / {ex.HResult:X8}）：{ex.Message}"); }
        if (!Healthy(profile.ProxyId) || DuplicateExit(profile.ProxyId)) throw new InvalidOperationException("線路未通過檢測或出口重複，未啟動遊戲。");
        if (config.Bindings.Any(b => b.TargetKey != profile.TargetKey && b.ProxyId == profile.ProxyId)) throw new InvalidOperationException("此線路仍被其他窗口使用。");
        if (config.Bindings.Count(b => b.TargetKey != profile.TargetKey) >= 10) throw new InvalidOperationException("管理數量已達十個。");
        EnsureEngine(); profile = profile with { Enabled = true };
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
        try { if (stopping) return []; profiles = config.SandboxInstances.ToArray(); revision = sandboxMembershipRevision; }
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
        verifiedSandboxMembers.Clear();
        foreach (var profile in config.SandboxInstances) {
            try {
                var observed = observations?.FirstOrDefault(o => o.Profile.Id == profile.Id && o.Profile.BoxName == profile.BoxName);
                if (observations is not null && observed is null) throw new IOException("等待新沙盒身分核驗。");
                if (observed?.Error is { } error) throw new IOException(error);
                var members = SandboxInstances.VerifiedMembers(profile, observed?.Members ?? SandboxRuntime.Processes(profile.BoxName), OwnerSid, LdPlayer.Identity);
                verifiedSandboxMembers.UnionWith(members);
                var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == profile.TargetKey);
                if (binding is not null) { binding.Processes = members; binding.Enabled = profile.Enabled && !sandboxLaunchErrors.ContainsKey(profile.Id); }
                targets[profile.TargetKey] = new(profile.TargetKey, profile.Name, "Sandbox", profile.LauncherPath, -1, members.Length > 0, members,
                    "沙盒：" + profile.BoxName + " · 固定線路");
                sandboxErrors.Remove(profile.Id);
                if (sandboxTickets.TryGetValue(profile.Id, out var pending) && pending.Expires < DateTimeOffset.UtcNow) {
                    sandboxTickets.Remove(profile.Id); sandboxLaunchErrors[profile.Id] = "啟動核驗逾時，請查看沙盒啟動紀錄。";
                    if (binding is not null) binding.Enabled = false;
                }
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                sandboxErrors[profile.Id] = ex.Message;
                var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == profile.TargetKey);
                if (binding is not null) binding.Enabled = false;
            }
        }
    }
    private void PauseSandbox(Guid id)
    {
        var profile = SandboxProfile(id);
        config.SandboxInstances = config.SandboxInstances.Select(p => p.Id == id ? p with { Enabled = false } : p).ToList();
        sandboxTickets.Remove(id);
        var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == profile.TargetKey); if (binding is not null) binding.Enabled = false;
        ApplyRules(); store.Save(config);
    }
    private void StopManagedSandboxes()
    {
        foreach (var profile in config.SandboxInstances) SandboxRuntime.TerminateOwned(profile, OwnerSid);
    }
    private void RemoveSandbox(Guid id)
    {
        var profile = SandboxProfile(id);
        if (SandboxRuntime.Processes(profile.BoxName).Length > 0) throw new InvalidOperationException("請先停止沙盒再移除規則。");
        config.SandboxInstances.RemoveAll(p => p.Id == id); config.Bindings.RemoveAll(b => b.TargetKey == profile.TargetKey);
        targets.Remove(profile.TargetKey); sandboxTickets.Remove(id); sandboxErrors.Remove(id); sandboxLaunchErrors.Remove(id); sandboxStages.Remove(id); sandboxBootstraps.Remove(id); sandboxNetworkChecks.Remove(id); ApplyRules(); store.Save(config);
    }
    private SandboxInstanceStatus[] SandboxStatuses() => config.SandboxInstances.Select(p => {
        var binding = config.Bindings.FirstOrDefault(b => b.TargetKey == p.TargetKey); var members = binding?.Processes ?? [];
        return new SandboxInstanceStatus(p.Id, members.Length > 0 && NativeEngine.Core_IsRunning() == 0 ? "代理核心未執行 · 請停止沙盒" : sandboxLaunchErrors.ContainsKey(p.Id) ? "啟動失敗 · 已封鎖" : sandboxErrors.ContainsKey(p.Id) ? "身分未確認 · 已封鎖" : sandboxTickets.ContainsKey(p.Id) ? "正在啟動" :
            members.Length == 0 ? "未啟動" : !p.Enabled ? "已暫停 · 已封鎖" : !Healthy(p.ProxyId) || DuplicateExit(p.ProxyId) ? "線路異常 · 已封鎖" : !sandboxNetworkChecks.ContainsKey(p.Id) ? "正在驗證出口" : "代理執行中",
            sandboxLaunchErrors.GetValueOrDefault(p.Id, sandboxErrors.GetValueOrDefault(p.Id, sandboxStages.GetValueOrDefault(p.Id, p.BoxName))), members) { Network = sandboxNetworkChecks.GetValueOrDefault(p.Id) };
    }).ToArray();
}

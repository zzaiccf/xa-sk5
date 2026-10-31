using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class Backend
{
    private readonly SandboxLaunchQueue sandboxLaunchQueue = new();
    private readonly SemaphoreSlim sandboxPreparationGate = new(1, 1);
    private readonly SemaphoreSlim sandboxStartGate = new(1, 1);
    public bool SandboxLaunchBusy => sandboxLaunchQueue.IsRunning;
    public Task<SandboxLaunchOutcome[]> StartSandboxes(Guid[] ids, int intervalSeconds, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => sandboxLaunchQueue.RunAsync(ids, intervalSeconds, StartSandbox, progress, ct), ct);
    public virtual Task<bool> SandboxInstalled(CancellationToken ct) => Task.Run(() => SandboxieRuntime.Installed, ct);
    public virtual Task<SandboxieSetupState> SandboxEnvironment(CancellationToken ct) => Task.Run(SandboxieInstaller.Inspect, ct);
    public virtual Task EnsureSandboxInstalled(IProgress<UpdateProgress>? progress, CancellationToken ct) => Task.Run(() => new SandboxieInstaller().EnsureInstalled(progress, ct), ct);
    public virtual Task<SandboxInstanceProfile[]> ResumeSandboxPreparation(string source, Guid[] proxies, SandboxInstanceProfile[] existing, CancellationToken ct) => Task.Run(() => SandboxPreparation.Resume(source, proxies, existing), ct);
    public virtual Task<SandboxInstanceProfile[]> ResumeSandboxNetworkPreparation(string source, SandboxNetworkChoice[] choices, SandboxInstanceProfile[] existing, CancellationToken ct) =>
        Task.Run(() => SandboxPreparation.ResumeNetwork(source, choices, existing), ct);
    public virtual Task<bool> DetectYongOrigin(string path, CancellationToken ct) => Task.Run(() => SandboxCompatibility.IsYongOrigin(path), ct);
    public virtual async Task SaveSandboxNetworks(SandboxInstanceProfile[] profiles, IProgress<string>? progress, CancellationToken ct)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        SandboxPreparation.ValidateBatch(profiles);
        foreach (var p in profiles) {
            RequireSandboxNetworkSupport(state, p);
            if (!state.SandboxInstances.Any(old => old.Id == p.Id)) throw new InvalidOperationException("新實例請使用準備環境。");
            if (state.SandboxStatuses.Any(s => s.Id == p.Id && s.Processes.Length > 0)) throw new InvalidOperationException("請先停止 " + p.Name + " 再更改網路模式或線路。");
        }
        int saved = 0;
        try {
            foreach (var p in profiles) {
                await Call<bool>(Command.Create("saveSandbox", p), ct).ConfigureAwait(false);
                saved++; progress?.Report("已保存 " + p.Name + (p.UseLocalNetwork ? " · 本地網路" : " · 代理線路"));
            }
        } catch (Exception ex) when (ex is not OutOfMemoryException && saved > 0) {
            throw new InvalidOperationException($"已保存 {saved} 個實例，其餘未完成；請重新整理確認後重試。{ex.Message}", ex);
        }
    }
    public virtual async Task PrepareSandboxes(SandboxInstanceProfile[] profiles, IProgress<string>? progress, CancellationToken ct)
    {
        if (!await sandboxPreparationGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("先前的準備或取消尚未完成，請稍後重試。");
        try { await PrepareSandboxesCore(profiles, progress, ct).ConfigureAwait(false); }
        finally { sandboxPreparationGate.Release(); }
    }
    private async Task PrepareSandboxesCore(SandboxInstanceProfile[] pair, IProgress<string>? progress, CancellationToken ct)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        if (!state.SupportsSandboxExperience) throw new InvalidOperationException("請先到設定安裝／修復 0.1.30 以上背景服務，再建立遊戲環境。");
        SandboxPreparation.ValidateBatch(pair);
        if (state.SandboxInstances.Select(p => p.Id).Concat(pair.Select(p => p.Id)).Distinct().Count() > 10 ||
            state.Bindings.Select(b => b.TargetKey).Concat(pair.Select(p => p.TargetKey)).Distinct().Count() > 10)
            throw new InvalidOperationException("最多管理十個實例，請先移除不使用的規則。");
        foreach (var profile in pair) {
            SandboxInstances.Validate(profile);
            RequireSandboxNetworkSupport(state, profile);
            if (profile.UseLocalNetwork) continue;
            if (state.Bindings.Any(b => b.TargetKey != profile.TargetKey && b.ProxyId == profile.ProxyId) ||
                state.SandboxInstances.Any(p => p.Id != profile.Id && p.ProxyId == profile.ProxyId) ||
                state.GlobalProxy.Enabled && state.GlobalProxy.ProxyId == profile.ProxyId)
                throw new InvalidOperationException(profile.Name + " 的線路已被其他規則使用，請選擇未使用的線路。");
            if (!state.Proxies.Any(p => p.Id == profile.ProxyId)) throw new InvalidOperationException("指定線路已移除，請重新選取。");
        }
        await Task.Run(() => {
            if (!SandboxieRuntime.Installed) throw new InvalidOperationException("請先安裝官方 Sandboxie Plus 1.18.8 x64，再按重新檢查。");
            SandboxPreparation.CheckCapacity(pair, ct);
            using var runtime = new SandboxieRuntime();
            foreach (var p in pair) if (runtime.Processes(p.BoxName).Length > 0) throw new InvalidOperationException("請先停止 " + p.Name + " 再準備環境。");
        }, ct).ConfigureAwait(false);
        foreach (var profile in pair) await PrepareSandbox(profile, progress, ct).ConfigureAwait(false);
    }
    public async Task<Snapshot> SandboxSnapshot(CancellationToken ct = default)
    {
        var state = await Call<Snapshot>(Command.Create("snapshot", new { }), ct).ConfigureAwait(false);
        if (!state.SupportsSandboxInstances) throw new InvalidOperationException("桌面沙盒需要 0.1.27 背景服務及核心，請先到設定安裝／修復。");
        if (!state.SupportsSandboxSafety) throw new InvalidOperationException("桌面沙盒需要 0.1.31 背景服務與原生核心的封包身分保護，請先更新完整元件。");
        return state;
    }
    private static void RequireSandboxNetworkSupport(Snapshot state, SandboxInstanceProfile profile)
    {
        if (profile.UseLocalNetwork && !state.SupportsSandboxLocalNetwork)
            throw new InvalidOperationException("本地網路沙盒需要 0.1.36 背景服務；請更新完整程式與服務後再保存或啟動。");
    }
    public async Task PrepareSandbox(SandboxInstanceProfile profile, IProgress<string>? progress, CancellationToken ct)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        RequireSandboxNetworkSupport(state, profile);
        SandboxInstances.Validate(profile);
        if (!SandboxieRuntime.Installed) throw new InvalidOperationException("請先安裝官方 Sandboxie Plus 1.18.8 x64。");
        if (!profile.UseLocalNetwork && !state.Proxies.Any(p => p.Id == profile.ProxyId)) throw new InvalidOperationException("找不到指定線路。");
        await Task.Run(() => { using var runtime = new SandboxieRuntime(); if (runtime.Processes(profile.BoxName).Length > 0) throw new InvalidOperationException("此沙盒仍在執行，請先停止。"); }, ct).ConfigureAwait(false);
        await Task.Run(() => SandboxPreparation.Prepare(profile.SourceLauncherPath, profile, progress, ct), ct);
        await Call<bool>(Command.Create("saveSandbox", profile), ct);
    }
    public virtual async Task<SandboxStartResult> StartSandbox(Guid id, CancellationToken ct = default)
    {
        if (!await sandboxStartGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("已有實例正在啟動，請等待核驗完成。");
        try { return await StartSandboxCore(id, ct).ConfigureAwait(false); }
        finally { sandboxStartGate.Release(); }
    }
    private async Task<SandboxStartResult> StartSandboxCore(Guid id, CancellationToken ct)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        var profile = state.SandboxInstances.Single(p => p.Id == id);
        RequireSandboxNetworkSupport(state, profile);
        if (profile.RemovalPending) throw new InvalidOperationException("此實例正在清理，請到儲存空間頁完成移除。");
        var current = state.SandboxStatuses.FirstOrDefault(s => s.Id == id);
        if (current is { Processes.Length: > 0 }) {
            if (SandboxLauncherState.IsStarted(current, profile, ProcessCatalog.ImagePath, LdPlayer.Identity)) return SandboxStartResult.AlreadyRunning;
            throw new InvalidOperationException(profile.Name + " 已有程序執行，但尚未就緒：" + current.State + "。請先停止後重試。");
        }
        if (!profile.UseLocalNetwork) {
            var probe = await Call<ProbeResult>(Command.Create("probe", new ProxyRequest(profile.ProxyId)), ct);
            if (!probe.Ready) throw new InvalidOperationException("線路未通過 TCP／UDP 檢測：" + probe.Message);
        }
        var ticket = await Call<SandboxLaunchTicket>(Command.Create("armSandbox", new SandboxRequest(id)), ct);
        bool handedOff = false;
        try {
            ct.ThrowIfCancellationRequested();
            string helper = Path.Combine(AppContext.BaseDirectory, "WindowSocks.Tools.exe");
            var start = new ProcessStartInfo(Path.Combine(SandboxieRuntime.InstallDirectory, "Start.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = profile.WorkingDirectory };
            foreach (string arg in new[] { "/box:" + profile.BoxName, "/hide_window", helper, "sandbox-bootstrap", id.ToString(), ticket.Token }) start.ArgumentList.Add(arg);
            await LaunchSandboxHelper(start);
            handedOff = true;
            // Once the helper has been handed to Sandboxie, service health policy
            // owns it. Closing a progress window cancels later queue entries only.
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            for (int attempt = 0; attempt < 150; attempt++) {
                await Task.Delay(500, verification.Token);
                var status = (await Call<SandboxInstanceStatus[]>(Command.Create("refreshSandboxes", new { }), verification.Token)).Single(s => s.Id == id);
                if (SandboxLauncherState.IsStarted(status, profile, ProcessCatalog.ImagePath, LdPlayer.Identity)) return SandboxStartResult.Started;
                if (status.State.Contains("異常") || status.State.Contains("未確認") || status.State.Contains("失敗")) throw new InvalidOperationException(status.Detail);
            }
            throw new TimeoutException("沙盒啟動核驗逾時，請查看沙盒／啟動器紀錄。");
        } catch {
            if (!handedOff) {
            await Call<bool>(Command.Create("pauseSandbox", new SandboxRequest(id)), CancellationToken.None);
            await SandboxControl("Start.exe", ["/box:" + profile.BoxName, "/terminate"], CancellationToken.None);
            }
            throw;
        }
    }
    protected virtual Task LaunchSandboxHelper(ProcessStartInfo start)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "WindowSocks.Tools.exe"))) throw new IOException("找不到沙盒啟動輔助程式，請使用完整測試版目錄。");
        using var process = Process.Start(start) ?? throw new IOException("無法啟動沙盒。"); return Task.CompletedTask;
    }
    protected virtual Task<string> SandboxControl(string exe, string[] args, CancellationToken ct) => SandboxieRuntime.Command(exe, args, ct);
    public virtual async Task StopSandbox(Guid id)
    {
        var state = await SandboxSnapshot().ConfigureAwait(false); var profile = state.SandboxInstances.Single(p => p.Id == id);
        await Call<bool>(Command.Create("pauseSandbox", new SandboxRequest(id)));
        await SandboxieRuntime.Command("Start.exe", ["/box:" + profile.BoxName, "/terminate"], CancellationToken.None);
        for (int attempt = 0; attempt < 50; attempt++) {
            var status = (await Call<SandboxInstanceStatus[]>(Command.Create("refreshSandboxes", new { }))).Single(p => p.Id == id);
            if (status.Processes.Length == 0) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("沙盒仍有程序未結束，代理保持封鎖，請重新整理確認。");
    }
}

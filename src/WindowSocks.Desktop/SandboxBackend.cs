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
    public bool SandboxLaunchBusy => sandboxLaunchQueue.IsRunning;
    public Task<SandboxLaunchOutcome[]> StartSandboxes(Guid[] ids, int intervalSeconds, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => sandboxLaunchQueue.RunAsync(ids, intervalSeconds, StartSandbox, progress, ct), ct);
    public virtual Task<bool> SandboxInstalled(CancellationToken ct) => Task.Run(() => SandboxieRuntime.Installed, ct);
    public virtual Task<bool> DetectYongOrigin(string path, CancellationToken ct) => Task.Run(() => SandboxCompatibility.IsYongOrigin(path), ct);
    public virtual async Task PrepareSandboxPair(SandboxInstanceProfile[] pair, IProgress<string>? progress, CancellationToken ct)
    {
        if (!await sandboxPreparationGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("先前的準備或取消尚未完成，請稍後重試。");
        try { await PrepareSandboxPairCore(pair, progress, ct).ConfigureAwait(false); }
        finally { sandboxPreparationGate.Release(); }
    }
    private async Task PrepareSandboxPairCore(SandboxInstanceProfile[] pair, IProgress<string>? progress, CancellationToken ct)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        if (!state.SupportsSandboxExperience) throw new InvalidOperationException("請先到設定安裝／修復 0.1.30 以上背景服務，再建立遊戲環境。");
        if (pair.Length != 2 || pair.Select(p => p.ProxyId).Distinct().Count() != 2) throw new InvalidOperationException("請選擇兩條不同線路。");
        if (state.SandboxInstances.Select(p => p.Id).Concat(pair.Select(p => p.Id)).Distinct().Count() > 10 ||
            state.Bindings.Select(b => b.TargetKey).Concat(pair.Select(p => p.TargetKey)).Distinct().Count() > 10)
            throw new InvalidOperationException("最多管理十個實例，請先移除不使用的規則。");
        foreach (var profile in pair) {
            SandboxInstances.Validate(profile);
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
    public async Task PrepareSandbox(SandboxInstanceProfile profile, IProgress<string>? progress, CancellationToken ct)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        if (!SandboxieRuntime.Installed) throw new InvalidOperationException("請先安裝官方 Sandboxie Plus 1.18.8 x64。");
        if (!state.Proxies.Any(p => p.Id == profile.ProxyId)) throw new InvalidOperationException("找不到指定線路。");
        await Task.Run(() => { using var runtime = new SandboxieRuntime(); if (runtime.Processes(profile.BoxName).Length > 0) throw new InvalidOperationException("此沙盒仍在執行，請先停止。"); }, ct).ConfigureAwait(false);
        await Task.Run(() => SandboxPreparation.Prepare(profile.SourceLauncherPath, profile, progress, ct), ct);
        await Call<bool>(Command.Create("saveSandbox", profile), ct);
    }
    public virtual async Task StartSandbox(Guid id, CancellationToken ct = default)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        var profile = state.SandboxInstances.Single(p => p.Id == id);
        var current = state.SandboxStatuses.FirstOrDefault(s => s.Id == id);
        if (current is { Processes.Length: > 0 }) {
            if (current.State == "代理執行中") return;
            throw new InvalidOperationException(profile.Name + " 已有程序執行，但尚未就緒：" + current.State + "。請先停止後重試。");
        }
        var probe = await Call<ProbeResult>(Command.Create("probe", new ProxyRequest(profile.ProxyId)), ct);
        if (!probe.Ready) throw new InvalidOperationException("線路未通過 TCP／UDP 檢測：" + probe.Message);
        var ticket = await Call<SandboxLaunchTicket>(Command.Create("armSandbox", new SandboxRequest(id)), ct);
        try {
            string helper = Path.Combine(AppContext.BaseDirectory, "WindowSocks.Tools.exe");
            if (!File.Exists(helper)) throw new IOException("找不到沙盒啟動輔助程式，請使用完整測試版目錄。");
            var start = new ProcessStartInfo(Path.Combine(SandboxieRuntime.InstallDirectory, "Start.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = profile.WorkingDirectory };
            foreach (string arg in new[] { "/box:" + profile.BoxName, "/hide_window", helper, "sandbox-bootstrap", id.ToString(), ticket.Token }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("無法啟動沙盒。");
            for (int attempt = 0; attempt < 150; attempt++) {
                await Task.Delay(500, ct);
                var status = (await Call<SandboxInstanceStatus[]>(Command.Create("refreshSandboxes", new { }), ct)).Single(s => s.Id == id);
                if (status.State == "代理執行中" && status.Detail.StartsWith("啟動器已建立", StringComparison.Ordinal)) return;
                if (status.State.Contains("異常") || status.State.Contains("未確認") || status.State.Contains("失敗")) throw new InvalidOperationException(status.Detail);
            }
            throw new TimeoutException("沙盒啟動核驗逾時，請查看沙盒／啟動器紀錄。");
        } catch {
            await Call<bool>(Command.Create("pauseSandbox", new SandboxRequest(id)), CancellationToken.None);
            await SandboxieRuntime.Command("Start.exe", ["/box:" + profile.BoxName, "/terminate"], CancellationToken.None);
            throw;
        }
    }
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

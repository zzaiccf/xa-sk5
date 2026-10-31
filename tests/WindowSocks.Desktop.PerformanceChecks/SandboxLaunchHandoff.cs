using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private sealed class HandoffFixture
    {
        public SandboxInstanceProfile Profile = SandboxInstances.Create("Synthetic", @"C:\Synthetic\Launcher.exe", Guid.NewGuid());
        public readonly SandboxInstanceProfile Second = SandboxInstances.Create("Synthetic B", @"C:\Synthetic\Launcher.exe", Guid.NewGuid());
        public readonly CancellationTokenSource Closing = new();
        public readonly HashSet<Guid> Started = [];
        public bool CancelOnLaunch = true, CancelOnArm;
        public bool SupportsLocal = true;
        public TaskCompletionSource? Hold, Entered;
        public int Pauses, Terminations, Probes, NetworkSaves;
        public Task<Reply> Transport(Command command, CancellationToken ct) => Task.FromResult(command.Action switch {
            "snapshot" => Reply.Success(new Snapshot([], [], [], [], [], new(false, false, false, false, "fixture", "", [])) {
                SupportsSandboxInstances = true, SupportsSandboxSafety = true, SupportsSandboxLocalNetwork = SupportsLocal, SandboxInstances = [Profile, Second], SandboxStatuses = Started.Select(Status).ToArray() }),
            "probe" => Probe(Json.Read<ProxyRequest>(command.Data).ProxyId),
            "armSandbox" => Arm(Json.Read<SandboxRequest>(command.Data).Id),
            "refreshSandboxes" => Reply.Success(Started.Select(Status).ToArray()),
            "pauseSandbox" => Pause(),
            "saveSandbox" => Save(Json.Read<SandboxInstanceProfile>(command.Data)),
            _ => throw new InvalidOperationException(command.Action)
        });
        private Reply Probe(Guid id) { Probes++; Check(id != Guid.Empty, "Local startup attempted a proxy probe."); return Reply.Success(new ProbeResult(id, true, true, true, true, "192.0.2.1", "192.0.2.1", 1, "", DateTimeOffset.UtcNow)); }
        private Reply Save(SandboxInstanceProfile p) { Check(p.Id == Profile.Id, "Network save modified unselected instance."); NetworkSaves++; Profile = p; return Reply.Success(true); }
        private SandboxInstanceStatus Status(Guid id) => new(id, id == Profile.Id && Profile.UseLocalNetwork ? "本地網路執行中" : "代理執行中", "arbitrary localized text", [LdPlayer.Identity(Environment.ProcessId)!.Value]) { LauncherStarted = true };
        private Reply Arm(Guid id) { if (CancelOnArm) Closing.Cancel(); return Reply.Success(new SandboxLaunchTicket(id == Profile.Id ? Profile : Second, "synthetic-token")); }
        private Reply Pause() { Pauses++; return Reply.Success(true); }
    }
    private sealed class HandoffBackend(HandoffFixture f) : Backend(Path.Combine(Output, "handoff-" + Guid.NewGuid().ToString("N")), f.Transport)
    {
        protected override async Task LaunchSandboxHelper(ProcessStartInfo start) {
            f.Entered?.TrySetResult(); if (f.Hold is { } hold) await hold.Task;
            Check(f.Started.Add(Guid.Parse(start.ArgumentList[4])), "Helper was launched twice for an already running instance.");
            if (f.CancelOnLaunch) f.Closing.Cancel();
        }
        protected override Task<string> SandboxControl(string exe, string[] args, CancellationToken ct) { f.Terminations++; return Task.FromResult(""); }
    }
    private static async Task CheckSandboxHandoff()
    {
        var fixture = new HandoffFixture(); var backend = new HandoffBackend(fixture);
        try { await backend.StartSandbox(fixture.Profile.Id, fixture.Closing.Token); }
        catch (OperationCanceledException) { }
        Check(fixture.Pauses == 0 && fixture.Terminations == 0, "Closing launch UI after helper handoff paused/terminated an acknowledged launcher.");
        fixture.CancelOnLaunch = false;
        var batch = await backend.StartSandboxes([fixture.Profile.Id, fixture.Second.Id], 1, null, default);
        Check(batch[0].Result == SandboxStartResult.AlreadyRunning && batch[1].Result == SandboxStartResult.Started && fixture.Started.Count == 2, "Start A then batch A/B did not skip A and launch B exactly once.");
        var concurrent = new HandoffFixture { CancelOnLaunch = false, Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var concurrentBackend = new HandoffBackend(concurrent);
        var first = concurrentBackend.StartSandbox(concurrent.Profile.Id);
        await concurrent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4)); bool duplicateRejected = false;
        try { await concurrentBackend.StartSandbox(concurrent.Profile.Id); } catch (InvalidOperationException) { duplicateRejected = true; }
        finally { concurrent.Hold.SetResult(); }
        await first; Check(duplicateRejected && concurrent.Started.Count == 1, "Repeated start created a second helper.");
        var early = new HandoffFixture { CancelOnArm = true }; var earlyBackend = new HandoffBackend(early);
        try { await earlyBackend.StartSandbox(early.Profile.Id, early.Closing.Token); } catch (OperationCanceledException) { }
        Check(early.Started.Count == 0 && early.Pauses == 1, "Cancellation before helper handoff did not release its own arm safely.");
        var local = new HandoffFixture { CancelOnLaunch = false, SupportsLocal = false };
        local.Profile = local.Profile with { UseLocalNetwork = true, ProxyId = Guid.Empty };
        var localBackend = new HandoffBackend(local); bool oldRejected = false;
        try { await localBackend.StartSandbox(local.Profile.Id); } catch (InvalidOperationException ex) { oldRejected = ex.Message.Contains("0.1.36"); }
        Check(oldRejected && local.Started.Count == 0 && local.Probes == 0, "Old service was accepted for explicit local mode.");
        local.SupportsLocal = true;
        Check(await localBackend.StartSandbox(local.Profile.Id) == SandboxStartResult.Started && local.Probes == 0, "Local launch requires a proxy node or probe.");
        var mixed = await localBackend.StartSandboxes([local.Profile.Id, local.Second.Id], 1, null, default);
        Check(mixed[0].Result == SandboxStartResult.AlreadyRunning && mixed[1].Result == SandboxStartResult.Started && local.Probes == 1, "Mixed local/proxy batch did not preserve routing choices.");
        var changed = local.Profile with { UseLocalNetwork = false, ProxyId = Guid.NewGuid() };
        bool runningRejected = false;
        try { await localBackend.SaveSandboxNetworks([changed], null, default); } catch (InvalidOperationException ex) { runningRejected = ex.Message.Contains("停止"); }
        Check(runningRejected && local.NetworkSaves == 0, "Running sandbox network changed before stopping.");
        local.Started.Remove(local.Profile.Id);
        await localBackend.SaveSandboxNetworks([changed], null, default);
        Check(local.NetworkSaves == 1 && !local.Profile.UseLocalNetwork && local.Started.Contains(local.Second.Id), "Saving stopped A interrupted running B or kept stale mode.");
    }
}

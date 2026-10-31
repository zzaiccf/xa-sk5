using System.Reflection;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxLocalControllerTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ArmRegisterAndLaunchWithoutNodesPreservesIdentityAndPauseChecks(bool pauseBeforeSuccess)
    {
        string root = Path.Combine(Path.GetTempPath(), "xa-local-service-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        File.WriteAllText(Path.Combine(root, "owner.sid"), sid);
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        var p = SandboxInstances.Create("Synthetic local", @"C:\Synthetic\Launcher.exe", Guid.Empty) with { UseLocalNetwork = true };
        string folder = Path.Combine(root, p.Id.ToString("N"));
        p = p with { WorkingDirectory = Path.Combine(folder, "Game"), FileRoot = Path.Combine(folder, "Box"), LauncherPath = Path.Combine(folder, "Game", "Launcher.exe") };
        Directory.CreateDirectory(p.WorkingDirectory); File.WriteAllText(p.LauncherPath, "synthetic; never executed");
        new ConfigStore(root).Save(new() { SandboxInstances = [p] });
        var runtime = new Runtime(); var firewall = new Firewall();
        Controller? controller = null;
        try {
            controller = new(root, measureProxy: (_, _, _) => throw new Exception("local must not probe"), sandboxRuntime: runtime,
                engineLifecycle: new Engine(), sandboxFirewall: firewall, discoverSandboxImages: profiles => profiles.Select(x => x.LauncherPath).ToArray());
            await ((Task)typeof(Controller).GetField("sandboxInitialization", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!);
            var armed = await controller.Dispatch(Command.Create("armSandbox", new SandboxRequest(p.Id)), default);
            Assert.True(armed.Ok, armed.Error); var ticket = Json.Read<SandboxLaunchTicket>(armed.Data);
            Assert.True(ticket.Instance.UseLocalNetwork);
            runtime.Members = [new(identity, p.BoxName, sid, 1)];
            var registration = new SandboxRegistration(p.Id, ticket.Token, identity);
            var legacy = await controller.Dispatch(Command.Create("registerSandbox", registration), default, identity.Pid);
            Assert.False(legacy.Ok); Assert.Contains("0.1.36", legacy.Error);
            var registered = await controller.Dispatch(Command.Create("registerSandbox", registration with { SupportsLocalNetwork = true }), default, identity.Pid);
            Assert.True(registered.Ok, registered.Error);
            Assert.True(Json.Read<SandboxLaunchPermission>(registered.Data).UseLocalNetwork);
            var forbiddenProbe = await controller.Dispatch(Command.Create("reportSandboxNetwork", new SandboxNetworkReport(p.Id, "192.0.2.1", "192.0.2.1", true)), default, identity.Pid);
            Assert.False(forbiddenProbe.Ok);
            if (pauseBeforeSuccess) Assert.True((await controller.Dispatch(Command.Create("pauseSandbox", new SandboxRequest(p.Id)), default)).Ok);
            var report = await controller.Dispatch(Command.Create("reportSandboxLaunch", new SandboxLaunchReport(p.Id, true, "")), default, identity.Pid);
            Assert.Equal(!pauseBeforeSuccess, report.Ok);
            if (!pauseBeforeSuccess) {
                var snapshot = Json.Read<Snapshot>((await controller.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.True(snapshot.SupportsSandboxLocalNetwork);
                var status = Assert.Single(snapshot.SandboxStatuses); Assert.Equal("本地網路執行中", status.State); Assert.True(status.LauncherStarted); Assert.Null(status.Network);
                Assert.True((await controller.Dispatch(Command.Create("reportSandboxLaunch", new SandboxLaunchReport(p.Id, false, "late transient error")), default, identity.Pid)).Ok == false);
                var change = await controller.Dispatch(Command.Create("saveSandbox", p with { UseLocalNetwork = false, ProxyId = Guid.NewGuid() }), default);
                Assert.False(change.Ok); Assert.Contains("停止", change.Error);
                runtime.Available = false; await controller.TickSandboxes(default);
                Assert.Contains("withdraw", firewall.Calls);
            }
            Assert.Empty(new ConfigStore(root).Load().Proxies);
        } finally { runtime.Members = []; controller?.Dispose(); Directory.Delete(root, true); }
    }
    private sealed class Runtime : ISandboxRuntime
    {
        public SandboxProcess[] Members = []; public bool Available = true;
        public bool IdentityAvailable => Available;
        public void VerifyCompatibility() { }
        public void ValidateSettings(SandboxInstanceProfile p) => SandboxInstances.Validate(p);
        public SandboxProcess[] Processes(string box) => Members.Where(p => p.BoxName == box).ToArray();
        public SandboxProcess? Query(int pid) => Members.FirstOrDefault(p => p.Identity.Pid == pid);
        public void TerminateOwned(SandboxInstanceProfile p, string owner) => Members = [];
        public void Dispose() { }
    }
    private sealed class Engine : IEngineLifecycle
    {
        public bool Present => true; public bool DriverPresent => true; public bool Compatible => true; public bool Healthy => true;
        public uint Error => 0; public bool Start(string path) => true; public bool ReplaceCapture(string path) => true; public void Stop() { }
    }
    private sealed class Firewall : ISandboxFirewall
    {
        public List<string> Calls = [];
        public void ReplaceImages(string[] images) => Calls.Add("replace");
        public void WithdrawPermit() => Calls.Add("withdraw"); public void AllowWhileHealthy() => Calls.Add("allow");
        public SandboxFirewallStatus Inspect() => new(0, 0, "synthetic"); public void Clear() { } public void Dispose() { }
    }
}

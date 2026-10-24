using System.Diagnostics;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class BulkActionTests
{
    [Fact] public void LargestAllowedProcessReportFitsThePipeEnvelope()
    {
        var outcomes = Enumerable.Range(1, Controller.MaximumCloseProcesses).Select(i =>
            new ProcessCloseOutcome(new(i, long.MaxValue), "failed", new string('錯', 64))).ToArray();
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Reply.Success(new CloseProxyProcessesResult(outcomes)), Json.Options);
        Assert.True(bytes.Length < PipeTransport.Limit);
    }
    [Theory]
    [InlineData("global")][InlineData("paused-binding")][InlineData("fixed-emulator")]
    [InlineData("pending-emulator")][InlineData("saved-vm")][InlineData("runtime-vm")]
    public void EntirePoolIsRejectedIfAnyNodeHasAnAssignment(string use)
    {
        var p = new ProxyProfile(); var config = new SavedConfig { Proxies = [p, new()] };
        VmwareRuntime[] runtime = [];
        switch (use) {
            case "global": config.GlobalProxy = new() { Enabled = true, ProxyId = p.Id }; break;
            case "paused-binding": config.Bindings = [new() { TargetKey = "paused", Enabled = false, ProxyId = p.Id }]; break;
            case "fixed-emulator": config.AutoProxy = new() { EmulatorAssignments = [new("LDPlayer", @"C:\test\ldconsole.exe", 0, "off", p.Id)] }; break;
            case "pending-emulator": config.PendingEmulatorRule = new() { EmulatorAssignments = [new("LDPlayer", @"C:\test\ldconsole.exe", 0, "pending", p.Id)] }; break;
            case "saved-vm": config.Vmware = new() { Enabled = false, Machines = [new(@"C:\test\off.vmx", "", p.Id)] }; break;
            case "runtime-vm": runtime = [new(@"C:\test\running.vmx", null, p.Id, "paused")]; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => ProxyPoolUsage.RequireUnused(config, runtime));
        Assert.Contains("未刪除任何節點", error.Message); Assert.Equal(2, config.Proxies.Count);
    }
    [Fact] public void LocalVmAndDeletedVmBackupDoNotReserveProxyNodes()
    {
        var p = new ProxyProfile();
        var config = new SavedConfig { Proxies = [p], Vmware = new() { RuleDeleted = true, Machines = [new(@"C:\test\old.vmx", "", p.Id)] } };
        ProxyPoolUsage.RequireUnused(config, [new(@"C:\test\local.vmx", null, p.Id, "local") { UseLocalNetwork = true }]);
        config.Vmware = config.Vmware with { RuleDeleted = false, Machines = [config.Vmware.Machines[0] with { UseLocalNetwork = true }] };
        ProxyPoolUsage.RequireUnused(config);
    }
    [Fact] public async Task ClearPoolIsAtomicAndLateProbeCannotRestoreDeletedNodes()
    {
        using var fixture = new ServiceFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new ProxyProfile { Host = "198.51.100.10", Port = 1080 };
        fixture.Store.Save(new() { Proxies = [proxy, new() { Host = "198.51.100.11", Port = 1080 }] });
        using var service = new Controller(fixture.Directory, async (p, _, ct) => {
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return new(p.Id, true, true, true, true, "203.0.113.10", "203.0.113.10", 1, "synthetic", DateTimeOffset.UtcNow);
        });
        try {
            var probe = service.Dispatch(Command.Create("probe", new ProxyRequest(proxy.Id)), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clear = await service.Dispatch(Command.Create("clearProxyPool", new { }), default);
            Assert.True(clear.Ok, clear.Error); Assert.Equal(2, Json.Read<ClearProxyPoolResult>(clear.Data).RemovedIds.Length);
            release.TrySetResult(); Assert.False((await probe).Ok);
            var state = await State(service); Assert.True(state.SupportsBulkActions); Assert.Empty(state.Proxies); Assert.Empty(state.Probes);
            Assert.Empty(fixture.Store.Load().Proxies); Assert.Empty(fixture.Store.Load().LastProbes);
            Assert.Empty(Json.Read<ClearProxyPoolResult>((await service.Dispatch(Command.Create("clearProxyPool", new { }), default)).Data).RemovedIds);
        } finally { release.TrySetResult(); }
    }
    [Fact] public async Task ClearDoesNotPartiallyDeleteUnusedNodesWhenAnotherNodeIsBound()
    {
        using var fixture = new ServiceFixture(); var p = new ProxyProfile { Host = "198.51.100.10", Port = 1080 };
        fixture.Store.Save(new() { Proxies = [p, new() { Host = "198.51.100.11", Port = 1080 }], Bindings = [new() { TargetKey = "reserved", ProxyId = p.Id }] });
        using var service = new Controller(fixture.Directory);
        var reply = await service.Dispatch(Command.Create("clearProxyPool", new { }), default);
        Assert.False(reply.Ok); Assert.Contains("未刪除任何節點", reply.Error);
        Assert.Equal(2, (await State(service)).Proxies.Length); Assert.Equal(2, fixture.Store.Load().Proxies.Count);
    }
    [Fact] public async Task ClosingApprovedScopeIncludesNewChildrenButPreservesNewRootsAndGlobalOnlyProcesses()
    {
        using var fixture = new ServiceFixture();
        using var launcher = StartHoldingFixture(fixture.Directory); Process? child = null;
        using var globalOnly = StartCommand(); using var newRoot = StartCommand();
        try {
            Assert.Contains("ready", (await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            var p = new ProxyProfile { Host = "198.51.100.10", Port = 1080 };
            var global = new GlobalProxySettings { Enabled = true, ProxyId = p.Id, ResolvedHost = p.Host, BypassLan = false };
            var root = LdPlayer.Identity(launcher.Id)!.Value;
            var other = LdPlayer.Identity(newRoot.Id)!.Value;
            var laterBinding = Binding(other, p.Id) with { Processes = [] };
            fixture.Store.Save(new() { Proxies = [p], GlobalProxy = global, Bindings = [Binding(root, p.Id), laterBinding] });
            using var service = new Controller(fixture.Directory);
            var prepare = await service.Dispatch(Command.Create("prepareCloseProxyProcesses", new { }), default);
            Assert.True(prepare.Ok, prepare.Error); var plan = Json.Read<CloseProxyProcessesPlan>(prepare.Data);
            Assert.Single(plan.Targets); Assert.True(plan.ProcessCount >= 1); // Windows may attach a console-host child.
            Assert.False((await service.Dispatch(Command.Create("closeProxyProcesses", new CloseProxyProcessesRequest(Guid.NewGuid())), default)).Ok);
            // A child created during confirmation is part of the approved program tree.
            await launcher.StandardInput.WriteLineAsync("spawn"); await launcher.StandardInput.FlushAsync();
            using var identity = System.Text.Json.JsonDocument.Parse((await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            child = Process.GetProcessById(identity.RootElement.GetProperty("clientPid").GetInt32());
            // A different saved scope becoming live during confirmation must not join the approved roots.
            var target = new TargetInstance(laterBinding.TargetKey, "new root", "Process", ProcessCatalog.ImagePath(other.Pid), -1, true, [other]);
            var bind = await service.Dispatch(Command.Create("syncTargets", new[] { target }), default);
            Assert.True(bind.Ok, bind.Error);
            var closed = await service.Dispatch(Command.Create("closeProxyProcesses", new CloseProxyProcessesRequest(plan.Token)), default);
            Assert.True(closed.Ok, closed.Error); var result = Json.Read<CloseProxyProcessesResult>(closed.Data);
            Assert.Contains(result.Processes, p => p.Identity == root && p.State == "closed");
            Assert.Contains(result.Processes, p => p.Identity.Pid == child.Id && p.State == "closed");
            Assert.Equal(0, result.Unclosed);
            Assert.True(launcher.HasExited); Assert.True(child.HasExited); Assert.False(globalOnly.HasExited); Assert.False(newRoot.HasExited);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(global, Json.Options), System.Text.Json.JsonSerializer.Serialize((await State(service)).GlobalProxy, Json.Options));
            Assert.Equal(2, (await State(service)).Bindings.Length);
            Assert.False((await service.Dispatch(Command.Create("closeProxyProcesses", new CloseProxyProcessesRequest(plan.Token)), default)).Ok);
        } finally { Stop(child); Stop(launcher); Stop(globalOnly); Stop(newRoot); }
    }
    [Fact] public async Task UnbindingDuringConfirmationPreventsClosingAProcessNowUsingGlobalRouting()
    {
        using var fixture = new ServiceFixture(); using var process = StartCommand();
        var identity = LdPlayer.Identity(process.Id)!.Value; var p = new ProxyProfile { Host = "198.51.100.10", Port = 1080 }; var binding = Binding(identity, p.Id);
        fixture.Store.Save(new() { Proxies = [p], GlobalProxy = new() { Enabled = true, ProxyId = p.Id, ResolvedHost = p.Host }, Bindings = [binding] });
        using var service = new Controller(fixture.Directory);
        try {
            var plan = Json.Read<CloseProxyProcessesPlan>((await service.Dispatch(Command.Create("prepareCloseProxyProcesses", new { }), default)).Data);
            Assert.True((await service.Dispatch(Command.Create("unbind", new TargetRequest(binding.TargetKey)), default)).Ok);
            var reply = await service.Dispatch(Command.Create("closeProxyProcesses", new CloseProxyProcessesRequest(plan.Token)), default);
            Assert.True(reply.Ok, reply.Error); Assert.Empty(Json.Read<CloseProxyProcessesResult>(reply.Data).Processes); Assert.False(process.HasExited);
            Assert.True((await State(service)).GlobalProxy.Enabled);
        } finally { Stop(process); }
    }
    [Fact] public async Task TerminationRejectsReusedIdentityWrongOwnerAndTheCurrentProxyHost()
    {
        using var process = StartCommand(); var identity = LdPlayer.Identity(process.Id)!.Value;
        try {
            string owner = WindowsIdentity.GetCurrent().User!.Value;
            Assert.Equal("skipped", Assert.Single(await ManagedProcessTerminator.CloseAsync([identity with { CreatedFileTime = identity.CreatedFileTime + 1 }], owner)).State);
            Assert.False(process.HasExited);
            string otherOwner = owner == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18";
            Assert.Equal("skipped", Assert.Single(await ManagedProcessTerminator.CloseAsync([identity], otherOwner)).State);
            Assert.False(process.HasExited);
            Assert.Equal("skipped", Assert.Single(await ManagedProcessTerminator.CloseAsync([LdPlayer.Identity(Environment.ProcessId)!.Value], owner)).State);
        } finally { Stop(process); }
    }
    private static InstanceBinding Binding(ProcessIdentity identity, Guid proxy) => new() { TargetKey = $"pid:{identity.Pid}:{identity.CreatedFileTime}", Name = "isolated test", Kind = "Process", Path = ProcessCatalog.ImagePath(identity.Pid), ProxyId = proxy, Processes = [identity], IncludeChildren = true, SharedProxy = true };
    private static Process StartCommand() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { Arguments = "/q /d", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
    private static Process StartHoldingFixture(string directory)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(output.Parent!.Parent!.Parent!.Parent!.FullName, "WindowSocks.GameLauncherFixture", "bin", output.Parent.Name, output.Name, "WindowSocks.GameLauncherFixture.exe");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--session-hold"); start.ArgumentList.Add(directory); return Process.Start(start)!;
    }
    private static async Task<Snapshot> State(Controller service) => Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
    private static void Stop(Process? process) { if (process is not null && !process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    private sealed class ServiceFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "WindowSocksBulkTests", Guid.NewGuid().ToString("N"));
        public ConfigStore Store { get; }
        public ServiceFixture() { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Path.Combine(Directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value); Store = new(Directory); }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ServiceExitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-exit-" + Guid.NewGuid());
    public ServiceExitTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value); }
    public void Dispose() => Directory.Delete(root, true);
    [Fact] public async Task StoppedOrMissingServiceDoesNotReceiveAStopRequest()
    {
        await ServiceExit.StopAsync(_ => throw new Exception("must not call"), () => false);
    }
    [Fact] public async Task AcknowledgementAloneNeverAllowsUiToExitBeforeServiceStops()
    {
        bool running = true, requested = false;
        var stop = ServiceExit.StopAsync(_ => { requested = true; return Task.CompletedTask; }, () => running);
        Assert.True(requested); Assert.False(stop.IsCompleted);
        running = false; await stop;
    }
    [Fact] public async Task LostPipeReplyStillAllowsExitWhenServiceStopIsConfirmed()
    {
        bool running = true;
        await ServiceExit.StopAsync(_ => { running = false; throw new IOException("reply lost"); }, () => running);
    }
    [Fact] public async Task UnstoppedServiceTimesOutInsteadOfPretendingToExit()
    {
        await Assert.ThrowsAsync<IOException>(() => ServiceExit.StopAsync(_ => Task.CompletedTask, () => true, timeout: TimeSpan.FromMilliseconds(20)));
    }
    [Fact] public void OwnerCanStartStopAndQueryWithoutReceivingServiceConfigurationRights()
    {
        string owner = WindowsIdentity.GetCurrent().User!.Value;
        var original = new RawSecurityDescriptor("D:(A;;GA;;;SY)(A;;GA;;;BA)"); byte[] bytes = new byte[original.BinaryLength]; original.GetBinaryForm(bytes, 0);
        var updated = WindowsProxyService.OwnerDescriptor(bytes, owner); var descriptor = new RawSecurityDescriptor(updated, 0);
        var acl = Assert.IsType<RawAcl>(descriptor.DiscretionaryAcl);
        var ace = Assert.Single(acl.Cast<GenericAce>().OfType<CommonAce>(), a => a.SecurityIdentifier.Value == owner);
        Assert.Equal(0x34, ace.AccessMask); Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
        Assert.Equal(3, acl.Count); Assert.Equal(updated, WindowsProxyService.OwnerDescriptor(updated, owner));
        Assert.DoesNotContain(acl.Cast<GenericAce>().OfType<CommonAce>(), a => a.SecurityIdentifier.Value == "S-1-1-0");
    }
    [Fact] public async Task ServiceStopKeepsSavedRulesAndRejectsLaterCommandsOrPreparation()
    {
        var saved = new SavedConfig { AutoProxy = new() { Configured = true, Kind = "Process", Pattern = "fixture.exe" }, Vmware = new() { PendingPreparation = true, Machines = [new(Path.Combine(root,"missing.vmx"),"192.168.70.20",Guid.Empty)] } };
        var store = new ConfigStore(root); store.Save(saved);
        using var controller = new Controller(root);
        Assert.True((await controller.Dispatch(Command.Create("stopService", new { }), default)).Ok);
        Assert.True((await controller.Dispatch(Command.Create("stopService", new { }), default)).Ok);
        Assert.False((await controller.Dispatch(Command.Create("setAutoProxy", new AutoProxyRule()), default)).Ok);
        await controller.Tick(default);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(saved, Json.Options), System.Text.Json.JsonSerializer.Serialize(store.Load(), Json.Options));
        Assert.True(store.Load().Vmware.PendingPreparation);
        Assert.False(File.Exists(Path.Combine(root,"vmware-network-backup.json")));
    }
    [Fact] public async Task PipeStopRepliesAndThenStopsTheHostedService()
    {
        string name = "xa-exit-test-" + Guid.NewGuid().ToString("N");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(_ => new Controller(root));
        builder.Services.AddHostedService(sp => new PipeWorker(sp.GetRequiredService<Controller>(), sp.GetRequiredService<ILogger<PipeWorker>>(), sp.GetRequiredService<IHostApplicationLifetime>(), name));
        using var host = builder.Build(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await host.StartAsync(timeout.Token);
        await using var pipe = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token); await PipeTransport.WriteAsync(pipe,Command.Create("stopService",new { }),timeout.Token);
        var reply = await PipeTransport.ReadAsync<Reply>(pipe,timeout.Token); Assert.True(reply.Ok);
        await host.WaitForShutdownAsync(timeout.Token);
        Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.IsCancellationRequested);
    }
}

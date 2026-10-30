using System.Reflection;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class P0SandboxRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-p0-sandbox-" + Guid.NewGuid());
    private readonly FakeRuntime runtime = new();
    private Controller? controller;
    private SavedConfig config = new();
    private SandboxInstanceProfile a = null!, b = null!;
    private readonly ProcessIdentity identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
    private readonly string sid = WindowsIdentity.GetCurrent().User!.Value;
    private void Setup()
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"owner.sid"),sid);
        a=SandboxInstances.Create("fixture A",@"C:\FakeGames\Launcher.exe",Guid.NewGuid()) with { Enabled=true };
        b=SandboxInstances.Create("fixture B",@"C:\FakeGames\Launcher.exe",Guid.NewGuid());
        config=new() { SandboxInstances=[a,b], Proxies=[new(){Id=a.ProxyId,Host="127.0.0.1",Port=1080},new(){Id=b.ProxyId,Host="127.0.0.1",Port=1081}],
            Bindings=[new(){TargetKey=a.TargetKey,Kind="Sandbox",ProxyId=a.ProxyId,Enabled=true},new(){TargetKey=b.TargetKey,Kind="Sandbox",ProxyId=b.ProxyId}] };
        new ConfigStore(root).Save(config);controller=new(root,sandboxRuntime:runtime);
        config=(SavedConfig)typeof(Controller).GetField("config",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!;
        runtime.Members=[new(identity,a.BoxName,sid,1)];
    }
    private object? Invoke(string method,params object?[] args) => typeof(Controller).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,args);
    [Fact] public async Task R7_SnapshotDoesNotWaitForConfigurationGate()
    {
        Setup(); await controller!.Gate.WaitAsync();
        var request = controller.Dispatch(Command.Create("snapshot",new{}),default);
        bool finished;
        try { finished = await Task.WhenAny(request,Task.Delay(150)) == request; }
        finally { controller.Gate.Release(); }
        Assert.True((await request).Ok); Assert.True(finished,"snapshot waited for Gate");
    }
    [Fact] public async Task R7_ValidateAndTerminateDoNotHoldGate()
    {
        Setup(); runtime.Members=[];
        runtime.CheckGate = () => { if(!controller!.Gate.Wait(0)) throw new IOException("operation held Gate"); controller.Gate.Release(); };
        var armed = await controller!.Dispatch(Command.Create("armSandbox",new SandboxRequest(a.Id)),default);
        Assert.DoesNotContain("operation held Gate",armed.Error??"");
        var stopped = await controller.Dispatch(Command.Create("stopService",new{}),default);
        Assert.True(stopped.Ok,stopped.Error);
    }
    [Fact] public async Task R7_UnchangedSandboxObservationDoesNotRepublishPolicy()
    {
        Setup(); await controller!.TickSandboxes(default);
        long Revision() => (long)typeof(Controller).GetField("routingPolicyRevision",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!;
        long before=Revision(); await controller.TickSandboxes(default); Assert.Equal(before,Revision());
    }
    [Fact] public void R3_StoppedSandboxDoesNotInstallHostWideIdentityProbe()
    {
        Setup(); runtime.Members = [];
        Assert.Equal(false, Invoke("ShouldGuardSandbox", a));
    }
    [Fact] public void R3_UnsupportedDriverIsRejectedBeforeArming()
    {
        Setup(); runtime.Members = []; runtime.CompatibilityError = "unsupported Sandboxie ABI";
        var error = Assert.Throws<TargetInvocationException>(() => Invoke("ArmSandbox", a.Id));
        Assert.Contains("unsupported Sandboxie ABI", error.InnerException!.Message);
    }
    [Fact] public void R3_RuntimeIdentityFailurePausesOnlyManagedSandbox()
    {
        Setup(); runtime.IdentityAvailable = false;
        Invoke("ReconcileSandboxes", new object?[] { null });
        Assert.False(config.Bindings.Single(x => x.TargetKey == a.TargetKey).Enabled);
        Assert.True(config.SandboxInstances.Single(x => x.Id == a.Id).Enabled);
        var states = (SandboxInstanceStatus[])Invoke("SandboxStatuses")!;
        Assert.Contains("身分查詢不可用", states.Single(x => x.Id == a.Id).State);
    }
    [Fact] public async Task N1_IdentityRecoveryKeepsIntentAndResumesWithoutRestart()
    {
        Setup(); runtime.IdentityAvailable=false;
        await controller!.TickSandboxes(default);
        Assert.False(config.Bindings.Single(x=>x.TargetKey==a.TargetKey).Enabled);
        Assert.True(new ConfigStore(root).Load().SandboxInstances.Single(x=>x.Id==a.Id).Enabled);
        runtime.IdentityAvailable=true;
        await controller.TickSandboxes(default);
        Assert.True(config.Bindings.Single(x=>x.TargetKey==a.TargetKey).Enabled);
        var states=(SandboxInstanceStatus[])Invoke("SandboxStatuses")!;
        Assert.DoesNotContain("身分查詢不可用",states.Single(x=>x.Id==a.Id).State);
    }
    [Fact] public void P01_ArmRejectsChangedSandboxRootBeforeIssuingTicket()
    {
        Setup();runtime.Members=[];runtime.SettingsError="FileRootPath mismatch";
        var error=Assert.Throws<TargetInvocationException>(()=>Invoke("ArmSandbox",a.Id));
        Assert.Contains("FileRootPath mismatch",error.InnerException!.Message);
    }
    [Fact] public void P02_TransientQueryFailureRetainsVerifiedMembersAndFixedBinding()
    {
        Setup();Invoke("ReconcileSandboxes",new object?[]{null});
        runtime.ListError=new IOException("fixture broker timeout");Invoke("ReconcileSandboxes",new object?[]{null});
        var binding=config.Bindings.Single(x=>x.TargetKey==a.TargetKey);
        Assert.Contains(identity,binding.Processes);Assert.True(binding.Enabled,"one failed observation must not pause a healthy existing binding");
        var verified=(HashSet<ProcessIdentity>)typeof(Controller).GetField("verifiedSandboxMembers",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!;
        Assert.Contains(identity,verified);
        Invoke("ReconcileSandboxes",new object?[]{null});Invoke("ReconcileSandboxes",new object?[]{null});
        Assert.False(binding.Enabled);Assert.Contains(identity,verified);
        runtime.ListError=null;Invoke("ReconcileSandboxes",new object?[]{null});Assert.True(binding.Enabled);Assert.Equal(a.ProxyId,binding.ProxyId);
    }
    [Fact] public void P02_PartialQueryFailureCannotBecomeACompleteEmptySnapshot()
    {
        Assert.Throws<IOException>(()=>SandboxieRuntime.CompleteSnapshot([null]));
    }
    [Fact] public void P03_StopAttemptsEveryBoxEvenWhenFirstTerminationFails()
    {
        Setup();runtime.TerminationErrorBox=a.BoxName;
        Assert.Throws<TargetInvocationException>(()=>Invoke("StopManagedSandboxes"));
        Assert.Equal(new[]{a.BoxName,b.BoxName},runtime.Stopped);
    }
    [Fact] public async Task P03_FailedTerminationDoesNotAcknowledgeServiceShutdown()
    {
        Setup();Invoke("ReconcileSandboxes",new object?[]{null});runtime.TerminationErrorBox=a.BoxName;
        var reply=await controller!.Dispatch(Command.Create("stopService",new{}),default);
        Assert.False(reply.Ok);Assert.Contains("拒絕停止",reply.Error);
        Assert.Contains(b.BoxName,runtime.Stopped);
        Assert.False((bool)typeof(Controller).GetField("stopping",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!);
        Assert.False(config.Bindings.Single(x=>x.TargetKey==a.TargetKey).Enabled);
    }
    public void Dispose(){runtime.ListError=null;runtime.TerminationErrorBox=null;runtime.Members=[];controller?.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
    private sealed class FakeRuntime : ISandboxRuntime
    {
        public SandboxProcess[] Members=[];public Exception? ListError;public string? SettingsError,TerminationErrorBox,CompatibilityError;public List<string> Stopped=[];
        public void VerifyCompatibility(){ if (CompatibilityError is not null) throw new IOException(CompatibilityError); }
        public bool IdentityAvailable { get; set; } = true;
        public Action? CheckGate;
        public SandboxProcess[] Processes(string box){if(ListError is{} e)throw e;return Members.Where(x=>x.BoxName==box).ToArray();}
        public SandboxProcess? Query(int pid)=>Members.FirstOrDefault(x=>x.Identity.Pid==pid);
        public void ValidateSettings(SandboxInstanceProfile p){CheckGate?.Invoke();if(SettingsError is{} e)throw new IOException(e);}
        public void TerminateOwned(SandboxInstanceProfile p,string owner){CheckGate?.Invoke();Stopped.Add(p.BoxName);if(p.BoxName==TerminationErrorBox)throw new IOException("fixture termination failed");}
        public void Dispose(){}
    }
}

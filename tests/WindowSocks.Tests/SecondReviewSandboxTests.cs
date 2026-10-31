using System.Reflection;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;
namespace WindowSocks.Tests;

public sealed class SecondReviewSandboxTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"xa-review2-"+Guid.NewGuid());
    private readonly Runtime runtime=new();private readonly Firewall firewall=new();
    private readonly SandboxInstanceProfile a=SandboxInstances.Create("A",@"C:\Fixture\Launcher.exe",Guid.NewGuid()) with{Enabled=true};
    private readonly SandboxInstanceProfile b=SandboxInstances.Create("B",@"C:\Fixture\Launcher.exe",Guid.NewGuid());
    private Controller controller=null!;private Action? scanning;
    private DateTimeOffset now=DateTimeOffset.UtcNow;
    private void Setup(bool profiles=true){
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"owner.sid"),WindowsIdentity.GetCurrent().User!.Value);
        new ConfigStore(root).Save(new(){SandboxInstances=profiles?[a,b]:[],Proxies=[new(){Id=a.ProxyId,Host="127.0.0.1",Port=1080},new(){Id=b.ProxyId,Host="127.0.0.1",Port=1081}],Bindings=profiles?[new(){Kind="Sandbox",TargetKey=a.TargetKey,ProxyId=a.ProxyId,Enabled=true},new(){Kind="Sandbox",TargetKey=b.TargetKey,ProxyId=b.ProxyId}]:[]});
        controller=new(root,sandboxRuntime:runtime,engineLifecycle:new Engine(),sandboxFirewall:firewall,handoffClock:()=>now,discoverSandboxImages:profiles=>{scanning?.Invoke();return profiles.Select(p=>p.LauncherPath).ToArray();});
    }
    private async Task WaitInitialization(){
        if(typeof(Controller).GetField("sandboxInitialization",BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(controller) is Task task)await task;
    }
    [Fact] public async Task N1_ControlWorkerPollsAndRestoresPermitWithoutArm(){
        Setup();await WaitInitialization();
        runtime.Members=[new(LdPlayer.Identity(Environment.ProcessId)!.Value,a.BoxName,WindowsIdentity.GetCurrent().User!.Value,1)];
        runtime.Available=false;runtime.CheckResult=false;
        typeof(Controller).GetField("started",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,true);
        runtime.OnList=()=>Assert.Contains("withdraw",firewall.Calls);
        await controller.TickSandboxes(default);runtime.OnList=null;Assert.Contains("withdraw",firewall.Calls);int checks=runtime.Checks;
        await controller.TickSandboxes(default);Assert.Equal(checks,runtime.Checks);
        runtime.CheckResult=true;now=now.AddSeconds(5);firewall.Calls.Clear();
        await controller.TickSandboxes(default);Assert.Equal(checks+1,runtime.Checks);Assert.Contains("allow",firewall.Calls);Assert.DoesNotContain("withdraw",firewall.Calls);
        var config=(SavedConfig)typeof(Controller).GetField("config",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(controller)!;
        Assert.True(config.Bindings.Single(x=>x.TargetKey==a.TargetKey).Enabled);
        Assert.True(new ConfigStore(root).Load().SandboxInstances.Single(x=>x.Id==a.Id).Enabled);
        runtime.Members=[];
    }
    [Fact] public async Task CleanupReservationIsDurableAndPreventsRearmingOrOrdinaryRemoval(){
        Setup();await WaitInitialization();
        var begin=await controller.Dispatch(Command.Create("beginSandboxRemoval",new SandboxRequest(b.Id)),default);
        Assert.True(begin.Ok,begin.Error);
        var saved=new ConfigStore(root).Load().SandboxInstances.Single(p=>p.Id==b.Id);
        Assert.True(saved.RemovalPending);Assert.False(saved.Enabled);
        foreach(string action in new[]{"armSandbox","removeSandbox"}){
            var reply=await controller.Dispatch(Command.Create(action,new SandboxRequest(b.Id)),default);
            Assert.False(reply.Ok);Assert.Contains("清理",reply.Error);
        }
        Assert.True((await controller.Dispatch(Command.Create("beginSandboxRemoval",new SandboxRequest(b.Id)),default)).Ok);
        Assert.True((await controller.Dispatch(Command.Create("finishSandboxRemoval",new {Id=b.Id,DeleteFiles=false}),default)).Ok);
        Assert.DoesNotContain(new ConfigStore(root).Load().SandboxInstances,p=>p.Id==b.Id);
        Assert.Contains(new ConfigStore(root).Load().SandboxInstances,p=>p.Id==a.Id);
    }
    [Fact] public async Task CleanupRejectsRunningMemberAndProtectsPendingProfileAfterRestart(){
        Setup();await WaitInitialization();
        runtime.Members=[new(LdPlayer.Identity(Environment.ProcessId)!.Value,b.BoxName,WindowsIdentity.GetCurrent().User!.Value,1)];
        var running=await controller.Dispatch(Command.Create("beginSandboxRemoval",new SandboxRequest(b.Id)),default);
        Assert.False(running.Ok);Assert.False(new ConfigStore(root).Load().SandboxInstances.Single(p=>p.Id==b.Id).RemovalPending);
        runtime.Members=[];
        Assert.True((await controller.Dispatch(Command.Create("beginSandboxRemoval",new SandboxRequest(b.Id)),default)).Ok);
        controller.Dispose();controller=new(root,sandboxRuntime:runtime,engineLifecycle:new Engine(),sandboxFirewall:firewall,discoverSandboxImages:ps=>ps.Select(p=>p.LauncherPath).ToArray());await WaitInitialization();
        foreach(var command in new[]{Command.Create("armSandbox",new SandboxRequest(b.Id)),Command.Create("saveSandbox",b),Command.Create("deleteRule",new DeleteRuleRequest(b.TargetKey))}) {
            var result=await controller.Dispatch(command,default);Assert.False(result.Ok);Assert.Contains("清理",result.Error);
        }
    }
    [Fact] public async Task CleanupCannotFinishPermanentRemovalWhileFilesRemain(){
        Setup();await WaitInitialization();
        string folder=Path.Combine(root,b.Id.ToString("N"));Directory.CreateDirectory(Path.Combine(folder,"Game"));
        var p=b with {WorkingDirectory=Path.Combine(folder,"Game"),FileRoot=Path.Combine(folder,"Box"),LauncherPath=Path.Combine(folder,"Game","Launcher.exe")};
        var config=(SavedConfig)typeof(Controller).GetField("config",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(controller)!;
        config.SandboxInstances=config.SandboxInstances.Select(x=>x.Id==b.Id?p:x).ToList();
        Assert.True((await controller.Dispatch(Command.Create("beginSandboxRemoval",new SandboxRequest(b.Id)),default)).Ok);
        var finish=await controller.Dispatch(Command.Create("finishSandboxRemoval",new SandboxRemovalRequest(b.Id,true)),default);
        Assert.False(finish.Ok);Assert.Contains("副本資料",finish.Error);
        Assert.True(new ConfigStore(root).Load().SandboxInstances.Single(x=>x.Id==b.Id).RemovalPending);
    }
    [Theory][InlineData("saveSandbox")][InlineData("removeSandbox")][InlineData("deleteRule")]
    public async Task N3_DiscoveryDoesNotHoldGate(string command){
        Setup();await WaitInitialization();
        scanning=()=>{bool acquired=controller.Gate.Wait(0);if(acquired)controller.Gate.Release();Assert.True(acquired,"recursive discovery held Gate");};
        var reply=await controller.Dispatch(command=="saveSandbox"?Command.Create(command,b):command=="deleteRule"?Command.Create(command,new DeleteRuleRequest(b.TargetKey)):Command.Create(command,new SandboxRequest(b.Id)),default);
        Assert.True(reply.Ok,reply.Error);
    }
    [Theory][InlineData("saveSandbox")][InlineData("removeSandbox")]
    public async Task N3_StaleDiscoveryNeverReplacesCurrentFirewall(string command){
        Setup();await WaitInitialization();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        scanning=()=>{entered.Set();release.Wait(TimeSpan.FromSeconds(5));};firewall.Calls.Clear();
        var request=controller.Dispatch(command=="saveSandbox"?Command.Create(command,b):Command.Create(command,new SandboxRequest(b.Id)),default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        await controller.Gate.WaitAsync();
        try{var field=typeof(Controller).GetField("sandboxMembershipRevision",BindingFlags.NonPublic|BindingFlags.Instance)!;field.SetValue(controller,(long)field.GetValue(controller)!+1);}finally{controller.Gate.Release();release.Set();}
        var reply=await request;Assert.False(reply.Ok);Assert.Contains("過期",reply.Error);Assert.Empty(firewall.Calls);
    }
    [Fact] public async Task N3_InitializationPausesWithoutPersistingDisabledIntent(){
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        scanning=()=>{entered.Set();release.Wait(TimeSpan.FromSeconds(5));};Setup();
        try{
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var config=(SavedConfig)typeof(Controller).GetField("config",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(controller)!;
            Assert.False(config.Bindings.Single(x=>x.TargetKey==a.TargetKey).Enabled);
            Assert.True(new ConfigStore(root).Load().SandboxInstances.Single(x=>x.Id==a.Id).Enabled);
            var reply=await controller.Dispatch(Command.Create("armSandbox",new SandboxRequest(b.Id)),default);Assert.False(reply.Ok);Assert.Contains("背景建立",reply.Error);
        }finally{release.Set();await WaitInitialization();}
    }
    [Fact] public async Task N3_StartupReturnsWhileDiscoveryIsBlocked(){
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        scanning=()=>{entered.Set();release.Wait(TimeSpan.FromSeconds(5));};
        var construction=Task.Run(()=>Setup());Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        bool returned=await Task.WhenAny(construction,Task.Delay(250))==construction;
        release.Set();await construction;await WaitInitialization();
        Assert.True(returned,"constructor blocked on recursive discovery");
    }
    [Theory][InlineData("armSandbox")][InlineData("saveSandbox")][InlineData("removeSandbox")]
    public async Task N2_UpdatingAnotherInstanceImmediatelyRefreshesPermit(string command){
        Setup();await WaitInitialization();
        typeof(Controller).GetField("started",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,true);
        firewall.Calls.Clear();
        await controller.Dispatch(command=="saveSandbox"?Command.Create(command,b):Command.Create(command,new SandboxRequest(b.Id)),default);
        Assert.True(firewall.Calls.Count>=2);Assert.Equal(new[]{"replace","allow"},firewall.Calls.Take(2));Assert.DoesNotContain("withdraw",firewall.Calls);
    }
    [Fact] public async Task SandboxFix_NewServiceAcceptsOldHelperAndRejectsItsLateFailure()
    {
        Setup(); await WaitInitialization();
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        runtime.Members = [new(identity, a.BoxName, WindowsIdentity.GetCurrent().User!.Value, 1)];
        ((Dictionary<Guid, ProcessIdentity>)typeof(Controller).GetField("sandboxBootstraps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!)[a.Id] = identity;
        ((Dictionary<Guid, SandboxNetworkReport>)typeof(Controller).GetField("sandboxNetworkChecks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!)[a.Id] = new(a.Id, "192.0.2.1", "192.0.2.1", true);
        // Original three-field wire message, with no 0.1.35 additions.
        var oldHelper = System.Text.Json.JsonSerializer.Deserialize<SandboxLaunchReport>(System.Text.Json.JsonSerializer.Serialize(new { id = a.Id, started = true, detail = "" }), Json.Options)!;
        var success = await controller.Dispatch(Command.Create("reportSandboxLaunch", oldHelper), default, identity.Pid);
        Assert.True(success.Ok, success.Error);
        var stale = await controller.Dispatch(Command.Create("reportSandboxLaunch", new SandboxLaunchReport(a.Id, false, "monitor pipe timeout")), default, identity.Pid);
        Assert.False(stale.Ok);
        Assert.True(new ConfigStore(root).Load().SandboxInstances.Single(p => p.Id == a.Id).Enabled);
        runtime.Members = [];
    }
    [Fact] public async Task SandboxFix_RealStartupFailureStillPausesAndExportsConcreteCause()
    {
        Setup(); await WaitInitialization();
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        runtime.Members = [new(identity, a.BoxName, WindowsIdentity.GetCurrent().User!.Value, 1)];
        ((Dictionary<Guid, ProcessIdentity>)typeof(Controller).GetField("sandboxBootstraps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!)[a.Id] = identity;
        var report = SandboxLaunchErrors.Report(a.Id, new InvalidOperationException("synthetic service rejection: actual cause"), "synthetic-error.txt");
        var reply = await controller.Dispatch(Command.Create("reportSandboxLaunch", report), default, identity.Pid);
        Assert.True(reply.Ok, reply.Error);
        Assert.False(new ConfigStore(root).Load().SandboxInstances.Single(p => p.Id == a.Id).Enabled);
        var diagnostic = (DiagnosticBundle)typeof(Controller).GetMethod("GetDiagnosticBundle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, [])!;
        Assert.Contains("actual cause", Assert.Single(diagnostic.SandboxLaunchErrors).Detail);
        Assert.Contains("actual cause", File.ReadAllText(Path.Combine(root, "service-session.log")));
        Assert.NotNull(diagnostic.RelayPerformance); // Keep the 0.1.34 diagnostic extension.
        runtime.Members = [];
    }
    public void Dispose(){scanning=null;controller?.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
    private sealed class Runtime:ISandboxRuntime{
        public bool Available=true;public bool? CheckResult;public int Checks;public SandboxProcess[] Members=[];public Action? OnList;
        public bool IdentityAvailable=>Available;public void VerifyCompatibility(){Checks++;if(CheckResult==false)throw new IOException("driver temporarily unavailable");if(CheckResult==true)Available=true;}
        public SandboxProcess[] Processes(string box){OnList?.Invoke();return Members.Where(m=>m.BoxName==box).ToArray();}public SandboxProcess? Query(int pid)=>Members.FirstOrDefault(m=>m.Identity.Pid==pid);public void ValidateSettings(SandboxInstanceProfile profile){}public void TerminateOwned(SandboxInstanceProfile profile,string sid){}public void Dispose(){}
    }
    private sealed class Engine:IEngineLifecycle{
        public bool Present=>true;public bool DriverPresent=>false;public bool Compatible=>true;public bool Healthy=>true;public uint Error=>0;public bool Start(string path)=>true;public bool ReplaceCapture(string path)=>true;public void Stop(){}
    }
    private sealed class Firewall:ISandboxFirewall{
        public List<string> Calls=[];public void ReplaceImages(string[] paths)=>Calls.Add("replace");public void AllowWhileHealthy()=>Calls.Add("allow");public void WithdrawPermit()=>Calls.Add("withdraw");public void Dispose(){}public void Clear(){}public SandboxFirewallStatus Inspect()=>new(0,0,"");
    }
}

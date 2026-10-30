using WindowSocks.Core;
using Xunit;
namespace WindowSocks.Tests;
public sealed class P0OwnershipRegressionTests
{
    [Fact] public void P07_AllSubsystemsShareOnePersistentReservationLedger()
    {
        ProxyProfile a=new(),b=new(),c=new(),d=new();
        var box=SandboxInstances.Create("A",@"C:\fixture\Launcher.exe",a.Id);
        var config=new SavedConfig{SandboxInstances=[box],Bindings=[new(){TargetKey="fixed",Kind="LDPlayer",ProxyId=b.Id}],
            VmwareSessions=[new(@"C:\fixture\a.vmx",new(12345,1),c.Id,"fixture")],GlobalProxy=new(){Enabled=true,ProxyId=d.Id}};
        var ledger=ProxyOwnershipLedger.From(config);
        foreach(var line in new[]{a,b,c,d})Assert.Throws<InvalidOperationException>(()=>ledger.Require(line.Id,"new-target","Process"));
        config.Bindings.Add(new(){TargetKey="accidental",Kind="Process",ProxyId=a.Id,SharedProxy=true});
        Assert.True(ProxyOwnershipLedger.From(config).DuplicateExit(a.Id,new Dictionary<Guid,ProbeResult>()));
        var probes=new Dictionary<Guid,ProbeResult>{[b.Id]=new(b.Id,true,true,true,true,"192.0.2.5","192.0.2.5",1,"",DateTimeOffset.UtcNow),[d.Id]=new(d.Id,true,true,true,true,"192.0.2.5","192.0.2.5",1,"",DateTimeOffset.UtcNow)};
        Assert.True(ledger.DuplicateExit(b.Id,probes));Assert.True(ledger.DuplicateExit(d.Id,probes));
    }
    [Fact] public void P07_VmwareStartupCannotConsumeSandboxReservation()
    {
        ProxyProfile[] pool=[new(),new()];string path=@"C:\fixtures\p0.vmx";
        var settings=new VmwareSettings{ByStartup=true,Machines=[new(path,"192.168.70.20",Guid.Empty)]};
        var reserved=new InstanceBinding{TargetKey="sandbox:A",Kind="Sandbox",ProxyId=pool[0].Id};
        var result=VmwarePlanner.Assign(settings,pool,new Dictionary<string,ProcessIdentity?>{[path]=new(12345,100)},[],[reserved]);
        Assert.Equal(pool[1].Id,Assert.Single(result).ProxyId);
    }
    [Fact] public void P07_AutomaticRulesCannotConsumeVmwareReservation()
    {
        ProxyProfile[] pool=[new(),new()];var target=new TargetInstance("p:1","fixture","Process",@"C:\fixture.exe",-1,true,[new(12345,100)]);
        var reserved=new InstanceBinding{TargetKey="vmware:A",Kind="VMware",ProxyId=pool[0].Id};
        var result=AutoProxyPlanner.Additions([target],[],pool,1,[],reservations:[reserved]);
        Assert.Equal(pool[1].Id,Assert.Single(result).ProxyId);
    }
    [Fact] public void P07_ManualVmwareCannotConsumeExclusiveReservation()
    {
        ProxyProfile p=new();string path=@"C:\fixtures\p0.vmx";
        var settings=new VmwareSettings{ByStartup=false,Machines=[new(path,"192.168.70.20",p.Id)]};
        var result=VmwarePlanner.Assign(settings,[p],new Dictionary<string,ProcessIdentity?>{[path]=new(12345,100)},[],[new(){TargetKey="sandbox:A",Kind="Sandbox",ProxyId=p.Id}]);
        Assert.Equal(Guid.Empty,Assert.Single(result).ProxyId);
    }
}

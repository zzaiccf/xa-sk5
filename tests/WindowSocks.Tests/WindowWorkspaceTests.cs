using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class WindowWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-workspace-" + Guid.NewGuid());
    private const string A = @"C:\fixtures\Windows A.vmx", B = @"C:\fixtures\Windows B.vmx";
    public WindowWorkspaceTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"owner.sid"),WindowsIdentity.GetCurrent().User!.Value); }
    public void Dispose() => Directory.Delete(root,true);
    private static Snapshot State(VmwareSettings settings, VmwareRuntime[] runtime, bool prepared=true, bool running=true) =>
        new([],[],[],[],[],new(true,false,true,running,"0.1.19","",[])) { Vmware=new(settings,runtime,prepared,running,settings.PreparationError) };
    [Fact] public void VmWindowsAppearWithoutManualBindingsAndIncludeTheirLineIdentityAndTraffic()
    {
        var proxy=new ProxyProfile{Name="台灣線路",Host="198.51.100.2",Port=1080};
        var settings=new VmwareSettings{Enabled=true,Machines=[new(A,"192.168.70.20",proxy.Id){Name="工作虛擬機"},new(B,"192.168.70.21",Guid.Empty){UseLocalNetwork=true}]};
        var stamp=DateTimeOffset.UtcNow;
        var state=State(settings,[new(A,new(1234,123),proxy.Id,"代理規則已套用",stamp){StatsAvailable=true,Sent=123,Received=456,TcpConnections=2,UdpAssociations=1},new(B,new(1235,124),Guid.Empty,"本地網路已套用"){UseLocalNetwork=true}]) with {Proxies=[proxy]};
        var rows=WindowInventory.List(state);
        Assert.Equal(2,rows.Length); Assert.All(rows,r=>Assert.True(r.Target.Running));
        var row=rows.Single(r=>r.Target.Path==A);Assert.Equal("工作虛擬機",row.Target.Name); Assert.Equal(proxy.Id,row.ProxyId);Assert.Contains("1234",row.InstanceDetail);Assert.Contains("192.168.70.20",row.Detail);
        Assert.Equal(123,row.Sent);Assert.Equal(456,row.Received);Assert.Equal(2u,row.TcpConnections);Assert.Equal(stamp,row.ConnectedAt);
        var local=rows.Single(r=>r.Target.Path==B);Assert.True(local.UseLocalNetwork);Assert.Equal(Guid.Empty,local.ProxyId);Assert.Null(local.Sent);
    }
    [Fact] public void StoppedRuntimeOverridesStaleDiscoveryAndUnconfiguredLiveVmsRemainVisible()
    {
        var settings=new VmwareSettings{Enabled=true,Machines=[new(A,"192.168.70.20",Guid.Empty)]};
        var state=State(settings,[new(A,null,Guid.Empty,"等待啟動")]);
        VmwareDiscovered[] seen=[new(new(A,"old name","a","","custom","VMnet1"),new(1234,123),""),new(new(B,"未納管 VM","b","","nat",""),new(1235,124),"")];
        var rows=WindowInventory.List(state,seen);
        Assert.False(rows.Single(r=>r.Target.Path==A).Target.Running);
        var live=Assert.Single(rows,r=>r.Target.Running); Assert.Equal(B,live.Target.Path); Assert.Equal("未設定規則",live.Rule);Assert.Contains("未設定規則",live.State);
    }
    [Fact] public void PendingFailedAndOfflineRulesRemainSeparateFromRunningWindowState()
    {
        var settings=new VmwareSettings{Enabled=true,PendingPreparation=true,NeedsNetworkPlan=true,PreparationError="fixture network unavailable",Machines=[new(A,"",Guid.Empty){Name="保存的 VM"}]};
        var state=State(settings,[new(A,new(1234,123),Guid.Empty,"等待分配")],false,false);
        Assert.Contains("準備未完成",Assert.Single(ProxyRules.List(state)).State);
        Assert.Contains("未代理",Assert.Single(WindowInventory.List(state)).State);
        var offline=state with{Diagnostics=state.Diagnostics with{ServiceOnline=false}};
        Assert.Equal("待服務連線",Assert.Single(ProxyRules.List(offline)).State);
        Assert.Contains("待確認",Assert.Single(WindowInventory.List(offline)).State);
        var stopped=state with{Vmware=state.Vmware! with{Machines=[state.Vmware!.Machines[0] with{Process=null}]}};
        Assert.Single(ProxyRules.List(stopped));Assert.DoesNotContain(WindowInventory.List(stopped),r=>r.Target.Running);
    }
    [Fact] public async Task SavingVmRuleWhilePoweredOnPersistsBeforeNetworkPreparationAndSurvivesRestart()
    {
        string path=Fixture();Directory.CreateDirectory(path+".lck");
        var request=new VmwareSettings{ByStartup=true,OverflowToLocal=true,Machines=[new(path,"",Guid.Empty)]};
        using(var service=new Controller(root)) {
            var reply=await service.Dispatch(Command.Create("setupVmware",request),default);Assert.True(reply.Ok,reply.Error);
            var state=Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot",new{}),default)).Data);
            Assert.Single(ProxyRules.List(state)); Assert.True(state.Vmware!.Settings.NeedsNetworkPlan);Assert.True(state.Vmware.Settings.PendingPreparation);
            Assert.Equal("保存測試 VM",state.Vmware.Settings.Machines[0].Name);
        }
        using(var restarted=new Controller(root)) {
            await restarted.Tick(default);
            var state=Json.Read<Snapshot>((await restarted.Dispatch(Command.Create("snapshot",new{}),default)).Data);
            Assert.Single(ProxyRules.List(state)); Assert.True(state.Vmware!.Settings.PendingPreparation);
        }
        Assert.Contains("connectionType = \"nat\"",File.ReadAllText(path)); Assert.False(File.Exists(Path.Combine(root,"vmware-network-backup.json")));
    }
    [Fact] public async Task MissingNetworkPlanSavesFailureReasonAndDoesNotDiscardTheRuleOrTouchVmx()
    {
        string path=Fixture(), original=File.ReadAllText(path);
        using(var service=new Controller(root)) {
            var reply=await service.Dispatch(Command.Create("setupVmware",new VmwareSettings{AdapterId="fixture-no-such-adapter",ByStartup=true,OverflowToLocal=true,Machines=[new(path,"",Guid.Empty)]}),default);
            Assert.True(reply.Ok,reply.Error);
            Assert.Empty(new ConfigStore(root).Load().Vmware.PreparationError);
            await service.Tick(default);
            var state=Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot",new{}),default)).Data);
            Assert.Contains("準備未完成",Assert.Single(ProxyRules.List(state)).State); Assert.NotEmpty(state.Vmware!.Settings.PreparationError);
        }
        using(var restarted=new Controller(root)) {
            var state=Json.Read<Snapshot>((await restarted.Dispatch(Command.Create("snapshot",new{}),default)).Data);
            Assert.Single(ProxyRules.List(state));Assert.NotEmpty(state.Vmware!.Settings.PreparationError);
        }
        Assert.Equal(original,File.ReadAllText(path)); Assert.False(File.Exists(Path.Combine(root,"vmware-network-backup.json")));
    }
    [Fact] public void UnplannedGuestAddressesAreOnlyAcceptedForDraftsAndCannotActivate()
    {
        var draft=new VmwareSettings{NeedsNetworkPlan=true,PendingPreparation=true,Machines=[new(A,"",Guid.Empty),new(B,"",Guid.Empty)]};
        VmwarePlanner.Validate(draft,[]);
        VmwarePlanner.Validate(draft with{PendingPreparation=false,Enabled=false},[]);
        Assert.Throws<InvalidOperationException>(()=>VmwarePlanner.Validate(draft,[],requirePlanned:true));
        Assert.Throws<InvalidOperationException>(()=>VmwarePlanner.Validate(draft with{NeedsNetworkPlan=false},[]));
        Assert.Throws<InvalidOperationException>(()=>VmwarePlanner.Validate(draft with{Machines=[draft.Machines[0],draft.Machines[0]]},[]));
    }
    private string Fixture()
    {
        string path=Path.Combine(root,"guest.vmx");File.WriteAllText(path,"displayName = \"保存測試 VM\"\nuuid.bios = \"fixture\"\nethernet0.present = \"TRUE\"\nethernet0.generatedAddress = \"00:0c:29:12:34:56\"\nethernet0.connectionType = \"nat\"\n");return path;
    }
}

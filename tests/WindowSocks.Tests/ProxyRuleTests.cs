using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ProxyRuleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-rule-tests-" + Guid.NewGuid());
    public ProxyRuleTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value); }
    public void Dispose() => Directory.Delete(root, true);
    private static Snapshot Snapshot(SavedConfig config) => new(config.Proxies.ToArray(), config.Bindings.ToArray(), [], [], [], new(false,false,false,false,"0.1.19","",[])) {
        AutoProxy = config.AutoProxy, Vmware = new(config.Vmware, config.VmwareSessions, false, false, "")
    };
    [Fact] public void ListShowsSavedInactiveRulesAndGroupsAutomaticInstances()
    {
        var config = new SavedConfig { AutoProxy = new() { Configured = true, Kind = "EmulatorSequence", PerProxy = 2 },
            Vmware = new() { PendingPreparation = true, Machines = [new(@"C:\fixtures\a.vmx", "192.168.70.20", Guid.Empty)] },
            Bindings = [new() { TargetKey = "auto1", AutoManaged = true }, new() { TargetKey = "auto2", AutoManaged = true }, new() { TargetKey = "manual", Name = "固定窗口", Enabled = false }] };
        var rows = ProxyRules.List(Snapshot(config)); Assert.Equal(3, rows.Length);
        Assert.Equal("已暫停", rows.Single(r => r.Key == "automatic").State); Assert.Contains("2 個", rows.Single(r => r.Key == "automatic").Members);
        Assert.Equal("待服務連線", rows.Single(r => r.Key == "vmware").State); Assert.Contains(rows, r => r.Key == "binding:manual");
    }
    [Fact] public void DeletingOneRulePreservesOtherRulesAndProxyNodes()
    {
        var config = new SavedConfig { Proxies = [new()], AutoProxy = new() { Configured = true }, Bindings = [new() { TargetKey = "auto", AutoManaged = true }, new() { TargetKey = "manual" }], Vmware = new() { Machines = [new(@"C:\fixtures\a.vmx", "192.168.70.20", Guid.Empty)] } };
        var next = ProxyRules.Delete(config, "automatic");
        Assert.Single(next.Proxies); Assert.Single(next.Bindings); Assert.Equal("manual", next.Bindings[0].TargetKey); Assert.False(ProxyRules.HasAutomatic(next.AutoProxy, next.Bindings)); Assert.Single(next.Vmware.Machines);
        Assert.Equal(2, config.Bindings.Count); Assert.True(config.AutoProxy.Configured);
        next = ProxyRules.Delete(next, "binding:manual"); Assert.Empty(next.Bindings); Assert.Contains("manual", next.AutoExcludedTargets);
    }
    [Fact] public async Task AutomaticRuleDeletionPersistsAcrossTickAndServiceRestart()
    {
        var store = new ConfigStore(root); store.Save(new() { AutoProxy = new() { Configured = true, Kind = "Process", Pattern = "fixture.exe" }, Bindings = [new() { TargetKey = "auto", AutoManaged = true }, new() { TargetKey = "manual", Kind = "LDPlayer", Path = @"C:\fixtures\ldconsole.exe", Index = 0 }], GameGroups = [new() { Root = new(1234567, 1), LauncherPath = @"C:\fixtures\launcher.exe", ClientPath = @"C:\fixtures\client.exe" }] });
        using (var service = new Controller(root)) {
            var reply = await service.Dispatch(Command.Create("deleteRule", new DeleteRuleRequest("automatic")), default); Assert.True(reply.Ok, reply.Error);
            await service.Tick(default);
            var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.DoesNotContain(ProxyRules.List(snapshot), r => r.Key == "automatic"); Assert.Single(snapshot.Bindings); Assert.Empty(store.Load().GameGroups);
        }
        using (var service = new Controller(root)) {
            await service.Tick(default);
            Assert.False(ProxyRules.HasAutomatic(store.Load().AutoProxy, store.Load().Bindings)); Assert.Single(store.Load().Bindings);
        }
    }
    [Fact] public async Task VmwareDeletionCancelsPendingSetupAndNeverChangesVmxOrBackup()
    {
        string vmx = Path.Combine(root, "guest.vmx"); File.WriteAllText(vmx, "original fixture, must remain untouched");
        var store = new ConfigStore(root); store.Save(new() { Vmware = new() { PendingPreparation = true, Machines = [new(vmx, "192.168.70.20", Guid.NewGuid())] } });
        using (var service = new Controller(root)) {
            var reply = await service.Dispatch(Command.Create("deleteRule", new DeleteRuleRequest("vmware")), default); Assert.True(reply.Ok, reply.Error); await service.Tick(default);
        }
        using (var service = new Controller(root)) { await service.Tick(default); }
        var saved = store.Load(); Assert.True(saved.Vmware.RuleDeleted); Assert.False(saved.Vmware.PendingPreparation); Assert.False(saved.Vmware.Enabled);
        Assert.Single(saved.Vmware.Machines); Assert.Empty(saved.VmwareSessions); Assert.DoesNotContain(ProxyRules.List(Snapshot(saved)), r => r.Key == "vmware");
        Assert.Equal("original fixture, must remain untouched", File.ReadAllText(vmx)); Assert.False(File.Exists(Path.Combine(root, "vmware-network-backup.json")));
    }
    [Fact] public void DeletedVmwareRuleRetainsRecoveryMetadata()
    {
        var config = new SavedConfig { Vmware = new() { Enabled = true, AdapterId = "recovery-adapter", Machines = [new(@"C:\fixtures\a.vmx", "192.168.70.20", Guid.NewGuid())] } };
        var next = ProxyRules.Delete(config, "vmware"); Assert.Equal(config.Vmware.AdapterId, next.Vmware.AdapterId); Assert.Equal(config.Vmware.Machines, next.Vmware.Machines); Assert.False(next.Vmware.Enabled); Assert.True(next.Vmware.RuleDeleted);
        Assert.Throws<InvalidOperationException>(() => ProxyRules.Delete(config, "unknown"));
    }
}

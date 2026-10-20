using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SequentialEmulatorTests
{
    private static AutoProxyRule Rule(string kind = "LDPlayer") => new() { Kind = "EmulatorSequence", EmulatorKind = kind,
        ConsolePath = kind == "MuMu" ? @"C:\Emulators\MuMu\nx_main\MuMuManager.exe" : @"C:\Emulators\LD\ldconsole.exe", Enabled = true };
    private static TargetInstance Target(int index, long birth, string kind = "LDPlayer") => new(
        EmulatorCatalog.Key(kind, Rule(kind).ConsolePath, index), $"Window {index}", kind, Rule(kind).ConsolePath, index, true, [new(100 + index, birth)]);
    private static InstanceBinding Binding(TargetInstance target, Guid proxy, bool automatic = true) => new() {
        TargetKey = target.Key, Kind = target.Kind, Path = target.Path, Index = target.Index, Processes = target.Processes,
        ProxyId = proxy, AutoManaged = automatic, Enabled = true, SharedProxy = true
    };

    [Theory] [InlineData("LDPlayer")] [InlineData("MuMu")]
    public void StartupTimeWinsOverIndexAndDiscoveryOrder(string kind)
    {
        ProxyProfile[] pool = [new(), new(), new()];
        var candidates = new[] { Target(1, 300, kind), Target(3, 100, kind), Target(2, 200, kind) };
        var items = AutoProxyPlanner.Additions(candidates.Reverse(), [], pool, 1, [], launchOrder: true);
        Assert.Equal(new[] { 3, 2, 1 }, items.Select(i => i.Target.Index));
        Assert.Equal(pool.Select(p => p.Id), items.Select(i => i.ProxyId));
    }
    [Fact] public void StoppedUnknownAndDifferentInstallationsConsumeNoAutomaticSlot()
    {
        var rule = Rule("MuMu"); var live = Target(3, 100, "MuMu");
        var candidates = SequentialEmulatorPlanner.Candidates([live, Target(1, 50), live with { Key = "stopped", Running = false },
            live with { Key = "unidentified", Processes = [] }, live with { Key = "other", Path = @"D:\MuMu\MuMuManager.exe" }], rule);
        Assert.Equal(live, Assert.Single(candidates));
        Assert.Empty(AutoProxyPlanner.Additions([live with { Processes = [] }, live with { Running = false }], [], [new()], 1, [], launchOrder: true));
    }
    [Fact] public void LiveIpIsStableButClosedWindowRejoinsTheQueueAfterPidReuse()
    {
        ProxyProfile[] pool = [new(), new(), new()];
        var old = Target(3, 100); var live = Target(2, 200); var restarted = Target(3, 400); var earlier = Target(1, 300);
        ProcessIdentity? Identity(int pid) => new[] { live, restarted, earlier }.SelectMany(t => t.Processes).Cast<ProcessIdentity?>().FirstOrDefault(p => p!.Value.Pid == pid);
        var retained = SequentialEmulatorPlanner.Prepare(Rule(), [Binding(old, pool[0].Id), Binding(live, pool[1].Id)], Identity);
        Assert.Equal(pool[1].Id, Assert.Single(retained).ProxyId);
        var added = AutoProxyPlanner.Additions([restarted, earlier, live], retained, pool, 1, [], launchOrder: true);
        Assert.Equal(earlier.Key, added[0].Target.Key); Assert.Equal(pool[0].Id, added[0].ProxyId);
        Assert.Equal(restarted.Key, added[1].Target.Key); Assert.Equal(pool[2].Id, added[1].ProxyId);
    }
    [Fact] public void SharingCapacityAndExplicitExclusionsAreStillRespected()
    {
        ProxyProfile[] pool = [new(), new()];
        var items = AutoProxyPlanner.Additions(Enumerable.Range(1, 6).Select(i => Target(i, 700 - i * 100)), [], pool, 2, [Target(6, 100).Key], launchOrder: true);
        Assert.Equal(new[] { 5, 4, 3, 2 }, items.Select(i => i.Target.Index));
        Assert.Equal(new[] { 2, 2 }, items.GroupBy(i => i.ProxyId).Select(g => g.Count()));
        Assert.Equal(10, AutoProxyPlanner.Additions(Enumerable.Range(1, 12).Select(i => Target(i, i * 100)), [], pool, 10, [], launchOrder: true).Length);
    }
    [Fact] public void ChangingModePreservesLivePauseManualBindingsAndOtherScopes()
    {
        var proxy = new ProxyProfile(); var live = Target(2, 200); var outside = Target(3, 300, "MuMu");
        var paused = Binding(live, proxy.Id) with { AutoPaused = true, Enabled = false };
        var oldOther = Binding(outside, proxy.Id); var manual = Binding(Target(4, 400), proxy.Id, false);
        var next = SequentialEmulatorPlanner.Prepare(Rule(), [paused, oldOther, manual, Binding(Target(5, 500), proxy.Id)], pid => pid == 102 ? live.Processes[0] : null);
        Assert.Equal(3, next.Count);
        Assert.False(next.Single(b => b.TargetKey == live.Key).Enabled);
        Assert.True(next.Single(b => b.TargetKey == manual.TargetKey).Enabled);
        var other = next.Single(b => b.TargetKey == outside.Key); Assert.False(other.AutoManaged); Assert.False(other.Enabled);
        Assert.True(oldOther.Enabled); // Planning cannot mutate installed policies before validation succeeds.
    }
    [Fact] public void InvalidScopeMixedFixedRulesAndLowerCapacityAreRejected()
    {
        var rule = Rule(); AutoProxyPlanner.Validate(rule); AutoProxyPlanner.Validate(Rule("MuMu"));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { EmulatorKind = "unknown" }));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { ConsolePath = @"C:\Emulators\other.exe" }));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { IncludeChildren = true }));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { EmulatorAssignments = [new("LDPlayer", rule.ConsolePath, 1, "one", Guid.NewGuid())] }));
        var id = Guid.NewGuid(); var targets = new[] { Target(1, 100), Target(2, 200) };
        Assert.Throws<InvalidOperationException>(() => SequentialEmulatorPlanner.Prepare(rule, targets.Select(t => Binding(t, id)), pid => targets.Single(t => t.Processes[0].Pid == pid).Processes[0]));
    }
    [Fact] public async Task DisabledSequenceRulePersistsWithoutStartingDriverOrAssigningClosedWindows()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        new ConfigStore(directory).Save(new() { Proxies = [new() { Host = "127.0.0.1", Port = 1080 }] });
        var rule = Rule("MuMu") with { Enabled = false, PerProxy = 2 };
        try {
            using (var controller = new Controller(directory)) {
                var reply = await controller.Dispatch(Command.Create("setAutoProxy", rule), default); Assert.True(reply.Ok, reply.Error);
                var snapshot = Json.Read<Snapshot>((await controller.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.True(snapshot.SupportsSequentialEmulators); Assert.Equal("MuMu", snapshot.AutoProxy.EmulatorKind);
                Assert.Empty(snapshot.Bindings); Assert.False(snapshot.Diagnostics.EngineRunning);
            }
            var saved = new ConfigStore(directory).Load(); Assert.Equal(rule.Kind, saved.AutoProxy.Kind);
            Assert.Equal(rule.EmulatorKind, saved.AutoProxy.EmulatorKind); Assert.Equal(rule.ConsolePath, saved.AutoProxy.ConsolePath);
            Assert.Equal(2, saved.AutoProxy.PerProxy); Assert.False(saved.AutoProxy.Enabled);
        } finally { Directory.Delete(directory, true); }
    }
}

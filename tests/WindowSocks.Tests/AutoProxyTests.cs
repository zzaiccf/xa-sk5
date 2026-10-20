using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class AutoProxyTests
{
    private static TargetInstance Target(int n, long birth = 100) => new($"pid:{n}:{birth}", $"Game {n}", "Process", @"C:\Games\Game.exe", -1, true, [new(n, birth)]);
    [Fact] public void AllocatesWholePoolWithoutChoosingIndividualIps()
    {
        ProxyProfile[] profiles = [new(), new()];
        var items = AutoProxyPlanner.Additions(Enumerable.Range(1, 6).Select(n => Target(n)), [], profiles, 3, []);
        Assert.Equal(6, items.Length); Assert.Equal(new[] { 3, 3 }, items.GroupBy(i => i.ProxyId).Select(g => g.Count()));
        Assert.All(AssignmentPlanner.Prepare([], profiles, new(items)), b => Assert.False(b.IncludeChildren));
    }
    [Fact] public void ExistingIpIsStableAndNewTargetsFillVacancies()
    {
        ProxyProfile[] profiles = [new(), new()];
        var existing = AssignmentPlanner.Prepare([], profiles, new([new(Target(10), profiles[1].Id)]));
        existing[0].Enabled = false; // A paused or failed existing assignment still owns its slot.
        var added = AutoProxyPlanner.Additions([Target(1), Target(10)], existing, profiles, 1, []);
        Assert.Equal(profiles[0].Id, Assert.Single(added).ProxyId);
        Assert.Equal(profiles[1].Id, existing[0].ProxyId);
        Assert.Empty(AutoProxyPlanner.Additions([Target(20)], existing, [profiles[1]], 1, []));
    }
    [Fact] public void CapacityOverflowExclusiveAndExplicitUnbindAreNotReassigned()
    {
        ProxyProfile[] profiles = [new(), new()];
        var existing = AssignmentPlanner.Prepare([], profiles, new([new(Target(1), profiles[0].Id, false)]));
        var added = AutoProxyPlanner.Additions([Target(2), Target(3), Target(4)], existing, profiles, 1, [Target(2).Key]);
        Assert.Equal(Target(3).Key, Assert.Single(added).Target.Key); Assert.Equal(profiles[1].Id, added[0].ProxyId);
        Assert.Empty(AutoProxyPlanner.Additions([Target(4)], existing, [], 1, []));
    }
    [Fact] public void SamePidWindowsAreDeduplicatedAndTenTargetLimitIsEnforced()
    {
        ProxyProfile[] profiles = [new(), new()];
        var items = AutoProxyPlanner.Additions([Target(1), Target(1) with { Key = "another-window" }], [], profiles, 10, []);
        Assert.Single(items);
        Assert.Equal(10, AutoProxyPlanner.Additions(Enumerable.Range(1, 20).Select(n => Target(n)), [], profiles, 10, []).Length);
    }
    [Fact] public void ExactExecutableMatchingRejectsBlankWildcardsAndSubstring()
    {
        var rule = new AutoProxyRule { Kind = "Process", Pattern = "GAME.exe" }; AutoProxyPlanner.Validate(rule);
        Assert.True(AutoProxyPlanner.Matches(Target(1), rule));
        Assert.False(AutoProxyPlanner.Matches(Target(1) with { Path = @"C:\Games\OtherGame.exe" }, rule));
        foreach (var text in new[] { "", "*.exe", "game", @"C:\Games\Game.exe" })
            Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { Pattern = text }));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { PerProxy = 0 }));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { Match = TargetMatch.WindowTitle }));
    }
    [Fact] public void SameNameDescendantsAreExcludedButReusedParentPidIsNotAnAncestor()
    {
        var candidates = new[] { Target(1, 100), Target(2, 200), Target(3, 300) };
        var parents = new Dictionary<int, int> { [2] = 1, [3] = 2 };
        ProcessIdentity? Identity(int pid) => candidates.First(t => t.Processes[0].Pid == pid).Processes[0];
        Assert.Equal(1, Assert.Single(AutoProxyPlanner.Roots(candidates, parents, Identity)).Processes[0].Pid);
        var reused = new[] { Target(1, 500), Target(2, 200) };
        Assert.Equal(2, AutoProxyPlanner.Roots(reused, parents, pid => reused.First(t => t.Processes[0].Pid == pid).Processes[0]).Length);
    }
    [Fact] public async Task MissingDriverCannotMarkAutomaticRuleAsEnabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        try {
            using var controller = new Controller(directory);
            var reply = await controller.Dispatch(Command.Create("setAutoProxy", new AutoProxyRule { Enabled = true, Kind = "Process", Pattern = "game.exe" }), default);
            Assert.False(reply.Ok);
            var snapshot = Json.Read<Snapshot>((await controller.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.False(snapshot.AutoProxy.Enabled); Assert.Empty(snapshot.Bindings); Assert.False(snapshot.Diagnostics.EngineRunning);
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task DisablingAutomaticRuleKeepsBindingsBlockedAndPersistsSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var profile = new ProxyProfile { Host = "127.0.0.1", Port = 1080 };
        var rule = new AutoProxyRule { Enabled = true, Kind = "Process", Pattern = "game.exe", PerProxy = 3 };
        var config = new SavedConfig { Proxies = [profile], AutoProxy = rule, Bindings = [new() { TargetKey = "test", ProxyId = profile.Id, AutoManaged = true, Enabled = true, SharedProxy = true }] };
        var store = new ConfigStore(directory); store.Save(config);
        try {
            using var controller = new Controller(directory);
            var reply = await controller.Dispatch(Command.Create("setAutoProxy", rule with { Enabled = false }), default);
            Assert.True(reply.Ok, reply.Error);
            var saved = store.Load(); Assert.False(saved.AutoProxy.Enabled); Assert.Equal(3, saved.AutoProxy.PerProxy);
            Assert.False(Assert.Single(saved.Bindings).Enabled); Assert.True(saved.Bindings[0].AutoManaged);
        } finally { Directory.Delete(directory, true); }
    }
}

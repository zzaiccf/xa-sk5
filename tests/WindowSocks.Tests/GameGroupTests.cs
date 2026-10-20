using System.Diagnostics;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class GameGroupTests
{
    private static readonly AutoProxyRule Rule = new() { Enabled = true, Kind = "GameGroup", Match = TargetMatch.ExecutablePath, Pattern = @"D:\遊戲\Login.exe", ClientPath = @"D:\遊戲\TWClient.bin" };
    private static GameProcessObservation Launcher(int pid, long birth, int parent = 99) => new(new(pid, birth), Rule.Pattern, parent);
    private static GameProcessObservation Client(int pid, long birth, int parent) => new(new(pid, birth), Rule.ClientPath, parent);

    [Fact] public void SameNameLaunchesAreSeparateGroupsAndQuotaCountsGroups()
    {
        AutoProxyPlanner.Validate(Rule);
        var scan = GameGroupTracker.Update([], Rule, [Launcher(1, 100), Launcher(2, 200), Client(3, 300, 1), Client(4, 400, 2)], new Dictionary<ProcessIdentity, long>(), 500);
        Assert.Equal(2, scan.Groups.Length); Assert.Empty(scan.Unmatched);
        Assert.Equal(new[] { 1, 3 }, scan.Groups[0].Target().Processes.Select(p => p.Pid));
        Assert.Equal(new[] { 2, 4 }, scan.Groups[1].Target().Processes.Select(p => p.Pid));
        ProxyProfile[] proxies = [new(), new()];
        var items = AutoProxyPlanner.Additions(scan.Groups.Select(g => g.Target()), [], proxies, 1, []);
        Assert.Equal(2, items.Select(i => i.ProxyId).Distinct().Count());
        Assert.Equal(2, AssignmentPlanner.Prepare([], proxies, new(items)).Count);
        Assert.Single(AutoProxyPlanner.Additions(scan.Groups.Select(g => g.Target()), [], [proxies[0]], 1, []));
    }
    [Fact] public void ChildDiscoveredAfterLauncherExitKeepsOriginalGroupAndProxy()
    {
        var scan = GameGroupTracker.Update([], Rule, [Launcher(1, 100)], new Dictionary<ProcessIdentity, long>(), 200);
        ProxyProfile[] proxies = [new()];
        var binding = AssignmentPlanner.Prepare([], proxies, new([new(scan.Groups[0].Target(), proxies[0].Id)]));
        scan = GameGroupTracker.Update(scan.Groups, Rule, [Client(3, 300, 1)], new Dictionary<ProcessIdentity, long> { [new(1, 100)] = 350 }, 500);
        Assert.Equal(binding[0].TargetKey, Assert.Single(scan.Groups).Key);
        Assert.Equal(3, Assert.Single(scan.Groups[0].Target().Processes).Pid);
        Assert.Empty(AutoProxyPlanner.Additions(scan.Groups.Select(g => g.Target()), binding, proxies, 1, []));
        // A persisted client retains its assignment without a surviving parent, including service restart.
        var saved = System.Text.Json.JsonSerializer.Deserialize<GameLaunchGroup[]>(System.Text.Json.JsonSerializer.Serialize(scan.Groups, Json.Options), Json.Options)!;
        scan = GameGroupTracker.Update(saved, Rule, [Client(3, 300, 1)], new Dictionary<ProcessIdentity, long>(), 600);
        Assert.Equal(binding[0].TargetKey, Assert.Single(scan.Groups).Key); Assert.Empty(scan.Unmatched);
    }
    [Fact] public void ReusedParentPidCannotStealClientAndUnknownExitCannotProveLineage()
    {
        var first = GameGroupTracker.Update([], Rule, [Launcher(1, 100)], new Dictionary<ProcessIdentity, long>(), 200);
        var scan = GameGroupTracker.Update(first.Groups, Rule, [Launcher(1, 500), Client(3, 300, 1), Client(4, 600, 1)], new Dictionary<ProcessIdentity, long> { [new(1, 100)] = 350 }, 700);
        Assert.Equal(3, Assert.Single(scan.Groups.Single(g => g.Root.CreatedFileTime == 100).Target().Processes).Pid);
        Assert.Equal(new[] { 1, 4 }, scan.Groups.Single(g => g.Root.CreatedFileTime == 500).Target().Processes.Select(p => p.Pid));
        var unknown = GameGroupTracker.Update(first.Groups, Rule, [Client(3, 300, 1)], new Dictionary<ProcessIdentity, long>(), 700);
        Assert.Single(unknown.Unmatched); Assert.Empty(unknown.Groups[0].Target().Processes);
    }
    [Fact] public void ExactImagesOnlyAndAmbiguousOrOrphanClientsRemainUnmatched()
    {
        var scan = GameGroupTracker.Update([], Rule, [Launcher(1, 100), Client(2, 200, 99), new(new(3, 300), @"D:\其他\TWClient.bin", 1), new(new(4, 400), @"D:\遊戲\helper.exe", 1)], new Dictionary<ProcessIdentity, long>(), 500);
        Assert.Single(scan.Groups); Assert.Single(scan.Groups[0].Target().Processes); Assert.Equal(3, scan.Unmatched.Length);
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(Rule with { ClientPath = "TWClient.bin" }));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(Rule with { ClientPath = Rule.Pattern }));
        var duplicate = scan.Groups[0] with { Root = new(9, 90) };
        scan = GameGroupTracker.Update([scan.Groups[0], duplicate], Rule, [Launcher(1, 100), Client(5, 500, 1)], new Dictionary<ProcessIdentity, long>(), 600);
        Assert.Single(scan.Unmatched); Assert.All(scan.Groups, g => Assert.DoesNotContain(g.Members, m => m.Identity.Pid == 5));
    }
    [Fact] public void PausedRuleStillTracksSpecifiedChildAndReleasesOnlyWhenWholeGroupEnds()
    {
        var scan = GameGroupTracker.Update([], Rule, [Launcher(1, 100), Client(2, 200, 1)], new Dictionary<ProcessIdentity, long>(), 300);
        scan = GameGroupTracker.Update(scan.Groups, Rule with { Enabled = false }, [Client(2, 200, 1)], new Dictionary<ProcessIdentity, long> { [new(1, 100)] = 250 }, 400);
        Assert.Single(scan.Groups); Assert.Single(scan.Groups[0].Target().Processes);
        scan = GameGroupTracker.Update(scan.Groups, Rule, [], new Dictionary<ProcessIdentity, long> { [new(2, 200)] = 500 }, 600);
        Assert.Single(scan.Groups); Assert.Empty(scan.Groups[0].Target().Processes);
        scan = GameGroupTracker.Update(scan.Groups, Rule, [], new Dictionary<ProcessIdentity, long>(), 600 + TimeSpan.FromSeconds(11).Ticks);
        Assert.Empty(scan.Groups);
    }
    [Fact] public async Task BothGameImagesAreGuardedBeforeDiscoveryAndCannotBeForgedBySync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var rule = Rule with { Pattern = Path.Combine(Environment.SystemDirectory, "ping.exe"), ClientPath = Path.Combine(Environment.SystemDirectory, "cmd.exe") };
        new ConfigStore(directory).Save(new() { AutoProxy = rule });
        using var child = Process.Start(new ProcessStartInfo(rule.Pattern) { Arguments = "-n 30 127.0.0.1", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        using var orphan = Process.Start(new ProcessStartInfo(rule.ClientPath) { Arguments = "/d /c pause", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try {
            using var service = new Controller(directory);
            var identity = LdPlayer.Identity(child.Id)!.Value; var clientIdentity = LdPlayer.Identity(orphan.Id)!.Value;
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)orphan.Id, (ulong)clientIdentity.CreatedFileTime, 0, out _));
            var forged = new TargetInstance("game:forged", "偽造組", "GameGroup", rule.Pattern, -1, true, [identity, clientIdentity]);
            Assert.False((await service.Dispatch(Command.Create("syncTargets", new[] { forged }), default)).Ok);
            await Task.Delay(700); await service.Tick(default); // No proxy: both launcher and unrelated cmd remain blocked.
            var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.Contains(snapshot.Targets, t => t.Kind == "GameGroup" && t.Processes.Contains(identity));
            Assert.Contains(snapshot.Targets, t => t.Kind == "GameOrphan" && t.Processes.Contains(clientIdentity));
            Assert.Empty(snapshot.Bindings);
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)orphan.Id, (ulong)clientIdentity.CreatedFileTime, 0, out _));
            NativeEngine.Core_GetProcessStats((uint)orphan.Id, (ulong)clientIdentity.CreatedFileTime, out var traffic, out uint tcp, out uint udp);
            Assert.Equal(0ul, traffic.Sent); Assert.Equal(0u, tcp); Assert.Equal(0u, udp);
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); foreach (var p in new[] { child, orphan }) { if (!p.HasExited) p.Kill(); p.WaitForExit(); } Directory.Delete(directory, true); }
    }
}

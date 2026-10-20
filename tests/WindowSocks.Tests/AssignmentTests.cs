using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class AssignmentTests
{
    private static TargetInstance Target(int n) => new($"pid:{n}:100", $"遊戲窗口 {n}", "Process", @"C:\Games\Game.exe", -1, true, [new(n, 100)]);
    [Fact] public void SixWindowsCanShareTwoProxiesInGroupsOfThree()
    {
        ProxyProfile[] profiles = [new(), new()];
        var items = AssignmentPlanner.Distribute(Enumerable.Range(1, 6).Select(Target).ToArray(), profiles.Select(p => p.Id).ToArray(), 3);
        var bindings = AssignmentPlanner.Prepare([], profiles, new(items));
        Assert.Equal(new[] { 3, 3 }, bindings.GroupBy(b => b.ProxyId).Select(g => g.Count()));
        Assert.All(bindings, b => { Assert.True(b.SharedProxy); Assert.False(b.IncludeChildren); Assert.False(b.Enabled); });
    }
    [Fact] public void OverCapacityAndDuplicateProcessesAreRejectedBeforeChanges()
    {
        var proxy = new ProxyProfile();
        Assert.Throws<InvalidOperationException>(() => AssignmentPlanner.Distribute([Target(1), Target(2), Target(3)], [proxy.Id], 2));
        var existing = AssignmentPlanner.Prepare([], [proxy], new([new(Target(1), proxy.Id)]));
        Assert.Throws<InvalidOperationException>(() => AssignmentPlanner.Prepare(existing, [proxy], new([new(Target(1) with { Key = "other-window-same-process" }, proxy.Id)])));
        Assert.Single(existing); Assert.Equal("pid:1:100", existing[0].TargetKey);
    }
    [Fact] public void ExclusiveBindingNeedsExplicitGroupConversion()
    {
        var proxy = new ProxyProfile();
        var exclusive = AssignmentPlanner.Prepare([], [proxy], new([new(Target(1), proxy.Id, false)]));
        Assert.Throws<InvalidOperationException>(() => AssignmentPlanner.Prepare(exclusive, [proxy], new([new(Target(2), proxy.Id)])));
        var converted = AssignmentPlanner.Prepare(exclusive, [proxy], new([new(Target(1), proxy.Id), new(Target(2), proxy.Id)]));
        Assert.Equal(2, converted.Count); Assert.All(converted, b => Assert.True(b.SharedProxy));
    }
    [Fact] public void MatchingSupportsFilenamePathAndTitleWithoutAutomaticInheritance()
    {
        Assert.True(AssignmentPlanner.Matches(Target(1), "GAME.EXE", TargetMatch.ExecutableName));
        Assert.True(AssignmentPlanner.Matches(Target(1), @"C:\Games", TargetMatch.ExecutablePath));
        Assert.True(AssignmentPlanner.Matches(Target(1), "窗口 1", TargetMatch.WindowTitle));
        Assert.False(AssignmentPlanner.Matches(Target(1), "窗口", TargetMatch.ExecutableName));
        Assert.False(new InstanceBinding().IncludeChildren); Assert.False(new ProcessRequest(1, Guid.NewGuid()).IncludeChildren);
    }
    [Fact] public void IntentionalSharingIsAllowedButSeparateProfilesWithSameExitAreFlagged()
    {
        var a = new ProxyProfile(); var b = new ProxyProfile();
        ProbeResult Result(Guid id) => new(id, true, true, true, true, "198.51.100.1", "198.51.100.1", 10, "", DateTimeOffset.UtcNow);
        var probes = new Dictionary<Guid, ProbeResult> { [a.Id] = Result(a.Id), [b.Id] = Result(b.Id) };
        var bindings = AssignmentPlanner.Prepare([], [a, b], new([new(Target(1), a.Id), new(Target(2), a.Id)]));
        Assert.False(AssignmentPlanner.HasDuplicateExit(a.Id, bindings, probes));
        bindings = AssignmentPlanner.Prepare(bindings, [a, b], new([new(Target(3), b.Id)]));
        Assert.True(AssignmentPlanner.HasDuplicateExit(a.Id, bindings, probes));
    }
    [Fact] public void NativeRulesExcludeRealChildEvenAfterPriorInheritanceWasCached()
    {
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe")) { Arguments = "-n 30 127.0.0.1", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try {
            var parentId = LdPlayer.Identity(Environment.ProcessId)!.Value; var childId = LdPlayer.Identity(child.Id)!.Value;
            Assert.Equal(1, NativeEngine.Core_SetProxy(201, "127.0.0.1", 1080, "", ""));
            NativeEngine.Rule row = new() { Pid = (uint)parentId.Pid, Created = (ulong)parentId.CreatedFileTime, Proxy = 201, Reserved = 1 };
            Assert.Equal(1, NativeEngine.Core_ReplacePolicy([row], 1, "\0\0"));
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)childId.CreatedFileTime, 0, out _));
            row.Reserved = 0; Assert.Equal(1, NativeEngine.Core_ReplacePolicy([row], 1, "\0\0"));
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)childId.CreatedFileTime, 0, out _));
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); if (!child.HasExited) child.Kill(); child.WaitForExit(); }
    }
    [Fact] public async Task ServicePreservesExplicitChildrenAndRejectsWholeBatchWithoutDriver()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var proxy = new ProxyProfile { Host = "127.0.0.1", Port = 1080 };
        var target = Target(Environment.ProcessId) with { Processes = [LdPlayer.Identity(Environment.ProcessId)!.Value] };
        var store = new ConfigStore(directory); store.Save(new() { Proxies = [proxy], Bindings = [new() { TargetKey = target.Key, ProxyId = proxy.Id, Kind = "Process", Processes = target.Processes, IncludeChildren = true }] });
        try {
            using var controller = new Controller(directory);
            var reply = await controller.Dispatch(Command.Create("bindBatch", new BatchBindRequest([new(target, proxy.Id)])), default);
            Assert.False(reply.Ok);
            var state = Json.Read<Snapshot>((await controller.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.Single(state.Bindings); Assert.True(state.Bindings[0].IncludeChildren); Assert.True(store.Load().Bindings[0].IncludeChildren);
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public void GeolocationChecksExactExitAndConvertsDisplayToTraditionalChinese()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{"success":true,"ip":"198.51.100.1","country":"台湾","region":"台北市","city":"台北市","connection":{"isp":"中华电信"}}""");
        var location = IpLocationLookup.Parse(json, "198.51.100.1");
        Assert.Equal("臺灣 · 臺北市", location.Label); Assert.Equal("中華電信", location.Isp);
        Assert.Throws<IOException>(() => IpLocationLookup.Parse(json, "198.51.100.2"));
        Assert.Throws<IOException>(() => IpLocationLookup.Parse(Encoding.UTF8.GetBytes("""{"success":false,"message":"rate limit"}"""), "198.51.100.1"));
    }
    [Fact] public async Task FailedExitDoesNotTriggerGeolocationOrChangeProbeHealth()
    {
        var proxy = new ProxyProfile(); var probe = new ProbeResult(proxy.Id, false, false, false, false, null, null, 1000, "失敗", DateTimeOffset.UtcNow);
        var enriched = await IpLocationLookup.EnrichAsync(proxy, probe);
        Assert.Null(enriched.Location); Assert.Equal("—", enriched.LatencyLabel); Assert.False(enriched.Ready);
    }
    [Fact] public async Task ProbeReturnsSeparateConnectionAndUdpTimings()
    {
        await using var fixture = new SocksFixture();
        var result = await ProxyProbe.RunAsync(fixture.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 3));
        Assert.True(result.Ready); Assert.NotNull(result.ConnectLatencyMs); Assert.NotNull(result.UdpLatencyMs);
        Assert.True(result.LatencyMs >= result.ConnectLatencyMs); Assert.Equal("TCP／UDP 可用", result.StatusLabel);
    }
}

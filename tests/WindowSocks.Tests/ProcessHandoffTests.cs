using System.Diagnostics;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ProcessHandoffTests
{
    [Fact] public void TransitionReservesSlotButAClosedGameEventuallyReleasesIt()
    {
        var grace = new ProcessHandoffGrace(); var now = DateTimeOffset.UtcNow;
        Assert.False(grace.Waiting("game", true, now));
        Assert.True(grace.Waiting("game", false, now.AddSeconds(1)));
        Assert.True(grace.Waiting("game", false, now.AddSeconds(9)));
        Assert.False(grace.Waiting("game", true, now.AddSeconds(10)));
        Assert.True(grace.Waiting("game", false, now.AddSeconds(11)));
        Assert.False(grace.Waiting("game", false, now.AddSeconds(22)));
    }
    [Theory] [InlineData(TargetMatch.ExecutableName, "Login.exe")] [InlineData(TargetMatch.ExecutablePath, @"C:\Game\Login.exe")]
    public void OrdinaryProcessRulesAutomaticallyFollowLaunchedGames(TargetMatch match, string pattern)
    {
        var rule = ProcessHandoffPolicy.Automatic(new() { Kind = "Process", Match = match, Pattern = pattern });
        AutoProxyPlanner.Validate(rule); Assert.True(rule.IncludeChildren);
        Assert.False(ProcessHandoffPolicy.Automatic(new() { Kind = "GameGroup" }).IncludeChildren);
    }
    [Fact] public void ChangingTheIpOfACapturedTreePreservesItsCurrentGameMembers()
    {
        ProxyProfile[] proxies = [new(), new()]; ProcessIdentity root = new(10, 100), member = new(20, 200);
        var target = new TargetInstance("captured", "登入器", "Process", @"C:\Game\Login.exe", -1, true, [root]);
        var previous = new InstanceBinding { TargetKey = target.Key, Kind = "Process", ProxyId = proxies[0].Id, IncludeChildren = true, Processes = [root], Descendants = [member] };
        var changed = Assert.Single(AssignmentPlanner.Prepare([previous], proxies, new([new(target, proxies[1].Id, true, true)])));
        Assert.Equal(proxies[1].Id, changed.ProxyId); Assert.Equal(new[] { member }, changed.Descendants); Assert.Equal(proxies[0].Id, previous.ProxyId);
        var replacement = Assert.Single(AssignmentPlanner.Prepare([previous], proxies, new([new(target with { Processes = [new(10, 500)] }, proxies[1].Id, true, true)])));
        Assert.Empty(replacement.Descendants);
    }
    [Fact] public async Task ExitedLauncherKeepsItsCapturedChildAndOriginalAssignment()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        using var launcher = StartLauncher(); Process? child = null;
        try {
            var root = LdPlayer.Identity(launcher.Id)!.Value;
            var proxy = new ProxyProfile { Host = "127.0.0.1", Port = 1080 };
            string key = $"pid:{root.Pid}:{root.CreatedFileTime}";
            var store = new ConfigStore(directory);
            store.Save(new() { Proxies = [proxy], AutoProxy = new() { Enabled = true, Kind = "Process", Pattern = "cmd.exe" },
                Bindings = [new() { TargetKey = key, Kind = "Process", Path = Environment.GetEnvironmentVariable("ComSpec")!, Processes = [root], ProxyId = proxy.Id, AutoManaged = true, SharedProxy = true }] });
            using var service = new Controller(directory);
            child = await SpawnAndExit(launcher);
            await service.Tick(default);
            var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            var binding = Assert.Single(snapshot.Bindings);
            Assert.Equal(key, binding.TargetKey); Assert.Equal(proxy.Id, binding.ProxyId); Assert.True(binding.IncludeChildren);
            Assert.True(snapshot.AutoProxy.IncludeChildren); Assert.Contains(LdPlayer.Identity(child.Id)!.Value, binding.Descendants);
            var member = LdPlayer.Identity(child.Id)!.Value;
            var childTarget = new TargetInstance($"pid:{member.Pid}:{member.CreatedFileTime}", "登入後的遊戲", "Process", ProcessCatalog.ImagePath(member.Pid), -1, true, [member]);
            var windows = WindowInventory.List(snapshot with { Targets = [.. snapshot.Targets, childTarget] });
            var window = Assert.Single(windows, item => item.Target.Key == key);
            Assert.DoesNotContain(windows, item => item.Target.Key == childTarget.Key);
            Assert.Equal(key, window.Target.Key); Assert.Contains(member, window.Target.Processes); Assert.True(window.Target.Running);
            var diagnostics = Json.Read<DiagnosticBundle>((await service.Dispatch(Command.Create("diagnose", new { }), default)).Data);
            var diagnosticTree = Assert.Single(diagnostics.Targets, t => t.Key == key);
            Assert.Contains(diagnosticTree.Processes, p => p.Identity == root && !p.Running);
            Assert.Contains(diagnosticTree.Processes, p => p.Identity == member && p.Running && p.Path.EndsWith("ping.exe", StringComparison.OrdinalIgnoreCase));
            var rebind = await service.Dispatch(Command.Create("bindBatch", new BatchBindRequest([new(window.Target, proxy.Id, true, true)])), default);
            Assert.False(rebind.Ok); Assert.Contains("WinDivert", rebind.Error); // Identity validation accepts the continuation; the isolated harness has no driver.
            // No running WinDivert is needed: a paused/unchecked binding remains quarantined after handoff.
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)LdPlayer.Identity(child.Id)!.Value.CreatedFileTime, 0, out _));
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); Stop(child); Stop(launcher); Directory.Delete(directory, true); }
    }
    [Fact] public async Task FirstChildRouteAfterParentExitStillInheritsBeforeServiceRefresh()
    {
        using var launcher = StartLauncher(); Process? child = null;
        try {
            var root = LdPlayer.Identity(launcher.Id)!.Value;
            Assert.Equal(1, NativeEngine.Core_SetProxy(902, "127.0.0.1", 1080, "", ""));
            NativeEngine.Rule rule = new() { Pid = (uint)root.Pid, Created = (ulong)root.CreatedFileTime, Proxy = 902, Reserved = 1 };
            Assert.Equal(1, NativeEngine.Core_ReplacePolicy([rule], 1, "\0\0"));
            child = await SpawnAndExit(launcher); var identity = LdPlayer.Identity(child.Id)!.Value;
            Assert.Null(LdPlayer.Identity(root.Pid));
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out uint proxy)); Assert.Equal(902u, proxy);
            rule.Blocked = 1; NativeEngine.Core_ReplacePolicy([rule], 1, "\0\0");
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); Stop(child); Stop(launcher); }
    }
    [Fact] public async Task ShortLivedIntermediateAndRootCanBothExitBeforeTheFirstChildPacket()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var launcher = StartLauncher(); Process? child = null;
        try {
            var before = ProcessCatalog.List("ping.exe").SelectMany(t => t.Processes).ToHashSet();
            var root = LdPlayer.Identity(launcher.Id)!.Value;
            NativeEngine.Core_SetProxy(903, "127.0.0.1", 1080, "", "");
            NativeEngine.Rule rule = new() { Pid = (uint)root.Pid, Created = (ulong)root.CreatedFileTime, Proxy = 903, Reserved = 1 };
            Assert.Equal(1, NativeEngine.Core_ReplacePolicy([rule], 1, "\0\0"));
            Assert.Equal(2, NativeEngine.Core_ProcessHandoffMode((uint)root.Pid, (ulong)root.CreatedFileTime));
            string helper = Path.Combine(directory, "short-helper.cmd");
            await File.WriteAllTextAsync(helper, "@echo off\r\nstart \"\" /b ping.exe -n 60 127.0.0.1 >nul\r\n");
            await launcher.StandardInput.WriteLineAsync($"cmd.exe /d /c \"\"{helper}\"\"");
            await launcher.StandardInput.WriteLineAsync("exit"); await launcher.StandardInput.FlushAsync();
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var identity = Assert.Single(ProcessCatalog.List("ping.exe").SelectMany(t => t.Processes), p => !before.Contains(p));
            child = Process.GetProcessById(identity.Pid);
            int helperPid = ProcessCatalog.Parents()[identity.Pid];
            Assert.NotEqual(root.Pid, helperPid); Assert.Null(LdPlayer.Identity(helperPid));
            // Neither a periodic snapshot nor a parent process handle has ever seen the intermediate.
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime, 0, out uint proxy)); Assert.Equal(903u, proxy);
            var descendants = new NativeEngine.Rule[16];
            uint count = NativeEngine.Core_GetDescendants((uint)root.Pid, (ulong)root.CreatedFileTime, descendants, 16);
            Assert.Contains(descendants.Take((int)count), r => r.Pid == identity.Pid && r.Created == (ulong)identity.CreatedFileTime);
            NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime, 0, out _));
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); Stop(child); Stop(launcher); Directory.Delete(directory, true); }
    }
    [Fact] public async Task TwoIdenticalLaunchersKeepSeparateIpsThroughTheirHandoffs()
    {
        using var first = StartLauncher(); using var second = StartLauncher(); Process? firstChild = null, secondChild = null;
        try {
            var one = LdPlayer.Identity(first.Id)!.Value; var two = LdPlayer.Identity(second.Id)!.Value;
            NativeEngine.Core_SetProxy(904, "127.0.0.1", 1080, "", ""); NativeEngine.Core_SetProxy(905, "127.0.0.1", 1081, "", "");
            NativeEngine.Rule[] rules = [new() { Pid = (uint)one.Pid, Created = (ulong)one.CreatedFileTime, Proxy = 904, Reserved = 1 },
                new() { Pid = (uint)two.Pid, Created = (ulong)two.CreatedFileTime, Proxy = 905, Reserved = 1 }];
            Assert.Equal(1, NativeEngine.Core_ReplacePolicy(rules, 2, "\0\0"));
            firstChild = await SpawnAndExit(first); secondChild = await SpawnAndExit(second);
            var oneChild = LdPlayer.Identity(firstChild.Id)!.Value; var twoChild = LdPlayer.Identity(secondChild.Id)!.Value;
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)oneChild.Pid, (ulong)oneChild.CreatedFileTime, 0, out uint firstProxy)); Assert.Equal(904u, firstProxy);
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)twoChild.Pid, (ulong)twoChild.CreatedFileTime, 0, out uint secondProxy)); Assert.Equal(905u, secondProxy);
            rules[0].Blocked = 1; NativeEngine.Core_ReplacePolicy(rules, 2, "\0\0");
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)oneChild.Pid, (ulong)oneChild.CreatedFileTime, 0, out _));
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)twoChild.Pid, (ulong)twoChild.CreatedFileTime, 0, out secondProxy)); Assert.Equal(905u, secondProxy);
            rules[0].Blocked = 0; NativeEngine.Core_ReplacePolicy(rules, 2, "\0\0");
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)oneChild.Pid, (ulong)oneChild.CreatedFileTime, 0, out firstProxy)); Assert.Equal(904u, firstProxy);
            // A recycled PID must never inherit a captured child's proxy.
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)oneChild.Pid, (ulong)oneChild.CreatedFileTime + 1, 0, out _));
            NativeEngine.Core_ReplacePolicy([rules[1]], 1, "\0\0");
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)oneChild.Pid, (ulong)oneChild.CreatedFileTime, 0, out _));
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)twoChild.Pid, (ulong)twoChild.CreatedFileTime, 0, out secondProxy)); Assert.Equal(905u, secondProxy);
            Assert.False(firstChild.HasExited); Assert.False(secondChild.HasExited);
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); Stop(firstChild); Stop(secondChild); Stop(first); Stop(second); }
    }
    [Fact] public async Task UnrelatedProcessCacheCannotHideANewRuleOrANewChild()
    {
        using var first = StartLauncher(); using var second = StartLauncher(); Process? child = null;
        try {
            var one = LdPlayer.Identity(first.Id)!.Value; var two = LdPlayer.Identity(second.Id)!.Value;
            NativeEngine.Core_SetProxy(906, "127.0.0.1", 1080, "", "");
            NativeEngine.Rule rule = new() { Pid = (uint)one.Pid, Created = (ulong)one.CreatedFileTime, Proxy = 906, Reserved = 1 };
            NativeEngine.Core_ReplacePolicy([rule], 1, "\0\0");
            for (int i = 0; i < 20; i++) Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)two.Pid, (ulong)two.CreatedFileTime, 0, out _));
            rule.Pid = (uint)two.Pid; rule.Created = (ulong)two.CreatedFileTime;
            NativeEngine.Core_ReplacePolicy([rule], 1, "\0\0");
            child = await SpawnAndExit(second); var identity = LdPlayer.Identity(child.Id)!.Value;
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime + 1, 0, out _));
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime, 0, out uint proxy)); Assert.Equal(906u, proxy);
            NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime, 0, out _));
            // Cache an unrelated living child under another followed tree, then attach that child's own tree.
            rule.Pid = (uint)one.Pid; rule.Created = (ulong)one.CreatedFileTime;
            NativeEngine.Core_ReplacePolicy([rule], 1, "\0\0");
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime, 0, out _));
            rule.Pid = (uint)identity.Pid; rule.Created = (ulong)identity.CreatedFileTime;
            NativeEngine.Core_ReplacePolicy([rule], 1, "\0\0");
            Assert.Equal(1, NativeEngine.Core_RouteForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime, 0, out proxy)); Assert.Equal(906u, proxy);
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); Stop(child); Stop(first); Stop(second); }
    }
    private static Process StartLauncher() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) {
        Arguments = "/d /q", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
    })!;
    private static async Task<Process> SpawnAndExit(Process launcher)
    {
        await launcher.StandardInput.WriteLineAsync("start \"\" /b ping.exe -n 60 127.0.0.1 >nul");
        await launcher.StandardInput.FlushAsync();
        Process? child = null;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5)) {
            int pid = ProcessCatalog.Parents().Where(p => p.Value == launcher.Id && ProcessCatalog.ImagePath(p.Key).EndsWith("\\ping.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).FirstOrDefault();
            if (pid != 0) { child = Process.GetProcessById(pid); break; }
            await Task.Delay(20);
        }
        Assert.NotNull(child);
        await launcher.StandardInput.WriteLineAsync("exit"); await launcher.StandardInput.FlushAsync();
        await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return child;
    }
    private static void Stop(Process? p) { if (p is null) return; if (!p.HasExited) p.Kill(); p.WaitForExit(); p.Dispose(); }
}

using System.Security.Principal;
using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class FixedEmulatorTests
{
    private static readonly string Console = @"C:\Emulators\LD\ldconsole.exe";
    private static EmulatorAssignment Slot(int index, Guid proxy, string kind = "LDPlayer", string? path = null) =>
        new(kind, path ?? Console, index, $"窗口 {index}", proxy);
    private static TargetInstance Target(EmulatorAssignment slot, int pid) =>
        new(slot.TargetKey, slot.Name, slot.Kind, slot.ConsolePath, slot.Index, true, [new(pid, pid * 100L)]);

    [Fact] public void LaunchOrderAndMissingWindowsNeverChangeSpecifiedProxy()
    {
        ProxyProfile[] pool = [new(), new(), new()];
        var slots = Enumerable.Range(1, 3).Select(i => Slot(i, pool[i - 1].Id)).ToArray();
        var rule = new AutoProxyRule { Enabled = true, Kind = "Emulator", EmulatorAssignments = slots };
        var first = FixedEmulatorPlanner.Prepare(rule, new(), [], pool, [Target(slots[2], 30)]);
        Assert.Equal(3, first.Count);
        Assert.All(first, b => Assert.Equal(pool[b.Index - 1].Id, b.ProxyId));
        Assert.Empty(first.Single(b => b.Index == 1).Processes);
        var restarted = FixedEmulatorPlanner.Prepare(rule, rule, first, pool, [Target(slots[2], 300), Target(slots[0], 100), Target(slots[1], 200)]);
        Assert.All(restarted, b => Assert.Equal(pool[b.Index - 1].Id, b.ProxyId));
        Assert.Equal(300, restarted.Single(b => b.Index == 3).Processes[0].Pid);
    }
    [Fact] public void ExplicitSharingOverridesQuickFillLimitAndDifferentVendorsDoNotCollide()
    {
        ProxyProfile[] pool = [new(), new()];
        var rule = new AutoProxyRule { Kind = "Emulator", PerProxy = 1, EmulatorAssignments = [Slot(1, pool[0].Id), Slot(2, pool[0].Id), Slot(1, pool[1].Id, "MuMu", @"C:\MuMu\shell\MuMuManager.exe")] };
        AutoProxyPlanner.Validate(rule);
        var bindings = FixedEmulatorPlanner.Prepare(rule, new(), [], pool, []);
        Assert.Equal(3, bindings.Count); Assert.Equal(2, bindings.Count(b => b.ProxyId == pool[0].Id)); Assert.All(bindings, b => Assert.True(b.SharedProxy));
        Assert.Equal(3, bindings.Select(b => b.TargetKey).Distinct().Count());
    }
    [Fact] public void DuplicateRowsMissingProxyAndManualExclusiveConflictAreRejected()
    {
        var proxy = new ProxyProfile(); var slot = Slot(1, proxy.Id);
        Assert.Throws<InvalidOperationException>(() => FixedEmulatorPlanner.Validate([slot, slot]));
        var rule = new AutoProxyRule { Kind = "Emulator", EmulatorAssignments = [slot] };
        Assert.Throws<InvalidOperationException>(() => FixedEmulatorPlanner.Prepare(rule, new(), [], [], []));
        InstanceBinding[] existing = [new() { TargetKey = "manual", ProxyId = proxy.Id, SharedProxy = false }];
        Assert.Throws<InvalidOperationException>(() => FixedEmulatorPlanner.Prepare(rule, new(), existing, [proxy], []));
    }
    [Fact] public void RemovedMappingsReleaseOnlyTheirAutomaticBindingsAndPauseIsPreserved()
    {
        var proxy = new ProxyProfile(); var old = new AutoProxyRule { Kind = "Emulator", Enabled = true, EmulatorAssignments = [Slot(1, proxy.Id), Slot(2, proxy.Id)] };
        var bindings = FixedEmulatorPlanner.Prepare(old, new(), [], [proxy], []);
        bindings[0].AutoPaused = true;
        bindings.Add(new() { TargetKey = "manual", ProxyId = proxy.Id, SharedProxy = true });
        var nextRule = old with { EmulatorAssignments = [old.EmulatorAssignments[0]] };
        var next = FixedEmulatorPlanner.Prepare(nextRule, old, bindings, [proxy], []);
        Assert.Equal(2, next.Count); Assert.Contains(next, b => b.TargetKey == "manual");
        Assert.False(next.Single(b => b.TargetKey == old.EmulatorAssignments[0].TargetKey).Enabled);
        Assert.DoesNotContain(next, b => b.Index == 2);
    }
    [Fact] public void TenWindowLimitIncludesExistingManualAssignments()
    {
        var proxy = new ProxyProfile();
        var rule = new AutoProxyRule { Kind = "Emulator", EmulatorAssignments = Enumerable.Range(0, 10).Select(i => Slot(i, proxy.Id)).ToArray() };
        Assert.Throws<InvalidOperationException>(() => FixedEmulatorPlanner.Prepare(rule, new(), [new() { TargetKey = "manual", ProxyId = proxy.Id, SharedProxy = true }], [proxy], []));
    }
    [Fact] public void SwitchingToFixedModePausesPriorAutomaticTargetsOutsideNewMap()
    {
        var proxy = new ProxyProfile(); var previous = new AutoProxyRule { Kind = "Process", Enabled = true, Pattern = "game.exe" };
        var rule = new AutoProxyRule { Kind = "Emulator", Enabled = true, EmulatorAssignments = [Slot(1, proxy.Id)] };
        var old = new InstanceBinding { TargetKey = "game", ProxyId = proxy.Id, SharedProxy = true, AutoManaged = true, Enabled = true };
        var next = FixedEmulatorPlanner.Prepare(rule, previous, [old], [proxy], []);
        var retained = next.Single(b => b.TargetKey == "game"); Assert.False(retained.AutoManaged); Assert.False(retained.Enabled); Assert.True(retained.AutoPaused);
        Assert.True(next.Single(b => b.Index == 1).Enabled);
        Assert.True(old.Enabled); // Planning does not mutate active rules before native validation succeeds.
    }
    [Fact] public void ClearingAllFixedRowsDisablesTheRuleAndKeepsUnrelatedManualTargets()
    {
        var proxy = new ProxyProfile(); var previous = new AutoProxyRule { Kind = "Emulator", Enabled = true, EmulatorAssignments = [Slot(1, proxy.Id)] };
        var bindings = FixedEmulatorPlanner.Prepare(previous, new(), [], [proxy], []);
        bindings.Add(new() { TargetKey = "manual", ProxyId = proxy.Id, SharedProxy = true });
        var clear = previous with { Enabled = false, EmulatorAssignments = [] }; AutoProxyPlanner.Validate(clear);
        Assert.Equal("manual", Assert.Single(FixedEmulatorPlanner.Prepare(clear, previous, bindings, [proxy], [])).TargetKey);
    }
    [Fact] public void MuMuUsesHeadlessPidAndOriginalIndexInEverySupportedJsonShape()
    {
        const string info = """{"index":"3","name":"MuMu 3","pid":111,"headless_pid":222,"is_process_started":true,"error_code":0}""";
        foreach (var json in new[] { info, "[" + info + "]", "{\"3\":" + info + "}", "{\"players\":[" + info + "]}" }) {
            var target = Assert.Single(EmulatorCatalog.ParseMuMu(json, @"C:\MuMu\nx_main\MuMuManager.exe", pid => new(pid, 100)));
            Assert.Equal(3, target.Index); Assert.Equal(222, Assert.Single(target.Processes).Pid); Assert.Equal("MuMu", target.Kind);
        }
    }
    [Fact] public void MuMuNeverUsesGuiPidAndRejectsAmbiguousIndicesOrErrorResponses()
    {
        const string stopped = """{"index":0,"pid":111,"is_process_started":false}""";
        var target = Assert.Single(EmulatorCatalog.ParseMuMu(stopped, @"C:\MuMu\MuMuManager.exe", pid => new(pid, 100)));
        Assert.False(target.Running); Assert.Empty(target.Processes);
        Assert.Throws<InvalidDataException>(() => EmulatorCatalog.ParseMuMu("[" + stopped + "," + stopped + "]", @"C:\MuMu\MuMuManager.exe", _ => null));
        Assert.Throws<InvalidDataException>(() => EmulatorCatalog.ParseMuMu("{\"error_code\":1}", @"C:\MuMu\MuMuManager.exe", _ => null));
    }
    [Fact] public void TimersSurviveUiRefreshAndResetOnPauseRestartOrProxyChange()
    {
        var clock = DateTimeOffset.UtcNow; var tracker = new ProxySessionTracker(); var proxy = Guid.NewGuid();
        var state = new ProxySessionState("window1", proxy, [new(10, 100)], true);
        tracker.Synchronize([state], clock); tracker.Synchronize([state], clock.AddHours(1)); Assert.Equal(clock, tracker.ConnectedAt("window1"));
        Assert.Equal("01:00:00", ProxySessionTracker.Duration(tracker.ConnectedAt("window1"), clock.AddHours(1)));
        tracker.Synchronize([state with { Active = false }], clock.AddHours(2)); Assert.Null(tracker.ConnectedAt("window1"));
        tracker.Synchronize([state], clock.AddHours(3)); Assert.Equal(clock.AddHours(3), tracker.ConnectedAt("window1"));
        tracker.Synchronize([state with { Processes = [new(10, 200)] }], clock.AddHours(4)); Assert.Equal(clock.AddHours(4), tracker.ConnectedAt("window1"));
        tracker.Synchronize([state with { ProxyId = Guid.NewGuid() }], clock.AddHours(5)); Assert.Equal(clock.AddHours(5), tracker.ConnectedAt("window1"));
        tracker.Synchronize([], clock.AddHours(6)); Assert.Null(tracker.ConnectedAt("window1"));
        Assert.Equal("—", ProxySessionTracker.Duration(null, clock)); Assert.Equal("1 天 01:00:00", ProxySessionTracker.Duration(clock, clock.AddHours(25)));
    }
    [Fact] public async Task FixedReservationsPersistWhilePausedWithoutStartingDriver()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var profile = new ProxyProfile { Host = "127.0.0.1", Port = 1080 }; new ConfigStore(directory).Save(new() { Proxies = [profile] });
        var rule = new AutoProxyRule { Kind = "Emulator", Enabled = false, EmulatorAssignments = [Slot(1, profile.Id), Slot(2, profile.Id)] };
        try {
            using (var controller = new Controller(directory)) {
                var reply = await controller.Dispatch(Command.Create("setAutoProxy", rule), default); Assert.True(reply.Ok, reply.Error);
                var snapshot = Json.Read<Snapshot>((await controller.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.True(snapshot.SupportsFixedEmulators); Assert.Equal(2, snapshot.Bindings.Length); Assert.False(snapshot.Diagnostics.EngineRunning);
                Assert.All(snapshot.Statuses, s => Assert.Null(s.ConnectedAt));
            }
            var restored = new ConfigStore(directory).Load(); Assert.Equal(2, restored.AutoProxy.EmulatorAssignments.Length); Assert.All(restored.Bindings, b => Assert.True(b.AutoManaged));
        } finally { Directory.Delete(directory, true); }
    }
}

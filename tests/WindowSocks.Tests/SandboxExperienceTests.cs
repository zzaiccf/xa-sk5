using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxExperienceTests
{
    [Fact] public void RollbackInputExcludesInheritedGlobalAndOtherSandboxSettings()
    {
        string[] lines = ["[GlobalSettings]", "OpenPipePath=global", "[Xa_test]", "OpenPipePath=first", "OpenPipePath=second=value", "; ignored=comment", "[Other]", "OpenPipePath=other"];
        var own = SandboxIniSettings.Read(lines, "xa_TEST");
        Assert.Single(own); Assert.Equal(new[] { "first", "second=value" }, own["OpenPipePath"]);
        Assert.Empty(SandboxIniSettings.Read(lines, "new"));
    }
    [Fact] public void CompatibilityIsStableDistinctAndPreservesLegacySettings()
    {
        var a = SandboxInstances.Create("Game A", @"C:\Games\Launcher.exe", Guid.NewGuid());
        var b = SandboxInstances.Create("Game B", a.SourceLauncherPath, Guid.NewGuid());
        Assert.DoesNotContain("DiskSerialNumber", SandboxCompatibility.Settings(a).Keys);
        Assert.Equal("n", SandboxCompatibility.Settings(a with { IsolateDeviceIdentity = false })["HideNetworkAdapterMAC"][0]);
        var sa = SandboxCompatibility.Settings(a with { IsolateDeviceIdentity = true });
        var sb = SandboxCompatibility.Settings(b with { IsolateDeviceIdentity = true });
        Assert.NotEqual(sa["DiskSerialNumber"], sb["DiskSerialNumber"]);
        Assert.Empty(sa["NetworkAdapterMAC"].Intersect(sb["NetworkAdapterMAC"]));
        Assert.Equal(32, sa["NetworkAdapterMAC"].Distinct().Count());
        Assert.Equal(sa["NetworkAdapterMAC"], SandboxCompatibility.Settings(a with { IsolateDeviceIdentity = true })["NetworkAdapterMAC"]);
    }
    [Fact] public void ImportRecognizesClientButDoesNotRedirectUnrelatedGames()
    {
        string folder = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            string client = Path.Combine(folder, "gameclient.exe"), launcher = Path.Combine(folder, "Launcher.exe");
            File.WriteAllText(client, "fixture"); File.WriteAllText(launcher, "fixture");
            Assert.Equal(client, SandboxCompatibility.PreferLauncher(client));
            File.WriteAllText(Path.Combine(folder, "YongOrigin.EXE"), "fixture");
            Assert.Equal(launcher, SandboxCompatibility.PreferLauncher(client));
            Assert.True(SandboxCompatibility.IsYongOrigin(launcher));
            Assert.Equal(Path.Combine(folder, "Other.exe"), SandboxCompatibility.PreferLauncher(Path.Combine(folder, "Other.exe")));
        } finally { Directory.Delete(folder, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedOrCancelledConfigurationRestoresOnlyTouchedKeys(bool cancel)
    {
        var values = new Dictionary<string, string[]> { ["a"] = ["old"], ["b"] = [], ["unrelated"] = ["keep"] };
        using var ct = new CancellationTokenSource(); int writes = 0, reloads = 0;
        var run = SandboxSettingsTransaction.Apply(new() { ["a"] = ["new"], ["b"] = ["new"] },
            (key, _) => Task.FromResult(values[key]), (key, value, token) => {
                values[key] = value; writes++;
                if (writes == 2) { if (cancel) { ct.Cancel(); token.ThrowIfCancellationRequested(); } else throw new IOException("synthetic write error"); }
                return Task.CompletedTask;
            }, _ => { reloads++; return Task.CompletedTask; }, ct.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.Equal(new[] { "old" }, values["a"]); Assert.Empty(values["b"]); Assert.Equal("keep", values["unrelated"][0]);
        Assert.Equal(4, writes); Assert.Equal(1, reloads);
    }
    [Fact] public async Task ReloadFailureRestoresAndUnchangedSettingsDoNotReload()
    {
        string[] value = ["old"]; int reloads = 0;
        Task<string[]> Read(string _, CancellationToken ct) => Task.FromResult(value);
        Task Write(string _, string[] next, CancellationToken ct) { value = next; return Task.CompletedTask; }
        Task Reload(CancellationToken ct) { if (++reloads == 1) throw new IOException("reload"); return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => SandboxSettingsTransaction.Apply(new() { ["a"] = ["new"] }, Read, Write, Reload, default));
        Assert.Equal(new[] { "old" }, value); Assert.Equal(2, reloads);
        await SandboxSettingsTransaction.Apply(new() { ["a"] = ["old"] }, Read, Write, Reload, default);
        Assert.Equal(2, reloads);
    }
    [Fact] public async Task QueuePreservesOrderSpacingAndContinuesAfterOneFailure()
    {
        var queue = new SandboxLaunchQueue(); Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var starts = new List<Guid>(); int seconds = 0;
        var results = await queue.RunAsync([a, a, b, c], 10, (id, _) => {
            starts.Add(id); if (id == b) throw new IOException("synthetic failure"); return Task.CompletedTask;
        }, null, default, (duration, _) => { seconds += (int)duration.TotalSeconds; return Task.CompletedTask; });
        Assert.Equal(new[] { a, b, c }, starts); Assert.Equal(20, seconds);
        Assert.Equal(new[] { true, false, true }, results.Select(r => r.Success)); Assert.False(queue.IsRunning);
    }
    [Fact] public async Task QueueRejectsConcurrentStartAndCancellationLeavesStartedInstanceAlone()
    {
        var queue = new SandboxLaunchQueue(); using var ct = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int starts = 0;
        var run = queue.RunAsync([Guid.NewGuid(), Guid.NewGuid()], 10, (_, _) => { starts++; return Task.CompletedTask; }, null, ct.Token,
            async (_, token) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        await waiting.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync([Guid.NewGuid()], 10, (_, _) => Task.CompletedTask, null, default));
        ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, starts); Assert.False(queue.IsRunning);
    }
    [Fact] public async Task TimedOutDiscoverySharesWorkerAndDoesNotBlockSnapshots()
    {
        using var release = new ManualResetEventSlim(); int calls = 0;
        var query = new SingleFlightQuery<int>(() => { Interlocked.Increment(ref calls); release.Wait(); return 42; });
        try {
            await Assert.ThrowsAsync<TimeoutException>(() => query.ReadAsync(TimeSpan.FromMilliseconds(100), default));
            var second = query.ReadAsync(TimeSpan.FromSeconds(5), default);
            Assert.Equal(1, calls); release.Set(); Assert.Equal(42, await second);
        } finally { release.Set(); }
        string folder = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        using var entered = new ManualResetEventSlim(); release.Reset();
        try {
            using var controller = new Controller(folder, discoverVmware: () => { entered.Set(); release.Wait(); return []; });
            var discover = controller.Dispatch(Command.Create("discoverVmware", new { }), default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var snapshot = await controller.Dispatch(Command.Create("snapshot", new { }), default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(snapshot.Ok); Assert.True(Json.Read<Snapshot>(snapshot.Data).SupportsSandboxExperience);
            release.Set(); Assert.True((await discover).Ok);
        } finally { release.Set(); Directory.Delete(folder, true); }
    }
}

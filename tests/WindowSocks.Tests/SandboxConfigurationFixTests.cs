using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxConfigurationFixTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
    private SandboxInstanceProfile Profile()
    {
        var p = SandboxInstances.Create("Synthetic A", @"C:\Synthetic\Launcher.exe", Guid.NewGuid());
        string folder = Path.Combine(root, p.Id.ToString("N")); Directory.CreateDirectory(folder);
        return p with { WorkingDirectory = Path.Combine(folder, "Game"), LauncherPath = Path.Combine(folder, "Game", "Launcher.exe"), FileRoot = Path.Combine(folder, "Box") };
    }
    [Fact] public async Task MissingIniIsCreatedByOfficialCommandBoundaryAndVerified()
    {
        var p = Profile(); string ini = Path.Combine(root, "custom.ini"), initial = Path.Combine(root, "missing-initial.ini"); int writes = 0, resolutions = 0;
        var values = new Dictionary<string, string[]>();
        Task<string> Command(string exe, IEnumerable<string> input, CancellationToken ct) {
            string[] args = input.ToArray();
            if (exe == "Start.exe") return Task.FromResult("");
            string key = args[2];
            if (args[0] == "query") return Task.FromResult(string.Join("\n", values.GetValueOrDefault(key, [])));
            writes++; values[key] = args.Length == 3 ? [] : args[0] == "append" ? [..values.GetValueOrDefault(key, []), args[3]] : [args[3]];
            File.WriteAllLines(ini, new[] { "[" + p.BoxName + "]" }.Concat(values.SelectMany(k => k.Value.Select(v => k.Key + "=" + v))));
            return Task.FromResult("");
        }
        await SandboxieRuntime.Configure(p, default, null, () => { resolutions++; return writes == 0 ? initial : ini; }, Command);
        Assert.True(resolutions > 1); Assert.False(File.Exists(initial));
        Assert.True(writes > 0); SandboxieRuntime.ValidatePersistedSettings(p, ini);
        Assert.Contains("無既有設定可備份", string.Join("\n", Directory.GetFiles(Path.Combine(Path.GetDirectoryName(p.WorkingDirectory)!, "ConfigBackups")).Select(File.ReadAllText)));
        int before = writes; await SandboxieRuntime.Configure(p, default, null, () => ini, Command); Assert.Equal(before, writes);
    }
    [Fact] public void SharedReaderDoesNotDenyConcurrentOfficialWriterOrRename()
    {
        Directory.CreateDirectory(root); string ini = Path.Combine(root, "Sandboxie.ini"); File.WriteAllText(ini, "[Other]\nKeep=y");
        using var reader = SandboxIniFile.Open(ini);
        using var writer = new FileStream(ini, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        string renamed = ini + ".moved"; File.Move(ini, renamed); Assert.True(File.Exists(renamed));
    }
    [Fact] public async Task MissingConfigurationRemainsPreparableAndDoesNotReinstall()
    {
        Directory.CreateDirectory(root);
        var state = SandboxieInstaller.InspectConfiguration(() => true, () => Path.Combine(root, "not-created.ini"));
        Assert.False(state.Ready); Assert.True(state.CanPrepare); Assert.False(state.CanInstall);
        var setup = new SandboxieInstaller(() => state, _ => throw new Exception("must not install"), _ => throw new Exception("must not download"), SandboxieInstaller.Package, null);
        await setup.EnsureInstalled(root, null, default);
        Assert.False(SandboxieInstaller.InspectConfiguration(() => false, () => throw new Exception("service not ready")).CanPrepare);
        Assert.False(SandboxieInstaller.InspectConfiguration(() => true, () => throw new UnauthorizedAccessException("denied")).CanPrepare);
        Assert.False(SandboxieInstaller.InspectConfiguration(() => true, () => throw new FileNotFoundException("helper missing")).CanPrepare);
        Assert.False(SandboxieInstaller.InspectConfiguration(() => true, () => Path.Combine(root, "missing-parent", "Sandboxie.ini")).CanPrepare);
    }
    [Theory]
    [InlineData("H", @"C:\SyntheticInstall\Sandboxie.ini")]
    [InlineData("W", @"C:\SyntheticWindows\Sandboxie.ini")]
    [InlineData(@"\??\D:\Custom\Sandboxie.ini", @"D:\Custom\Sandboxie.ini")]
    public void EffectiveDriverLocationSupportsRegistryCustomPathAndFallback(string location, string expected)
        => Assert.Equal(expected, SandboxConfiguration.ResolveLocation(location, @"C:\SyntheticInstall", @"C:\SyntheticWindows"));
    [Fact] public async Task RetryIsBoundedAndPermissionAndPathFailuresAreImmediate()
    {
        int calls = 0;
        Assert.Equal(3, await SandboxConfiguration.Retry(() => ++calls < 3 ? throw new IOException("sharing", unchecked((int)0x80070020)) : Task.FromResult(calls), default));
        calls = 0; await Assert.ThrowsAsync<IOException>(() => SandboxConfiguration.Retry(() => { calls++; return Task.FromException(new SandboxConfigurationPendingException("unstable")); }, default));
        Assert.Equal(4, calls);
        calls = 0; await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SandboxConfiguration.Retry(() => { calls++; return Task.FromException(new UnauthorizedAccessException()); }, default)); Assert.Equal(1, calls);
        calls = 0; await Assert.ThrowsAsync<DirectoryNotFoundException>(() => SandboxConfiguration.Retry(() => { calls++; return Task.FromException(new DirectoryNotFoundException()); }, default)); Assert.Equal(1, calls);
    }
    [Fact] public async Task PrepareAndRemoveShareGateWithoutNestedDeadlockAndReleaseOnFailure()
    {
        var p = Profile(); string ini = Path.Combine(root, "Sandboxie.ini");
        File.WriteAllText(ini, "[" + p.BoxName + "]\nFileRootPath=" + p.FileRoot + "\nEnabled=y");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); bool removalEntered = false;
        var preparing = SandboxieRuntime.Configure(p, default, null, () => ini, async (_, _, _) => { entered.TrySetResult(); await release.Task; throw new UnauthorizedAccessException("synthetic denied"); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var removal = new SandboxBoxRemoval(_ => { removalEntered = true; return Task.FromResult(Array.Empty<string>()); }, (_, args, _) => {
            Assert.Equal(new[] { "query", "*" }, args); return Task.FromResult("GlobalSettings\n");
        }, _ => { });
        var removing = removal.Remove(p, default);
        Assert.False(removalEntered);
        release.SetResult(); await Assert.ThrowsAnyAsync<IOException>(() => preparing);
        await removing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(removalEntered);
        await SandboxConfiguration.Run(() => SandboxConfiguration.Run(() => Task.CompletedTask, default), default).WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact] public void TimelineContainsKeysAndExitCodesWithoutValues()
    {
        Directory.CreateDirectory(root);
        using (SandboxCommandTimeline.Begin(root)) SandboxCommandTimeline.Record(DateTimeOffset.UtcNow, "SbieIni.exe", ["set", "private-box", "FileRootPath", "private-value"], 5, "private-box private-value token=sensitive");
        string log = File.ReadAllText(Assert.Single(Directory.GetFiles(root)));
        Assert.Contains("FileRootPath", log); Assert.Contains("exitCode", log); Assert.DoesNotContain("private-value", log); Assert.DoesNotContain("private-box", log); Assert.DoesNotContain("sensitive", log);
    }
    [Fact] public async Task ResidentMismatchReloadsWithoutRewritingUnchangedKeys()
    {
        var p = Profile(); string ini = Path.Combine(root, "Sandboxie.ini");
        var expected = SandboxCompatibility.Settings(p);
        File.WriteAllLines(ini, new[] { "[" + p.BoxName + "]" }.Concat(expected.SelectMany(pair => pair.Value.Select(v => pair.Key + "=" + v))));
        int reloads = 0, writes = 0;
        Task<string> Tool(string exe, IEnumerable<string> arguments, CancellationToken ct) {
            var args = arguments.ToArray(); if (exe == "Start.exe") { reloads++; return Task.FromResult(""); }
            if (args[0] != "query") { writes++; throw new Exception("unchanged key was written"); }
            return Task.FromResult(reloads == 0 ? "old resident value" : string.Join("\n", expected[args[2]]));
        }
        await SandboxieRuntime.Configure(p, default, null, () => ini, Tool);
        Assert.Equal(1, reloads); Assert.Equal(0, writes);
    }
    [Fact] public async Task InterruptedAppendRetriesWholeKeyWithoutDuplicatesOrChangingOtherSections()
    {
        var p = Profile() with { IsolateDeviceIdentity = true }; string ini = Path.Combine(root, "Sandboxie.ini");
        File.WriteAllText(ini, "[Other]\nKeep=original\n");
        var values = new Dictionary<string, string[]>(); bool conflict = true; int retrySets = 0;
        Task<string> Tool(string exe, IEnumerable<string> arguments, CancellationToken ct) {
            var args = arguments.ToArray(); if (exe == "Start.exe") return Task.FromResult("");
            string key = args[2]; if (args[0] == "query") return Task.FromResult(string.Join("\n", values.GetValueOrDefault(key, [])));
            if (key == "NetworkAdapterMAC" && args[0] == "set") retrySets++;
            values[key] = args.Length == 3 ? [] : args[0] == "append" ? [..values.GetValueOrDefault(key, []), args[3]] : [args[3]];
            File.WriteAllLines(ini, new[] { "[Other]", "Keep=original", "[" + p.BoxName + "]" }.Concat(values.SelectMany(pair => pair.Value.Select(v => pair.Key + "=" + v))));
            if (key == "NetworkAdapterMAC" && args[0] == "append" && conflict) { conflict = false; throw new IOException("synthetic sharing conflict", unchecked((int)0x80070020)); }
            return Task.FromResult("");
        }
        await SandboxieRuntime.Configure(p, default, null, () => ini, Tool);
        Assert.Equal(2, retrySets); Assert.Equal(32, values["NetworkAdapterMAC"].Length); Assert.Equal(32, values["NetworkAdapterMAC"].Distinct().Count());
        Assert.Equal("original", SandboxIniSettings.Read(File.ReadAllLines(ini), "Other")["Keep"][0]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingDiskSectionCannotAuthorizeRemovalWhenResidentBoxStillExists(bool remove)
    {
        var p = Profile(); int writes = 0;
        var box = new SandboxBoxRemoval(_ => Task.FromResult(Array.Empty<string>()), (_, args, _) => {
            if (args[0] != "query") writes++;
            return Task.FromResult(p.BoxName + "\n");
        }, _ => { });
        await Assert.ThrowsAsync<IOException>(() => remove ? box.Remove(p, default) : box.Disable(p, default));
        Assert.Equal(0, writes);
    }
    [Fact] public async Task NestedConfigurationBoundariesDoNotMultiplyExhaustedSharingRetries()
    {
        int calls = 0;
        await Assert.ThrowsAsync<IOException>(() => SandboxConfiguration.Retry(() => SandboxConfiguration.Retry(() => {
            calls++; return Task.FromException(new IOException("synthetic sharing violation", unchecked((int)0x80070020)));
        }, default), default));
        Assert.Equal(4, calls);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

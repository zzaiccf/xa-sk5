using System.Diagnostics;
using System.Text.Json;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxRemovalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-removal-" + Guid.NewGuid().ToString("N"));
    private SandboxStorage Storage => new(Path.Combine(root, "Storage"));
    private SandboxInstanceProfile Create(string name)
    {
        string source = Path.Combine(root, "Original", "Launcher.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!); File.WriteAllText(source, "original");
        var p = SandboxInstances.Create(name, source, Guid.NewGuid());
        string folder = Path.Combine(Storage.Root, p.Id.ToString("N"));
        p = p with { WorkingDirectory = Path.Combine(folder, "Game"), LauncherPath = Path.Combine(folder, "Game", "Launcher.exe"), FileRoot = Path.Combine(folder, "Box") };
        Directory.CreateDirectory(p.WorkingDirectory); Directory.CreateDirectory(p.FileRoot);
        File.WriteAllText(p.LauncherPath, "copied"); File.WriteAllText(Path.Combine(p.FileRoot, "save.dat"), "save");
        SandboxStorage.WriteJson(Path.Combine(folder, "instance.json"), p);
        return p;
    }
    [Fact] public async Task PermanentRemovalIsScopedAndFinishesOnlyAfterSettingsAndFilesAreGone()
    {
        var a = Create("A"); var b = Create("B"); var host = new Host();
        host.OnSettings = () => Assert.False(Directory.Exists(Storage.InstanceRoot(a)));
        host.OnRule = () => { Assert.False(Directory.Exists(Storage.InstanceRoot(a))); Assert.Contains("settings", host.Calls); };
        await new SandboxRemoval(Storage, host).Run(a, true, null, default);
        Assert.Equal(new[] { "reserve", "disable", "settings", "rule" }, host.Calls);
        Assert.Equal("original", File.ReadAllText(a.SourceLauncherPath)); Assert.Equal("copied", File.ReadAllText(b.LauncherPath));
        Assert.Equal(b.Id, Assert.Single(Storage.Scan([], default)).Profile!.Id);
    }
    [Fact] public async Task RetainedDataStaysVisibleAndCanBePermanentlyRemovedLater()
    {
        var a = Create("A"); await new SandboxRemoval(Storage, new Host()).Run(a, false, null, default);
        Assert.True(File.Exists(a.LauncherPath)); var retained = Assert.Single(Storage.Scan([], default));
        Assert.False(retained.Registered); Assert.True(retained.CanRemove); Assert.True(retained.Bytes > 10);
        await new SandboxRemoval(Storage, new Host()).Run(retained.Profile!, true, null, default);
        Assert.Empty(Storage.Scan([], default));
    }
    [Fact] public async Task LockedFileLeavesRetryRecordWithoutFinishingAndRetrySucceeds()
    {
        var a = Create("A"); var host = new Host();
        using (File.Open(a.LauncherPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => new SandboxRemoval(Storage, host).Run(a, true, null, default));
        Assert.DoesNotContain("rule", host.Calls); Assert.DoesNotContain("settings", host.Calls);
        Assert.True(Assert.Single(Storage.Scan([], default)).CanRemove);
        await new SandboxRemoval(Storage, new Host()).Run(a, true, null, default); Assert.Empty(Storage.Scan([], default));
    }
    [Fact] public async Task CancellationAndSettingsFailureKeepDurableRetryEvidence()
    {
        var a = Create("A"); using var cancel = new CancellationTokenSource();
        var host = new Host { OnDisable = () => cancel.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SandboxRemoval(Storage, host).Run(a, true, null, cancel.Token));
        Assert.True(File.Exists(a.LauncherPath)); Assert.DoesNotContain("rule", host.Calls);
        var failure = new Host { OnSettings = () => throw new IOException("synthetic settings failure") };
        await Assert.ThrowsAsync<IOException>(() => new SandboxRemoval(Storage, failure).Run(a, true, null, default));
        Assert.False(Directory.Exists(Storage.InstanceRoot(a))); Assert.DoesNotContain("rule", failure.Calls);
        Assert.Equal(0, Assert.Single(Storage.Scan([], default)).Bytes);
        await new SandboxRemoval(Storage, new Host()).Run(a, true, null, default); Assert.Empty(Storage.Scan([], default));
    }
    [Fact] public void PartialAndLegacyCopiesAreDiscoveredButUnknownOrForeignDirectoriesAreNotRemovable()
    {
        var a = Create("A"); var b = Create("B");
        File.Move(Path.Combine(Storage.InstanceRoot(b), "instance.json"), Path.Combine(Storage.InstanceRoot(b), "prepared.json"));
        Directory.CreateDirectory(Path.Combine(Storage.Root, "unknown"));
        var rows = Storage.Scan([a], default);
        Assert.Equal(3, rows.Length); Assert.True(rows.Single(r => r.Profile?.Id == a.Id).Registered);
        Assert.True(rows.Single(r => r.Profile?.Id == b.Id).CanRemove); Assert.False(rows.Single(r => r.Profile is null).CanRemove);
        var foreign = a with { WorkingDirectory = Path.Combine(root, a.Id.ToString("N"), "Game"), FileRoot = Path.Combine(root, a.Id.ToString("N"), "Box"), LauncherPath = Path.Combine(root, a.Id.ToString("N"), "Game", "Launcher.exe") };
        Assert.False(Storage.Inspect(foreign, false, default).CanRemove);
        Assert.Throws<IOException>(() => Storage.DeleteFiles(foreign, null, default));
        Assert.False(Storage.Inspect(a with { SourceLauncherPath = a.LauncherPath }, true, default).CanRemove);
        Assert.True(File.Exists(a.SourceLauncherPath));
    }
    [Fact] public async Task CancellationDuringFileDeletionKeepsRetryablePartialCopy()
    {
        var a = Create("A"); var host = new Host(); using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(message => { if (message.StartsWith("已清除 ", StringComparison.Ordinal)) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SandboxRemoval(Storage, host).Run(a, true, progress, cancel.Token));
        Assert.DoesNotContain("rule", host.Calls); Assert.Equal(a.Id, Assert.Single(Storage.Scan([], default)).Profile!.Id);
        Assert.Equal("original", File.ReadAllText(a.SourceLauncherPath));
        await new SandboxRemoval(Storage, new Host()).Run(a, true, null, default); Assert.Empty(Storage.Scan([], default));
    }
    [Fact] public void MalformedJournalIsVisibleWithoutHidingValidInstances()
    {
        var a = Create("A"); var records = Path.Combine(Storage.Root, "RemovalRecords"); Directory.CreateDirectory(records);
        File.WriteAllText(Path.Combine(records, "broken.json"), "not-json");
        var rows = Storage.Scan([a], default); Assert.Equal(2, rows.Length);
        Assert.True(rows.Single(r => r.Profile?.Id == a.Id).CanRemove); Assert.False(rows.Single(r => r.Profile is null).CanRemove);
    }
    [Fact] public void PreparationJournalFindsOlderPartialCopyAndForgetDoesNotResurrectIt()
    {
        var a = Create("A"); var b = Create("B"); string drafts = Path.Combine(Storage.Root, "Preparation"); Directory.CreateDirectory(drafts);
        File.Delete(Path.Combine(Storage.InstanceRoot(a), "instance.json"));
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(a.SourceLauncherPath).ToUpperInvariant())));
        SandboxStorage.WriteJson(Path.Combine(drafts, key + ".json"), new[] { a, b });
        Assert.Equal(2, Storage.Scan([], default).Length);
        SandboxPreparation.ForgetDraft(a, drafts);
        var pending = JsonSerializer.Deserialize<SandboxInstanceProfile[]>(File.ReadAllText(Path.Combine(drafts, key + ".json")), Json.Options)!;
        Assert.Equal(b, Assert.Single(pending));
        var resumed = SandboxPreparation.Resume(a.SourceLauncherPath, [b.ProxyId, a.ProxyId], [b], drafts);
        Assert.Equal(b.Id, resumed[0].Id); Assert.DoesNotContain(resumed, p => p.Id == a.Id);
    }
    [Fact] public void JunctionIsRejectedBeforeDeletingAnyFilesAndExternalTargetSurvives()
    {
        var a = Create("A"); string outside = Path.Combine(root, "Outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        string link = Path.Combine(a.FileRoot, "link");
        var command = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", link, outside }) command.ArgumentList.Add(arg);
        using var process = Process.Start(command)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try {
            Assert.False(Storage.Inspect(a, true, default).CanRemove);
            Assert.Throws<IOException>(() => Storage.DeleteFiles(a, null, default));
            Assert.True(File.Exists(a.LauncherPath)); Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        } finally { Directory.Delete(link, false); }
    }
    [Fact] public async Task BoxRemovalUsesExactSectionAndVerifiesPersistedAndActiveState()
    {
        var a = Create("A"); var calls = new List<string>();
        string[] lines = ["[Other]", "Enabled=y", "[" + a.BoxName + "]", "FileRootPath=" + a.FileRoot, "Enabled=y"];
        var box = new SandboxBoxRemoval(_ => Task.FromResult(lines), (exe, args, _) => {
            calls.Add(exe + " " + string.Join(" ", args));
            if (args.SequenceEqual(new[] { "set", a.BoxName, "Enabled", "n" })) lines[^1] = "Enabled=n";
            if (args.SequenceEqual(new[] { "set", a.BoxName, "*" })) lines = lines[..2];
            return Task.FromResult(args[0] == "query" ? args[^1] == "Enabled" ? "n\r\n" : "Other\r\n" : "");
        }, _ => { });
        await box.Disable(a, default); await box.Remove(a, default);
        Assert.Equal(new[] { "[Other]", "Enabled=y" }, lines);
        Assert.Contains("SbieIni.exe set " + a.BoxName + " *", calls); Assert.DoesNotContain(calls, c => c.Contains("delete_sandbox"));
    }
    [Theory] [InlineData("wrong-root")] [InlineData("running")] [InlineData("active-not-disabled")]
    public async Task UnverifiedBoxIsNeverRemoved(string scenario)
    {
        var a = Create("A"); int deletes = 0;
        string[] lines = ["[" + a.BoxName + "]", "FileRootPath=" + (scenario == "wrong-root" ? root : a.FileRoot), "Enabled=y"];
        var box = new SandboxBoxRemoval(_ => Task.FromResult(lines), (_, args, _) => {
            if (args[0] == "set" && args[^1] == "*") deletes++;
            if (args[^1] == "n") lines[^1] = "Enabled=n";
            return Task.FromResult("");
        }, _ => { if (scenario == "running") throw new IOException("running"); });
        await Assert.ThrowsAsync<IOException>(() => box.Disable(a, default)); Assert.Equal(0, deletes); Assert.True(File.Exists(a.LauncherPath));
    }
    private sealed class Host : ISandboxRemovalHost
    {
        public readonly List<string> Calls = []; public Action? OnDisable, OnSettings, OnRule;
        public Task Reserve(SandboxInstanceProfile p, CancellationToken ct) { Calls.Add("reserve"); return Task.CompletedTask; }
        public Task DisableStoppedBox(SandboxInstanceProfile p, CancellationToken ct) { Calls.Add("disable"); OnDisable?.Invoke(); return Task.CompletedTask; }
        public Task RemoveRule(SandboxInstanceProfile p, bool delete, CancellationToken ct) { Calls.Add("rule"); OnRule?.Invoke(); return Task.CompletedTask; }
        public Task RemoveBoxSettings(SandboxInstanceProfile p, CancellationToken ct) { Calls.Add("settings"); OnSettings?.Invoke(); return Task.CompletedTask; }
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

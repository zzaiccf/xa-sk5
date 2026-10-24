using System.IO.Compression;
using System.Text.Json;
using WindowSocks.Core;
using Xunit;
using Worker = WindowSocks.Updater.Program;

namespace WindowSocks.Tests;

public sealed class UpdateWorkerLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-worker-state-" + Guid.NewGuid().ToString("N"));
    public UpdateWorkerLifecycleTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Fact] public void ServiceStopsBeforeDownloadAndResumesAfterSuccess()
    {
        var (job, work) = Prepare(); var service = new FakeService(work, true);
        Assert.Equal(0, Worker.Execute(job, Path.Combine(work, "job.json"), service));
        Assert.True(service.Running); Assert.Equal(1, service.Starts); Assert.Equal(1, service.Stops); Assert.Equal(1, service.ReadinessChecks);
        Assert.Equal("new", File.ReadAllText(Path.Combine(job.Target, "WindowSocks.exe")));
        Assert.Equal("keep-user-settings", File.ReadAllText(Path.Combine(job.Target, "settings.dpapi")));
    }
    [Fact] public void CorruptDownloadRestoresTheOriginalServiceAndLeavesApplicationUntouched()
    {
        var (job, work) = Prepare(); job = job with { Download = job.Download! with { Release = job.Download.Release with { Portable = job.Download.Release.Portable with { Sha256 = new string('A', 64) } } } };
        var service = new FakeService(work, true);
        Assert.Equal(1, Worker.Execute(job, Path.Combine(work, "job.json"), service));
        Assert.True(service.Running); Assert.Equal(1, service.Starts);
        Assert.Equal("old", File.ReadAllText(Path.Combine(job.Target, "WindowSocks.exe")));
    }
    [Fact] public void CancelAfterStoppingServiceRestoresItWithoutStartingTheDownload()
    {
        var (job, work) = Prepare(); var service = new FakeService(work, true) { CancelAfterStop = true };
        Assert.Equal(1, Worker.Execute(job, Path.Combine(work, "job.json"), service));
        Assert.True(service.Running); Assert.Equal(1, service.Starts); Assert.False(File.Exists(Path.Combine(work, "update.zip")));
    }
    [Fact] public void AnInitiallyStoppedServiceRemainsStoppedAfterAnUpdate()
    {
        var (job, work) = Prepare(); var service = new FakeService(work, false);
        Assert.Equal(0, Worker.Execute(job, Path.Combine(work, "job.json"), service));
        Assert.False(service.Running); Assert.Equal(0, service.Starts); Assert.Equal(0, service.ReadinessChecks);
    }
    private (UpdateJob Job, string Work) Prepare()
    {
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"), work = Path.Combine(root, "work");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target); Directory.CreateDirectory(work);
        foreach (string name in new[] { "WindowSocks.dll", "WindowSocks.Core.dll", "WindowSocks.Service.dll", "WindowSocks.Maintenance.dll", "WindowSocks.Updater.dll", "WindowSocks.Tools.dll" }) File.Copy(typeof(AppReleasePackage).Assembly.Location, Path.Combine(source, name));
        foreach (string name in new[] { "WindowSocks.exe", "xa sk5.exe", "WindowSocks.Service.exe", "WindowSocksNative.dll", "WindowSocks.Maintenance.exe", "WindowSocks.Updater.exe" }) File.WriteAllText(Path.Combine(source, name), "new");
        var files = Directory.EnumerateFiles(source).Select(path => new InstallFile(Path.GetFileName(path), AppReleasePackage.Hash(path))).ToArray();
        File.WriteAllText(Path.Combine(source, "install-manifest.json"), JsonSerializer.Serialize(files, Json.Options));
        File.WriteAllText(Path.Combine(target, "WindowSocks.exe"), "old"); File.WriteAllText(Path.Combine(target, "settings.dpapi"), "keep-user-settings");
        string archive = Path.Combine(root, "release.zip"); ZipFile.CreateFromDirectory(source, archive);
        var release = new AppRelease(1, AppReleasePackage.CurrentVersion, DateTimeOffset.UtcNow, "Synthetic", new("release.zip", new FileInfo(archive).Length, AppReleasePackage.Hash(archive)));
        return (new("", target, release.Version, Path.Combine(target, "xa sk5.exe"), 0, 0, false) { Download = new(release, LocalArchive: archive), PauseService = true }, work);
    }
    private sealed class FakeService(string work, bool running) : Worker.IUpdateServiceControl
    {
        public bool Running = running; public int Starts, Stops, ReadinessChecks; public bool CancelAfterStop;
        public void Validate() { }
        public bool IsRunning => Running; public string Version => AppReleasePackage.CurrentVersion;
        public void Stop() {
            if (++Stops == 1) Assert.False(File.Exists(Path.Combine(work, "update.zip")));
            Running = false;
            if (CancelAfterStop) { File.WriteAllText(Path.Combine(work, "cancel"), "cancel"); Thread.Sleep(150); }
        }
        public void Start() { Starts++; Running = true; }
        public Snapshot WaitReady(string version, Action<string> report) { ReadinessChecks++; return new([], [], [], [], [], new(true, true, true, true, version, "", [])); }
    }
}

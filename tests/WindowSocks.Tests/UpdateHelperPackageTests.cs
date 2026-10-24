using System.Text.Json;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class UpdateHelperPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-helper-package-" + Guid.NewGuid().ToString("N"));
    public UpdateHelperPackageTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Fact] public void OnlyTheUpdaterDependencyClosureIsStaged()
    {
        var source = Source(); string destination = Path.Combine(root, "helper");
        int count = UpdateHelperPackage.Stage(source, destination);
        Assert.Equal(5, count); Assert.False(File.Exists(Path.Combine(destination, "UnrelatedDesktop.dll")));
        Assert.True(File.Exists(Path.Combine(destination, "WindowSocks.Updater.exe")));
    }
    [Fact] public void CorruptedSelectedFilesCannotBecomeAnExecutableHelper()
    {
        var source = Source(); File.AppendAllText(Path.Combine(source, "WindowSocks.Updater.exe"), "tampered");
        Assert.Throws<InvalidDataException>(() => UpdateHelperPackage.Stage(source, Path.Combine(root, "helper")));
    }
    [Fact] public void UnsafeManifestPathsAreRejectedBeforeStaging()
    {
        var source = Source(); File.WriteAllText(Path.Combine(source, "install-manifest.json"), JsonSerializer.Serialize(new[] { new InstallFile("../escape.exe", new string('A', 64)) }, Json.Options));
        string destination = Path.Combine(root, "helper");
        Assert.Throws<InvalidDataException>(() => UpdateHelperPackage.Stage(source, destination)); Assert.False(Directory.Exists(destination));
    }
    private string Source()
    {
        string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        foreach (string name in new[] { "WindowSocks.Updater.dll", "WindowSocks.Core.dll", "UnrelatedDesktop.dll" }) File.Copy(typeof(AppReleasePackage).Assembly.Location, Path.Combine(source, name));
        foreach (string name in new[] { "WindowSocks.Updater.exe", "WindowSocks.Updater.deps.json", "WindowSocks.Updater.runtimeconfig.json" }) File.WriteAllText(Path.Combine(source, name), "synthetic");
        var files = Directory.EnumerateFiles(source).Select(p => new InstallFile(Path.GetFileName(p), AppReleasePackage.Hash(p))).ToArray();
        File.WriteAllText(Path.Combine(source, "install-manifest.json"), JsonSerializer.Serialize(files, Json.Options)); return source;
    }
}

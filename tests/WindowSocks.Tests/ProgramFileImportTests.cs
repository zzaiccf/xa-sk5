using System.Runtime.InteropServices;
using WindowSocks.Desktop;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ProgramFileImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-import-" + Guid.NewGuid());
    public ProgramFileImportTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string FileAt(string name) { string path = Path.Combine(root, name); File.WriteAllText(path, "not an executable"); return path; }
    [Theory]
    [InlineData("登入器 with spaces.exe")]
    [InlineData("遊戲本體.BIN")]
    public void DirectGameFileImportsItsFullPathWithoutRunningIt(string name)
    {
        string file = FileAt(name);
        Assert.True(ProgramFileImport.CanImport([file])); Assert.Equal(file, ProgramFileImport.Resolve([file]));
    }
    [Fact] public void ShortcutResolvesTargetWithoutSavingOrExecutingArguments()
    {
        string target = FileAt("遊戲本體.bin"), shortcut = Path.Combine(root, "桌面遊戲.lnk");
        CreateShortcut(shortcut, target); byte[] original = File.ReadAllBytes(shortcut);
        Assert.Equal(target, ProgramFileImport.Resolve([shortcut])); Assert.Equal(original, File.ReadAllBytes(shortcut));
        File.Delete(target); Assert.Throws<InvalidOperationException>(() => ProgramFileImport.Resolve([shortcut]));
    }
    [Theory]
    [InlineData("README.txt")]
    [InlineData("遊戲網址.url")]
    [InlineData("command.cmd")]
    public void UnsupportedFilesAreRejected(string name) => Assert.Throws<InvalidOperationException>(() => ProgramFileImport.Resolve([FileAt(name)]));
    [Fact] public void MissingMultipleAndInvalidShortcutsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => ProgramFileImport.Resolve([]));
        Assert.Throws<InvalidOperationException>(() => ProgramFileImport.Resolve([FileAt("a.exe"), FileAt("b.exe")]));
        Assert.Throws<InvalidOperationException>(() => ProgramFileImport.Resolve([Path.Combine(root, "missing.exe")]));
        Assert.Throws<InvalidOperationException>(() => ProgramFileImport.Resolve([FileAt("invalid.lnk")]));
    }
    private static void CreateShortcut(string path, string target)
    {
        object? shell = null, link = null;
        try {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            link = ((dynamic)shell!).CreateShortcut(path); dynamic value = link;
            value.TargetPath = target; value.Arguments = "--unused-test-argument"; value.Save();
        } finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }
}

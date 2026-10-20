using Microsoft.Win32;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class WindowsStartupTests
{
    private const string Exe = @"C:\My Apps\xa sk5\xa sk5.exe";
    [Fact] public void FirstUseDefaultsOnAndCommandsQuotePathsWithSpaces()
    {
        var store = new MemoryRegistration(); StartupConfiguration.Initialize(store, Exe);
        Assert.True(store.State.Enabled); Assert.Equal("\"" + Exe + "\" --autostart", store.State.Command);
        int writes = store.Writes; StartupConfiguration.Initialize(store, Exe); Assert.Equal(writes, store.Writes);
    }
    [Fact] public void DisabledPreferenceSurvivesRestartMoveAndRepairInitialization()
    {
        var store = new MemoryRegistration { State = new(false, "stale entry") };
        StartupConfiguration.Initialize(store, Exe);
        StartupConfiguration.Initialize(store, @"D:\New Folder\xa sk5.exe");
        Assert.False(store.State.Enabled); Assert.Null(store.State.Command);
        var fresh = new MemoryRegistration(); StartupConfiguration.Initialize(fresh, Exe, defaultEnabled: false);
        Assert.False(fresh.State.Enabled); Assert.Null(fresh.State.Command);
    }
    [Fact] public void EnabledPreferenceUpdatesMovedExecutableAndDisablingNeedsNoValidRunCommand()
    {
        var store = new MemoryRegistration(); StartupConfiguration.Initialize(store, Exe);
        StartupConfiguration.Initialize(store, @"D:\Moved\xa sk5.exe");
        Assert.Contains(@"D:\Moved\xa sk5.exe", store.State.Command);
        StartupConfiguration.Apply(store, new MemoryService(), "unavailable old path", false);
        Assert.False(store.State.Enabled); Assert.Null(store.State.Command);
    }
    [Fact] public void EnableAndDisableSynchronizeTheGuiAndServiceWithoutRunningOrStoppingIt()
    {
        var store = new MemoryRegistration { State = new(true, StartupConfiguration.Command(Exe)) };
        var service = new MemoryService { Mode = new(2, true) };
        StartupConfiguration.Apply(store, service, Exe, false);
        Assert.Equal(new(3), service.Mode); Assert.False(store.State.Enabled); Assert.Null(store.State.Command);
        StartupConfiguration.Apply(store, service, Exe, true);
        Assert.Equal(new(2, true), service.Mode); Assert.True(store.State.Enabled); Assert.Equal(2, service.Writes);
    }
    [Fact] public void PortableOnlyChoicePersistsAndExistingMatchingServiceNeedsNoWrite()
    {
        var store = new MemoryRegistration(); var absent = new MemoryService();
        StartupConfiguration.Apply(store, absent, Exe, true); Assert.Equal(0, absent.Writes);
        var existing = new MemoryService { Mode = new(2, true) };
        StartupConfiguration.Apply(store, existing, Exe, true); Assert.Equal(0, existing.Writes);
    }
    [Fact] public void RegistryFailureRollsBackEvenPartiallyWrittenPreferencesAndServiceMode()
    {
        var original = new StartupRegistrationState(true, StartupConfiguration.Command(Exe));
        var store = new MemoryRegistration { State = original, FailNext = true };
        var service = new MemoryService { Mode = new(2, true) };
        Assert.Throws<IOException>(() => StartupConfiguration.Apply(store, service, Exe, false));
        Assert.Equal(original, store.State); Assert.Equal(new(2, true), service.Mode);
    }
    [Fact] public void ServiceFailureDoesNotCommitGuiChoiceAndRestoresManualOrDisabledMode()
    {
        foreach (int oldMode in new[] { 3, 4 }) {
            var store = new MemoryRegistration { State = new(false, null) };
            var service = new MemoryService { Mode = new(oldMode), FailNext = true };
            Assert.Throws<IOException>(() => StartupConfiguration.Apply(store, service, Exe, true));
            Assert.False(store.State.Enabled); Assert.Null(store.State.Command); Assert.Equal(new(oldMode), service.Mode);
        }
    }
    [Theory]
    [InlineData("xa sk5.exe")] [InlineData(@"C:\Other\malicious.exe")] [InlineData("C:\\Bad\"path\\xa sk5.exe")]
    public void InvalidRunPathsAreRejectedBeforeAnyChanges(string path)
    {
        var store = new MemoryRegistration(); var service = new MemoryService { Mode = new(3) };
        Assert.Throws<InvalidDataException>(() => StartupConfiguration.Apply(store, service, path, true));
        Assert.Equal(0, store.Writes); Assert.Equal(0, service.Writes);
    }
    [Fact] public void RealRegistryRoundtripUsesOnlyAnIsolatedTestKeyAndPreservesUnrelatedValues()
    {
        string rootPath = @"Software\WindowSocksTests\" + Guid.NewGuid().ToString("N");
        try {
            using var root = Registry.CurrentUser.CreateSubKey(rootPath);
            using (var run = root.CreateSubKey("Run")) run.SetValue("Other App", "unrelated");
            var store = new StartupRegistry(root, "Preferences", "Run");
            StartupConfiguration.Initialize(store, Exe); Assert.True(store.Read().Enabled);
            StartupConfiguration.Apply(store, new MemoryService(), Exe, false);
            var reopened = new StartupRegistry(root, "Preferences", "Run"); Assert.False(reopened.Read().Enabled); Assert.Null(reopened.Read().Command);
            StartupConfiguration.Initialize(reopened, Exe); Assert.False(reopened.Read().Enabled);
            using var reopenedRun = root.OpenSubKey("Run"); Assert.Equal("unrelated", reopenedRun!.GetValue("Other App"));
        } finally { Registry.CurrentUser.DeleteSubKeyTree(rootPath, false); }
    }
    [Fact] public void UninstallRemovesOnlyTheRegistrationForThatInstallation()
    {
        string rootPath = @"Software\WindowSocksTests\" + Guid.NewGuid().ToString("N");
        try {
            using var root = Registry.CurrentUser.CreateSubKey(rootPath); var store = new StartupRegistry(root, "Preferences", "Run");
            StartupConfiguration.Initialize(store, Exe); store.RemoveForDirectory(@"D:\Another Copy"); Assert.NotNull(store.Read().Command);
            store.RemoveForDirectory(Path.GetDirectoryName(Exe)!); Assert.Null(store.Read().Command); Assert.True(store.Read().Enabled);
        } finally { Registry.CurrentUser.DeleteSubKeyTree(rootPath, false); }
    }
    private sealed class MemoryRegistration : IStartupRegistration
    {
        public StartupRegistrationState State = new(null, null); public int Writes; public bool FailNext;
        public StartupRegistrationState Read() => State;
        public void Write(StartupRegistrationState state) { Writes++; State = state; if (FailNext) { FailNext = false; throw new IOException("Simulated registry failure"); } }
    }
    private sealed class MemoryService : IStartupService
    {
        public StartupServiceMode? Mode; public int Writes; public bool FailNext;
        public StartupServiceMode? Read() => Mode;
        public void Write(StartupServiceMode mode) { Writes++; Mode = mode; if (FailNext) { FailNext = false; throw new IOException("Simulated service failure"); } }
    }
}

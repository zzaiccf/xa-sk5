using Microsoft.Win32;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class WindowsShortcutsTests
{
    [Fact] public void DefaultShortcutsTrackMovesAndRespectManualRemoval()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        string registry = @"Software\WindowSocks.Tests\" + Guid.NewGuid().ToString("N");
        string desktop = Path.Combine(directory, "Desktop"), programs = Path.Combine(directory, "Programs");
        Directory.CreateDirectory(directory);
        try {
            string first = Path.Combine(directory, "xa sk5.exe"); File.WriteAllBytes(first, []);
            using var user = Registry.CurrentUser.CreateSubKey(registry);
            WindowsShortcuts.Initialize(user, first, desktop, programs);
            string shortcut = Path.Combine(desktop, "xa sk5.lnk"), start = Path.Combine(programs, "xa sk5.lnk");
            Assert.Equal(first, WindowsShortcuts.Target(shortcut)); Assert.Equal(first, WindowsShortcuts.Target(start));
            string moved = Path.Combine(directory, "Moved", "xa sk5.exe"); Directory.CreateDirectory(Path.GetDirectoryName(moved)!); File.WriteAllBytes(moved, []);
            WindowsShortcuts.Initialize(user, moved, desktop, programs); Assert.Equal(moved, WindowsShortcuts.Target(shortcut));
            File.Delete(shortcut); WindowsShortcuts.Initialize(user, moved, desktop, programs);
            Assert.False(File.Exists(shortcut)); Assert.True(File.Exists(start));
        } finally { Registry.CurrentUser.DeleteSubKeyTree(registry, false); Directory.Delete(directory, true); }
    }
}

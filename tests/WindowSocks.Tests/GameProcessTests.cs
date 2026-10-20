using System.Diagnostics;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class GameProcessTests
{
    [Fact] public void BinaryClientMatchesExactUnicodePathWithoutMatchingLauncher()
    {
        var rule = new AutoProxyRule { Enabled = true, Kind = "Process", Match = TargetMatch.ExecutablePath, Pattern = @"D:\遊戲測試\TWClient.bin" };
        AutoProxyPlanner.Validate(rule);
        var game = new TargetInstance("game", "遊戲", "Process", rule.Pattern, -1, true, [new(123, 45)]);
        Assert.True(AutoProxyPlanner.Matches(game, rule));
        Assert.False(AutoProxyPlanner.Matches(game with { Path = @"D:\遊戲測試\Login.exe" }, rule));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { Pattern = @"D:\遊戲測試\*.bin" }));
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(rule with { Pattern = @"D:\遊戲測試\data.txt" }));
    }
    [Fact] public void LimitedProcessQueryFindsImageWithoutReadingModules()
    {
        Assert.Equal(Environment.ProcessPath, ProcessCatalog.ImagePath(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
        Assert.Empty(ProcessCatalog.ImagePath(int.MaxValue));
    }
    [Fact] public async Task SavedFullPathRuleGuardsStartupBeforeDiscoveryAndExplicitOverflowCanBypass()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        new ConfigStore(directory).Save(new() { AutoProxy = new() { Enabled = true, Kind = "Process", Match = TargetMatch.ExecutablePath, Pattern = ping } });
        using var child = Process.Start(new ProcessStartInfo(ping) { Arguments = "-n 30 127.0.0.1", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try {
            using var service = new Controller(directory);
            var identity = LdPlayer.Identity(child.Id)!.Value;
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            var target = new TargetInstance($"pid:{child.Id}:{identity.CreatedFileTime}", "ping", "Process", ping, -1, true, [identity]);
            Assert.True((await service.Dispatch(Command.Create("syncTargets", new[] { target }), default)).Ok);
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            await service.Tick(default); // No proxies: classify the known target as unassigned, preserving the overflow contract.
            Assert.Equal(3, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); if (!child.HasExited) child.Kill(); child.WaitForExit(); Directory.Delete(directory, true); }
    }
}

using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxInstanceTests
{
    [Fact] public void PersistedIdentityRetainsFixedMappingRegardlessOfOrder()
    {
        var a = SandboxInstances.Create("A", @"C:\Games\Launcher.exe", Guid.NewGuid());
        var b = SandboxInstances.Create("B", @"C:\Games\Launcher.exe", Guid.NewGuid());
        SandboxInstances.Validate(a); SandboxInstances.Validate(b);
        var decoded = System.Text.Json.JsonSerializer.Deserialize<SandboxInstanceProfile[]>(System.Text.Json.JsonSerializer.Serialize(new[] { b, a }, Json.Options), Json.Options)!;
        Assert.Equal(a, decoded.Single(p => p.Id == a.Id)); Assert.Equal(b.ProxyId, decoded[0].ProxyId);
        Assert.NotEqual(a.BoxName, b.BoxName); Assert.NotEqual(a.FileRoot, b.FileRoot);
    }
    [Fact] public void MembershipRequiresBoxUserAndCreationTime()
    {
        var a = SandboxInstances.Create("A", @"C:\Games\Launcher.exe", Guid.NewGuid());
        SandboxProcess[] observed = [new(new(10, 100), a.BoxName, "owner", 1), new(new(11, 100), "OtherBox", "owner", 1),
            new(new(12, 100), a.BoxName, "other-user", 1), new(new(13, 100), a.BoxName, "owner", 1)];
        var members = SandboxInstances.VerifiedMembers(a, observed, "owner", pid => new(pid, pid == 13 ? 101 : 100));
        Assert.Equal(new[] { new ProcessIdentity(10, 100) }, members);
    }
    [Fact] public void DirectoryBoundaryCannotIncludeSiblingOrTraversal()
    {
        Assert.True(SandboxInstances.Within(@"C:\Game\bin\Client.exe", @"C:\Game"));
        Assert.False(SandboxInstances.Within(@"C:\Games\Client.exe", @"C:\Game"));
        Assert.False(SandboxInstances.Within(@"C:\Game\..\Other\Client.exe", @"C:\Game"));
        var a = SandboxInstances.Create("A", @"C:\Games\Launcher.exe", Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => SandboxInstances.Validate(a with { BoxName = "DefaultBox" }));
        Assert.Throws<InvalidOperationException>(() => SandboxInstances.Validate(a with { WorkingDirectory = @"C:\Windows", LauncherPath = @"C:\Windows\notepad.exe" }));
        Assert.Throws<InvalidOperationException>(() => SandboxInstances.Validate(a with { LauncherPath = a.SourceLauncherPath }));
        Assert.Throws<InvalidOperationException>(() => SandboxInstances.Validate(a with { Name = "A\nEnabled=n" }));
    }
    [Fact] public void SavedSandboxPreventsDeletingItsLineEvenWhileStopped()
    {
        var a = SandboxInstances.Create("A", @"C:\Games\Launcher.exe", Guid.NewGuid());
        var config = new SavedConfig { SandboxInstances = [a], Proxies = [new() { Id = a.ProxyId }] };
        Assert.Throws<InvalidOperationException>(() => ProxyPoolUsage.RequireUnused(config));
    }
    [Fact] public void LaunchRequiresBothSandboxExitsToMatchTheReservedLine()
    {
        var probe = new ProbeResult(Guid.NewGuid(), true, true, true, true, "192.0.2.1", "192.0.2.1", 1, "", DateTimeOffset.UtcNow);
        var report = new SandboxNetworkReport(Guid.NewGuid(), "192.0.2.1", "192.0.2.1", false);
        Assert.True(SandboxInstances.ExitMatches(report, probe));
        Assert.False(SandboxInstances.ExitMatches(report with { TcpExit = "192.0.2.2" }, probe));
        Assert.False(SandboxInstances.ExitMatches(report with { UdpExit = null }, probe));
        Assert.False(SandboxInstances.ExitMatches(report, probe with { UdpPending = true }));
    }
    [Fact] public void ManagedDirectoryGuardSurvivesRestrictedTokensAndKeepsDirectoryBoundaries()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try {
            var identity = LdPlayer.Identity(child.Id)!.Value;
            NativeEngine.Core_SetOwner("S-1-5-18");
            NativeEngine.Core_ReplacePolicy([], 0, "@dir:" + Environment.SystemDirectory + "-sibling\\\0\0");
            Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            NativeEngine.Core_ReplacePolicy([], 0, "@dir:" + Environment.SystemDirectory + "\\\0\0");
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 1, out _));
        } finally {
            NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
            NativeEngine.Core_SetOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
            if (!child.HasExited) child.Kill();
        }
    }
}

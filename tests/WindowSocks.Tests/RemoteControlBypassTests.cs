using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class RemoteControlBypassTests : IDisposable
{
    private const uint Proxy = 801;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> processes = [];
    public RemoteControlBypassTests()
    {
        Directory.CreateDirectory(directory);
        NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
        Assert.True(NativeEngine.SupportsRemoteControlBypass);
        Assert.Equal(1, NativeEngine.Core_SetProxy(Proxy, "127.0.0.1", 1080, "", ""));
    }
    public void Dispose()
    {
        NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
        foreach (var process in processes) { if (!process.HasExited) process.Kill(); process.WaitForExit(); process.Dispose(); }
        Directory.Delete(directory, true);
    }
    private ProcessIdentity Start(string filename, string subdirectory = "")
    {
        string folder = Path.Combine(directory, subdirectory); Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, filename); File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("-n"); info.ArgumentList.Add("30"); info.ArgumentList.Add("127.0.0.1");
        var process = Process.Start(info)!; processes.Add(process);
        return LdPlayer.Identity(process.Id)!.Value;
    }
    private static int Route(ProcessIdentity identity, bool ipv6 = false, ushort localPort = 443) =>
        NativeEngine.Core_RoutePacketForTest((uint)identity.Pid, (ulong)identity.CreatedFileTime, ipv6 ? 1 : 0, localPort, out _);
    private static int Apply(uint state = 1, bool enabled = true, string? images = null, NativeEngine.Rule[]? rules = null, string guards = "\0\0",
        uint rdpPid = 0, ulong rdpBirth = 0, uint rdpPort = 0)
    {
        rules ??= [];
        return NativeEngine.Core_ReplacePolicyEx2(rules, (uint)rules.Length, guards, Proxy, state, 1, enabled ? 1u : 0u,
            images ?? RemoteControlBypass.NativeImages([]), rdpPid, rdpBirth, rdpPort);
    }
    [Theory]
    [InlineData("AnyDesk.exe")]
    [InlineData("remoting_host.exe")]
    [InlineData("DeskIn.exe")]
    [InlineData("RdViewer.exe")]
    [InlineData("TeamViewer_Service.exe")]
    [InlineData("rustdesk.exe")]
    [InlineData("RDxaomao.exe")]
    [InlineData("RDxaomaoHost.exe")]
    [InlineData("RDxaomaoControl.exe")]
    [InlineData("rdXAOMAOcontrol.EXE")]
    [InlineData("mstsc.exe")]
    public void KnownRemoteProgramsBypassIpv4Ipv6AndQuarantineWithoutPortWildcards(string name)
    {
        var remote = Start(name); var game = Start("Game.exe");
        Assert.Equal(1, Apply()); Assert.Equal(3, Route(remote)); Assert.Equal(3, Route(remote, true, 50000)); Assert.Equal(1, Route(game));
        Assert.Equal(1, Apply(state: 2)); Assert.Equal(3, Route(remote)); Assert.Equal(3, Route(remote, true)); Assert.Equal(2, Route(game));
        Assert.Equal(1, Apply(enabled: false)); Assert.Equal(1, Route(remote)); Assert.Equal(2, Route(remote, true));
        Assert.Equal(1, Apply(state: 0)); Assert.Equal(0, Route(remote));
    }
    [Theory]
    [InlineData("AnyDesk.exe")]
    [InlineData("RDxaomaoHost.exe")]
    [InlineData("RDxaomaoControl.exe")]
    public void ExplicitProxyBlockedAndStartupGuardOverrideRemoteException(string name)
    {
        var remote = Start(name);
        var rule = new NativeEngine.Rule { Pid = (uint)remote.Pid, Created = (ulong)remote.CreatedFileTime, Proxy = Proxy };
        Assert.Equal(1, Apply(rules: [rule])); Assert.Equal(1, Route(remote)); Assert.Equal(2, Route(remote, true));
        Assert.Equal(1, Apply(rules: [rule with { Blocked = 1 }])); Assert.Equal(2, Route(remote));
        Assert.Equal(1, Apply(guards: Path.Combine(directory, name) + "\0\0")); Assert.Equal(2, Route(remote));
        Assert.Equal(1, Apply()); Assert.Equal(3, Route(remote));
    }
    [Fact] public void OtherForkNamesNeedAnExactCustomPathRatherThanAPrefixMatch()
    {
        var prefix = Start("NotRDxaomaoHost.exe"); var suffix = Start("RDxaomaoControlHelper.exe");
        var custom = Start("RDxaomaoExperimental.exe", "custom"); var other = Start("RDxaomaoExperimental.exe", "other");
        Assert.Equal(1, Apply());
        foreach (var process in new[] { prefix, suffix, custom, other }) Assert.Equal(1, Route(process));
        Assert.Equal(1, Apply(images: RemoteControlBypass.NativeImages([Path.Combine(directory, "custom", "RDxaomaoExperimental.exe")])));
        Assert.Equal(3, Route(custom)); Assert.Equal(3, Route(custom, true)); Assert.Equal(1, Route(other));
    }
    [Fact] public void ExactCustomPathDoesNotExemptSameFilenameElsewhereOrPartialNames()
    {
        var remote = Start("renamed-support.exe", "remote"); var unrelated = Start("renamed-support.exe", "other"); var partial = Start("NotAnyDesk.exe");
        string images = RemoteControlBypass.NativeImages([Path.Combine(directory, "remote", "renamed-support.exe")]);
        Assert.Equal(1, Apply(images: images)); Assert.Equal(3, Route(remote)); Assert.Equal(1, Route(unrelated)); Assert.Equal(1, Route(partial));
        Assert.Equal(1, Apply()); Assert.Equal(1, Route(remote)); // Cache must invalidate when the custom path is removed.
        Assert.Equal(1, Apply(images: images)); Assert.Equal(3, Route(remote));
    }
    [Fact] public void PidBirthCacheAndAtomicValidationDoNotReuseStaleExemptions()
    {
        var remote = Start("AnyDesk.exe");
        Assert.Equal(1, Apply()); Assert.Equal(3, Route(remote));
        Assert.Equal(1, Route(remote with { CreatedFileTime = remote.CreatedFileTime + 1 }));
        Assert.Equal(0, NativeEngine.Core_ReplacePolicyEx2([], 0, "\0\0", Proxy, 1, 1, 2, "\0\0", 0, 0, 0));
        Assert.Equal(0, Apply(images: "*.exe\0\0")); Assert.Equal(0, Apply(rdpPid: 100, rdpBirth: 1, rdpPort: 70000));
        Assert.Equal(3, Route(remote));
        Assert.Equal(1, NativeEngine.Core_ReplacePolicyEx([], 0, "\0\0", Proxy, 1, 1)); Assert.Equal(1, Route(remote));
    }
    [Fact] public void RdpExceptionIsLimitedToVerifiedServiceIdentityAndConfiguredLocalPort()
    {
        var service = Start("RdpServiceFixture.exe"); var other = Start("UnrelatedService.exe");
        Assert.Equal(1, Apply(rdpPid: (uint)service.Pid, rdpBirth: (ulong)service.CreatedFileTime, rdpPort: 43389));
        Assert.Equal(3, Route(service, localPort: 43389)); Assert.Equal(3, Route(service, true, 43389));
        Assert.Equal(1, Route(service, localPort: 443)); Assert.Equal(1, Route(service, localPort: 3389)); Assert.Equal(1, Route(other, localPort: 43389));
        Assert.Equal(1, Route(service with { CreatedFileTime = service.CreatedFileTime + 1 }, localPort: 43389));
        Assert.Equal(3, NativeEngine.Core_RoutePacketForTest(4, 0, 0, 43389, out _));
        Assert.Equal(2, NativeEngine.Core_RoutePacketForTest(0, 0, 0, 43389, out _));
        Assert.Equal(1, Apply(enabled: false, rdpPid: (uint)service.Pid, rdpBirth: (ulong)service.CreatedFileTime, rdpPort: 43389));
        Assert.Equal(1, Route(service, localPort: 43389));
    }
    [Fact] public void DefaultIsEnabledForNewAndLegacyConfigurationsAndOptOutPersists()
    {
        Assert.True(new GlobalProxySettings().BypassRemoteControl);
        var old = JsonSerializer.Deserialize<GlobalProxySettings>("{\"enabled\":false,\"bypassLan\":true}", Json.Options)!;
        Assert.True(old.BypassRemoteControl);
        var store = new ConfigStore(directory); store.Save(new() { GlobalProxy = old with { BypassRemoteControl = false, RemoteControlExecutables = [@"C:\Remote\custom.exe"] } });
        var restored = store.Load(); Assert.False(restored.GlobalProxy.BypassRemoteControl); Assert.Equal(@"C:\Remote\custom.exe", Assert.Single(restored.GlobalProxy.RemoteControlExecutables));
        foreach (var value in new[] { "custom.exe", @"C:\Remote\*.exe", @"\\server\share\remote.exe", @"C:\Remote\remote.dll" })
            Assert.Throws<InvalidOperationException>(() => RemoteControlBypass.NormalizeCustom([value]));
        Assert.Single(RemoteControlBypass.NormalizeCustom([@"C:\Remote\custom.exe", @"c:\remote\CUSTOM.exe"]));
    }
    [Fact] public async Task ServicePublishesCapabilityAndRetainsRemotePreferencesWhenGlobalIsStopped()
    {
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var settings = new GlobalProxySettings { BypassRemoteControl = false, RemoteControlExecutables = [@"C:\Remote\custom.exe"] };
        var store = new ConfigStore(directory); store.Save(new() { GlobalProxy = settings });
        using var service = new Controller(directory);
        var state = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
        Assert.True(state.SupportsRemoteControlBypass);
        Assert.True((await service.Dispatch(Command.Create("setGlobalProxy", new GlobalProxySettings()), default)).Ok);
        Assert.False(store.Load().GlobalProxy.BypassRemoteControl); Assert.Equal(settings.RemoteControlExecutables, store.Load().GlobalProxy.RemoteControlExecutables);
    }
    [Fact] public void ChangingExemptionsReusesPinnedAddressButReapplyingSameNodeStillRefreshesDns()
    {
        var previous = new GlobalProxySettings { Enabled = true, ProxyId = Guid.NewGuid(), ResolvedHost = "198.51.100.2" };
        Assert.Null(RemoteControlBypass.PinnedAddressForPolicyChange(previous, previous));
        Assert.Equal(previous.ResolvedHost, RemoteControlBypass.PinnedAddressForPolicyChange(previous, previous with { BypassRemoteControl = false }));
        Assert.Equal(previous.ResolvedHost, RemoteControlBypass.PinnedAddressForPolicyChange(previous, previous with { RemoteControlExecutables = [@"C:\Remote\renamed.exe"] }));
        Assert.Null(RemoteControlBypass.PinnedAddressForPolicyChange(previous, previous with { ProxyId = Guid.NewGuid(), BypassRemoteControl = false }));
        Assert.Null(RemoteControlBypass.PinnedAddressForPolicyChange(previous with { Enabled = false }, previous with { BypassRemoteControl = false }));
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class GlobalProxyTests : IDisposable
{
    private const uint Proxy = 301;
    public GlobalProxyTests()
    {
        NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
        Assert.Equal(1, NativeEngine.Core_SetProxy(Proxy, "127.0.0.1", 1080, "", ""));
    }
    public void Dispose() => NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
    [Fact] public void GlobalFallbackCoversUnlistedProcessesButPreservesExplicitRulesAndEngineExclusion()
    {
        Assert.Equal(1, NativeEngine.Core_SetProxy(302, "127.0.0.1", 1081, "", ""));
        NativeEngine.Rule[] rules = [
            new() { Pid = 123001, Created = 100, Proxy = 302 },
            new() { Pid = 123002, Created = 100, Blocked = 1 },
            new() { Pid = 123003, Created = 100, Blocked = 2 }
        ];
        Assert.Equal(1, NativeEngine.Core_ReplacePolicyEx(rules, 3, "\0\0", Proxy, 1, 1));
        Assert.Equal(1, NativeEngine.Core_RouteForTest(123004, 100, 0, out var proxy)); Assert.Equal(Proxy, proxy);
        Assert.Equal(1, NativeEngine.Core_RouteForTest(123001, 100, 0, out proxy)); Assert.Equal(302u, proxy);
        Assert.Equal(2, NativeEngine.Core_RouteForTest(123002, 100, 0, out _));
        Assert.Equal(3, NativeEngine.Core_RouteForTest(123003, 100, 0, out _));
        Assert.Equal(0, NativeEngine.Core_RouteForTest((uint)Environment.ProcessId, (ulong)LdPlayer.Identity(Environment.ProcessId)!.Value.CreatedFileTime, 0, out _));
        Assert.Equal(2, NativeEngine.Core_RouteForTest(123004, 100, 1, out _));
        Assert.Equal(2, NativeEngine.Core_RouteForTest(123004, 0, 0, out _));
        Assert.Equal(1, NativeEngine.Core_ReplacePolicyEx(rules, 3, "\0\0", Proxy, 2, 1));
        Assert.Equal(2, NativeEngine.Core_RouteForTest(123004, 100, 0, out _));
        Assert.Equal(1, NativeEngine.Core_RouteForTest(123001, 100, 0, out _));
        NativeEngine.Core_ReplacePolicyEx(rules, 3, "\0\0", 0, 0, 1);
        Assert.Equal(0, NativeEngine.Core_RouteForTest(123004, 100, 0, out _));
    }
    [Fact] public void StartupGuardStillBlocksUnderGlobalAndFailedPolicyReplacementIsAtomic()
    {
        using var child = StartPing();
        var identity = LdPlayer.Identity(child.Id)!.Value;
        try {
            Assert.Equal(1, NativeEngine.Core_ReplacePolicyEx([], 0, Path.Combine(Environment.SystemDirectory, "ping.exe") + "\0\0", Proxy, 1, 1));
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            Assert.Equal(0, NativeEngine.Core_ReplacePolicyEx([], 0, "\0\0", 0, 1, 1));
            Assert.Equal(0, NativeEngine.Core_ReplacePolicyEx([], 0, "\0\0", Proxy, 3, 1));
            Assert.Equal(0, NativeEngine.Core_ReplacePolicyEx([], 0, "\0\0", 9999, 1, 1));
            Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            Assert.Equal(1, NativeEngine.Core_RouteForTest(123004, 100, 0, out _));
        } finally { Stop(child); }
    }
    [Theory]
    [InlineData("10.0.0.1", 443, 1)]
    [InlineData("172.16.0.1", 443, 1)]
    [InlineData("172.31.255.1", 443, 1)]
    [InlineData("172.32.0.1", 443, 0)]
    [InlineData("192.168.1.1", 443, 1)]
    [InlineData("169.254.1.1", 443, 1)]
    [InlineData("224.0.0.251", 5353, 1)]
    [InlineData("255.255.255.255", 67, 1)]
    [InlineData("192.168.1.1", 53, 0)]
    [InlineData("1.1.1.1", 443, 0)]
    public void LanBypassNeverBypassesDnsAndCanBeDisabled(string address, int port, int expected)
    {
        byte[] packet = new byte[28]; packet[0] = 0x45; packet[9] = 17;
        System.Net.IPAddress.Parse(address).GetAddressBytes().CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)port);
        Assert.Equal(1, NativeEngine.Core_ReplacePolicyEx([], 0, "\0\0", Proxy, 1, 1));
        Assert.Equal(expected, NativeEngine.Core_GlobalBypassForTest(packet, 28));
        Assert.Equal(0, NativeEngine.Core_GlobalBypassForTest(packet, 19));
        Assert.Equal(1, NativeEngine.Core_ReplacePolicyEx([], 0, "\0\0", Proxy, 1, 0));
        Assert.Equal(0, NativeEngine.Core_GlobalBypassForTest(packet, 28));
    }
    [Fact] public async Task GlobalCannotBeEnabledWithoutDriverAndPersistedGlobalCanAlwaysBeDisabled()
    {
        string directory = TempDirectory();
        try {
            var store = new ConfigStore(directory);
            var profile = new ProxyProfile { Host = "127.0.0.1", Port = 1080 };
            store.Save(new() { Proxies = [profile] });
            using (var service = new Controller(directory)) {
                var reply = await service.Dispatch(Command.Create("setGlobalProxy", new GlobalProxySettings { Enabled = true, ProxyId = profile.Id }), default);
                Assert.False(reply.Ok); Assert.False(store.Load().GlobalProxy.Enabled);
            }
            store.Save(new() { Proxies = [profile], GlobalProxy = new() { Enabled = true, ProxyId = profile.Id, ResolvedHost = profile.Host } });
            using (var service = new Controller(directory)) {
                var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.True(snapshot.GlobalProxy.Enabled); Assert.False(snapshot.Diagnostics.EngineRunning);
                Assert.False(snapshot.GlobalProxyRouting);
                Assert.Equal(2, NativeEngine.Core_RouteForTest(123004, 100, 0, out _)); // no cached health after restart
                Assert.False((await service.Dispatch(Command.Create("deleteProxy", new ProxyRequest(profile.Id)), default)).Ok);
                Assert.True((await service.Dispatch(Command.Create("setGlobalProxy", new GlobalProxySettings()), default)).Ok);
                Assert.False(store.Load().GlobalProxy.Enabled);
                Assert.Equal(0, NativeEngine.Core_RouteForTest(123004, 100, 0, out _));
                Assert.True((await service.Dispatch(Command.Create("deleteProxy", new ProxyRequest(profile.Id)), default)).Ok);
            }
        } finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void Ipv6CannotEscapeViaGlobalLanException(string address)
    {
        byte[] packet = new byte[48]; packet[0] = 0x60; packet[6] = 17;
        System.Net.IPAddress.Parse(address).GetAddressBytes().CopyTo(packet, 24);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42), 443);
        NativeEngine.Core_ReplacePolicyEx([], 0, "\0\0", Proxy, 1, 1);
        Assert.Equal(0, NativeEngine.Core_GlobalBypassForTest(packet, 48));
    }
    [Fact] public async Task ExplicitTreeMembershipSurvivesParentExitAndServiceRestartUntilLastChildExits()
    {
        using var child = StartPing();
        string directory = TempDirectory();
        var identity = LdPlayer.Identity(child.Id)!.Value;
        var profile = new ProxyProfile { Host = "127.0.0.1", Port = 1080 };
        var store = new ConfigStore(directory);
        store.Save(new() { Proxies = [profile], Bindings = [new() {
            TargetKey = "tree", Kind = "Process", ProxyId = profile.Id, IncludeChildren = true,
            Processes = [new(int.MaxValue, 1)], Descendants = [identity]
        }] });
        try {
            using (var service = new Controller(directory)) {
                await service.Tick(default);
                var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.Contains(identity, Assert.Single(snapshot.Bindings).Descendants);
                Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)child.Id, (ulong)identity.CreatedFileTime, 0, out _));
            }
            var now = DateTimeOffset.UtcNow;
            using (var service = new Controller(directory, handoffClock: () => now)) {
                await service.Tick(default); Assert.Single(store.Load().Bindings);
                Stop(child); await service.Tick(default); Assert.Single(store.Load().Bindings);
                now = now.AddSeconds(11); await service.Tick(default); Assert.Empty(store.Load().Bindings);
            }
        } finally { Stop(child); Directory.Delete(directory, true); }
    }
    [Fact] public void ChildRoutingRequiresExplicitProcessOptInAndSurvivesUnrelatedAssignment()
    {
        var profile = new ProxyProfile();
        TargetInstance Target(int pid) => new($"p{pid}", "emulator", "Process", @"C:\Emulator\backend.exe", -1, true, [new(pid, 100)]);
        var bindings = AssignmentPlanner.Prepare([], [profile], new([new(Target(1), profile.Id, true, true)]));
        bindings = AssignmentPlanner.Prepare(bindings, [profile], new([new(Target(2), profile.Id)]));
        Assert.True(bindings[0].IncludeChildren); Assert.False(bindings[1].IncludeChildren);
        Assert.Throws<InvalidOperationException>(() => AssignmentPlanner.Prepare([], [profile], new([new(Target(1) with { Kind = "LDPlayer" }, profile.Id, true, true)])));
        AutoProxyPlanner.Validate(new() { Kind = "Process", Pattern = "backend.exe", IncludeChildren = true });
        Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(new() { Kind = "LDPlayer", IncludeChildren = true }));
    }
    [Fact] public void HealthGraceRequiresPriorSuccessAndExpiresWithoutAllowingFirstFailedCheck()
    {
        var now = DateTimeOffset.UtcNow;
        var failure = new ProbeResult(Guid.NewGuid(), true, true, true, false, "198.51.100.1", null, 10, "UDP failed", now);
        Assert.False(ProxyHealth.CanRoute(failure, null, 1, now));
        Assert.True(ProxyHealth.CanRoute(failure, now.AddSeconds(-60), 1, now));
        Assert.False(ProxyHealth.CanRoute(failure, now.AddSeconds(-60), 2, now));
        Assert.False(ProxyHealth.CanRoute(failure, now - ProbeTiming.HealthValidity, 1, now));
        Assert.True(ProxyHealth.CanRoute(failure with { Udp = true }, null, 0, now));
        Assert.False(ProxyHealth.CanRoute(failure with { Udp = true, At = now - ProbeTiming.HealthValidity }, null, 0, now));
    }
    [Fact] public void ReducedProbeFrequencyDoesNotBlockHealthySessionsAtOldThreeMinuteCutoff()
    {
        var successAt = DateTimeOffset.UtcNow;
        var success = new ProbeResult(Guid.NewGuid(), true, true, true, true, "198.51.100.1", "198.51.100.1", 10, "OK", successAt);
        Assert.True(ProxyHealth.CanRoute(success, successAt, 0, successAt.AddMinutes(30)));
        var failure = success with { Udp = false, At = successAt.AddMinutes(30) };
        Assert.True(ProxyHealth.CanRoute(failure, successAt, 1, successAt.AddMinutes(35)));
        Assert.False(ProxyHealth.CanRoute(failure, successAt, 2, successAt.AddMinutes(35)));
        Assert.False(ProxyHealth.CanRoute(failure, successAt, 1, successAt + ProbeTiming.HealthValidity));
    }
    [Fact] public async Task GlobalHealthCheckResolvesStunAtProxyWithoutDependingOnQuarantinedSystemDns()
    {
        await using var fixture = new SocksFixture();
        const string host = "stun.this-must-not-resolve.invalid";
        var result = await ProxyProbe.RunAsync(fixture.Profile, new("http://127.0.0.1:8080", host, 3478, 3, RemoteStunDns: true));
        Assert.True(result.Ready, result.Message); Assert.Equal(host, fixture.LastUdpDomain);
        Assert.Equal("198.51.100.7", result.UdpExit);
    }
    private static Process StartPing() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe")) { Arguments = "-n 30 127.0.0.1", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    private static void Stop(Process process) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); }
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value); return path;
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using WindowSocks.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace WindowSocks.Tests;

public sealed class CoreTests
{
    [Fact] public void ImportsAllFormatsWithoutExposingPassword()
    {
        var p = ProxyImport.Parse("host,port,username,password,name\n\"127.0.0.1\",1080,\"a,b\",\"p:/@,word\",\"測試\"").Single();
        Assert.Equal("a,b", p.Username); Assert.Equal("p:/@,word", p.Password); Assert.DoesNotContain(p.Password, ProxyImport.Export([p]));
        var uri = ProxyImport.Parse("socks5://a%2Cb:p%3A%2F%40%2Cword@127.0.0.1:1080").Single(); Assert.Equal(p.Password, uri.Password);
        Assert.Equal("pw", ProxyImport.Parse("127.0.0.1/1080/user/pw/2026-09-11").Single().Password);
        Assert.Equal(1080, ProxyImport.Parse("127.0.0.1:1080").Single().Port);
        Assert.Equal("p:a", ProxyImport.Parse("127.0.0.1:1080:u:p:a").Single().Password);
    }
    [Theory] [InlineData("host:0")] [InlineData("host:65536")] [InlineData("http://host:80")] [InlineData("host:bad:SECRET:PASS")]
    public void RejectsInvalidImport(string input) { var error = Assert.Throws<FormatException>(() => ProxyImport.Parse(input)); Assert.DoesNotContain("SECRET", error.Message); }
    [Fact] public void LdPlayerHandlesNamesWithCommasAndStoppedPids()
    {
        var rows = LdPlayer.Parse("0,我的,測試,10,11,1,50,51,1600,900,240\n1,備用,0,0,0,-1,-1,1600,900,240", @"C:\LDPlayer\ldconsole.exe", pid => new(pid, 123));
        Assert.Equal(2, rows.Length); Assert.Equal("我的,測試", rows[0].Name); Assert.Equal(new[] { 51 }, rows[0].Processes.Select(p => p.Pid)); Assert.Empty(rows[1].Processes);
        var old = LdPlayer.Parse("0,舊版,10,11,1,50,51", @"C:\LDPlayer\ldconsole.exe", pid => new(pid, 123)); Assert.Single(old[0].Processes);
    }
    [Fact] public void NativeRulesRejectPidReuseAndMissingProxy()
    {
        Assert.Equal(1, NativeEngine.Core_SetProxy(90, "127.0.0.1", 1080, "", ""));
        NativeEngine.Rule[] rules = [new() { Pid = 123456, Created = 42, Proxy = 90 }];
        Assert.Equal(1, NativeEngine.Core_ReplaceRules(rules, 1)); Assert.Equal(1, NativeEngine.Core_RouteForTest(123456, 42, 0, out var proxy)); Assert.Equal(90u, proxy);
        Assert.Equal(0, NativeEngine.Core_RouteForTest(123456, 43, 0, out _)); Assert.Equal(2, NativeEngine.Core_RouteForTest(123456, 42, 1, out _));
        rules[0].Proxy = 999; NativeEngine.Core_ReplaceRules(rules, 1); Assert.Equal(2, NativeEngine.Core_RouteForTest(123456, 42, 0, out _));
        Assert.Equal(0, NativeEngine.Core_ReplaceRules([rules[0], rules[0]], 2)); Assert.Equal(2, NativeEngine.Core_RouteForTest(123456, 42, 0, out _));
        NativeEngine.Core_ReplaceRules([], 0);
    }
    [Fact] public void DpapiRoundTripDoesNotStorePlaintext()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); var store = new ConfigStore(dir, DataProtectionScope.CurrentUser);
        var config = new SavedConfig { Proxies = [new() { Host = "localhost", Port = 1, Password = "SECRET-password-123" }] };
        try { store.Save(config); store.Save(config); Assert.Equal(config.Proxies[0].Password, store.Load().Proxies[0].Password); Assert.DoesNotContain("SECRET-password", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "settings.dpapi")))); Assert.True(File.Exists(Path.Combine(dir, "settings.dpapi.bak"))); }
        finally { Directory.Delete(dir, recursive: true); }
    }
    [Fact] public async Task RealNativeSocksHandshakeAndUdpDatagrams()
    {
        await using var fixture = new SocksFixture(); var result = await ProxyProbe.RunAsync(fixture.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 4));
        Assert.True(result.Reachable); Assert.True(result.Authenticated); Assert.True(result.Tcp, result.Message); Assert.True(result.Udp, result.Message); Assert.Equal("198.51.100.7", result.TcpExit); Assert.Equal(result.TcpExit, result.UdpExit);
    }
    [Fact] public async Task WrongCredentialsFailBothTransports()
    {
        await using var fixture = new SocksFixture(); var result = await ProxyProbe.RunAsync(fixture.Profile with { Password = "wrong" }, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 3));
        Assert.True(result.Reachable); Assert.False(result.Authenticated); Assert.False(result.Tcp); Assert.False(result.Udp);
    }
    [Fact] public async Task TcpSuccessDoesNotImplyUdpSupport()
    {
        await using var fixture = new SocksFixture(udpEnabled: false); var result = await ProxyProbe.RunAsync(fixture.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 3));
        Assert.True(result.Tcp); Assert.False(result.Udp); Assert.False(result.Ready);
    }
    [Fact] public async Task SuppliedCredentialsCannotDowngradeToNoAuthentication()
    {
        await using var fixture = new SocksFixture(downgradeAuth: true);
        var result = await ProxyProbe.RunAsync(fixture.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 3));
        Assert.True(result.Reachable); Assert.False(result.Authenticated); Assert.False(result.Ready);
    }
    [Fact] public async Task TwoProxiesKeepIndependentUdpAssociations()
    {
        await using var one = new SocksFixture("198.51.100.1"); await using var two = new SocksFixture("198.51.100.2");
        var results = await Task.WhenAll(new[] { one, two }.Select(f => ProxyProbe.RunAsync(f.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 5))));
        Assert.All(results, r => Assert.True(r.Ready, r.Message)); Assert.Equal(new[] { "198.51.100.1", "198.51.100.2" }, results.Select(r => r.UdpExit));
    }
    [Fact] public async Task TenParallelWorkloadsRemainSeparated()
    {
        var fixtures = Enumerable.Range(1, 10).Select(i => new SocksFixture($"198.51.100.{i}")).ToArray();
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("WINDOWSOCKS_SOAK_SECONDS"), out int requested) ? Math.Clamp(requested, 0, 86400) : 0;
        string? report = Environment.GetEnvironmentVariable("WINDOWSOCKS_SOAK_REPORT");
        var timer = System.Diagnostics.Stopwatch.StartNew(); double nextSample = 0;
        if (seconds > 0 && report is not null) File.WriteAllText(report, "utc,seconds,batches,private_bytes,handles,threads,cpu_seconds\n");
        try { for (int batch = 0; seconds > 0 ? timer.Elapsed.TotalSeconds < seconds : batch < 5; batch++) {
            var results = await Task.WhenAll(fixtures.Select(f => ProxyProbe.RunAsync(f.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 10))));
            Assert.All(results, r => Assert.True(r.Ready, r.Message)); Assert.Equal(10, results.Select(r => r.UdpExit).Distinct().Count());
            if (seconds > 0) {
                if (report is not null && timer.Elapsed.TotalSeconds >= nextSample) {
                    using var process = System.Diagnostics.Process.GetCurrentProcess();
                    File.AppendAllText(report, FormattableString.Invariant($"{DateTimeOffset.UtcNow:O},{timer.Elapsed.TotalSeconds:F2},{batch + 1},{process.PrivateMemorySize64},{process.HandleCount},{process.Threads.Count},{process.TotalProcessorTime.TotalSeconds:F2}\n")); nextSample = timer.Elapsed.TotalSeconds + 10;
                }
                await Task.Delay(1000);
            }
        } }
        finally { foreach (var fixture in fixtures) await fixture.DisposeAsync(); }
    }
    [Fact] public async Task OversizedPipeMessageIsRejected()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(PipeTransport.Limit + 1)); await Assert.ThrowsAsync<IOException>(() => PipeTransport.ReadAsync<Command>(stream, default));
    }
    [Fact] public async Task FailureReplySurvivesPipeSerialization()
    {
        using var stream = new MemoryStream(); await PipeTransport.WriteAsync(stream, Reply.Failure("代理異常，已封鎖"), default);
        stream.Position = 0; var reply = await PipeTransport.ReadAsync<Reply>(stream, default);
        Assert.False(reply.Ok); Assert.Equal("代理異常，已封鎖", reply.Error); Assert.Equal(System.Text.Json.JsonValueKind.Null, reply.Data.ValueKind);
    }
    [Fact] public void RulesPauseSwitchAndExplicitlyReleaseInstance()
    {
        Assert.Equal(1, NativeEngine.Core_SetProxy(91, "127.0.0.1", 1081, "", ""));
        Assert.Equal(1, NativeEngine.Core_SetProxy(92, "127.0.0.1", 1082, "", ""));
        NativeEngine.Rule row = new() { Pid = 456789, Created = 111, Proxy = 91 };
        try {
            Assert.Equal(1, NativeEngine.Core_ReplacePolicy([row], 1, "C:\\test.exe\0\0"));
            Assert.Equal(1, NativeEngine.Core_RouteForTest(row.Pid, row.Created, 0, out var id)); Assert.Equal(91u, id);
            row.Blocked = 1; NativeEngine.Core_ReplacePolicy([row], 1, "\0\0"); Assert.Equal(2, NativeEngine.Core_RouteForTest(row.Pid, row.Created, 0, out _));
            row.Blocked = 0; row.Proxy = 92; NativeEngine.Core_ReplacePolicy([row], 1, "\0\0"); Assert.Equal(1, NativeEngine.Core_RouteForTest(row.Pid, row.Created, 0, out id)); Assert.Equal(92u, id);
            row.Blocked = 2; NativeEngine.Core_ReplacePolicy([row], 1, "\0\0"); Assert.Equal(3, NativeEngine.Core_RouteForTest(row.Pid, row.Created, 1, out _));
            row.Blocked = 99; Assert.Equal(0, NativeEngine.Core_ReplacePolicy([row], 1, "\0\0")); Assert.Equal(3, NativeEngine.Core_RouteForTest(row.Pid, row.Created, 0, out _));
        } finally { NativeEngine.Core_ReplacePolicy([], 0, "\0\0"); }
    }
    [Fact] public async Task UdpAssociateSuccessWithoutDatagramsIsNotReady()
    {
        await using var fixture = new SocksFixture(udpReplies: false);
        var result = await ProxyProbe.RunAsync(fixture.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 1));
        Assert.True(result.Tcp); Assert.True(result.Authenticated); Assert.False(result.Udp); Assert.False(result.Ready);
    }
    [Fact] public void MissingDriverNeverReportsEngineRunning()
    {
        for (int i = 0; i < 3; i++) { Assert.Equal(0, NativeEngine.Core_Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll"))); Assert.Equal(0, NativeEngine.Core_IsRunning()); Assert.NotEqual(0u, NativeEngine.Core_LastError()); }
    }
    [Fact] public async Task UdpRunsWhileTcpResponseIsPendingAndLatencySeparatesConnectionFromHttp()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new SocksFixture(tcpResponseRelease: release.Task);
        try {
            var probe = ProxyProbe.RunAsync(fixture.Profile, new("http://127.0.0.1:8080", "127.0.0.1", 3478, 5));
            await Task.WhenAll(fixture.TcpRequestReceived.Task, fixture.UdpRequestReceived.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(probe.IsCompleted); await Task.Delay(150); release.TrySetResult();
            var result = await probe; Assert.True(result.Ready); Assert.NotNull(result.ConnectLatencyMs);
            Assert.True(result.LatencyMs >= result.ConnectLatencyMs + 100);
            Assert.Equal($"{result.ConnectLatencyMs} ms", result.LatencyLabel); Assert.NotNull(result.TotalDurationMs);
            Assert.NotNull(result.UdpLatencyMs);
        } finally { release.TrySetResult(); }
    }
    [Fact] public async Task TcpFirstReturnsAnIpBeforeUdpStartsAndPendingIsNotAFailure()
    {
        await using var fixture = new SocksFixture(udpReplies: false);
        var options = new ProbeOptions("http://127.0.0.1:8080", "127.0.0.1", 3478, 1);
        var tcp = await ProxyProbe.RunTcpAsync(fixture.Profile, options);
        Assert.True(tcp.Tcp); Assert.NotNull(tcp.TcpExit); Assert.True(tcp.UdpPending); Assert.False(tcp.UdpChecked);
        Assert.False(tcp.Ready); Assert.Equal("背景檢測中", tcp.UdpLabel); Assert.False(fixture.UdpRequestReceived.Task.IsCompleted);
        Assert.False(ProxyHealth.CanRoute(tcp, null, 0, DateTimeOffset.UtcNow));
        var supplemental = ProxyProbe.SupplementAsync(fixture.Profile, tcp, options);
        await fixture.UdpRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(supplemental.IsCompleted);
        var result = await supplemental; Assert.True(result.Tcp); Assert.False(result.UdpPending); Assert.True(result.UdpChecked);
        Assert.False(result.Udp); Assert.Equal(tcp.CheckId, result.CheckId); Assert.Equal(tcp.TcpExit, result.TcpExit);
    }
    [Fact] public void StunRejectsMismatchedTransaction() { Assert.Throws<IOException>(() => ProxyProbe.ParseStun(new byte[32], new byte[12])); }
}

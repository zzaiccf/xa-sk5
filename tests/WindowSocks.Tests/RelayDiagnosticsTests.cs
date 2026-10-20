using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class RelayDiagnosticsTests
{
    private static async Task<(Socket Client, Socket Accepted)> Pair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); return (client, await listener.AcceptSocketAsync());
    }
    [Fact] public async Task NativeRelayPreservesResponseAfterClientHalfCloseAndRecordsBothEofs()
    {
        var left = await Pair(); var right = await Pair();
        using var client = left.Client; using var accepted = left.Accepted; using var upstream = right.Client; using var server = right.Accepted;
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        ulong before = NativeEngine.FlowEvents().LastOrDefault().Sequence;
        var pump = Task.Run(() => NativeEngine.Core_RelayForTest((nuint)accepted.Handle, (nuint)upstream.Handle, (uint)identity.Pid, (ulong)identity.CreatedFileTime));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var clientStream = new NetworkStream(client, false);
        await client.SendAsync(Encoding.ASCII.GetBytes("request"), timeout.Token); client.Shutdown(SocketShutdown.Send);
        using var serverStream = new NetworkStream(server, false); var request = new byte[7]; await serverStream.ReadExactlyAsync(request, timeout.Token);
        Assert.Equal("request", Encoding.ASCII.GetString(request)); Assert.Equal(0, await serverStream.ReadAsync(new byte[1], timeout.Token));
        await server.SendAsync(Encoding.ASCII.GetBytes("response"), timeout.Token); server.Shutdown(SocketShutdown.Send);
        var response = new byte[8]; await clientStream.ReadExactlyAsync(response, timeout.Token);
        Assert.Equal("response", Encoding.ASCII.GetString(response)); Assert.Equal(0, await clientStream.ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(5u, await pump.WaitAsync(timeout.Token));
        var events = NativeEngine.FlowEvents().Where(e => e.Sequence > before).ToArray();
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, events.Select(e => e.Stage));
        Assert.Equal(7ul, events[^1].Sent); Assert.Equal(8ul, events[^1].Received);
        Assert.Single(events.Select(e => e.Flow).Distinct()); Assert.Equal(88, Marshal.SizeOf<NativeEngine.FlowEvent>());
    }
    [Fact] public async Task UpstreamResetIsRecordedAsReadFailureWithWinsockError()
    {
        var left = await Pair(); var right = await Pair();
        using var client = left.Client; using var accepted = left.Accepted; using var upstream = right.Client; using var server = right.Accepted;
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        ulong before = NativeEngine.FlowEvents().LastOrDefault().Sequence;
        var pump = Task.Run(() => NativeEngine.Core_RelayForTest((nuint)accepted.Handle, (nuint)upstream.Handle, (uint)identity.Pid, (ulong)identity.CreatedFileTime));
        server.LingerState = new(true, 0); server.Close();
        Assert.Equal(24u, await pump.WaitAsync(TimeSpan.FromSeconds(8)));
        var failure = NativeEngine.FlowEvents().Last(e => e.Sequence > before && e.Stage == 24);
        Assert.Equal(10054u, failure.Error); Assert.DoesNotContain("伺服器封鎖", NetworkFlowEvent.StageLabel(failure.Stage));
    }
    [Fact] public async Task PassiveProxyTargetTestDistinguishesSuccessfulTunnelAndUpstreamEof()
    {
        await using var fixture = new SocksFixture(closeAfterConnect: true);
        var result = await TargetConnectivityProbe.RunAsync(fixture.Profile, new(fixture.Profile.Id, "198.51.100.8", 10001, 1));
        Assert.True(result.Connected, result.Message); Assert.True(result.UpstreamEof); Assert.Equal(0, result.ReceivedBytes);
        Assert.DoesNotContain(fixture.Profile.Username, JsonSerializer.Serialize(result, Json.Options));
        Assert.DoesNotContain(fixture.Profile.Password, JsonSerializer.Serialize(result, Json.Options));
        Assert.Contains("無法區分", result.Message);
    }
    [Fact] public async Task PassiveProxyTargetTestDoesNotRequireAnApplicationRequestOrClaimGameSuccess()
    {
        await using var fixture = new SocksFixture();
        var result = await TargetConnectivityProbe.RunAsync(fixture.Profile, new(fixture.Profile.Id, "198.51.100.8", 10001, 1));
        Assert.True(result.Connected, result.Message); Assert.False(result.UpstreamEof); Assert.Equal(0, result.ReceivedBytes);
        Assert.Contains("不代表遊戲登入", result.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TargetConnectivityProbe.RunAsync(fixture.Profile, new(fixture.Profile.Id, "hostname", 0)));
    }
    [Fact] public async Task DiagnosticExportIncludesProcessIdentityAndNeverIncludesProxyCredentials()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var profile = new ProxyProfile { Host = "127.0.0.1", Port = 1080, Username = "private-diag-user", Password = "private-diag-password" };
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        new ConfigStore(directory).Save(new() { Proxies = [profile], Bindings = [new() { TargetKey = "diagnostic", Kind = "Process", Name = "測試", Path = Environment.ProcessPath!, ProxyId = profile.Id, Processes = [identity] }] });
        try {
            using var service = new Controller(directory);
            var reply = await service.Dispatch(Command.Create("diagnose", new { }), default); Assert.True(reply.Ok, reply.Error);
            string text = reply.Data.GetRawText(); Assert.DoesNotContain(profile.Username, text); Assert.DoesNotContain(profile.Password, text);
            var bundle = Json.Read<DiagnosticBundle>(reply.Data); Assert.Equal(identity, Assert.Single(Assert.Single(bundle.Targets).Processes).Identity);
            Assert.All(bundle.FlowEvents, e => Assert.True(e.At > DateTimeOffset.UtcNow.AddMinutes(-10)));
        } finally { Directory.Delete(directory, true); }
    }
}

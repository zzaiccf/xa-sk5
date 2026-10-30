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
    private static async Task<(Socket Client, Socket Accepted)> Pair(int? receiveBuffer = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        if (receiveBuffer is { } serverSize) listener.Server.ReceiveBufferSize = serverSize;
        listener.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (receiveBuffer is { } clientSize) client.ReceiveBufferSize = clientSize;
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); return (client, await listener.AcceptSocketAsync());
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task NativeRelayBackpressureInOneDirectionDoesNotStallReverseMessages(bool uploadBlocked)
    {
        var left = await Pair(1024); var right = await Pair(1024);
        using var client = left.Client; using var accepted = left.Accepted; using var upstream = right.Client; using var server = right.Accepted;
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reader = uploadBlocked ? server : client; var writer = uploadBlocked ? client : server;
        reader.ReceiveBufferSize = 1024; writer.SendBufferSize = 8192;
        var pump = Task.Run(() => NativeEngine.Core_RelayForTest((nuint)accepted.Handle, (nuint)upstream.Handle, (uint)identity.Pid, (ulong)identity.CreatedFileTime));
        Task? flooding = null;
        try {
            flooding = Task.Run(async () => {
                var data = new byte[65536];
                while (!lifetime.IsCancellationRequested) await writer.SendAsync(data, SocketFlags.None, lifetime.Token);
            });
            while (reader.Available == 0) await Task.Delay(10, lifetime.Token);
            await Task.Delay(200, lifetime.Token);
            Assert.False(flooding.IsCompleted, "Fixture must still be applying backpressure.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await reader.SendAsync(new byte[] { 0x42 }, SocketFlags.None, lifetime.Token);
            var response = new byte[1]; using var stream = new NetworkStream(writer, false);
            await stream.ReadExactlyAsync(response, lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0x42, response[0]);
            Console.WriteLine($"Reverse message under {(uploadBlocked ? "upload" : "download")} backpressure: {watch.Elapsed.TotalMilliseconds:F2} ms");
            Assert.False(flooding.IsCompleted, "Reverse message should arrive before the blocked direction drains.");
        } finally {
            lifetime.Cancel(); client.Dispose(); accepted.Dispose(); upstream.Dispose(); server.Dispose();
            if (flooding is not null) try { await flooding; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            await pump.WaitAsync(TimeSpan.FromSeconds(7));
        }
    }
    [Theory] [InlineData(2)] [InlineData(4)]
    public async Task ParallelRelaysPreserveDuplexBinaryDataAndDrainBeforeHalfClose(int connections)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Task.WhenAll(Enumerable.Range(0, connections).Select(async index => {
            var left = await Pair(); var right = await Pair();
            using var client = left.Client; using var accepted = left.Accepted; using var upstream = right.Client; using var server = right.Accepted;
            client.SendBufferSize = server.SendBufferSize = 8192;
            var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
            var pump = Task.Run(() => NativeEngine.Core_RelayForTest((nuint)accepted.Handle, (nuint)upstream.Handle, (uint)identity.Pid, (ulong)identity.CreatedFileTime));
            try {
                var request = new byte[512 * 1024 + index]; var response = new byte[513 * 1024 + index];
                new Random(123 + index).NextBytes(request); new Random(456 + index).NextBytes(response);
                using var clientStream = new NetworkStream(client, false); using var serverStream = new NetworkStream(server, false);
                async Task SendAndClose(NetworkStream stream, Socket socket, byte[] data) {
                    await stream.WriteAsync(data, timeout.Token); socket.Shutdown(SocketShutdown.Send);
                }
                async Task ReadAndVerify(NetworkStream stream, byte[] expected) {
                    var actual = new byte[expected.Length]; await stream.ReadExactlyAsync(actual, timeout.Token);
                    Assert.True(expected.AsSpan().SequenceEqual(actual), "Relay changed, lost or reordered binary data.");
                    Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
                }
                await Task.WhenAll(SendAndClose(clientStream, client, request), SendAndClose(serverStream, server, response),
                    ReadAndVerify(serverStream, request), ReadAndVerify(clientStream, response));
                Assert.Equal(5u, await pump.WaitAsync(timeout.Token));
            } finally {
                client.Dispose(); accepted.Dispose(); upstream.Dispose(); server.Dispose();
                await pump.WaitAsync(TimeSpan.FromSeconds(7));
            }
        }));
    }
    [Fact] public async Task PersistentBackpressureTimesOutAndIsVisibleInRelayDiagnostics()
    {
        var left = await Pair(1024); var right = await Pair(1024);
        using var client = left.Client; using var accepted = left.Accepted; using var upstream = right.Client; using var server = right.Accepted;
        client.SendBufferSize = 8192; server.ReceiveBufferSize = 1024;
        var before = NativeEngine.ReadRelayPerformance(); Assert.NotNull(before);
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pump = Task.Run(() => NativeEngine.Core_RelayForTest((nuint)accepted.Handle, (nuint)upstream.Handle, (uint)identity.Pid, (ulong)identity.CreatedFileTime));
        var flooding = Task.Run(async () => {
            var data = new byte[65536];
            while (!lifetime.IsCancellationRequested) await client.SendAsync(data, SocketFlags.None, lifetime.Token);
        });
        try {
            Assert.Equal(25u, await pump.WaitAsync(TimeSpan.FromSeconds(9)));
            Assert.InRange(watch.Elapsed.TotalSeconds, 4.9, 9);
            var failure = NativeEngine.FlowEvents().Last(e => e.Stage == 25);
            Assert.Equal(10060u, failure.Error);
            var after = NativeEngine.ReadRelayPerformance(); Assert.NotNull(after);
            Assert.True(after.StallTimeouts > before.StallTimeouts);
            Assert.True(after.QueueSamples > before.QueueSamples);
            Assert.True(after.QueueMicroseconds > before.QueueMicroseconds);
            Assert.True(after.QueueMaxMicroseconds >= 4_900_000);
            Assert.True(after.WouldBlock > before.WouldBlock);
            Console.WriteLine($"No-progress timeout: {watch.Elapsed.TotalSeconds:F2} s");
        } finally {
            lifetime.Cancel(); client.Dispose(); accepted.Dispose(); upstream.Dispose(); server.Dispose();
            try { await flooding; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            await pump.WaitAsync(TimeSpan.FromSeconds(7));
        }
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
            Assert.NotNull(bundle.RelayPerformance);
            Assert.Contains("不是遊戲 ping", bundle.RelayPerformance.Note);
            Assert.Contains("不是丟包", bundle.RelayPerformance.Note);
            Assert.All(bundle.FlowEvents, e => Assert.True(e.At > DateTimeOffset.UtcNow.AddMinutes(-10)));
        } finally { Directory.Delete(directory, true); }
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowSocks.Core;

namespace WindowSocks.Tests;

internal sealed class SocksFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancel = new();
    private readonly List<Task> clients = [];
    private readonly Task loop;
    private readonly string exit;
    private readonly bool udpEnabled;
    private readonly bool udpReplies;
    private readonly bool downgradeAuth;
    private readonly bool closeAfterConnect;
    private readonly bool echoTcp, closeEchoAfterFirst;
    private readonly Task? tcpResponseRelease;
    public TaskCompletionSource TcpRequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource UdpRequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ProxyProfile Profile { get; }
    public string? LastUdpDomain { get; private set; }
    public SocksFixture(string exit = "198.51.100.7", bool udpEnabled = true, bool udpReplies = true, bool downgradeAuth = false, bool closeAfterConnect = false, bool echoTcp = false, bool closeEchoAfterFirst = false, Task? tcpResponseRelease = null)
    {
        this.exit = exit; this.udpEnabled = udpEnabled; this.udpReplies = udpReplies; this.downgradeAuth = downgradeAuth; this.closeAfterConnect = closeAfterConnect; this.echoTcp = echoTcp; this.closeEchoAfterFirst = closeEchoAfterFirst; this.tcpResponseRelease = tcpResponseRelease; listener.Start();
        Profile = new() { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "測試,user", Password = "p:/@,word", Name = "本機測試" };
        loop = Accept();
    }
    private async Task Accept()
    {
        try { while (!cancel.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(cancel.Token); clients.RemoveAll(task => task.IsCompleted); clients.Add(Handle(client)); } }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private static async Task<byte[]> Read(Stream stream, int n, CancellationToken ct) { var b = new byte[n]; await stream.ReadExactlyAsync(b, ct); return b; }
    private static async Task Fragmented(Stream stream, byte[] b, CancellationToken ct) { foreach (byte value in b) await stream.WriteAsync(new[] { value }, ct); }
    private async Task Handle(TcpClient client)
    {
        using (client)
        try
        {
            var ct = cancel.Token; var stream = client.GetStream(); var greeting = await Read(stream, 2, ct); _ = await Read(stream, greeting[1], ct);
            if (downgradeAuth) { await Fragmented(stream, [5, 0], ct); return; }
            await Fragmented(stream, [5, 2], ct);
            var auth = await Read(stream, 2, ct); string user = Encoding.UTF8.GetString(await Read(stream, auth[1], ct)); int passwordLength = (await Read(stream, 1, ct))[0]; string password = Encoding.UTF8.GetString(await Read(stream, passwordLength, ct));
            bool valid = user == Profile.Username && password == Profile.Password; await Fragmented(stream, [1, valid ? (byte)0 : (byte)1], ct); if (!valid) return;
            var request = await Read(stream, 4, ct); int addressLength = request[3] == 1 ? 4 : request[3] == 4 ? 16 : (await Read(stream, 1, ct))[0];
            _ = await Read(stream, addressLength + 2, ct);
            if (request[1] == 1)
            {
                // Domain-form BND reply verifies variable-length and fragmented parsing.
                await Fragmented(stream, [5, 0, 0, 3, 1, (byte)'x', 0, 0], ct);
                if (closeAfterConnect) return;
                if (echoTcp) {
                    for (int i = 0; i < 2; i++) {
                        var line = new List<byte>(); byte[] next = new byte[1];
                        while (await stream.ReadAsync(next, ct) > 0) { line.Add(next[0]); if (next[0] == 10) break; }
                        if (line.Count == 0) return;
                        await stream.WriteAsync(line.ToArray(), ct);
                        if (closeEchoAfterFirst) return;
                    }
                    return;
                }
                var buffer = new byte[8192]; _ = await stream.ReadAsync(buffer, ct);
                TcpRequestReceived.TrySetResult();
                if (tcpResponseRelease is not null) await tcpResponseRelease.WaitAsync(ct);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {exit.Length}\r\nConnection: close\r\n\r\n{exit}");
                await stream.WriteAsync(response, ct);
            }
            else if (request[1] == 3)
            {
                if (!udpEnabled) { await Fragmented(stream, [5, 7, 0, 1, 0, 0, 0, 0, 0, 0], ct); return; }
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                await Fragmented(stream, [5, 0, 0, 1, 0, 0, 0, 0, (byte)(port >> 8), (byte)port], ct);
                using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receive = Task.Run(async () => {
                    try { while (!done.IsCancellationRequested) {
                        var message = await udp.ReceiveAsync(done.Token); UdpRequestReceived.TrySetResult(); var b = message.Buffer; if (b.Length < 5 || !udpReplies) continue;
                        int offset = b[3] == 1 ? 10 : b[3] == 3 ? 7 + b[4] : 0;
                        if (offset == 0 || b.Length < offset + 20) continue;
                        byte[] response = new byte[42]; response[3] = 1;
                        if (b[3] == 3) { LastUdpDomain = Encoding.ASCII.GetString(b, 5, b[4]); IPAddress.Loopback.GetAddressBytes().CopyTo(response, 4); b.AsSpan(offset - 2, 2).CopyTo(response.AsSpan(8)); }
                        else b.AsSpan(0, 10).CopyTo(response);
                        response[10] = 1; response[11] = 1; response[13] = 12;
                        b.AsSpan(offset + 4, 16).CopyTo(response.AsSpan(14)); // cookie + transaction ID
                        response[30] = 0; response[31] = 0x20; response[32] = 0; response[33] = 8; response[35] = 1;
                        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(36), (ushort)(message.RemoteEndPoint.Port ^ 0x2112));
                        uint ip = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(exit).GetAddressBytes()); BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(38), ip ^ 0x2112a442);
                        await udp.SendAsync(response, message.RemoteEndPoint, done.Token);
                    }} catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
                }, ct);
                try { _ = await stream.ReadAsync(new byte[1], ct); } finally { done.Cancel(); await receive; }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { cancel.Cancel(); listener.Stop(); await loop; await Task.WhenAll(clients); cancel.Dispose(); }
}

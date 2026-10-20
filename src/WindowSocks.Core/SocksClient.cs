using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WindowSocks.Core;

public static class SocksClient
{
    public static async Task<Socket> OpenProxyAsync(ProxyProfile profile, CancellationToken ct)
    {
        ProxyImport.Validate(profile);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        { NoDelay = true, ReceiveTimeout = 5000, SendTimeout = 5000 };
        try { await socket.ConnectAsync(profile.Host, profile.Port, ct); return socket; } catch { socket.Dispose(); throw; }
    }
    public static async Task<NetworkStream> ConnectAsync(ProxyProfile profile, string host, int port, CancellationToken ct)
    {
        var socket = await OpenProxyAsync(profile, ct);
        try
        {
            using var registration = ct.Register(socket.Dispose);
            var code = await Task.Run(() => NativeEngine.Core_Connect((nuint)socket.Handle, host, checked((ushort)port), profile.Username, profile.Password), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (code != 0) throw new IOException("SOCKS5 協商、驗證或目的端連線失敗。");
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
    public static async Task<UdpAssociation> AssociateAsync(ProxyProfile profile, CancellationToken ct)
    {
        var socket = await OpenProxyAsync(profile, ct);
        try
        {
            using var registration = ct.Register(socket.Dispose);
            var result = await Task.Run(() => { var code = NativeEngine.Core_Associate((nuint)socket.Handle, profile.Username, profile.Password, out var ip, out var port); return (code, ip, port); }, CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (result.code != 0 || result.port == 0) throw new IOException("UDP ASSOCIATE 未成功。");
            var address = result.ip == 0 ? ((IPEndPoint)socket.RemoteEndPoint!).Address : new IPAddress(BitConverter.GetBytes(result.ip));
            var udp = new UdpClient(AddressFamily.InterNetwork); udp.Connect(new IPEndPoint(address, result.port));
            return new(socket, udp);
        }
        catch { socket.Dispose(); throw; }
    }
}
public sealed class UdpAssociation(Socket control, UdpClient udp) : IDisposable
{
    public async Task SendDomainAsync(string host, int port, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var address)) { await SendAsync(address, port, payload, ct); return; }
        string name = new System.Globalization.IdnMapping().GetAscii(host);
        if (name.Length is < 1 or > 253 || Uri.CheckHostName(name) != UriHostNameType.Dns) throw new ArgumentException("無效的 UDP 目的主機名稱。");
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(name), msg = new byte[7 + bytes.Length + payload.Length];
        msg[3] = 3; msg[4] = (byte)bytes.Length; bytes.CopyTo(msg, 5);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(5 + bytes.Length), checked((ushort)port)); payload.CopyTo(msg.AsMemory(7 + bytes.Length));
        await udp.SendAsync(msg, ct);
    }
    public async Task SendAsync(IPAddress address, int port, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("僅支援 IPv4。");
        byte[] msg = new byte[10 + payload.Length]; msg[3] = 1; address.GetAddressBytes().CopyTo(msg, 4); BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), checked((ushort)port)); payload.CopyTo(msg.AsMemory(10));
        await udp.SendAsync(msg, ct);
    }
    public async Task<(IPEndPoint Source, byte[] Payload)> ReceiveAsync(CancellationToken ct)
    {
        var result = await udp.ReceiveAsync(ct); var b = result.Buffer;
        if (b.Length < 10 || b[0] != 0 || b[1] != 0 || b[2] != 0 || b[3] != 1) throw new IOException("無效或不支援的 SOCKS5 UDP 封包。");
        return (new(new IPAddress(b.AsSpan(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(8, 2))), b[10..]);
    }
    public void Dispose() { udp.Dispose(); control.Dispose(); }
}
public sealed record ProbeOptions(string TcpUrl = "https://api.ipify.org", string StunHost = "stun.cloudflare.com", int StunPort = 3478, int TimeoutSeconds = 12, bool RemoteStunDns = false);
public static class ProxyProbe
{
    public static async Task<ProbeResult> RunAsync(ProxyProfile profile, ProbeOptions? options = null, CancellationToken ct = default)
    {
        options ??= new(); var timer = Stopwatch.StartNew(); bool reachable = false, authenticated = false, tcp = false, udp = false; string? tcpExit = null, udpExit = null; long? connectLatency = null, udpLatency = null; List<string> notes = [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            using (await SocksClient.OpenProxyAsync(profile, timeout.Token)) reachable = true;
            connectLatency = timer.ElapsedMilliseconds;
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
                ConnectCallback = async (context, token) => await SocksClient.ConnectAsync(profile, context.DnsEndPoint.Host, context.DnsEndPoint.Port, token) };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
            using var response = await client.GetAsync(options.TcpUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] bytes = new byte[129]; int length = 0;
            while (length < bytes.Length) { int read = await body.ReadAsync(bytes.AsMemory(length), timeout.Token); if (read == 0) break; length += read; }
            if (length > 128) throw new IOException("出口檢測回覆過長。");
            var text = System.Text.Encoding.UTF8.GetString(bytes, 0, length).Trim();
            if (!IPAddress.TryParse(text, out var ip)) throw new IOException("出口檢測未回覆 IP。");
            tcpExit = ip.ToString(); tcp = authenticated = true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        { notes.Add("TCP 檢測失敗（連線、驗證、TLS 或出口服務），未切換直連。"); }
        var latency = timer.ElapsedMilliseconds;
        try
        {
            using var udpTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); udpTimeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var assoc = await SocksClient.AssociateAsync(profile, udpTimeout.Token);
            authenticated = true;
            IPAddress? dest = null;
            if (options.RemoteStunDns) IPAddress.TryParse(options.StunHost, out dest);
            else dest = (await Dns.GetHostAddressesAsync(options.StunHost, udpTimeout.Token)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            byte[] request = new byte[20]; request[1] = 1; BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), 0x2112a442); RandomNumberGenerator.Fill(request.AsSpan(8));
            var udpTimer = Stopwatch.StartNew();
            if (dest is null) await assoc.SendDomainAsync(options.StunHost, options.StunPort, request, udpTimeout.Token);
            else await assoc.SendAsync(dest, options.StunPort, request, udpTimeout.Token);
            var reply = await assoc.ReceiveAsync(udpTimeout.Token);
            if (dest is not null && !reply.Source.Address.Equals(dest) || reply.Source.Port != options.StunPort) throw new IOException("UDP 來源不符。");
            udpExit = ParseStun(reply.Payload, request.AsSpan(8, 12)); udpLatency = udpTimer.ElapsedMilliseconds; udp = true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { notes.Add("UDP 未完成實際 STUN 收發，不視為可用。"); }
        return new(profile.Id, reachable, authenticated, tcp, udp, tcpExit, udpExit, latency, notes.Count == 0 ? "TCP／UDP 出口皆已實測；仍須從受管窗口確認實際出口。" : string.Join(' ', notes), DateTimeOffset.UtcNow)
            { ConnectLatencyMs = connectLatency, UdpLatencyMs = udpLatency };
    }
    public static string ParseStun(ReadOnlySpan<byte> response, ReadOnlySpan<byte> transaction)
    {
        if (response.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(response) != 0x0101 || BinaryPrimitives.ReadUInt32BigEndian(response[4..]) != 0x2112a442 || !response.Slice(8, 12).SequenceEqual(transaction)) throw new IOException("STUN 回覆不符。");
        int end = 20 + BinaryPrimitives.ReadUInt16BigEndian(response[2..]); if (end > response.Length) throw new IOException("STUN 長度不符。");
        for (int i = 20; i + 4 <= end;)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(response[i..]), length = BinaryPrimitives.ReadUInt16BigEndian(response[(i + 2)..]); i += 4;
            if (i + length > end) throw new IOException("STUN 屬性截斷。");
            if ((type == 0x20 || type == 1) && length >= 8 && response[i + 1] == 1)
            {
                uint ip = BinaryPrimitives.ReadUInt32BigEndian(response[(i + 4)..]); if (type == 0x20) ip ^= 0x2112a442;
                Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, ip); return new IPAddress(bytes).ToString();
            }
            i += (length + 3) & ~3;
        }
        throw new IOException("STUN 缺少 IPv4 出口。");
    }
}

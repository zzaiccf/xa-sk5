using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WindowSocks.Core;

// Deliberately uses ordinary sockets with no SOCKS configuration. This checks the installed interception path.
internal static class TransparentTrafficProbe
{
    public static async Task<string> TcpExitAsync(int timeoutSeconds = 18)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds), AllowAutoRedirect = false };
        handler.ConnectCallback = async (context, ct) => {
            var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct)).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        string text = (await client.GetStringAsync("https://api.ipify.org")).Trim();
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork) throw new IOException("出口回覆不是 IPv4。");
        return text;
    }
    public static async Task<object> RunAsync(bool dnsOnly = false)
    {
        string? tcpExit = null, udpExit = null; bool dns = false;
        var errors = new List<string>(); var watch = Stopwatch.StartNew();
        if (!dnsOnly) try {
            tcpExit = await TcpExitAsync();
        } catch (Exception ex) { errors.Add("TCP: " + ex.GetType().Name); }
        if (!dnsOnly) try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var address = (await Dns.GetHostAddressesAsync("stun.cloudflare.com", timeout.Token)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            udp.Connect(address, 3478);
            byte[] request = new byte[20]; request[1] = 1; BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), 0x2112a442);
            RandomNumberGenerator.Fill(request.AsSpan(8, 12));
            await udp.SendAsync(request, timeout.Token);
            var response = await udp.ReceiveAsync(timeout.Token);
            udpExit = ProxyProbe.ParseStun(response.Buffer, request.AsSpan(8, 12));
        } catch (Exception ex) { errors.Add("UDP: " + ex.GetType().Name); }
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            // A deliberately unreachable local resolver proves port-53 rewriting instead of testing the original resolver.
            udp.Connect(IPAddress.Parse("192.0.2.53"), 53);
            byte[] query = [0x5a, 0x17, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
            await udp.SendAsync(query, timeout.Token);
            var response = (await udp.ReceiveAsync(timeout.Token)).Buffer;
            dns = response.Length >= 12 && response[0] == query[0] && response[1] == query[1] && (response[2] & 0x80) != 0 && (response[3] & 15) == 0 && BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)) > 0;
            if (!dns) errors.Add("DNS: InvalidResponse");
        } catch (Exception ex) { errors.Add("DNS: " + ex.GetType().Name); }
        return new { pid = Environment.ProcessId, tcpExit, udpExit, dns, elapsedMs = watch.ElapsedMilliseconds, errors, at = DateTimeOffset.Now };
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WindowSocks.Core;

public sealed record DnsConnectivityResult(string ProxyEndpoint, string Resolver, bool Associated, bool ReplyReceived,
    bool ValidDns, string? ReplySource, int ReplyBytes, long ElapsedMs, string Message);
public static class DnsConnectivityProbe
{
    public static async Task<DnsConnectivityResult> RunAsync(ProxyProfile profile, string resolver, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(resolver, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("DNS 診斷需要 IPv4 位址。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var watch = Stopwatch.StartNew(); bool associated = false, received = false, valid = false; string? source = null; int length = 0;
        string message;
        try {
            using var udp = await SocksClient.AssociateAsync(profile, timeout.Token); associated = true;
            byte[] query = [0x5a, 0x17, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
            await udp.SendAsync(ip, 53, query, timeout.Token); var response = await udp.ReceiveAsync(timeout.Token);
            received = true; source = response.Source.ToString(); var b = response.Payload; length = b.Length;
            valid = length >= 12 && b[0] == query[0] && b[1] == query[1] && (b[2] & 0x80) != 0 && (b[3] & 15) == 0 && BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(6)) > 0;
            message = valid ? "經 SOCKS UDP 取得有效 DNS 回覆。" : "經 SOCKS UDP 收到回覆，但不是有效 DNS 成功回覆。";
        } catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { message = "SOCKS UDP DNS 未完成：" + ex.GetType().Name; }
        return new(profile.Endpoint, ip.ToString(), associated, received, valid, source, length, watch.ElapsedMilliseconds, message);
    }
}

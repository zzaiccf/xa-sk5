using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace WindowSocks.Core;

public static class IpLocationLookup
{
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Until, IpLocation? Value, string Message)> Cache = new();
    public static async Task<ProbeResult> EnrichAsync(ProxyProfile profile, ProbeResult result, CancellationToken ct = default)
    {
        if (!result.Tcp || result.TcpExit is null) return result with { LocationMessage = "未取得 TCP 出口，未查詢所在地" };
        string ip = result.TcpExit;
        if (Cache.TryGetValue(ip, out var cached) && cached.Until > DateTimeOffset.UtcNow)
            return result with { Location = cached.Value, LocationMessage = cached.Message };
        IpLocation? location = null; string message;
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(6));
            // Query the measured IP explicitly through the same SOCKS proxy. No direct fallback or credentials in URL.
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
                ConnectCallback = async (context, token) => await SocksClient.ConnectAsync(profile, context.DnsEndPoint.Host, context.DnsEndPoint.Port, token) };
            using var client = new HttpClient(handler);
            if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) throw new IOException("出口不是 IPv4。");
            using var response = await client.GetAsync($"https://ipwho.is/{parsed}?lang=zh-CN&fields=success,ip,country,region,city,connection.isp", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] buffer = new byte[16385]; int length = 0;
            while (length < buffer.Length) { int read = await body.ReadAsync(buffer.AsMemory(length), timeout.Token); if (read == 0) break; length += read; }
            if (length > 16384) throw new IOException("所在地回覆過長。");
            location = Parse(buffer.AsMemory(0, length), ip); message = "IP 資料庫估計所在地 · ipwho.is";
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or OperationCanceledException or JsonException or ObjectDisposedException) {
            message = "所在地查詢失敗或限流；不影響 TCP／UDP 檢測結果";
        }
        if (Cache.Count > 2000) Cache.Clear();
        Cache[ip] = (DateTimeOffset.UtcNow.AddMinutes(location is null ? 5 : 1440), location, message);
        return result with { Location = location, LocationMessage = message };
    }
    public static IpLocation Parse(ReadOnlyMemory<byte> json, string expectedIp)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("ip", out var ip) || ip.ValueKind != JsonValueKind.String || ip.GetString() != expectedIp) throw new IOException("所在地回覆與出口 IP 不符。");
        static string Read(JsonElement item, string name) {
            if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return "";
            string text = new((value.GetString() ?? "").Where(c => !char.IsControl(c)).Take(160).ToArray());
            if (text.Length == 0) return text;
            char[] converted = new char[text.Length * 2];
            int length = LCMapStringEx("zh-TW", 0x04000000, text, text.Length, converted, converted.Length, 0, 0, 0);
            return length > 0 ? new string(converted, 0, length) : text;
        }
        string isp = root.TryGetProperty("connection", out var connection) && connection.ValueKind == JsonValueKind.Object ? Read(connection, "isp") : "";
        var result = new IpLocation(expectedIp, Read(root, "country"), Read(root, "region"), Read(root, "city"), isp, "ipwho.is");
        if (result.Label.Length == 0) throw new IOException("所在地資料缺失。");
        return result;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength, [Out] char[] destination, int destinationLength, nint version, nint reserved, nint sort);
}

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WindowSocks.Core;

public sealed record IdleTcpResult(string ProxyEndpoint, string Destination, int RequestedIdleSeconds,
    bool Connected, bool InitialEcho, bool IdleCompleted, bool FinalEcho, bool UpstreamEof, long IdleObservedMs,
    string Stage, string Message, DateTimeOffset At);

public static class IdleTcpProbe
{
    // A diagnostic echo service only; never sends guessed application heartbeats to a game server.
    public static async Task<IdleTcpResult> RunAsync(ProxyProfile proxy, int idleSeconds = 150,
        string host = "tcpbin.com", int port = 4242, CancellationToken ct = default)
    {
        if (idleSeconds is < 1 or > 240 || string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            throw new ArgumentException("閒置測試需 1–240 秒及有效目的端。");
        bool connected = false, initial = false, completed = false, final = false, eof = false;
        string stage = "Connect", message; var idleWatch = new Stopwatch();
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(idleSeconds + 30));
        try {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(overall.Token); connect.CancelAfter(TimeSpan.FromSeconds(12));
            using var stream = await SocksClient.ConnectAsync(proxy, host, port, connect.Token); connected = true;
            stage = "InitialEcho";
            byte[] first = Encoding.ASCII.GetBytes("WindowSocks idle diagnostic " + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + "\n");
            initial = await Echo(stream, first, overall.Token);
            if (!initial) throw new InvalidDataException();
            stage = "Idle"; idleWatch.Start();
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            var pending = stream.ReadAsync(new byte[1], observation.Token).AsTask();
            var delay = Task.Delay(TimeSpan.FromSeconds(idleSeconds), overall.Token);
            if (await Task.WhenAny(pending, delay) == pending) {
                eof = await pending == 0; idleWatch.Stop();
                message = eof ? "閒置時收到上游 EOF；只能證明此測試連線被關閉，無法單獨區分代理與回聲服務。" : "閒置時收到非預期資料，本次不算純閒置驗收。";
                return Result();
            }
            await delay; observation.Cancel();
            try { int n = await pending; eof = n == 0; message = eof ? "閒置結束時觀察到上游 EOF。" : "閒置結束時收到非預期資料，本次不算純閒置驗收。"; return Result(); }
            catch (OperationCanceledException) when (!overall.IsCancellationRequested) { }
            idleWatch.Stop(); completed = true; stage = "FinalEcho";
            final = await Echo(stream, Encoding.ASCII.GetBytes("WindowSocks idle diagnostic complete\n"), overall.Token);
            stage = final ? "Passed" : "FinalEchoMismatch";
            message = final ? "同一 SOCKS TCP 通道經指定閒置時間後仍能實際收發；不代表遊戲登入或其他目的端通過。" : "閒置後回聲未匹配，不能判定長連線可用。";
        } catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) {
            eof |= ex is EndOfStreamException;
            message = "測試未完成：" + stage + " / " + ex.GetType().Name + "；未切換直連，未記錄帳密。";
        }
        return Result();
        IdleTcpResult Result() => new(proxy.Endpoint, $"{host}:{port}", idleSeconds, connected, initial, completed, final, eof,
            idleWatch.ElapsedMilliseconds, stage, message, DateTimeOffset.UtcNow);
    }
    private static async Task<bool> Echo(Stream stream, byte[] payload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await stream.WriteAsync(payload, timeout.Token);
        byte[] response = new byte[payload.Length]; await stream.ReadExactlyAsync(response, timeout.Token);
        return payload.AsSpan().SequenceEqual(response);
    }
}

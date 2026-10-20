using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WindowSocks.Core;

public sealed record NetworkFlowEvent(ulong Sequence, ulong FlowId, DateTimeOffset At, ProcessIdentity Process,
    string ProxyEndpoint, string Protocol, string SourceAddress, int SourcePort, string DestinationAddress, int DestinationPort,
    uint StageCode, string Stage, uint Error, ulong Sent, ulong Received)
{
    public string Label => $"{At.ToLocalTime():HH:mm:ss.fff} · PID {Process.Pid} · {Protocol} {DestinationAddress}:{DestinationPort} · {Stage}{(Error == 0 ? "" : $"（錯誤 {Error}）")} · ↑{Sent} ↓{Received}";
    public static string StageLabel(uint code) => code switch {
        1 => "捕獲新連線", 2 => "SOCKS 通道已建立", 3 => "遊戲／程序已關閉傳送方向", 4 => "代理上游 EOF（來源可能為代理或目的伺服器）",
        5 => "連線已結束", 6 => "規則變更／封鎖，關閉連線", 7 => "程序 socket 已消失，清理殘留連線", 8 => "閒置逾時清理",
        20 => "無法連線至代理端", 21 => "SOCKS 驗證／目的端協商失敗", 22 => "等待 socket 失敗", 23 => "讀取遊戲／程序失敗",
        24 => "讀取代理上游失敗", 25 => "傳送至代理失敗", 26 => "傳送至遊戲／程序失敗", 27 => "UDP 關聯失敗", 28 => "背景核心停止", _ => "未分類事件"
    };
}
public sealed record DiagnosticProcess(ProcessIdentity Identity, string Path, bool Running, bool? Responding,
    string WindowTitle, long WindowHandle, long Sent, long Received, long Dropped, uint Tcp, uint Udp);
public sealed record DiagnosticTarget(string Key, string Name, string ProxyEndpoint, RuntimeStatus? Status,
    ProbeResult? ProxyProbe, DiagnosticProcess[] Processes);
public sealed record DiagnosticBundle(DateTimeOffset At, string Version, bool EngineRunning, DiagnosticTarget[] Targets,
    NetworkFlowEvent[] FlowEvents, string[] Logs, string Note)
{
    public DriverCompatibility? Compatibility { get; init; }
}
public sealed record DriverCompatibility(string Mode, bool DriverFilesPresent, bool EngineRunning,
    string DllVersion, string DriverSha256, string Signature, string Assessment);
public sealed record TargetProbeRequest(Guid ProxyId, string Address, int Port, int ObserveSeconds = 3);
public sealed record TargetProbeResult(string ProxyEndpoint, string Destination, bool Connected, long ConnectMs,
    bool UpstreamEof, int ReceivedBytes, long ObservedMs, string Message, DateTimeOffset At);

public static class TargetConnectivityProbe
{
    // Sends no game commands, credentials or guessed heartbeats; only SOCKS negotiation and passive reads.
    public static async Task<TargetProbeResult> RunAsync(ProxyProfile proxy, TargetProbeRequest request, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(request.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || request.Port is < 1 or > 65535 || request.ObserveSeconds is < 1 or > 10)
            throw new InvalidOperationException("目的端請填寫 IPv4、1–65535 連接埠及 1–10 秒觀察時間。");
        var watch = Stopwatch.StartNew(); bool connected = false, eof = false; int received = 0; long connectMs = 0;
        string message;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try {
            using var stream = await SocksClient.ConnectAsync(proxy, ip.ToString(), request.Port, timeout.Token);
            connected = true; connectMs = watch.ElapsedMilliseconds;
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); observation.CancelAfter(TimeSpan.FromSeconds(request.ObserveSeconds));
            byte[] bytes = new byte[1024];
            try {
                while (received < 65536) {
                    int n = await stream.ReadAsync(bytes, observation.Token);
                    if (n == 0) { eof = true; break; } received += n;
                }
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            message = eof ? "代理通道已建立，觀察到上游 EOF；單靠 EOF 無法區分代理或目的端關閉。" : "代理通道已建立；觀察期間未收到 EOF。不代表遊戲登入或長連線驗收通過。";
        } catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) {
            message = connected ? "通道建立後讀取失敗：" + ex.GetType().Name : "代理連線／驗證／目的端協商失敗：" + ex.GetType().Name;
        }
        return new(proxy.Endpoint, $"{ip}:{request.Port}", connected, connectMs, eof, received, watch.ElapsedMilliseconds, message, DateTimeOffset.UtcNow);
    }
}

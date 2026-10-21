using System;
using System.Linq;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public sealed record GlobalProxyDisplay
{
    public string Title { get; init; } = "全局未開啟";
    public string Hint { get; init; } = "";
    public string Foreground { get; init; } = "#68778C";
    public string Background { get; init; } = "#FFFFFF";
    public string Border { get; init; } = "#E7ECF3";
    public bool ShowBanner { get; init; }
    public bool ShowNode { get; init; }
    public bool CanStop { get; init; }
    public string NodeName { get; init; } = "";
    public string ExitLabel { get; init; } = "實際 IP";
    public string ExitIp { get; init; } = "未驗證";
    public string ExitHint { get; init; } = "";
    public string AccountPort { get; init; } = "—";
    public string EntryIp { get; init; } = "—";
    public string RemotePolicy { get; init; } = "";
    public string LanPolicy { get; init; } = "";
    public string Detail { get; init; } = "";

    public static GlobalProxyDisplay From(Snapshot current, bool online, Snapshot? lastConnected = null)
    {
        bool connected = online && current.Diagnostics.ServiceOnline;
        var source = connected ? current : lastConnected;
        var settings = source?.GlobalProxy;
        var profile = source?.Proxies.FirstOrDefault(p => p.Id == settings?.ProxyId);
        var probe = source?.Probes.FirstOrDefault(p => p.ProxyId == profile?.Id);
        bool enabled = settings?.Enabled == true;
        bool engine = connected && current.Diagnostics.EngineRunning;
        var result = new GlobalProxyDisplay {
            ShowBanner = enabled,
            ShowNode = enabled && profile is not null,
            CanStop = connected && enabled,
            NodeName = profile?.Name ?? (enabled ? "指定節點不存在" : ""),
            ExitIp = string.IsNullOrWhiteSpace(probe?.TcpExit) ? "未驗證" : probe.TcpExit,
            ExitLabel = !connected ? "上次實際 IP" : probe is not null && ProbeTiming.IsStale(probe.At, DateTimeOffset.UtcNow) ? "實際 IP（已過期）" : "實際 IP",
            ExitHint = probe is null ? "尚無節點 TCP 出口檢測結果。" : $"節點 TCP 出口檢測：{probe.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}。各程式的實際出口仍須從程式內確認。",
            AccountPort = profile is null ? "—" : $"{(string.IsNullOrEmpty(profile.Username) ? "免帳號" : profile.Username)}／{profile.Port}",
            EntryIp = profile?.Host ?? "—",
            Detail = connected ? current.GlobalProxyStatus : "背景服務未連線，無法確認目前代理與封鎖狀態。" + (enabled ? "顯示上次連線取得的節點資料。" : "")
        };
        if (!connected) return Warning(result with {
            Title = "全局狀態未確認", Hint = enabled ? "背景服務未連線；以下為上次線路資料，當前狀態未確認。" : "背景服務未連線，請重新連線後確認。",
            RemotePolicy = enabled ? $"遠控直連：上次{(settings!.BypassRemoteControl ? "開啟" : "關閉")}" : "遠控直連：未確認",
            LanPolicy = enabled ? $"區網直連：上次{(settings!.BypassLan ? "開啟" : "關閉")}" : "區網直連：未確認"
        });
        if (!enabled) return result;
        result = result with {
            RemotePolicy = !settings!.BypassRemoteControl ? "遠控直連：關閉"
                : !current.SupportsRemoteControlBypass ? "遠控直連：待更新核心"
                : !engine ? "遠控直連：待核心啟用" : "遠控直連：開啟",
            LanPolicy = $"區網直連：{(settings.BypassLan ? "開啟" : "關閉")}"
        };
        if (!engine) return Warning(result with { Title = "全局尚未生效", Hint = "全局設定已開啟，代理核心尚未執行。" });
        if (profile is null) return Warning(result with { Title = "全局代理受阻", Hint = "找不到指定節點，請重新選擇線路。" });

        // Compatibility with 0.1.9–0.1.18 services. Match the complete state prefix,
        // including the node name, rather than mistaking a name for a routing result.
        bool? routing = current.GlobalProxyRouting;
        if (routing is null) {
            string prefix = $"全局：{profile.Name} · ";
            if (current.GlobalProxyStatus.StartsWith(prefix + "代理執行中 · ", StringComparison.Ordinal)) routing = true;
            else if (current.GlobalProxyStatus.StartsWith(prefix + "代理未通過檢測，外網保持封鎖 · ", StringComparison.Ordinal)) routing = false;
        }
        if (routing is null) return Warning(result with { Title = "全局狀態未確認", Hint = "已開啟全局設定，尚未確認線路狀態。" });
        if (!routing.Value) return result with {
            Title = "全局外網已封鎖", Hint = "節點檢測未通過，其餘外網保持封鎖。",
            Foreground = "#B5473C", Background = "#FFF3F1", Border = "#F3CCC6"
        };
        return result with {
            Title = "全局代理中", Hint = "其餘 IPv4 TCP／UDP 使用此線路；指定窗口規則優先。",
            Foreground = "#218552", Background = "#EDF8F1", Border = "#BFE3CD"
        };
    }
    private static GlobalProxyDisplay Warning(GlobalProxyDisplay result) => result with { Foreground = "#A36D14", Background = "#FFF8EB", Border = "#EED9AD" };
}

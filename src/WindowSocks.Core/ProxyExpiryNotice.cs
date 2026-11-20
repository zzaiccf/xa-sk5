namespace WindowSocks.Core;

public sealed record ProxyExpiryNotice(Guid Id, DateOnly Date, string Stage, string Message)
{
    public static string DateLabel(DateOnly? expires, DateOnly today)
    {
        if (expires is not { } date) return "";
        int days = date.DayNumber - today.DayNumber;
        return days <= 0 ? $"{date:yyyy-MM-dd}（已到期）" : days <= 3 ? $"{date:yyyy-MM-dd}（剩 {days} 天）" : $"{date:yyyy-MM-dd}";
    }

    public static ProxyExpiryNotice[] Collect(Snapshot snapshot, DateOnly today)
    {
        var notices = new List<ProxyExpiryNotice>();
        var global = snapshot.GlobalProxy;
        if (!global.Enabled && global.ExpiryFallbackOn is { } switched)
            notices.Add(new(global.ProxyId, switched, "local", $"全局線路已於 {switched:yyyy-MM-dd} 到期，一般程式已切換本地網路。若要恢復代理，請選擇有效線路。"));
        foreach (var p in snapshot.Proxies.Where(p => p.ExpiresOn.HasValue)
            .OrderByDescending(p => global.Enabled && p.Id == global.ProxyId).ThenBy(p => p.ExpiresOn)) {
            var date = p.ExpiresOn!.Value;
            int days = date.DayNumber - today.DayNumber;
            if (days > 3 || (!global.Enabled && global.ProxyId == p.Id && global.ExpiryFallbackOn == date)) continue;
            bool activeGlobal = global.Enabled && p.Id == global.ProxyId;
            string name = string.IsNullOrWhiteSpace(p.Name) ? p.Endpoint : p.Name;
            string text = $"{(activeGlobal ? "全局線路" : "線路")}「{name}」{(days <= 0 ? "已到期" : $"將於 {date:MM-dd} 到期（剩 {days} 天）")}。";
            if (activeGlobal) text += snapshot.SupportsGlobalExpiryFallback
                ? (days > 0 ? "到期日 00:00 將自動切換本地網路。" : "正在等候背景服務確認切換。")
                : "請停用全局代理以切換本地網路，並更新背景服務。";
            else text += "請在「代理與檢測」續期或為使用中的窗口換線。";
            notices.Add(new(p.Id, date, (activeGlobal ? "global-" : "node-") + (days <= 0 ? "expired" : days == 1 ? "tomorrow" : "soon"), text));
        }
        return notices.ToArray();
    }

    public static string Summary(IReadOnlyList<ProxyExpiryNotice> notices) => notices.Count switch {
        0 => "",
        1 => notices[0].Message,
        _ => $"{notices[0].Message} 另有 {notices.Count - 1} 則線路提醒，請查看「代理與檢測」。"
    };
}

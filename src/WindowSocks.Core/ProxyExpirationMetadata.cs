namespace WindowSocks.Core;

// Compatibility for a local desktop update while the installed service keeps running.
public static class ProxyExpirationMetadata
{
    public static void ValidateLegacyImport(IEnumerable<ProxyProfile> before, IReadOnlyCollection<ProxyProfile> imported)
    {
        foreach (var p in imported.Where(p => p.ExpiresOn.HasValue))
            if (before.Count(x => ProxyImport.SameEndpointAccount(x, p)) > 1 || imported.Count(x => ProxyImport.SameEndpointAccount(x, p)) > 1)
                throw new InvalidOperationException("目前背景服務無法區分相同入口與帳號的多組密碼節點到期時間，請先更新背景服務後再匯入。");
    }

    public static void Remember(IEnumerable<ProxyProfile> before, IEnumerable<ProxyProfile> after,
        IEnumerable<ProxyProfile> imported, IDictionary<Guid, DateOnly> dates)
    {
        var previous = before.Select(p => p.Id).ToHashSet();
        foreach (var p in imported.Where(p => p.ExpiresOn.HasValue)) {
            var matches = after.Where(x => ProxyImport.SameEndpointAccount(x, p)).ToArray();
            var added = matches.Where(x => !previous.Contains(x.Id)).ToArray();
            var target = added.Length == 1 ? added[0] : matches.Length == 1 ? matches[0] : null;
            if (target is null) throw new InvalidOperationException("無法確認到期時間對應的節點，請更新背景服務後重新匯入。");
            dates[target.Id] = p.ExpiresOn!.Value;
        }
    }

    public static ProxyProfile[] Apply(IEnumerable<ProxyProfile> profiles, IReadOnlyDictionary<Guid, DateOnly> dates) =>
        profiles.Select(p => p.ExpiresOn is null && dates.TryGetValue(p.Id, out var date) ? p with { ExpiresOn = date } : p).ToArray();
}

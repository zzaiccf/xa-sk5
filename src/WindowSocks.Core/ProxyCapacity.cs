namespace WindowSocks.Core;

public static class ProxyCapacity
{
    public static int For(Guid proxy, int fallback, IReadOnlyDictionary<Guid, int>? capacities) => capacities?.GetValueOrDefault(proxy, fallback) ?? fallback;
    public static void Validate(int fallback, IReadOnlyDictionary<Guid, int>? capacities)
    {
        if (fallback is < 1 or > 10) throw new InvalidOperationException("預設線路數量請填 1–10。");
        if (capacities is null || capacities.Count > 10000 || capacities.Any(p => p.Key == Guid.Empty || p.Value is < 0 or > 10))
            throw new InvalidOperationException("各線路可分配數量請填 0–10；0 表示不再分配新目標。");
    }
    public static AutoProxyRule Remove(AutoProxyRule rule, IEnumerable<Guid> ids)
    {
        var removed = ids.ToHashSet();
        return rule with { ProxyCapacities = rule.ProxyCapacities.Where(p => !removed.Contains(p.Key)).ToDictionary() };
    }
}

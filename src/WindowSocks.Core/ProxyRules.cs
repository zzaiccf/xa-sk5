namespace WindowSocks.Core;

public sealed record ProxyRuleItem(string Key, string Name, string Type, string Allocation, string Capacity, string Members, string State, string Detail);
public sealed record DeleteRuleRequest(string Key);

public static class ProxyRules
{
    public static bool HasAutomatic(AutoProxyRule rule, IEnumerable<InstanceBinding> bindings) => rule.Configured || rule.Enabled || rule.Kind is "Emulator" or "EmulatorSequence" || rule.Pattern.Length > 0 || rule.ClientPath.Length > 0 || rule.EmulatorAssignments.Length > 0 || bindings.Any(b => b.AutoManaged);
    public static ProxyRuleItem[] List(Snapshot state)
    {
        var rows = new List<ProxyRuleItem>(); var rule = state.AutoProxy;
        if (HasAutomatic(rule, state.Bindings)) {
            bool fixedMode = rule.Kind == "Emulator";
            string type = rule.Kind switch { "GameGroup" => "遊戲組", "Process" => "一般程序", _ => "模擬器" };
            string name = rule.Kind switch { "Process" => rule.Pattern, "GameGroup" => rule.Pattern + " ＋ " + rule.ClientPath, "Emulator" => "模擬器固定分配", "EmulatorSequence" when rule.EmulatorKind == "MuMu" => "MuMu 啟動順序", _ => "雷電啟動順序" };
            rows.Add(new("automatic", name, type, fixedMode ? "固定窗口" : rule.Kind is "LDPlayer" or "EmulatorSequence" ? "依啟動順序" : "自動匹配", fixedMode ? "依固定對應" : $"每 IP {rule.PerProxy} 個窗口", fixedMode ? $"{rule.EmulatorAssignments.Length} 個固定窗口" : $"{state.Bindings.Count(b => b.AutoManaged)} 個管理中", state.HasPendingEmulatorConfiguration ? "等待服務套用" : rule.Enabled ? "已啟用" : "已暫停", state.AutoProxyStatus));
        }
        if (state.Vmware is { } vm && vm.Settings.Machines.Length > 0 && !vm.Settings.RuleDeleted) {
            var settings = vm.Settings;
            rows.Add(new("vmware", "VMware 虛擬機代理", "虛擬機", settings.ByStartup ? "依啟動順序" : "固定虛擬機", settings.ByStartup ? $"每 IP {settings.PerProxy} 個窗口" + (settings.OverflowToLocal ? " · 超額本地" : "") : "依固定對應", $"{settings.Machines.Length} 台虛擬機", !state.Diagnostics.ServiceOnline ? "待服務連線" : settings.PendingPreparation ? settings.PreparationError.Length > 0 ? "已儲存 · 準備未完成" : "已儲存 · 等待準備" : !settings.Enabled ? "已暫停" : !vm.Prepared ? "尚未準備" : "已啟用", string.Join("、", settings.Machines.Select(m => m.Name.Length > 0 ? m.Name : System.IO.Path.GetFileNameWithoutExtension(m.VmxPath))) + "；" + vm.Detail));
        }
        rows.AddRange(state.Bindings.Where(b => !b.AutoManaged).Select(b => new ProxyRuleItem("binding:" + b.TargetKey, b.Name, EmulatorCatalog.IsEmulator(b.Kind) ? "模擬器" : "一般程序", "指定代理", b.SharedProxy ? "共用 IP" : "專用 IP", "1 個窗口", b.Enabled ? "已啟用" : "已暫停", b.Path)));
        return rows.ToArray();
    }
    public static SavedConfig Delete(SavedConfig current, string key)
    {
        if (key == "automatic") return current with { AutoProxy = new(), PendingEmulatorRule = null, Bindings = current.Bindings.Where(b => !b.AutoManaged).ToList(), GameGroups = [], AutoExcludedTargets = [], AutoExcludedDescendants = [] };
        if (key == "vmware") return current with { Vmware = current.Vmware with { Enabled = false, PendingPreparation = false, RuleDeleted = true }, VmwareSessions = [] };
        if (key.StartsWith("binding:", StringComparison.Ordinal)) {
            string target = key[8..];
            if (!current.Bindings.Any(b => b.TargetKey == target)) throw new InvalidOperationException("此規則已移除，請重新整理。");
            return current with { Bindings = current.Bindings.Where(b => b.TargetKey != target).ToList(), AutoExcludedTargets = current.AutoExcludedTargets.Append(target).Distinct().ToList() };
        }
        throw new InvalidOperationException("無法辨識要刪除的規則。");
    }
}

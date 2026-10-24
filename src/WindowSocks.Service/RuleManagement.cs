using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed partial class Controller
{
    private void DeleteRule(DeleteRuleRequest request)
    {
        var original = config with { }; var previousRuntime = vmwareRuntime;
        var next = ProxyRules.Delete(config, request.Key);
        try {
            ReplaceRuleState(next);
            if (request.Key == "vmware") vmwareRuntime = [];
            if (request.Key == "automatic") gameObserver.ResetRule(config.AutoProxy, config.GameGroups);
            ApplyRules(); store.Save(config);
            automaticUnassigned.Clear();
            if (request.Key == "vmware") vmwareError = "VMware 規則已刪除，代理已停止；原網路備份保留，可在進階設定回復。";
            if (request.Key == "automatic") autoStatus = "自動代理規則已刪除";
            Log("已刪除代理規則，其他規則及節點保留。");
        } catch {
            ReplaceRuleState(original); vmwareRuntime = previousRuntime;
            if (request.Key == "automatic") gameObserver.ResetRule(config.AutoProxy, config.GameGroups);
            ApplyRules(); throw;
        }
    }
    private void ReplaceRuleState(SavedConfig state)
    {
        config.AutoProxy = state.AutoProxy; config.PendingEmulatorRule = state.PendingEmulatorRule;
        config.Bindings = state.Bindings; config.GameGroups = state.GameGroups;
        config.AutoExcludedTargets = state.AutoExcludedTargets; config.AutoExcludedDescendants = state.AutoExcludedDescendants;
        config.Vmware = state.Vmware; config.VmwareSessions = state.VmwareSessions;
    }
}

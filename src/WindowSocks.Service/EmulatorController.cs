using System.Management;
using System.Security.Cryptography;
using System.Text;
using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed partial class Controller
{
    private static string CommandHash(string command) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Trim())));
    private TargetInstance CaptureEmulatorIdentity(TargetInstance target)
    {
        if (target.Kind != "MuMu" || target.Processes.Length == 0) return target;
        ValidateTarget(target);
        var process = target.Processes.Single();
        using var searcher = new ManagementObjectSearcher($"SELECT ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId={process.Pid}");
        using var rows = searcher.Get();
        foreach (ManagementObject row in rows) using (row) {
            string path = (string?)row["ExecutablePath"] ?? "", command = (string?)row["CommandLine"] ?? "";
            if (path.Length > 0 && command.Length > 0 && LdPlayer.Identity(process.Pid) == process)
                return target with { EmulatorProcessPath = path, EmulatorCommandHash = CommandHash(command) };
        }
        return target with { EmulatorProcessPath = "", EmulatorCommandHash = "" };
    }
    private void SetFixedEmulators(AutoProxyRule rule)
    {
        AutoProxyPlanner.Validate(rule);
        var next = FixedEmulatorPlanner.Prepare(rule, config.AutoProxy, config.Bindings, config.Proxies, targets.Values);
        if (rule.Enabled) {
            foreach (var item in rule.EmulatorAssignments) {
                EmulatorCatalog.ValidateConsole(item.Kind, item.ConsolePath);
                if (item.Kind == "LDPlayer" && !LdPlayer.IsNat(item.ConsolePath, item.Index)) throw new InvalidOperationException($"雷電 #{item.Index} 不是可辨識的單網卡 NAT 模式。");
            }
            EnsureEngine();
        }
        var previousRule = config.AutoProxy; var previousBindings = config.Bindings;
        try {
            config.AutoProxy = rule; config.Bindings = next;
            config.AutoExcludedTargets.RemoveAll(k => rule.EmulatorAssignments.Any(a => a.TargetKey == k));
            gameObserver.SetRule(rule);
            DiscoverAutomaticTargets(); ApplyRules(); store.Save(config);
        } catch {
            config.AutoProxy = previousRule; config.Bindings = previousBindings; gameObserver.SetRule(previousRule); ApplyRules(); throw;
        }
        autoStatus = rule.Enabled ? $"固定窗口 {rule.EmulatorAssignments.Length} 個 · 等待程序辨識與 TCP／UDP 檢測" : "固定窗口配置已儲存 · 自動代理已暫停";
        Log(autoStatus);
    }
    private void SetSequentialEmulators(AutoProxyRule rule)
    {
        AutoProxyPlanner.Validate(rule);
        var next = SequentialEmulatorPlanner.Prepare(rule, config.Bindings, LdPlayer.Identity);
        if (rule.Enabled) { EmulatorCatalog.ValidateConsole(rule.EmulatorKind, rule.ConsolePath); EnsureEngine(); }
        var previousRule = config.AutoProxy; var previousBindings = config.Bindings;
        var previousExcluded = config.AutoExcludedTargets.ToList();
        try {
            bool changedScope = previousRule.Kind != rule.Kind || previousRule.EmulatorKind != rule.EmulatorKind ||
                !previousRule.ConsolePath.Equals(rule.ConsolePath, StringComparison.OrdinalIgnoreCase);
            if (changedScope) config.AutoExcludedTargets.Clear();
            config.AutoProxy = rule; config.Bindings = next; gameObserver.SetRule(rule);
            DiscoverAutomaticTargets(); ApplyRules(); store.Save(config);
        } catch {
            config.AutoProxy = previousRule; config.Bindings = previousBindings; config.AutoExcludedTargets = previousExcluded;
            gameObserver.SetRule(previousRule); ApplyRules(); throw;
        }
        autoStatus = rule.Enabled ? "依啟動順序分配 · 已有窗口保留 IP，關閉後釋放名額" : "依啟動順序配置已儲存 · 自動代理已暫停";
        Log(autoStatus);
    }
    private void DiscoverMuMuRestarts()
    {
        var bindings = config.Bindings.Where(b => b.Kind == "MuMu").Concat(targets.Values.Where(t => t.Kind == "MuMu" &&
            config.AutoProxy.Enabled && config.AutoProxy.Kind == "EmulatorSequence" && SequentialEmulatorPlanner.Matches(t, config.AutoProxy))
            .Select(t => new InstanceBinding { TargetKey = t.Key, Name = t.Name, Kind = t.Kind, Path = t.Path, Index = t.Index, Processes = t.Processes,
                EmulatorProcessPath = t.EmulatorProcessPath, EmulatorCommandHash = t.EmulatorCommandHash })).DistinctBy(b => b.TargetKey).ToArray();
        if (bindings.Length == 0) return;
        // No user-owned MuMuManager is executed as LocalSystem. Learn the VM command fingerprint
        // from CLI-discovered, owner-validated headless PIDs; ambiguity never falls back to launch order.
        var names = bindings.Where(b => b.EmulatorProcessPath.Length > 0).Select(b => Path.GetFileName(b.EmulatorProcessPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var found = new List<(ProcessIdentity Identity, string Path, string Hash)>();
        if (names.Length > 0 && EmulatorProcessesPresent(names)) {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE " + string.Join(" OR ", names.Select(n => "Name='" + n.Replace("'", "''") + "'")));
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows) using (row) {
                int pid = checked((int)(uint)row["ProcessId"]); string path = (string?)row["ExecutablePath"] ?? "", command = (string?)row["CommandLine"] ?? "";
                if (command.Length > 0 && IsOwned(pid) && LdPlayer.Identity(pid) is { } identity) found.Add((identity, path, CommandHash(command)));
            }
        }
        foreach (var binding in bindings) {
            var current = targets.GetValueOrDefault(binding.TargetKey);
            string path = current?.EmulatorProcessPath.Length > 0 ? current.EmulatorProcessPath : binding.EmulatorProcessPath;
            string hash = current?.EmulatorCommandHash.Length > 0 ? current.EmulatorCommandHash : binding.EmulatorCommandHash;
            var matches = found.Where(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.Hash == hash).ToArray();
            if (matches.Length == 1) targets[binding.TargetKey] = new(binding.TargetKey, binding.Name, "MuMu", binding.Path, binding.Index, true, [matches[0].Identity]) { EmulatorProcessPath = path, EmulatorCommandHash = hash };
            else if (matches.Length > 1 || !binding.Processes.Any(p => LdPlayer.Identity(p.Pid) == p))
                targets[binding.TargetKey] = new(binding.TargetKey, binding.Name, "MuMu", binding.Path, binding.Index, false, [], matches.Length > 1 ? "MuMu 程序識別重複，等待唯一辨識" : "等待 MuMu 啟動；首次連入請開啟介面讀取清單") { EmulatorProcessPath = path, EmulatorCommandHash = hash };
        }
    }
    private void UpdateSessions()
    {
        bool running = NativeEngine.Present && NativeEngine.Core_IsRunning() != 0;
        globalSession.Synchronize(!stopping && config.GlobalProxy.Enabled && running && Healthy(config.GlobalProxy.ProxyId)
            ? config.GlobalProxy.ProxyId : null, DateTimeOffset.UtcNow);
        sessions.Synchronize(config.Bindings.Select(b => new ProxySessionState(b.TargetKey, b.ProxyId,
            b.Kind == "Process" && b.IncludeChildren ? b.Processes : b.Processes.Where(p => LdPlayer.Identity(p.Pid) == p && IsOwned(p.Pid)).ToArray(),
            running && b.Enabled && !conflicts.Contains(b.TargetKey) && Healthy(b.ProxyId) && !DuplicateExit(b.ProxyId))), DateTimeOffset.UtcNow);
    }
}

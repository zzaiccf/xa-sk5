using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowSocks.Core;

public sealed record VmwareMachine(string Path, string Name, string Uuid, string Mac, string Connection, string Vnet)
{
    public string Key => "vmware:" + System.IO.Path.GetFullPath(Path).ToUpperInvariant();
}
public sealed record VmwareMapping(string VmxPath, string GuestIp, Guid ProxyId)
{
    public string Mac { get; init; } = "";
    public string Uuid { get; init; } = "";
    public bool UseLocalNetwork { get; init; }
    public string Name { get; init; } = "";
}
public sealed record VmwareSettings
{
    public bool Enabled { get; init; }
    public bool ByStartup { get; init; }
    public int PerProxy { get; init; } = 1;
    public string AdapterId { get; init; } = "";
    public VmwareMapping[] Machines { get; init; } = [];
    public bool PendingPreparation { get; init; }
    public bool RuleDeleted { get; init; }
    public bool OverflowToLocal { get; init; }
    public bool NeedsNetworkPlan { get; init; }
    public string PreparationError { get; init; } = "";
}
public sealed record VmwareRuntime(string VmxPath, ProcessIdentity? Process, Guid ProxyId, string State, DateTimeOffset? ConnectedAt = null)
{
    public bool UseLocalNetwork { get; init; }
    public long Sent { get; init; }
    public long Received { get; init; }
    public uint TcpConnections { get; init; }
    public uint UdpAssociations { get; init; }
    public bool StatsAvailable { get; init; }
}
public sealed record VmwareAdapter(string Id, string Name, int Index, string Address, string Mask);
public sealed record VmwareState(VmwareSettings Settings, VmwareRuntime[] Machines, bool Prepared, bool Running, string Detail)
{
    public bool HasBackup { get; init; }
}

public static class VmwareVmx
{
    public static Encoding TextEncoding(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string name = Regex.Match(text, "(?m)^\\s*\\.encoding\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
        return name.Length == 0 ? new UTF8Encoding(false, true) : Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public static string ReadText(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("VMX 設定超出限制。");
        byte[] bytes = File.ReadAllBytes(path);
        return TextEncoding(Encoding.ASCII.GetString(bytes)).GetString(bytes).TrimStart('\ufeff');
    }
    public static string NormalizePath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !System.IO.Path.GetExtension(path).Equals(".vmx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("請選擇本機 VMware .vmx 檔案。");
        string full = System.IO.Path.GetFullPath(path); AppReleasePackage.RejectLinks(full); return full;
    }
    public static Dictionary<string, string> Parse(string text)
    {
        if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("VMX 設定超出限制。");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n')) {
            var match = Regex.Match(line, "^\\s*([A-Za-z0-9_.:-]+)\\s*=\\s*\"([^\"]*)\"\\s*(?:#.*)?$");
            if (match.Success && !values.TryAdd(match.Groups[1].Value, match.Groups[2].Value)) throw new InvalidDataException("VMX 有重複欄位，無法安全辨識。");
        }
        return values;
    }
    public static VmwareMachine Read(string path)
    {
        path = NormalizePath(path); if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("VMX 設定超出限制。");
        return Describe(path, Parse(ReadText(path)));
    }
    public static VmwareMachine Describe(string path, IReadOnlyDictionary<string, string> values)
    {
        string Get(string key) => values.GetValueOrDefault(key) ?? "";
        if (!Get("ethernet0.present").Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
            values.Any(v => Regex.IsMatch(v.Key, @"^ethernet[1-9]\d*\.present$", RegexOptions.IgnoreCase) && v.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("目前只支援單張啟用網卡的 VMware 虛擬機。");
        string mac = Get("ethernet0.addressType").Equals("static", StringComparison.OrdinalIgnoreCase) ? Get("ethernet0.address") : Get("ethernet0.generatedAddress");
        if (mac.Length > 0 && !Regex.IsMatch(mac, @"^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$", RegexOptions.IgnoreCase)) throw new InvalidDataException("虛擬網卡 MAC 無效。");
        string uuid = Get("uuid.bios"); if (uuid.Length == 0) throw new InvalidDataException("請先由 VMware 開啟並關閉此虛擬機一次，以建立固定身分。");
        return new(path, Get("displayName") is { Length: > 0 } name ? name : System.IO.Path.GetFileNameWithoutExtension(path), uuid, mac.ToLowerInvariant(), Get("ethernet0.connectionType"), Get("ethernet0.vnet"));
    }
    public static string Configure(string text, string vnet, out string mac)
    {
        if (!Regex.IsMatch(vnet, @"^VMnet(?:[1-7]|9|1[0-9])$", RegexOptions.IgnoreCase)) throw new InvalidDataException("請使用獨立 Host-only VMnet，不能使用 VMnet0 或 VMnet8。");
        var values = Parse(text); var vm = Describe("fixture.vmx", values); mac = vm.Mac;
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ethernet0.connectionType"] = "custom", ["ethernet0.vnet"] = vnet, ["ethernet0.startConnected"] = "TRUE" };
        if (mac.Length == 0) { byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(vm.Uuid)); mac = $"00:50:56:{hash[0] % 64:x2}:{hash[1]:x2}:{hash[2]:x2}"; changes["ethernet0.addressType"] = "static"; changes["ethernet0.address"] = mac; }
        string result = text;
        foreach (var pair in changes) {
            string replacement = pair.Key + " = \"" + pair.Value + "\"";
            string pattern = @"(?m)^\s*" + Regex.Escape(pair.Key) + @"\s*=.*$";
            result = Regex.IsMatch(result, pattern, RegexOptions.IgnoreCase) ? Regex.Replace(result, pattern, _ => replacement, RegexOptions.IgnoreCase) : result.TrimEnd() + "\r\n" + replacement + "\r\n";
        }
        return result;
    }
    public static string[] CommandArguments(string command)
    {
        // VMware VMX paths are quoted Windows arguments, not arbitrary substring matches.
        return Regex.Matches(command, "\"([^\"]*)\"|([^\\s\"]+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
    }
}

public static class VmwarePlanner
{
    public static bool HasDuplicateExit(Guid id, IEnumerable<VmwareRuntime> runtime, IReadOnlyDictionary<Guid, ProbeResult> probes) =>
        AssignmentPlanner.HasDuplicateExit(id, runtime.Where(r => r.Process.HasValue && r.ProxyId != Guid.Empty).Select(r => new InstanceBinding { ProxyId = r.ProxyId }), probes);
    public static void Validate(VmwareSettings settings, IReadOnlyCollection<ProxyProfile> proxies, bool requirePlanned = false)
    {
        if (requirePlanned && settings.NeedsNetworkPlan) throw new InvalidOperationException("網路計畫尚未完成，目前只保存規則，尚未代理。");
        if (settings.Machines.Length > 10 || settings.PerProxy is < 1 or > 10) throw new InvalidOperationException("最多管理 10 台虛擬機，每個 IP 可供 1–10 台共用。");
        if (settings.Machines.Select(m => VmwareVmx.NormalizePath(m.VmxPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Machines.Length || settings.Machines.Where(m => m.GuestIp.Length > 0).Select(m => m.GuestIp).Distinct().Count() != settings.Machines.Count(m => m.GuestIp.Length > 0))
            throw new InvalidOperationException("虛擬機或客體 IP 重複。");
        foreach (var item in settings.Machines) {
            if (!(settings.NeedsNetworkPlan && item.GuestIp.Length == 0) && (!IPAddress.TryParse(item.GuestIp, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) throw new InvalidOperationException("客體 IP 須為 IPv4。");
            if (item.UseLocalNetwork && item.ProxyId != Guid.Empty) throw new InvalidOperationException("本地網路不能同時指定代理 IP。");
            if (!settings.ByStartup && !item.UseLocalNetwork && item.ProxyId != Guid.Empty && proxies.All(p => p.Id != item.ProxyId)) throw new InvalidOperationException("指定的代理已移除，請重新分配。");
        }
    }
    public static VmwareRuntime[] Assign(VmwareSettings settings, IReadOnlyCollection<ProxyProfile> proxies, IReadOnlyDictionary<string, ProcessIdentity?> live, IReadOnlyCollection<VmwareRuntime> previous)
    {
        Validate(settings, proxies);
        var result = new List<VmwareRuntime>(); var used = new Dictionary<Guid, int>();
        foreach (var mapping in settings.Machines.OrderBy(m => live.GetValueOrDefault(m.VmxPath)?.CreatedFileTime ?? long.MaxValue).ThenBy(m => m.VmxPath, StringComparer.OrdinalIgnoreCase)) {
            var process = live.GetValueOrDefault(mapping.VmxPath); Guid selected = settings.ByStartup || mapping.UseLocalNetwork ? Guid.Empty : mapping.ProxyId;
            if (settings.ByStartup && !mapping.UseLocalNetwork && process.HasValue) {
                var existing = previous.FirstOrDefault(r => r.VmxPath.Equals(mapping.VmxPath, StringComparison.OrdinalIgnoreCase) && r.Process == process && proxies.Any(p => p.Id == r.ProxyId));
                if (existing is not null && used.GetValueOrDefault(existing.ProxyId) < settings.PerProxy) selected = existing.ProxyId;
                // Live assignments are reserved in a separate pass below, before new arrivals consume capacity.
            }
            result.Add(new(mapping.VmxPath, process, selected, process is null ? "等待啟動" : selected == Guid.Empty ? "等待分配 · 已封鎖" : "等待檢測",
                previous.FirstOrDefault(r => r.VmxPath.Equals(mapping.VmxPath, StringComparison.OrdinalIgnoreCase) && r.Process == process && r.ProxyId == selected && r.UseLocalNetwork == mapping.UseLocalNetwork)?.ConnectedAt) { UseLocalNetwork = mapping.UseLocalNetwork });
            if (settings.ByStartup && selected != Guid.Empty) used[selected] = used.GetValueOrDefault(selected) + 1;
        }
        if (settings.ByStartup) for (int i = 0; i < result.Count; i++) if (result[i].Process.HasValue && result[i].ProxyId == Guid.Empty && !result[i].UseLocalNetwork) {
            Guid next = proxies.FirstOrDefault(p => used.GetValueOrDefault(p.Id) < settings.PerProxy)?.Id ?? Guid.Empty;
            if (next != Guid.Empty) { used[next] = used.GetValueOrDefault(next) + 1; result[i] = result[i] with { ProxyId = next, State = "等待檢測" }; }
            else if (settings.OverflowToLocal) result[i] = result[i] with { UseLocalNetwork = true };
        }
        for (int i = 0; i < result.Count; i++) {
            var item = result[i];
            result[i] = item with { ConnectedAt = previous.FirstOrDefault(r => r.VmxPath.Equals(item.VmxPath, StringComparison.OrdinalIgnoreCase) && r.Process == item.Process && r.ProxyId == item.ProxyId && r.UseLocalNetwork == item.UseLocalNetwork)?.ConnectedAt,
                State = item.UseLocalNetwork ? item.Process is null ? "等待啟動 · 本地網路" : "本地網路待套用" : item.State };
        }
        return result.ToArray();
    }
}

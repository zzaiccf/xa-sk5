using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.ServiceProcess;
using WindowSocks.Core;

namespace WindowSocks.Service;

internal sealed record VmwareFileBackup(string Path, string Original, string Applied);
internal sealed record VmwareNetworkBackup(string AdapterId, int Index, bool Forwarding, VmwareFileBackup[] Files, string Phase);
internal static class VmwareNetwork
{
    internal static string DhcpPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VMware", "vmnetdhcp.conf");
    internal static VmwareAdapter[] Adapters() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(n.Name, @"\bVMnet(?:[1-7]|9|1[0-9])\b", RegexOptions.IgnoreCase))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && a.IPv4Mask.ToString() == "255.255.255.0")
            .Select(a => new VmwareAdapter(n.Id, n.Name, n.GetIPProperties().GetIPv4Properties().Index, a.Address.ToString(), a.IPv4Mask.ToString()))).ToArray();
    internal static VmwareAdapter Adapter(string id) => Adapters().SingleOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("找不到所選 VMware /24 虛擬網卡。請先在 VMware 虛擬網路編輯器新增專用 Host-only VMnet，並啟用主機虛擬網卡與 DHCP。");
    internal static bool OverlapsSubnet(string prefix, uint subnet)
    {
        string[] parts = prefix.Trim().Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int length) || length is < 0 or > 32 || !System.Net.IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("無法辨識 NAT 網段，尚未變更 VMware 網路。");
        uint mask = length == 0 ? 0 : length >= 24 ? 0xffffff00 : uint.MaxValue << (32 - length);
        return (subnet & mask) == (VmwareDhcp.Number(parts[0]) & mask);
    }
    private static string ReadFile(string path) => Path.GetExtension(path).Equals(".vmx", StringComparison.OrdinalIgnoreCase) ? VmwareVmx.ReadText(path) : File.ReadAllText(path);
    private static void Atomic(string path, string value)
    {
        AppReleasePackage.RejectLinks(path); string temporary = path + ".xa-sk5-tmp"; AppReleasePackage.RejectLinks(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { byte[] bytes = (Path.GetExtension(path).Equals(".vmx", StringComparison.OrdinalIgnoreCase) ? VmwareVmx.TextEncoding(value) : Encoding.UTF8).GetBytes(value); stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static string Powershell(string script)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("無法變更專用網路轉送設定。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(); throw new IOException("專用網路轉送設定逾時。"); }
        if (process.ExitCode != 0) throw new IOException("專用網路轉送設定失敗，請確認 VMware 虛擬網卡狀態。");
        _ = error.GetAwaiter().GetResult(); return output.GetAwaiter().GetResult().Trim();
    }
    private static bool Forwarding(int index) => Powershell($"[Console]::Write((Get-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4).Forwarding -eq 'Enabled')") == "True";
    private static void Forwarding(int index, bool enabled) => Powershell($"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -Forwarding {(enabled ? "Enabled" : "Disabled")}");
    internal static void SetForwarding(string adapterId, bool enabled) => Forwarding(Adapter(adapterId).Index, enabled);
    private static void RestartDhcp()
    {
        using var service = new ServiceController("VMnetDHCP");
        if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
        service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }
    internal static void RequirePoweredOff(string path)
    {
        _ = VmwareVmx.Read(path);
        if (VmwareVmx.Parse(VmwareVmx.ReadText(path)).GetValueOrDefault("checkpoint.vmState") is { Length: > 0 }) throw new InvalidOperationException("此虛擬機已暫停，請先恢復並正常關機一次。");
        if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!, "*.lck").Any()) throw new InvalidOperationException("請先將所選虛擬機完整關機並關閉 VMware 窗口，再準備或回復網路。");
    }
    internal static bool Prepared(string journal) => File.Exists(journal) && Read(journal).Phase == "applied";
    internal static bool CanPrepare(string journal) => !File.Exists(journal) || Read(journal).Phase == "restored";
    private static VmwareNetworkBackup Read(string journal)
    {
        AppReleasePackage.RejectLinks(journal); if (new FileInfo(journal).Length > 32 * 1024 * 1024) throw new IOException("網路備份大小無效。");
        var backup = JsonSerializer.Deserialize<VmwareNetworkBackup>(File.ReadAllText(journal), Json.Options) ?? throw new IOException("網路備份無效。");
        if (backup.Files.Length is < 2 or > 11 || backup.Files.Any(f => !f.Path.Equals(DhcpPath, StringComparison.OrdinalIgnoreCase) && VmwareVmx.NormalizePath(f.Path) != f.Path)) throw new IOException("網路備份內容無效。");
        return backup;
    }
    internal static VmwareNetworkBackup Plan(VmwareSettings settings, string journal, bool requirePoweredOff = true)
    {
        if (File.Exists(journal) && Read(journal).Phase != "restored") throw new InvalidOperationException("已有專用網路備份，請先回復原網路。");
        var adapter = Adapter(settings.AdapterId);
        var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Id == adapter.Id);
        if (nic.OperationalStatus != OperationalStatus.Up || nic.GetIPProperties().GatewayAddresses.Count != 0) throw new InvalidOperationException("所選 VMnet 必須為啟用中且沒有預設閘道的獨立 Host-only 網路。");
        if (!File.Exists(DhcpPath)) throw new InvalidOperationException("缺少 VMware DHCP 設定，請先完成 VMware 虛擬網路安裝。");
        AppReleasePackage.RejectLinks(DhcpPath);
        using (var service = new ServiceController("VMnetDHCP")) if (service.Status != ServiceControllerStatus.Running) throw new InvalidOperationException("請先啟動 VMware DHCP 服務。");
        string natFile = Path.Combine(Path.GetDirectoryName(DhcpPath)!, "vmnetnat.conf");
        uint subnet = VmwareDhcp.Number(adapter.Address) & 0xffffff00;
        foreach (var other in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Id != adapter.Id))
            foreach (var address in other.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) {
                uint mask = VmwareDhcp.Number(address.IPv4Mask.ToString()), otherIp = VmwareDhcp.Number(address.Address.ToString());
                if ((otherIp & 0xffffff00) == subnet || (subnet & mask) == (otherIp & mask)) throw new InvalidOperationException("專用 VMnet 與其他網卡網段重疊，請另選未使用的網段。");
            }
        string natPrefixes = Powershell("if (Get-Command Get-NetNat -ErrorAction SilentlyContinue) { Get-NetNat | ForEach-Object { [Console]::WriteLine($_.InternalIPInterfaceAddressPrefix) } }");
        if (natPrefixes.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(prefix => OverlapsSubnet(prefix, subnet))) throw new InvalidOperationException("專用網段與 Windows NAT 重疊，請另建未共用的 Host-only 網路。");
        if (File.Exists(natFile) && Regex.Matches(File.ReadAllText(natFile), @"(?m)^\s*ip\s*=\s*(\d+\.\d+\.\d+\.\d+)(?:/(\d+))?").Any(m => OverlapsSubnet(m.Groups[1].Value + "/" + (m.Groups[2].Success ? m.Groups[2].Value : "24"), subnet))) throw new InvalidOperationException("所選網段由 VMware NAT 使用，請另建專用 Host-only VMnet。");
        string vnet = Regex.Match(adapter.Name, @"VMnet\d+", RegexOptions.IgnoreCase).Value;
        var files = new List<VmwareFileBackup>(); var guests = new List<(string Ip, string Mac)>();
        foreach (var mapping in settings.Machines) {
            string path = VmwareVmx.NormalizePath(mapping.VmxPath); if (requirePoweredOff) RequirePoweredOff(path);
            string original = VmwareVmx.ReadText(path); string applied = VmwareVmx.Configure(original, vnet, out string mac);
            files.Add(new(path, original, applied)); guests.Add((mapping.GuestIp, mac));
        }
        string dhcp = File.ReadAllText(DhcpPath); files.Add(new(DhcpPath, dhcp, VmwareDhcp.Configure(dhcp, adapter.Address, guests)));
        bool originalForwarding = Forwarding(adapter.Index);
        if (originalForwarding) throw new InvalidOperationException("所選 VMnet 已有路由轉送用途，請另建未使用的 Host-only 網路。");
        return new VmwareNetworkBackup(adapter.Id, adapter.Index, originalForwarding, files.ToArray(), "prepared");
    }
    internal static void Apply(VmwareNetworkBackup backup, string journal, Action<bool>? setForwarding = null, Action? restartDhcp = null)
    {
        setForwarding ??= enabled => Forwarding(Adapter(backup.AdapterId).Index, enabled); restartDhcp ??= RestartDhcp;
        foreach (var file in backup.Files) {
            AppReleasePackage.RejectLinks(file.Path);
            if (Path.GetExtension(file.Path).Equals(".vmx", StringComparison.OrdinalIgnoreCase)) RequirePoweredOff(file.Path);
            if (ReadFile(file.Path) != file.Original) throw new IOException("VMware 設定在準備期間有變更，請關閉 VMware 後重試，尚未套用網路。");
        }
        string serialized = JsonSerializer.Serialize(backup, Json.Options);
        if (Encoding.UTF8.GetByteCount(serialized) > 32 * 1024 * 1024) throw new IOException("網路備份超出可回復大小，尚未變更 VMware 設定。");
        Atomic(journal, serialized);
        try {
            foreach (var file in backup.Files) Atomic(file.Path, file.Applied);
            setForwarding(true); restartDhcp();
            Atomic(journal, JsonSerializer.Serialize(backup with { Phase = "applied" }, Json.Options));
        } catch { RestoreFiles(backup, journal, setForwarding, restartDhcp); throw; }
    }
    internal static void Restore(string journal)
    {
        var backup = Read(journal); if (backup.Phase == "restored") return;
        var adapter = Adapter(backup.AdapterId);
        RestoreFiles(backup, journal, enabled => Forwarding(adapter.Index, enabled), RestartDhcp);
    }
    internal static void RestoreFiles(VmwareNetworkBackup backup, string journal, Action<bool> setForwarding, Action restartDhcp)
    {
        foreach (var file in backup.Files) {
            AppReleasePackage.RejectLinks(file.Path);
            if (Path.GetExtension(file.Path).Equals(".vmx", StringComparison.OrdinalIgnoreCase)) RequirePoweredOff(file.Path);
            string current = ReadFile(file.Path);
            if (current != file.Applied && current != file.Original) throw new IOException("網路設定已被其他程式修改，請保留備份後人工確認，未覆寫新設定。");
        }
        foreach (var file in backup.Files) Atomic(file.Path, file.Original);
        setForwarding(backup.Forwarding); restartDhcp();
        Atomic(journal, JsonSerializer.Serialize(backup with { Phase = "restored" }, Json.Options));
    }
}

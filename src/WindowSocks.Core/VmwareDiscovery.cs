using System.Diagnostics;

namespace WindowSocks.Core;

public sealed record VmwareDiscovered(VmwareMachine Machine, ProcessIdentity? Process, string Detail);

public static class VmwareDiscovery
{
    public static VmwareDiscovered[] Registered(string? inventory = null)
    {
        inventory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMware", "inventory.vmls");
        if (!File.Exists(inventory)) return [];
        var values = VmwareVmx.Parse(VmwareVmx.ReadText(inventory));
        var result = new List<VmwareDiscovered>();
        foreach (string path in values.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"^vmlist\d+\.config$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Select(p => p.Value).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) {
            try { result.Add(new(VmwareVmx.Read(path), null, "已登錄 · 未在執行")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        return result.ToArray();
    }
    public static VmwareAdapter[] Adapters() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(n.Name, @"\bVMnet(?:[1-7]|9|1[0-9])\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && a.IPv4Mask.ToString() == "255.255.255.0")
            .Select(a => new VmwareAdapter(n.Id, n.Name, n.GetIPProperties().GetIPv4Properties().Index, a.Address.ToString(), a.IPv4Mask.ToString()))).ToArray();
    public static string[] ParseRunningPaths(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim()).Where(s => Path.IsPathFullyQualified(s) && s.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase))
        .Select(VmwareVmx.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // vmrun is only a discovery fallback. Its output order never supplies startup order or PID identity.
    public static async Task<VmwareDiscovered[]> Running(CancellationToken ct = default)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VMware", "VMware Workstation", "vmrun.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException("找不到 VMware Workstation 的 vmrun.exe，請手動加入 VMX。");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("list");
        using var process = Process.Start(start) ?? throw new IOException("無法讀取 VMware 虛擬機。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(); } catch { } throw; }
        string text = await output; _ = await error;
        if (process.ExitCode != 0) throw new IOException("VMware 未能列出執行中的虛擬機。");
        return ParseRunningPaths(text).Select(path => new VmwareDiscovered(VmwareVmx.Read(path), null, "執行中 · 關機後可準備網路")).ToArray();
    }
}

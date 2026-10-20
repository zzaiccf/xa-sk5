using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowSocks.Core;

public static class LdPlayer
{
    public static string Key(string console, int index) => $"ld:{System.IO.Path.GetFullPath(console).ToUpperInvariant()}:{index}";
    public static TargetInstance[] Parse(string output, string console, Func<int, ProcessIdentity?> identity)
    {
        List<TargetInstance> result = [];
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',');
            // 9.1 includes width/height/DPI after the original seven fields. Names can contain commas.
            int suffix = fields.Length >= 10 && fields.TakeLast(8).All(x => long.TryParse(x, out _)) ? 8 : 5;
            if (fields.Length < suffix + 2 || !int.TryParse(fields[0], out int index) || index < 0 || index == 99999) continue;
            int start = fields.Length - suffix;
            if (!int.TryParse(fields[start + 3], out int guiPid) || !int.TryParse(fields[start + 4], out int vmPid)) continue;
            // NAT traffic belongs to the VM, while dnplayer hosts local UI/control connections.
            var identities = new[] { vmPid }.Where(p => p > 0).Select(identity).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            string name = string.Join(',', fields.Skip(1).Take(start - 1));
            result.Add(new(Key(console, index), name, "LDPlayer", console, index, vmPid > 0, identities,
                vmPid > 0 && identities.Length == 0 ? "無法讀取程序身分" : "NAT 模式才支援"));
        }
        return result.ToArray();
    }
    public static ProcessIdentity? Identity(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return new(pid, p.StartTime.ToUniversalTime().ToFileTimeUtc()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
    public static async Task<string> RunAsync(string console, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(console) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("無法啟動雷電命令列。");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } catch { try { process.Kill(); } catch { } throw; }
        if (process.ExitCode != 0) throw new IOException("雷電命令列執行失敗，請檢查安裝路徑與實例狀態。");
        await stderr;
        return await stdout;
    }
    public static bool IsNat(string console, int index)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(console)!, "vms", $"leidian{index}");
        var file = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.vbox").FirstOrDefault() : null;
        if (file is null) return false;
        var xml = System.Xml.Linq.XDocument.Load(file);
        var adapters = xml.Descendants().Where(x => x.Name.LocalName == "Adapter" && (string?)x.Attribute("enabled") == "true").ToArray();
        return adapters.Length == 1 && adapters[0].Elements().Any(x => x.Name.LocalName == "NAT");
    }
}

public static class ProcessCatalog
{
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder path, ref uint length);
    public static string ImagePath(int pid)
    {
        nint handle = OpenProcess(0x1000, false, pid); if (handle == 0) return "";
        try { uint length = 32768; var path = new StringBuilder((int)length); return QueryFullProcessImageNameW(handle, 0, path, ref length) ? path.ToString() : ""; }
        finally { CloseHandle(handle); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry
    { public uint Size, Usage, Pid; public nuint Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe; }
    [DllImport("kernel32.dll")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    public static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>(); var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == -1) return result;
        try { var e = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Exe = "" }; if (Process32FirstW(handle, ref e)) do { result[(int)e.Pid] = (int)e.Parent; } while (Process32NextW(handle, ref e)); }
        finally { CloseHandle(handle); }
        return result;
    }
    public static ProcessIdentity[] WithChildren(IEnumerable<ProcessIdentity> roots)
    {
        var valid = roots.Where(r => LdPlayer.Identity(r.Pid) == r).ToDictionary(r => r.Pid);
        var parents = Parents(); bool changed;
        do { changed = false; foreach (var (pid, parent) in parents) if (!valid.ContainsKey(pid) && valid.TryGetValue(parent, out var p) && LdPlayer.Identity(pid) is { } child && child.CreatedFileTime >= p.CreatedFileTime) { valid[pid] = child; changed = true; } } while (changed);
        return valid.Values.ToArray();
    }
    public static TargetInstance[] List(string? executableHint = null)
    {
        var result = new List<TargetInstance>();
        foreach (var p in Process.GetProcesses()) using (p)
        {
            try {
                if (executableHint is not null && !p.ProcessName.Equals(Path.GetFileNameWithoutExtension(executableHint), StringComparison.OrdinalIgnoreCase) && !p.ProcessName.Equals(executableHint, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Id == Environment.ProcessId || p.SessionId == 0) continue; var identity = LdPlayer.Identity(p.Id); if (identity is null) continue;
                string path = ImagePath(p.Id); if (path.Length == 0) continue;
                result.Add(new($"pid:{p.Id}:{identity.Value.CreatedFileTime}", string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle,
                    "Process", path, -1, true, [identity.Value], $"{p.ProcessName} · PID {p.Id}")); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return result.OrderBy(x => x.Name).ToArray();
    }
}

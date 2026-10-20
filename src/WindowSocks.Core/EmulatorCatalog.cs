using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace WindowSocks.Core;

public static class EmulatorCatalog
{
    public static bool IsEmulator(string kind) => kind is "LDPlayer" or "MuMu";
    public static string Name(string kind) => kind == "MuMu" ? "MuMu" : "雷電";
    public static string Key(string kind, string console, int index) => kind == "LDPlayer" ? LdPlayer.Key(console, index) :
        kind == "MuMu" ? $"mumu:{Path.GetFullPath(console).ToUpperInvariant()}:{index}" : throw new InvalidOperationException("不支援的模擬器。");
    public static string InstallRoot(string console)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(console))!;
        return new[] { "shell", "nx_main" }.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder)! : folder;
    }
    public static void ValidateConsole(string kind, string console, bool requireFile = true)
    {
        var names = kind == "LDPlayer" ? new[] { "ldconsole.exe", "dnconsole.exe" } : kind == "MuMu" ? new[] { "MuMuManager.exe" } : [];
        if (!Path.IsPathFullyQualified(console) || console.StartsWith(@"\\") || !names.Contains(Path.GetFileName(console), StringComparer.OrdinalIgnoreCase) || requireFile && !File.Exists(console))
            throw new InvalidOperationException(kind == "MuMu" ? "請選擇本機 MuMuManager.exe。" : "請選擇本機雷電 ldconsole.exe／dnconsole.exe。");
    }
    public static async Task<TargetInstance[]> DiscoverAsync(string kind, string console, CancellationToken ct)
    {
        ValidateConsole(kind, console);
        var output = await RunAsync(console, kind == "MuMu" ? ["info", "-v", "all"] : ["list2"], ct);
        return kind == "MuMu" ? ParseMuMu(output, console, LdPlayer.Identity) : LdPlayer.Parse(output, console, LdPlayer.Identity);
    }
    public static async Task<string> RunAsync(string console, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(console) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("無法啟動模擬器命令列。");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } catch { try { process.Kill(); } catch { } throw; }
        await stderr;
        if (process.ExitCode != 0) throw new IOException("模擬器清單讀取失敗，請確認版本與安裝路徑。");
        return await stdout;
    }
    // MuMuManager info -v all: index identifies the instance; only headless_pid owns VM NAT traffic.
    public static TargetInstance[] ParseMuMu(string output, string console, Func<int, ProcessIdentity?> identity)
    {
        using var document = JsonDocument.Parse(output);
        var result = new List<TargetInstance>();
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array) { foreach (var child in value.EnumerateArray()) Visit(child); return; }
            if (value.ValueKind != JsonValueKind.Object) return;
            if (value.TryGetProperty("error_code", out var error) && int.TryParse(error.ToString(), out int code) && code != 0)
                throw new InvalidDataException("MuMu 回報實例資訊錯誤，請確認模擬器版本與狀態。");
            if (value.TryGetProperty("index", out var indexElement) && int.TryParse(indexElement.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 0)
            {
                if (result.Count >= 500) throw new InvalidDataException("模擬器清單超出限制。");
                string name = value.TryGetProperty("name", out var n) ? n.GetString() ?? $"MuMu {index}" : $"MuMu {index}";
                int pid = value.TryGetProperty("headless_pid", out var p) && int.TryParse(p.ToString(), out int parsed) ? parsed : 0;
                bool stopped = value.TryGetProperty("is_process_started", out var started) && started.ValueKind == JsonValueKind.False;
                var process = !stopped && pid > 0 ? identity(pid) : null;
                result.Add(new(Key("MuMu", console, index), name, "MuMu", console, index, !stopped && pid > 0,
                    process.HasValue ? [process.Value] : [], pid > 0 && !process.HasValue && !stopped ? "等待辨識虛擬機程序" : "僅支援可辨識的 NAT 網路程序"));
                return;
            }
            foreach (var child in value.EnumerateObject()) Visit(child.Value);
        }
        Visit(document.RootElement);
        if (result.Select(t => t.Key).Distinct().Count() != result.Count) throw new InvalidDataException("MuMu 回傳重複的實例編號。");
        return result.OrderBy(t => t.Index).ToArray();
    }
}

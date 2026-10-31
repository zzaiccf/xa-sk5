using System.Diagnostics;
using System.Text.Json;

namespace WindowSocks.Core;

// A single process-wide gate covers prepare and removal, including rollback. Nested
// calls inherit ownership only within the awaited operation, never its caller.
public static class SandboxConfiguration
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly AsyncLocal<bool> owner = new();
    public static async Task Run(Func<Task> action, CancellationToken ct)
    {
        if (owner.Value) { await action().ConfigureAwait(false); return; }
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { owner.Value = true; await action().ConfigureAwait(false); }
        finally { owner.Value = false; gate.Release(); }
    }
    public static async Task<T> Run<T>(Func<Task<T>> action, CancellationToken ct)
    {
        T result = default!;
        await Run(async () => { result = await action().ConfigureAwait(false); }, ct).ConfigureAwait(false);
        return result;
    }
    public static async Task<T> Retry<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++) {
            ct.ThrowIfCancellationRequested();
            try { return await action().ConfigureAwait(false); }
            catch (Exception ex) when (attempt < 3 && (ex is SandboxConfigurationPendingException ||
                ex is IOException && ((ex.HResult & 0xffff) is 32 or 33))) {
                await Task.Delay(100 << attempt, ct).ConfigureAwait(false);
            }
            catch (SandboxConfigurationPendingException ex) { throw new IOException(ex.Message + "（有限次重試已用盡）", ex); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) {
                // Do not let an outer transaction/read boundary multiply an
                // already exhausted retry budget. Preserve the original cause.
                throw new IOException("Sandboxie 設定分享衝突；有限次重試已用盡，請關閉其他設定編輯工作後重試。", ex);
            }
        }
    }
    public static Task Retry(Func<Task> action, CancellationToken ct) => Retry(async () => { await action().ConfigureAwait(false); return true; }, ct);

    // IniLocation is the driver's effective path, including registry custom paths
    // and fallback. Query in the existing isolated helper, never load SbieDll in
    // the routing service. No configuration or service changes are performed.
    public static string ResolvePath()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WindowSocks.Tools.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("sandbox-ini-path");
        using var process = Process.Start(start) ?? throw new IOException("無法查詢 Sandboxie 設定位置。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { try { process.Kill(); } catch { } throw new TimeoutException("Sandboxie 設定位置查詢逾時，請確認 SbieSvc 已啟動。"); }
        if (process.ExitCode != 0) throw new IOException("Sandboxie 設定位置不可用：" + error.GetAwaiter().GetResult());
        return JsonSerializer.Deserialize<string>(output.GetAwaiter().GetResult()) ?? throw new IOException("Sandboxie 未回報設定位置。");
    }
    internal static string ResolveLocation(string location, string install, string windows) => location switch {
        "H" => Path.Combine(install, "Sandboxie.ini"), "W" => Path.Combine(windows, "Sandboxie.ini"),
        _ when location.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase) => @"\\" + location[8..],
        _ when location.StartsWith(@"\??\") => location[4..],
        _ when Path.IsPathFullyQualified(location) && !location.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) => location,
        _ => throw new IOException("Sandboxie 回報不支援的設定路徑，請由官方管理器確認。")
    };
}

public sealed class SandboxConfigurationPendingException(string message) : IOException(message);

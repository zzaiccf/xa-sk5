using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("WindowSocks.Tests")]
namespace WindowSocks.Core;

public sealed record SandboxieSetupState(bool Ready, bool CanInstall, string Detail);

public sealed class SandboxieInstaller
{
    public const string OfficialPage = "https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.8";
    public static readonly ReleaseAsset Package = new(
        "https://github.com/sandboxie-plus/Sandboxie/releases/download/v1.18.8/Sandboxie-Plus-x64-v1.18.8.exe",
        24930680, "F27078FF74A07906FD51476079E1A7A8F5E20C8BB75A9D2AE86169D47179AE76");
    private static readonly SemaphoreSlim installGate = new(1, 1);
    private readonly Func<SandboxieSetupState> inspect;
    private readonly Func<string, Task<int>> execute;
    private readonly Action<string> verifySignature;
    private readonly HttpMessageHandler? testHandler;
    private readonly ReleaseAsset package;
    public SandboxieInstaller() : this(Inspect, Execute, DriverTrust.VerifySignature, Package, null) { }
    internal SandboxieInstaller(Func<SandboxieSetupState> inspect, Func<string, Task<int>> execute, Action<string> verifySignature,
        ReleaseAsset package, HttpMessageHandler? handler)
    { this.inspect = inspect; this.execute = execute; this.verifySignature = verifySignature; this.package = package; testHandler = handler; }

    public static SandboxieSetupState Inspect()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64) return new(false, false, "此版本需要 Windows x64。");
        string root = SandboxieRuntime.InstallDirectory;
        AppReleasePackage.RejectLinks(root);
        bool ready = new[] { "SandMan.exe", "Start.exe", "SbieDll.dll", "SbieIni.exe", "SbieSvc.exe", "SbieDrv.sys" }.All(f => File.Exists(Path.Combine(root, f))) &&
            FileVersionInfo.GetVersionInfo(Path.Combine(root, "SandMan.exe")).FileVersion == "1.18.8" &&
            FileVersionInfo.GetVersionInfo(Path.Combine(root, "SbieDrv.sys")).FileVersion == "5.73.8";
        if (ready) return new(true, false, "Sandboxie Plus 1.18.8 已安裝 · 可準備環境（啟動時另核驗驅動）");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var driver = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SbieDrv");
        using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sandboxie-Plus_is1");
        using var classic = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sandboxie");
        bool existing = driver is not null || uninstall is not null || classic is not null || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
        return existing ? new(false, false, "偵測到既有、不同版本或未完整安裝的 Sandboxie；請先保留設定並由官方安裝程式處理，再重新檢查。未自動覆蓋。") :
            new(false, true, "尚未安裝 Sandboxie · 可一鍵安裝，或按準備環境時自動安裝。");
    }

    public Task EnsureInstalled(IProgress<UpdateProgress>? progress, CancellationToken ct) => EnsureInstalled(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks", "Downloads"), progress, ct);
    internal async Task EnsureInstalled(string cacheRoot, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        if (!await installGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("Sandboxie 正在安裝，請等待完成後再重新檢查。");
        try {
            var initial = inspect(); if (initial.Ready) return;
            if (!initial.CanInstall) throw new InvalidOperationException(initial.Detail);
            string directory = Path.Combine(cacheRoot, "Sandboxie-" + Guid.NewGuid().ToString("N"));
            AppReleasePackage.RejectLinks(directory); Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "Sandboxie-Plus-x64-v1.18.8.exe");
            progress?.Report(new("正在從官方下載 Sandboxie Plus 1.18.8…") { CanCancel = true });
            await Download(file, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Hold the verified file against replacement until Setup exits.
            using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            progress?.Report(new("正在核對安裝檔 SHA-256 與數位簽章…") { CanCancel = true });
            AppReleasePackage.VerifyArchive(file, package); verifySignature(file);
            ct.ThrowIfCancellationRequested();
            var current = inspect(); if (current.Ready) return;
            if (!current.CanInstall) throw new InvalidOperationException(current.Detail);
            progress?.Report(new("請確認 Windows 管理員授權。官方程式正在安裝；如需取消，請在安裝視窗操作。") { CanCancel = false });
            int code;
            try { code = await execute(file).ConfigureAwait(false); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new IOException("你已取消管理員授權，尚未安裝 Sandboxie；可再按一鍵安裝。", ex); }
            if (code == 3010) throw new IOException("Sandboxie 已安裝但需要重新啟動 Windows；請自行重開後再按重新檢查，尚未準備或啟動遊戲。");
            if (code != 0) throw new IOException($"Sandboxie 安裝未完成（退出碼 {code}）。請查看 {directory}\\setup.log，處理後重新檢查。");
            var result = inspect();
            if (!result.Ready) throw new IOException("安裝程式已結束，但尚未通過環境檢查。" + result.Detail);
            ct.ThrowIfCancellationRequested(); // Closing the window never kills an in-progress driver installer.
            progress?.Report(new("Sandboxie 安裝完成並已重新檢查，可繼續準備遊戲。") { CanCancel = true });
        } finally { installGate.Release(); }
    }
    private async Task Download(string file, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var client = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: testHandler is null);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("xa-sk5/" + AppReleasePackage.CurrentVersion);
        var uri = new Uri(package.Url);
        for (int i = 0; i < 6; i++) {
            if (!ComponentPackage.AllowedDownloadUri(uri)) throw new IOException("下載連結不是允許的官方發布主機，已停止。");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { uri = new Uri(uri, location); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != package.Size) throw new InvalidDataException("官方安裝檔大小不符，未執行安裝。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            byte[] buffer = new byte[65536]; long received = 0; int lastPercent = -1;
            while (true) {
                int read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (read == 0) break;
                received += read; if (received > package.Size) throw new InvalidDataException("安裝檔超過大小限制，未執行安裝。");
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                int percent = (int)(received * 100 / package.Size);
                if (percent != lastPercent) { lastPercent = percent; progress?.Report(new($"下載 Sandboxie {percent}%", received, package.Size) { CanCancel = true }); }
            }
            return;
        }
        throw new IOException("官方下載重新導向次數過多，請稍後重試。");
    }
    internal static ProcessStartInfo StartInfo(string file)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(file)!, WindowStyle = ProcessWindowStyle.Normal };
        foreach (string arg in new[] { "/SP-", "/SILENT", "/NORESTART", "/RESTARTEXITCODE=3010", "/ALLUSERS", "/PORTABLE=0", "/OPEN_AGENT=0", "/TASKS=", "/DIR=" + SandboxieRuntime.InstallDirectory, "/LOG=" + Path.Combine(Path.GetDirectoryName(file)!, "setup.log") }) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task<int> Execute(string file)
    {
        using var process = Process.Start(StartInfo(file)) ?? throw new IOException("無法啟動官方安裝程式。");
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return process.ExitCode;
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public sealed record DesktopStartupState(bool Enabled, bool ServiceInstalled, string Message);
public interface IDesktopStartupController
{
    DesktopStartupState Read();
    Task SetEnabledAsync(bool enabled);
    Task StartServiceAsync();
}

public sealed class DesktopStartupController : IDesktopStartupController
{
    private readonly StartupRegistry registration = new(Registry.CurrentUser);
    private readonly WindowsStartupService service = new();
    private readonly string executable = Path.Combine(AppContext.BaseDirectory, File.Exists(Path.Combine(AppContext.BaseDirectory, "xa sk5.exe")) ? "xa sk5.exe" : "WindowSocks.exe");
    private string? initializationError;
    public DesktopStartupController()
    {
        try { StartupConfiguration.Initialize(registration, executable, service.Read()?.Automatic ?? true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { initializationError = ex.Message; }
    }
    public DesktopStartupState Read()
    {
        try {
            var saved = registration.Read(); var mode = service.Read(); bool enabled = saved.Enabled ?? mode?.Automatic ?? true;
            bool registered = !enabled || string.Equals(saved.Command, StartupConfiguration.Command(executable), StringComparison.OrdinalIgnoreCase);
            string message = initializationError ?? (enabled
                ? "Windows 登入時開啟主程式；背景代理服務於開機時啟動。"
                : "已取消下次開機自動啟動，目前的代理連線維持執行。");
            if (mode is not null && mode.Automatic != enabled) message = "主程式與背景服務的開機設定不同，請重新勾選或取消以同步。";
            if (!registered && initializationError is null) message = "主程式的登入啟動項目未就緒，請重新勾選以同步。";
            return new(enabled && registered && (mode?.Automatic ?? true), mode is not null, message);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return new(false, false, "無法讀取開機設定：" + ex.Message); }
    }
    public async Task SetEnabledAsync(bool enabled)
    {
        var mode = service.Read();
        if (mode is not null && mode != StartupConfiguration.ServiceMode(enabled)) await RunHelper("--set-startup", enabled.ToString());
        else StartupConfiguration.Apply(registration, service, executable, enabled);
        initializationError = null;
        var state = Read();
        if (state.Enabled != enabled) throw new IOException("開機設定尚未同步，請重新套用。");
    }
    public Task StartServiceAsync() => RunHelper("--start-service", null);
    private static async Task RunHelper(string operation, string? value)
    {
        await Task.Run(() => ComponentPackage.VerifyManifest(AppContext.BaseDirectory));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WindowSocks.Maintenance.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(operation); if (value is not null) start.ArgumentList.Add(value);
        start.ArgumentList.Add("--owner-sid"); start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        try {
            using var process = Process.Start(start) ?? throw new IOException("無法啟動開機設定工具。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(process.ExitCode == 6 ? "開機設定未完成回復，請重新套用設定。" : $"設定未完成（代碼 {process.ExitCode}）；請確認安裝使用者、完整程式目錄與 Windows 服務權限。");
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new IOException("你已取消 Windows 管理員授權，開機設定維持原狀。", ex); }
    }
}

// Alternate Backend constructors are used by isolated UI checks, never by the application entry point.
internal sealed class PreviewStartupController : IDesktopStartupController
{
    private bool enabled = true;
    public DesktopStartupState Read() => new(enabled, false, enabled ? "Windows 登入時開啟主程式；背景代理服務於開機時啟動。" : "已取消下次開機自動啟動，目前的代理連線維持執行。");
    public Task SetEnabledAsync(bool value) { enabled = value; return Task.CompletedTask; }
    public Task StartServiceAsync() => Task.CompletedTask;
}

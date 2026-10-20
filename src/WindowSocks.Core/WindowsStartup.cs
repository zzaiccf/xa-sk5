using System.Diagnostics;
using Microsoft.Win32;

namespace WindowSocks.Core;

public sealed record StartupRegistrationState(bool? Enabled, string? Command);
public sealed record StartupServiceMode(int StartType, bool Delayed = false)
{
    public bool Automatic => StartType == 2;
}
public interface IStartupRegistration
{
    StartupRegistrationState Read();
    void Write(StartupRegistrationState state);
}
public interface IStartupService
{
    StartupServiceMode? Read();
    void Write(StartupServiceMode mode);
}

public static class StartupConfiguration
{
    public static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 ||
            !new[] { "xa sk5.exe", "WindowSocks.exe" }.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("開機啟動需要 xa sk5 主程式的完整路徑。");
        string command = "\"" + Path.GetFullPath(executable) + "\" --autostart";
        if (command.Length > 260) throw new InvalidDataException("程式路徑過長，請移至較短的目錄後啟用開機啟動。");
        return command;
    }
    public static StartupServiceMode ServiceMode(bool enabled) => enabled ? new(2, true) : new(3);
    public static void Initialize(IStartupRegistration registration, string executable, bool defaultEnabled = true)
    {
        var old = registration.Read(); bool enabled = old.Enabled ?? defaultEnabled;
        var desired = new StartupRegistrationState(enabled, enabled ? Command(executable) : null);
        if (old != desired) try { registration.Write(desired); }
            catch { registration.Write(old); throw; }
    }
    public static void Apply(IStartupRegistration registration, IStartupService service, string executable, bool enabled)
    {
        string? command = enabled ? Command(executable) : null; var old = registration.Read(); var oldService = service.Read();
        var desiredService = ServiceMode(enabled); bool changeService = oldService is not null && oldService != desiredService;
        try {
            if (changeService) service.Write(desiredService);
            registration.Write(new(enabled, enabled ? command : null));
        } catch (Exception original) {
            var failures = new List<Exception>();
            try { registration.Write(old); } catch (Exception ex) { failures.Add(ex); }
            if (changeService) try { service.Write(oldService!); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 0) throw new AggregateException("開機設定未完成回復，請重新套用設定。", new[] { original }.Concat(failures));
            throw new IOException("開機設定未變更，已保留原有設定。" + original.Message, original);
        }
    }
}

// A separate preference keeps an explicit opt-out across moves, repairs and software updates.
public sealed class StartupRegistry(RegistryKey userRoot, string preferencesPath = @"Software\WindowSocks\Startup",
    string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "xa sk5") : IStartupRegistration
{
    public StartupRegistrationState Read()
    {
        using var preferences = userRoot.OpenSubKey(preferencesPath);
        using var run = userRoot.OpenSubKey(runPath);
        object? value = preferences?.GetValue("Enabled");
        bool? enabled = value is null ? null : value is int n && n is 0 or 1 ? n == 1 : throw new InvalidDataException("開機啟動偏好無效，請重新套用設定。");
        return new(enabled, run?.GetValue(valueName) as string);
    }
    public void Write(StartupRegistrationState state)
    {
        using var run = userRoot.CreateSubKey(runPath, true);
        if (state.Command is null) run.DeleteValue(valueName, false); else run.SetValue(valueName, state.Command, RegistryValueKind.String);
        using var preferences = userRoot.CreateSubKey(preferencesPath, true);
        if (state.Enabled is null) preferences.DeleteValue("Enabled", false); else preferences.SetValue("Enabled", state.Enabled.Value ? 1 : 0, RegistryValueKind.DWord);
    }
    public void RemoveForDirectory(string directory)
    {
        var state = Read();
        if (new[] { "xa sk5.exe", "WindowSocks.exe" }.Any(name => state.Command?.Equals(StartupConfiguration.Command(Path.Combine(directory, name)), StringComparison.OrdinalIgnoreCase) == true))
            Write(state with { Command = null });
    }
}

public sealed class WindowsStartupService : IStartupService
{
    public StartupServiceMode? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WindowSocks");
        if (key is null) return null;
        string expected = Path.Combine(UpdatePaths.Installed, "WindowSocks.Service.exe");
        if (!(key.GetValue("ImagePath") as string ?? "").Trim('"').Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("背景服務安裝位置不符，無法變更開機啟動。");
        int mode = Convert.ToInt32(key.GetValue("Start", 3));
        if (mode is not (2 or 3 or 4)) throw new IOException("背景服務啟動類型不支援此設定。");
        return new(mode, mode == 2 && Convert.ToInt32(key.GetValue("DelayedAutoStart", 0)) == 1);
    }
    public void Write(StartupServiceMode mode)
    {
        if (Read() is null) throw new IOException("找不到已安裝的背景服務。");
        string value = mode.StartType switch { 2 => mode.Delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => throw new InvalidDataException("不支援的服務啟動類型。") };
        RunSc(["config", "WindowSocks", "start=", value]);
        if (Read() != mode) throw new IOException("背景服務開機設定未成功保存。");
    }
    public void Start()
    {
        if (Read() is not { } mode) throw new IOException("找不到已安裝的背景服務。");
        if (mode.StartType == 4) throw new IOException("背景服務已由 Windows 停用，請先在服務設定中解除停用。");
        RunSc(["start", "WindowSocks"], allowAlreadyRunning: true);
    }
    private static void RunSc(string[] arguments, bool allowAlreadyRunning = false)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("無法變更背景服務設定。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("背景服務設定逾時。"); }
        string message = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (process.ExitCode != 0 && !(allowAlreadyRunning && process.ExitCode == 1056)) throw new IOException("背景服務操作失敗：" + message.Trim());
    }
}

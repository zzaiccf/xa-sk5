using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Setup;

internal static partial class Program
{
    private static readonly string InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowSocks");
    private static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowSocks");
    private static string installOwnerSid = WindowsIdentity.GetCurrent().User!.Value;
    private static string ProductVersion => Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    [STAThread] private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--verify-payload")) { VerifyEmbeddedPayload(); return 0; }
            bool maintenance = Assembly.GetExecutingAssembly().GetName().Name == "WindowSocks.Maintenance";
            if (!maintenance && !args.Any(a => a is "--setup-worker" or "--uninstall" or "--set-startup" or "--start-service" or "--install-components")) {
                using var wizard = new SetupWizardForm(args.Contains("--update"));
                wizard.Shown += (_, _) => { wizard.Refresh(); NotifyBootstrapReady(); };
                Application.Run(wizard); return wizard.Result;
            }
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (string argument in args) start.ArgumentList.Add(argument);
                if (!args.Contains("--owner-sid")) { start.ArgumentList.Add("--owner-sid"); start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value); }
                try { using var elevated = Process.Start(start) ?? throw new IOException("無法啟動安裝程式。"); elevated.WaitForExit(); return elevated.ExitCode; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new IOException("你已取消 Windows 管理員授權，程式與背景服務未變更。"); }
            }
            int ownerIndex = Array.IndexOf(args, "--owner-sid");
            if (ownerIndex >= 0) { if (ownerIndex + 1 >= args.Length) throw new ArgumentException("缺少安裝使用者。"); var owner = new SecurityIdentifier(args[ownerIndex + 1]); if (!owner.IsAccountSid()) throw new ArgumentException("無效安裝使用者。"); _ = owner.Translate(typeof(NTAccount)); installOwnerSid = owner.Value; }
            if (args.Contains("--setup-worker")) return RunSetupWorker(args);
            if (args.Contains("--set-startup") || args.Contains("--start-service")) {
                RunStartupCommand(args); return 0;
            }
            if (args.Contains("--uninstall")) { Uninstall(); return 0; }
            if (args.Contains("--install-components") || Assembly.GetExecutingAssembly().GetName().Name == "WindowSocks.Maintenance") return RunComponentInstaller(args);
            throw new ArgumentException("無效的安裝指令。");
        }
        catch (Exception ex) {
            if (args.Contains("--set-startup") || args.Contains("--start-service")) { Console.Error.WriteLine(ex.Message); return ex is AggregateException ? 6 : 5; }
            if (args.Contains("--verify-payload") || args.Contains("--setup-worker")) Console.Error.WriteLine(ex.Message); else MessageBox.Show(ex.Message, "xa sk5 安裝未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
        }
    }
    private static void NotifyBootstrapReady()
    {
        const string prefix = @"Local\WindowSocks.SetupReady.";
        string? name = Environment.GetEnvironmentVariable("XA_SK5_SETUP_READY_EVENT");
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "D", out _)) return;
        try { using var ready = EventWaitHandle.OpenExisting(name); ready.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void RunStartupCommand(string[] args)
    {
        string ownerFile = Path.Combine(DataDirectory, "owner.sid");
        if (File.Exists(ownerFile) && File.ReadAllText(ownerFile).Trim() != installOwnerSid) throw new IOException("請由安裝使用者變更開機設定。");
        var service = new WindowsStartupService();
        if (args.Length == 3 && args[0] == "--start-service" && args[1] == "--owner-sid") { service.Start(); return; }
        if (args.Length != 4 || args[0] != "--set-startup" || args[2] != "--owner-sid" || !bool.TryParse(args[1], out bool enabled)) throw new ArgumentException("開機設定參數無效。");
        string exe = Path.Combine(AppContext.BaseDirectory, File.Exists(Path.Combine(AppContext.BaseDirectory, "xa sk5.exe")) ? "xa sk5.exe" : "WindowSocks.exe");
        if (!File.Exists(exe)) throw new IOException("找不到主程式，請保留完整程式資料夾。");
        using var user = Registry.Users.OpenSubKey(installOwnerSid, true) ?? throw new IOException("找不到安裝使用者的登入設定。");
        StartupConfiguration.Apply(new StartupRegistry(user), service, exe, enabled);
    }
    private static void RegisterServiceAndShell(string uninstall, Action<string>? report = null)
    {
            report?.Invoke("設定背景代理服務與開機啟動偏好…");
            string ownerFile = Path.Combine(DataDirectory, "owner.sid");
            string startupOwner = File.Exists(ownerFile) ? new SecurityIdentifier(File.ReadAllText(ownerFile).Trim()).Value : installOwnerSid;
            using var user = Registry.Users.OpenSubKey(startupOwner, true) ?? throw new IOException("找不到安裝使用者的登入設定，未改寫開機偏好。");
            var startup = new StartupRegistry(user); bool autoStart = startup.Read().Enabled ?? true;
            string startType = autoStart ? "delayed-auto" : "demand";
            Run("sc.exe", ["create", "WindowSocks", "binPath=", $"\"{Path.Combine(InstallDirectory, "WindowSocks.Service.exe")}\"", "start=", startType, "DisplayName=", "xa sk5 SOCKS5 背景服務"], false);
            Run("sc.exe", ["config", "WindowSocks", "binPath=", $"\"{Path.Combine(InstallDirectory, "WindowSocks.Service.exe")}\"", "start=", startType, "DisplayName=", "xa sk5 SOCKS5 背景服務"]);
            Run("sc.exe", ["description", "WindowSocks", "xa sk5：程序級 SOCKS5 TCP／UDP 代理。"]);
            WindowsProxyService.GrantOwnerControl(startupOwner);
            Run("sc.exe", ["failure", "WindowSocks", "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"]);
            Run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", "name=WindowSocks relay"], false);
            foreach (var protocol in new[] { "TCP", "UDP" }) Run("netsh.exe", ["advfirewall", "firewall", "add", "rule", "name=WindowSocks relay", "dir=in", "action=allow", "program=" + Path.Combine(InstallDirectory, "WindowSocks.Service.exe"), "protocol=" + protocol, "profile=any"]);
            report?.Invoke("建立桌面與開始功能表捷徑…");
            string shortcuts = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string oldShortcut = Path.Combine(shortcuts, "窗流 WindowSocks.lnk");
            string mainExecutable = Path.Combine(InstallDirectory, File.Exists(Path.Combine(InstallDirectory, "xa sk5.exe")) ? "xa sk5.exe" : "WindowSocks.exe");
            WindowsShortcuts.Initialize(user, mainExecutable, WindowsShortcuts.DesktopForUser(user), shortcuts);
            if (File.Exists(oldShortcut)) File.Delete(oldShortcut);
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowSocks");
            key.SetValue("DisplayName", "xa sk5"); key.SetValue("DisplayVersion", ProductVersion); key.SetValue("Publisher", "xa sk5 local build"); key.SetValue("InstallLocation", InstallDirectory); key.SetValue("UninstallString", $"\"{uninstall}\" --uninstall"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
            StartupConfiguration.Initialize(startup, Path.Combine(InstallDirectory, File.Exists(Path.Combine(InstallDirectory, "xa sk5.exe")) ? "xa sk5.exe" : "WindowSocks.exe"));
    }
    private static Stream Payload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidDataException("安裝包缺少 payload.zip，請使用 scripts/Build.ps1 產生安裝包。");
    private static Process[] DesktopProcesses() => new[] { "WindowSocks", "xa sk5" }.SelectMany(Process.GetProcessesByName).ToArray();
    private static bool DesktopRunning()
    {
        var processes = DesktopProcesses();
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static string SafePath(string entry)
    {
        string path = Path.GetFullPath(Path.Combine(InstallDirectory, entry));
        if (!path.StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("套件路徑不合法。"); return path;
    }
    private static void Validate(ZipArchive zip)
    {
        foreach (var entry in zip.Entries) _ = SafePath(entry.FullName);
        foreach (var file in new[] { "WindowSocks.exe", "WindowSocks.Service.exe", "WindowSocksNative.dll", "操作說明.html" }) if (zip.GetEntry(file) is null) throw new InvalidDataException("套件缺少 " + file);
    }
    private static void Protect(string directory, bool userRead)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("安裝目錄不可為重新解析點或符號連結。");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) acl.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (userRead) acl.AddAccessRule(new(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        // Configuration contains machine-DPAPI secrets: only its owner, SYSTEM and administrators may read it.
        if (directory == DataDirectory) {
            acl.RemoveAccessRuleAll(new(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            var ownerFile = Path.Combine(directory, "owner.sid");
            if (File.Exists(ownerFile) && (File.GetAttributes(ownerFile) & FileAttributes.ReparsePoint) != 0) throw new IOException("安裝使用者設定不可為符號連結。");
            var owner = File.Exists(ownerFile) ? new SecurityIdentifier(File.ReadAllText(ownerFile).Trim()) : new SecurityIdentifier(installOwnerSid);
            acl.AddAccessRule(new(owner, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
    private static void Run(string file, string[] args, bool required = true)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new IOException("系統服務操作逾時。"); }
        if (required && process.ExitCode != 0) throw new IOException($"{file} 執行失敗：{output.GetAwaiter().GetResult()} {error.GetAwaiter().GetResult()}");
    }
    private static void WaitForServiceStop()
    {
        for (int i = 0; i < 60; i++) {
            bool alive = false;
            foreach (var process in Process.GetProcessesByName("WindowSocks.Service")) using (process) {
                try { alive |= !process.HasExited && string.Equals(process.MainModule?.FileName, Path.Combine(InstallDirectory, "WindowSocks.Service.exe"), StringComparison.OrdinalIgnoreCase); }
                catch (InvalidOperationException) { }
            }
            if (!alive) return;
            Thread.Sleep(250);
        }
        throw new IOException("背景服務尚未停止，沒有覆寫執行中的檔案。");
    }
    private static void Uninstall()
    {
        if (MessageBox.Show("解除安裝將停止代理服務並恢復原有連線。\n請先關閉受管遊戲／模擬器。加密設定將保留。", "解除安裝 xa sk5", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        if (DesktopRunning()) throw new InvalidOperationException("請先從系統匣結束 xa sk5。");
        string vmwareBackup = Path.Combine(DataDirectory, "vmware-network-backup.json");
        if (File.Exists(vmwareBackup)) {
            using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(vmwareBackup));
            if (record.RootElement.GetProperty("phase").GetString() != "restored") throw new InvalidOperationException("請先啟動 xa sk5，將 VMware 虛擬機關機後，在「VMware 虛擬機」按「回復原網路」，再解除安裝。");
        }
        string ownerFile = Path.Combine(DataDirectory, "owner.sid");
        string ownerSid = File.Exists(ownerFile) ? new SecurityIdentifier(File.ReadAllText(ownerFile).Trim()).Value : installOwnerSid;
        using (var user = Registry.Users.OpenSubKey(ownerSid, true)) { if (user is not null) { new StartupRegistry(user).RemoveForDirectory(InstallDirectory); WindowsShortcuts.RemoveDesktopForDirectory(user, InstallDirectory); } }
        Run("sc.exe", ["stop", "WindowSocks"], false); WaitForServiceStop(); Run("sc.exe", ["delete", "WindowSocks"], false);
        Run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", "name=WindowSocks relay"], false);
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowSocks", false);
        foreach (string name in new[] { "xa sk5.lnk", "窗流 WindowSocks.lnk" }) {
            string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name); if (File.Exists(shortcut)) File.Delete(shortcut);
        }
        foreach (string file in Directory.EnumerateFiles(InstallDirectory, "*", SearchOption.AllDirectories))
        {
            if (!Path.GetFullPath(file).StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("拒絕刪除安裝範圍之外的檔案。");
            try { File.Delete(file); } catch (IOException) { MoveFileEx(file, null, 4); }
        }
        foreach (string folder in Directory.EnumerateDirectories(InstallDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) {
            if (!Path.GetFullPath(folder).StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("拒絕刪除安裝範圍之外的目錄。");
            try { Directory.Delete(folder, recursive: false); } catch (IOException) { MoveFileEx(folder, null, 4); }
        }
        try { Directory.Delete(InstallDirectory, recursive: false); } catch (IOException) { MoveFileEx(InstallDirectory, null, 4); }
        MessageBox.Show("xa sk5 已解除安裝。被鎖定的檔案將於重新啟動後移除；設定仍保留於 ProgramData\\WindowSocks。", "xa sk5");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existing, string? destination, int flags);
}

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WindowSocks.Core;

namespace WindowSocks.Setup;

internal static partial class Program
{
    private static int RunComponentInstaller(string[] args)
    {
        int ownerIndex = Array.IndexOf(args, "--owner-sid");
        if (ownerIndex >= 0) {
            if (ownerIndex + 1 >= args.Length) throw new ArgumentException("缺少安裝使用者。");
            var sid = new SecurityIdentifier(args[ownerIndex + 1]);
            if (!sid.IsAccountSid()) throw new ArgumentException("安裝使用者不是有效帳戶。");
            _ = sid.Translate(typeof(NTAccount)); installOwnerSid = sid.Value;
        }
        using var form = new ComponentInstallerForm(); Application.Run(form); return form.Result;
    }

    internal static async Task InstallComponents(IProgress<string> progress, CancellationToken ct)
    {
        var lines = new List<string>();
        void Report(string message) { lines.Add($"{DateTimeOffset.Now:O} {message}"); progress.Report(message); }
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        bool sameDirectory = source.Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase);
        string? staging = null; bool serviceStopped = false;
        try {
            Report("檢查程式套件完整性…");
            var files = ComponentPackage.VerifyManifest(source);
            if (!sameDirectory) {
                foreach (var process in DesktopProcesses()) using (process) {
                    if (process.MainModule?.FileName is { } exe && Path.GetDirectoryName(exe)!.Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("已安裝的 xa sk5 仍在執行。請先從其系統匣結束，再由可攜版重新安裝。可攜版視窗可保持開啟。");
                }
            }
            Directory.CreateDirectory(DataDirectory); Protect(DataDirectory, true);
            string owner = Path.Combine(DataDirectory, "owner.sid"); if (!File.Exists(owner)) File.WriteAllText(owner, installOwnerSid);
            staging = ComponentPackage.ContainedPath(DataDirectory, "component-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string stagedApp = Path.Combine(staging, "app"); Directory.CreateDirectory(stagedApp);
            if (!sameDirectory) {
                foreach (var file in files) {
                    ct.ThrowIfCancellationRequested();
                    string destination = ComponentPackage.ContainedPath(stagedApp, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(ComponentPackage.ContainedPath(source, file.Path), destination);
                    if (!Hash(destination).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("複製程式元件時完整性檢查失敗。");
                }
                File.Copy(Path.Combine(source, "install-manifest.json"), Path.Combine(stagedApp, "install-manifest.json"));
            }
            string driver = Path.Combine(staging, "driver"); Directory.CreateDirectory(driver);
            // Reuse a complete existing installation after validation; otherwise use only the pinned official URL.
            if (ComponentPackage.DriverFiles.All(name => File.Exists(Path.Combine(InstallDirectory, name)))) {
                Report("檢查既有 WinDivert x64 元件與簽章…");
                foreach (string name in ComponentPackage.DriverFiles) File.Copy(Path.Combine(InstallDirectory, name), Path.Combine(driver, name));
            } else if (ComponentPackage.DriverFiles.All(name => File.Exists(Path.Combine(source, name)) && files.Any(file => file.Path.Equals(name, StringComparison.OrdinalIgnoreCase)))) {
                Report("檢查套件內 WinDivert x64 元件與簽章…");
                foreach (string name in ComponentPackage.DriverFiles) File.Copy(Path.Combine(source, name), Path.Combine(driver, name));
            } else {
                Report("從 GitHub 官方發布頁下載 WinDivert 2.2.2（約 400 KB）…");
                string archive = Path.Combine(staging, "WinDivert-2.2.2-A.zip");
                await ComponentPackage.DownloadDriverAsync(archive, ct);
                Report("檢查官方壓縮包、x64 架構與驅動數位簽章…");
                using var stream = File.OpenRead(archive); ComponentPackage.ExtractDriver(stream, driver);
            }
            foreach (string name in ComponentPackage.DriverFiles) ComponentPackage.RequireX64(Path.Combine(driver, name));
            DriverTrust.VerifySignature(Path.Combine(driver, "WinDivert64.sys"));
            Report("驅動簽章通過；SHA-256 " + Hash(Path.Combine(driver, "WinDivert64.sys")));
            ct.ThrowIfCancellationRequested();
            Report("正在安裝代理元件；請勿關機或結束安裝程序…");
            Run("sc.exe", ["stop", "WindowSocks"], false); WaitForServiceStop(); serviceStopped = true;
            Directory.CreateDirectory(InstallDirectory); Protect(InstallDirectory, true);
            foreach (string entry in Directory.EnumerateFileSystemEntries(InstallDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) {
                // Each destination ancestor is checked again before writing; never follow a user-created junction.
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("安裝範圍包含不允許的符號連結。");
            }
            if (!sameDirectory) {
                foreach (string file in Directory.EnumerateFiles(stagedApp, "*", SearchOption.AllDirectories)) {
                    string destination = ComponentPackage.ContainedPath(InstallDirectory, Path.GetRelativePath(stagedApp, file));
                    RejectLinkedPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, overwrite: true);
                }
            }
            foreach (string name in ComponentPackage.DriverFiles) {
                string destination = Path.Combine(InstallDirectory, name), staged = Path.Combine(driver, name);
                RejectLinkedPath(destination);
                if (File.Exists(destination) && Hash(destination) == Hash(staged)) continue;
                // Never unload a shared WinDivert service used by another application.
                File.Copy(staged, destination, overwrite: true);
            }
            Report("以不攔截任何流量的篩選器測試驅動載入…");
            DriverTrust.ProbeInstalled(Path.Combine(InstallDirectory, "WinDivert.dll"));
            Report("WinDivert 安裝及載入測試通過；建立背景服務…");
            string uninstall = Path.Combine(InstallDirectory, "WindowSocks-Uninstall.exe");
            // The renamed apphost still uses WindowSocks.Maintenance.dll, which is included in the payload.
            if (!File.Exists(uninstall)) File.Copy(Path.Combine(InstallDirectory, "WindowSocks.Maintenance.exe"), uninstall);
            RegisterServiceAndShell(uninstall); Run("sc.exe", ["start", "WindowSocks"]); serviceStopped = false;
            Report("服務已啟動，驅動載入測試已通過。窗口分流仍須另行綁定並驗收。");
        }
        catch (Exception ex) {
            uint code = unchecked((uint)ex.HResult) & 0xffff;
            string error = code is 225 or 226 or 577 or 1275 ? ComponentPackage.DriverError(code) : ex is OperationCanceledException ? "下載／準備已取消。" : ex is HttpRequestException ? "無法下載官方驅動，請檢查網路、GitHub 可用性及 Windows 安全性紀錄。" : ex.Message;
            Report("未完成：" + error);
            if (serviceStopped) { Run("sc.exe", ["start", "WindowSocks"], false); Report("已嘗試恢復既有服務，請在主程式檢查實際狀態。"); }
            throw new IOException(error, ex);
        }
        finally {
            try { if (Directory.Exists(DataDirectory)) File.WriteAllLines(Path.Combine(DataDirectory, "component-install.log"), lines, Encoding.UTF8); } catch (IOException) { }
            if (staging is not null) {
                // This random directory is beneath the ACL-protected application data directory.
                string verified = ComponentPackage.ContainedPath(DataDirectory, Path.GetFileName(staging));
                if (verified.Equals(staging, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(verified, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void RejectLinkedPath(string path)
    {
        for (string? current = path; current is not null && current.StartsWith(InstallDirectory, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("安裝路徑包含不允許的符號連結。");
    }
}

internal sealed class ComponentInstallerForm : Form
{
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Label status = new() { Text = "正在準備…", Dock = DockStyle.Top, Height = 65 };
    private readonly Button close = new() { Text = "取消準備", Dock = DockStyle.Bottom, Height = 38 };
    private readonly CancellationTokenSource cancel = new();
    private bool finished;
    public int Result { get; private set; } = 1;
    public ComponentInstallerForm()
    {
        Text = "xa sk5 — 安裝／修復代理元件"; Width = 820; Height = 490; StartPosition = FormStartPosition.CenterScreen; Padding = new Padding(20); Font = new Font("Microsoft JhengHei UI", 10);
        Controls.Add(log); Controls.Add(status); Controls.Add(close);
        close.Click += (_, _) => { if (finished) Close(); else { cancel.Cancel(); close.Enabled = false; } };
        FormClosing += (_, e) => { if (!finished) e.Cancel = true; };
        Shown += async (_, _) => {
            var progress = new Progress<string>(message => { status.Text = message; log.AppendText(message + Environment.NewLine); if (message.StartsWith("正在安裝")) close.Enabled = false; });
            try { await Task.Run(() => Program.InstallComponents(progress, cancel.Token)); Result = 0; status.Text = "完成：服務已啟動、驅動可載入。可回到主程式分配代理。"; }
            catch (Exception ex) { status.Text = "未完成：" + ex.Message; }
            finally { finished = true; close.Text = "關閉並返回 xa sk5"; close.Enabled = true; }
        };
        FormClosed += (_, _) => cancel.Dispose();
    }
}

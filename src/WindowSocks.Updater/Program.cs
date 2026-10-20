using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Updater;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try {
            if (args.Length != 2 || args[0] is not ("--update" or "--worker")) throw new ArgumentException("請從 xa sk5 的「檢查更新」啟動更新。");
            string jobFile = Path.GetFullPath(args[1]); AppReleasePackage.RejectLinks(jobFile);
            if (new FileInfo(jobFile).Length > 16384) throw new IOException("更新工作資料超出限制。");
            var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(jobFile), Json.Options) ?? throw new IOException("缺少更新工作。");
            ValidateJob(job);
            if (args[0] == "--worker") return Execute(job, jobFile);
            using var form = new UpdateForm(job, jobFile); Application.Run(form); return form.Result;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "xa sk5 更新未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    private static void ValidateJob(UpdateJob job)
    {
        _ = AppReleasePackage.ParseVersion(job.Version);
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Target));
        AppReleasePackage.RejectLinks(target);
        if (!Path.IsPathFullyQualified(job.Target) || target.Length < 4 || !Directory.Exists(target) || !File.Exists(Path.Combine(target, "WindowSocks.exe"))) throw new IOException("找不到原有 xa sk5 程式目錄。");
        if (!Path.GetDirectoryName(Path.GetFullPath(job.RestartExe))!.Equals(target, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(job.RestartExe) is not ("xa sk5.exe" or "WindowSocks.exe")) throw new IOException("重新開啟的位置與原程式不符。");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (target.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("不可更新 Windows 系統目錄內的程式。");
        if (target.Equals(UpdatePaths.Installed, StringComparison.OrdinalIgnoreCase) && !job.IncludeService) throw new IOException("已安裝版本必須一併更新背景服務。");
    }
    private static bool Administrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static void AtomicJson<T>(string path, T data) { File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, Json.Options)); File.Move(path + ".tmp", path, true); }
    internal static int Execute(UpdateJob job, string jobFile)
    {
        string work = Path.GetDirectoryName(jobFile)!;
        var backups = new List<string>(); var transactions = new List<FileUpdateTransaction>();
        bool stopped = false, wasRunning = false; string? secureStage = null;
        var settings = new List<(string Source, string Backup)>();
        string? previousDisplayVersion = null;
        void Report(string value) { AtomicJson(Path.Combine(work, "status.json"), new UpdateProgress(value)); File.AppendAllText(Path.Combine(work, "update.log"), $"{DateTimeOffset.Now:O} {value}\n"); }
        using var mutex = new Mutex(false, @"Global\WindowSocks.AppUpdate");
        bool acquired = false;
        try {
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一個 xa sk5 更新正在執行。");
            if (job.IncludeService && !Administrator) throw new IOException("更新背景服務需要使用者核准 Windows 管理員授權。");
            string[] targets = new[] { job.Target }.Concat(job.IncludeService ? [UpdatePaths.Installed] : Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string target in targets) {
                AppReleasePackage.RejectLinks(target);
                foreach (var process in new[] { "xa sk5", "WindowSocks" }.SelectMany(Process.GetProcessesByName)) using (process) {
                    if (process.MainModule?.FileName is { } exe && Path.GetDirectoryName(exe)!.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new IOException("此程式目錄仍有 xa sk5 介面執行中；沒有替換任何檔案。請結束該介面後再試。");
                }
            }
            string source = job.Payload;
            if (!job.Recover) {
                Report("重新驗證更新套件與元件版本…"); AppReleasePackage.VerifyPayload(source, job.Version);
                foreach (string target in targets) {
                    string dll = Path.Combine(target, "WindowSocks.Core.dll");
                    if (File.Exists(dll) && AppReleasePackage.ParseVersion(System.Reflection.AssemblyName.GetAssemblyName(dll).Version!.ToString(3)) > AppReleasePackage.ParseVersion(job.Version)) throw new IOException("此更新比現有元件舊，已拒絕降版。");
                }
            }
            if (job.IncludeService) {
                using var registration = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowSocks"); previousDisplayVersion = registration?.GetValue("DisplayVersion") as string;
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WindowSocks");
                string path = (key?.GetValue("ImagePath") as string ?? "").Trim('"');
                if (!path.Equals(Path.Combine(UpdatePaths.Installed, "WindowSocks.Service.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("背景服務路徑與標準安裝不符，已停止。");
                using var service = new ServiceController("WindowSocks"); wasRunning = service.Status != ServiceControllerStatus.Stopped;
                // Stage into an administrator-owned directory before touching a privileged installation.
                secureStage = Path.Combine(UpdatePaths.ServiceData, "update-stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(secureStage); Protect(secureStage);
                if (!job.Recover) {
                    Report("複製並驗證受保護的更新暫存…");
                    foreach (var file in AppReleasePackage.VerifyPayload(source, job.Version).Select(f => f.Path).Append("install-manifest.json")) {
                        string destination = AppReleasePackage.SafePath(secureStage, "payload/" + file); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(AppReleasePackage.SafePath(source, file), destination);
                    }
                    source = Path.Combine(secureStage, "payload"); AppReleasePackage.VerifyPayload(source, job.Version);
                }
                Report("停止 xa sk5 背景服務；代理連線將暫時中斷…"); StopService(); stopped = true;
                foreach (string name in new[] { "settings.dpapi", "settings.dpapi.bak", "owner.sid" }) {
                    string original = Path.Combine(UpdatePaths.ServiceData, name);
                    if (!File.Exists(original)) continue; AppReleasePackage.RejectLinks(original);
                    string backup = Path.Combine(secureStage, name); File.Copy(original, backup); settings.Add((original, backup));
                }
                backups.Add(secureStage);
            }
            if (job.Recover) {
                Report("正在回復未完成更新的備份…");
                ServiceRecovery? recovery = null;
                var pendingBackups = targets.SelectMany(FileUpdateTransaction.Pending).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string pending in pendingBackups.ToArray()) {
                    string group = Path.Combine(pending, "group-recovery.json");
                    if (File.Exists(group)) foreach (string backup in JsonSerializer.Deserialize<string[]>(File.ReadAllText(group), Json.Options) ?? []) {
                        if (!targets.Contains(Path.GetDirectoryName(Path.GetFullPath(backup))!, StringComparer.OrdinalIgnoreCase)) throw new IOException("此更新另含其他程式目錄，請勾選一併更新背景服務後重試回復。");
                        pendingBackups.Add(backup);
                    }
                }
                foreach (string pending in pendingBackups) {
                    string record = Path.Combine(pending, "service-recovery.json");
                    if (job.IncludeService && File.Exists(record)) recovery = JsonSerializer.Deserialize<ServiceRecovery>(File.ReadAllText(record), Json.Options);
                    var transaction = FileUpdateTransaction.Open(pending); transactions.Add(transaction); backups.Add(pending); transaction.Rollback();
                }
                if (transactions.Count == 0) throw new IOException("沒有找到可回復的未完成更新。");
                if (recovery is not null) {
                    string stagePrefix = UpdatePaths.ServiceData + Path.DirectorySeparatorChar + "update-stage-";
                    if (!Path.GetFullPath(recovery.Directory).StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase) && !Path.GetFullPath(recovery.Directory).StartsWith(UpdatePaths.ServiceData + Path.DirectorySeparatorChar + "setup-stage-", StringComparison.OrdinalIgnoreCase)) throw new IOException("服務設定備份位置無效。");
                    AppReleasePackage.RejectLinks(recovery.Directory);
                    foreach (var file in recovery.Files) {
                        if (file.Path is not ("settings.dpapi" or "settings.dpapi.bak" or "owner.sid")) throw new IOException("服務設定備份內容無效。");
                        string backup = AppReleasePackage.SafePath(recovery.Directory, file.Path); AppReleasePackage.RejectLinks(backup);
                        if (AppReleasePackage.Hash(backup) != file.Sha256) throw new IOException("服務設定備份完整性失敗。");
                    }
                    foreach (var file in recovery.Files) File.Copy(Path.Combine(recovery.Directory, file.Path), Path.Combine(UpdatePaths.ServiceData, file.Path), true);
                    wasRunning = recovery.WasRunning; backups.Add(recovery.Directory);
                }
            } else {
                Report("備份原程式並檢查檔案占用…");
                foreach (string target in targets) {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (true) {
                        try { var transaction = FileUpdateTransaction.Prepare(source, target, job.Version); transactions.Add(transaction); backups.Add(transaction.BackupDirectory); break; }
                        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && DateTime.UtcNow < deadline) { Thread.Sleep(700); }
                    }
                }
                if (job.IncludeService && secureStage is not null) {
                    var recovery = new ServiceRecovery(secureStage, wasRunning, settings.Select(s => new InstallFile(Path.GetFileName(s.Source), AppReleasePackage.Hash(s.Backup))).ToArray());
                    foreach (var transaction in transactions) AtomicJson(Path.Combine(transaction.BackupDirectory, "service-recovery.json"), recovery);
                }
                foreach (var transaction in transactions) AtomicJson(Path.Combine(transaction.BackupDirectory, "group-recovery.json"), transactions.Select(t => t.BackupDirectory).ToArray());
                foreach (var transaction in transactions) {
                    Report("安裝已驗證的程式檔案…");
                    transaction.Apply(source, beforeFile: () => { if (job.IncludeService) { using var service = new ServiceController("WindowSocks"); if (service.Status != ServiceControllerStatus.Stopped) throw new IOException("檔案替換期間服務重新啟動，已中止並準備回復。"); } });
                }
            }
            if (job.IncludeService && wasRunning) {
                Report("重新啟動並驗證背景服務…"); StartService();
                var snapshot = PipeTransport.CallAsync<Snapshot>(Command.Create("snapshot", new { }), new CancellationTokenSource(TimeSpan.FromSeconds(25)).Token).GetAwaiter().GetResult();
                string expectedVersion = job.Recover ? System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(UpdatePaths.Installed, "WindowSocks.Core.dll")).Version!.ToString(3) : job.Version;
                if (snapshot.Diagnostics.Version.Split(' ')[0] != expectedVersion) throw new IOException("服務回報的版本不符，將回復原版本。");
            }
            if (job.IncludeService) {
                string displayVersion = job.Recover ? System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(UpdatePaths.Installed, "WindowSocks.Core.dll")).Version!.ToString(3) : job.Version;
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowSocks", true); key?.SetValue("DisplayVersion", displayVersion);
            }
            if (!job.Recover) foreach (var transaction in transactions) transaction.Commit();
            else foreach (var transaction in transactions) File.WriteAllText(Path.Combine(transaction.BackupDirectory, "service-recovery-complete"), "complete");
            Report(job.Recover ? "已回復原程式；正在重新開啟 xa sk5。" : "更新完成；正在重新開啟 xa sk5。");
            AtomicJson(Path.Combine(work, "result.json"), new UpdateResult(true, job.Recover, job.Recover ? "已回復未完成的更新。" : "已更新至 " + job.Version, backups.ToArray(), DateTimeOffset.Now));
            return 0;
        }
        catch (Exception ex) {
            string message = ex.Message;
            try {
                if (stopped) StopService();
                if (!job.Recover) {
                    foreach (var transaction in transactions.AsEnumerable().Reverse()) transaction.Rollback();
                    foreach (var (original, backup) in settings) File.Copy(backup, original, true);
                }
                if (stopped && wasRunning) StartService();
                if (!job.Recover) foreach (var transaction in transactions) File.WriteAllText(Path.Combine(transaction.BackupDirectory, "service-recovery-complete"), "complete");
                if (previousDisplayVersion is not null) { using var registration = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowSocks", true); registration?.SetValue("DisplayVersion", previousDisplayVersion); }
                message += transactions.Count > 0 ? " 已回復原程式，備份保留。" : " 原程式未變更。";
            }
            catch (Exception recovery) { message += " 回復尚未完成：" + recovery.Message + "；請保留備份，使用「回復上次更新」。"; }
            Report("更新未完成：" + message); AtomicJson(Path.Combine(work, "result.json"), new UpdateResult(false, false, message, backups.ToArray(), DateTimeOffset.Now)); return 1;
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
    private sealed record ServiceRecovery(string Directory, bool WasRunning, InstallFile[] Files);
    private static void Protect(string path)
    {
        AppReleasePackage.RejectLinks(path); var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544" }) acl.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    private static void StopService()
    {
        using var service = new ServiceController("WindowSocks");
        if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(40)); }
        // Dispose every process handle before copying its mapped binaries.
        for (int i = 0; i < 60; i++) { bool alive = false; foreach (var p in Process.GetProcessesByName("WindowSocks.Service")) using (p) { alive |= !p.HasExited; } if (!alive) return; Thread.Sleep(500); }
        throw new IOException("背景服務程序仍在結束中，尚未覆寫程式。");
    }
    private static void StartService()
    {
        using var service = new ServiceController("WindowSocks"); if (service.Status != ServiceControllerStatus.Running) service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(40));
    }

    private sealed class UpdateForm : Form
    {
        private readonly Label status = new() { Dock = DockStyle.Top, Height = 90, Text = "準備更新…" };
        private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Button close = new() { Dock = DockStyle.Bottom, Height = 40, Text = "更新中…", Enabled = false };
        private bool finished;
        public int Result { get; private set; } = 1;
        public UpdateForm(UpdateJob job, string jobFile)
        {
            Text = "xa sk5 — 更新進度"; Width = 740; Height = 400; StartPosition = FormStartPosition.CenterScreen; Padding = new(20); Font = new("Microsoft JhengHei UI", 10);
            Controls.Add(details); Controls.Add(status); Controls.Add(close); close.Click += (_, _) => Close(); FormClosing += (_, e) => { if (!finished) e.Cancel = true; };
            Shown += async (_, _) => {
                string work = Path.GetDirectoryName(jobFile)!;
                try {
                    // Establish readiness before asking the GUI to release its own binaries.
                    File.WriteAllText(Path.Combine(work, "ready"), "ready");
                    if (job.ParentPid > 0) {
                        try {
                            using var parent = Process.GetProcessById(job.ParentPid);
                            if (parent.StartTime.ToUniversalTime().Ticks == job.ParentStartTime) { status.Text = "等候 xa sk5 介面關閉，背景服務目前維持運作…"; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await parent.WaitForExitAsync(timeout.Token); }
                        } catch (ArgumentException) { }
                    }
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add("--worker"); start.ArgumentList.Add(jobFile); if (job.IncludeService) start.Verb = "runas";
                    status.Text = job.IncludeService ? "等候你核准 Windows 管理員授權；取消授權會重新開啟原介面。" : "開始備份與更新程式…"; status.Refresh();
                    using var worker = Process.Start(start) ?? throw new IOException("無法啟動更新工作。");
                    string previous = "";
                    while (!worker.HasExited) {
                        string file = Path.Combine(work, "status.json");
                        if (File.Exists(file)) try { var progress = JsonSerializer.Deserialize<UpdateProgress>(File.ReadAllText(file), Json.Options); if (progress?.Message is { } message && message != previous) { previous = message; status.Text = message; details.AppendText(message + Environment.NewLine); } } catch (IOException) { } catch (JsonException) { }
                        await Task.Delay(250);
                    }
                    var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(Path.Combine(work, "result.json")), Json.Options)!;
                    Result = result.Success ? 0 : 1; status.Text = result.Message; details.AppendText("\r\n備份：\r\n" + string.Join("\r\n", result.Backups));
                    Restart(job, Path.Combine(work, "result.json"));
                    if (result.Success) { finished = true; Close(); return; }
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { status.Text = "你已取消 Windows 管理員授權。程式與背景服務均未變更。"; Restart(job, null); }
                catch (Exception ex) { status.Text = "更新未完成：" + ex.Message; try { Restart(job, null); } catch { } }
                finished = true; close.Enabled = true; close.Text = "關閉更新視窗";
            };
        }
        private static void Restart(UpdateJob job, string? result)
        {
            // This coordinator stays at the original user's privilege level, even when its worker elevates.
            var start = new ProcessStartInfo(job.RestartExe) { UseShellExecute = false, WorkingDirectory = job.Target };
            if (result is not null) { start.ArgumentList.Add("--update-result"); start.ArgumentList.Add(result); }
            Process.Start(start)?.Dispose();
        }
    }
}

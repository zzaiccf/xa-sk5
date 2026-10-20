using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Setup;

internal static partial class Program
{
    private static string ExtractEmbedded(string stage)
    {
        string archive = Path.Combine(stage, "payload.zip"), payload = Path.Combine(stage, "payload");
        using (var input = Payload()) using (var output = File.Create(archive)) input.CopyTo(output);
        AppReleasePackage.Extract(archive, payload, ProductVersion); return payload;
    }
    private static void VerifyEmbeddedPayload()
    {
        string stage = Path.Combine(Path.GetTempPath(), "xa-sk5-verify-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try {
            string payload = ExtractEmbedded(stage); var files = AppReleasePackage.VerifyPayload(payload, ProductVersion);
            // No write to the installation directory, no service operations and no UAC required by this code path.
            Console.WriteLine($"OK: {ProductVersion}; {files.Length} verified files.");
        } finally { Directory.Delete(stage, true); }
    }
    private static void InstallEmbeddedPayload()
    {
        using var updateMutex = new Mutex(false, @"Global\WindowSocks.AppUpdate");
        bool acquired;
        try { acquired = updateMutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("另一個 xa sk5 更新正在執行。");
        try { InstallEmbeddedPayloadLocked(); } finally { updateMutex.ReleaseMutex(); }
    }
    private static void InstallEmbeddedPayloadLocked()
    {
        using var previousRegistration = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowSocks");
        string? previousVersion = previousRegistration?.GetValue("DisplayVersion") as string;
        foreach (var process in DesktopProcesses()) using (process)
            if (process.MainModule?.FileName is { } exe && Path.GetDirectoryName(exe)!.Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase)) throw new IOException("請從系統匣結束已安裝的 xa sk5 介面後再安裝。");
        if (File.Exists(Path.Combine(InstallDirectory, "WindowSocks.Core.dll")) && AppReleasePackage.ParseVersion(AssemblyName.GetAssemblyName(Path.Combine(InstallDirectory, "WindowSocks.Core.dll")).Version!.ToString(3)) > AppReleasePackage.ParseVersion(ProductVersion)) throw new IOException("此安裝包比已安裝版本舊，已拒絕降版。");
        Directory.CreateDirectory(DataDirectory); Protect(DataDirectory, true);
        string stage = Path.Combine(DataDirectory, "setup-stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        FileUpdateTransaction? transaction = null; bool stopped = false;
        var settings = new List<(string Original, string Backup)>();
        try {
            string payload = ExtractEmbedded(stage);
            // Stage and verify everything before stopping an active service.
            Run("sc.exe", ["stop", "WindowSocks"], false); WaitForServiceStop(); stopped = true;
            foreach (string name in new[] { "settings.dpapi", "settings.dpapi.bak", "owner.sid" }) {
                string original = Path.Combine(DataDirectory, name); if (!File.Exists(original)) continue; AppReleasePackage.RejectLinks(original);
                string backup = Path.Combine(stage, name); File.Copy(original, backup); settings.Add((original, backup));
            }
            Directory.CreateDirectory(InstallDirectory); Protect(InstallDirectory, true);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true) {
                try { transaction = FileUpdateTransaction.Prepare(payload, InstallDirectory, ProductVersion); break; }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && DateTime.UtcNow < deadline) { Thread.Sleep(700); }
            }
            File.WriteAllText(Path.Combine(transaction.BackupDirectory, "service-recovery.json"), JsonSerializer.Serialize(new { directory = stage, wasRunning = true, files = settings.Select(s => new InstallFile(Path.GetFileName(s.Original), AppReleasePackage.Hash(s.Backup))).ToArray() }, Json.Options));
            transaction.Apply(payload);
            string sidFile = Path.Combine(DataDirectory, "owner.sid"); if (!File.Exists(sidFile)) File.WriteAllText(sidFile, installOwnerSid);
            string uninstall = Path.Combine(InstallDirectory, "WindowSocks-Uninstall.exe");
            // A maintenance apphost resolves the versioned Maintenance.dll in the installation.
            File.Copy(Path.Combine(InstallDirectory, "WindowSocks.Maintenance.exe"), uninstall, true);
            RegisterServiceAndShell(uninstall); Run("sc.exe", ["start", "WindowSocks"]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var snapshot = PipeTransport.CallAsync<Snapshot>(Command.Create("snapshot", new { }), timeout.Token).GetAwaiter().GetResult();
            if (snapshot.Diagnostics.Version.Split(' ')[0] != ProductVersion) throw new IOException("安裝後服務版本驗證失敗。");
            transaction.Commit();
            File.WriteAllText(Path.Combine(DataDirectory, "last-update.json"), JsonSerializer.Serialize(new { version = ProductVersion, success = true, backup = transaction.BackupDirectory, at = DateTimeOffset.Now }));
        }
        catch {
            if (stopped) {
                Run("sc.exe", ["stop", "WindowSocks"], false); WaitForServiceStop();
                transaction?.Rollback(); foreach (var (original, backup) in settings) File.Copy(backup, original, true);
                Run("sc.exe", ["start", "WindowSocks"], false);
                if (transaction is not null) File.WriteAllText(Path.Combine(transaction.BackupDirectory, "service-recovery-complete"), "complete");
            }
            if (previousVersion is not null) { using var registration = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowSocks", true); registration?.SetValue("DisplayVersion", previousVersion); }
            throw;
        }
        // Stage and settings backups intentionally remain available under the protected data directory.
    }
}

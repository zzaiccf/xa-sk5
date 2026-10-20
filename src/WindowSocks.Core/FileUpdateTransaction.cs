using System.Text.Json;

namespace WindowSocks.Core;

public sealed record UpdateFileState(string Path, bool Existed, string OriginalHash);
public sealed record UpdateJournal(string Target, string Version, string Phase, UpdateFileState[] Files);

// The journal and immutable backups survive a terminated helper. Recovery always restores the old set,
// including removing newly introduced files. User configuration is never part of the program payload.
public sealed class FileUpdateTransaction
{
    public string BackupDirectory { get; }
    private UpdateJournal journal;
    private string JournalPath => Path.Combine(BackupDirectory, "update-journal.json");
    private FileUpdateTransaction(string backup, UpdateJournal journal) { BackupDirectory = backup; this.journal = journal; }
    public static FileUpdateTransaction Prepare(string source, string target, string version)
    {
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)); AppReleasePackage.RejectLinks(target);
        if (!Directory.Exists(target)) Directory.CreateDirectory(target);
        if (Pending(target).Length > 0)
            throw new IOException("發現未完成的更新。請先使用更新視窗的「回復上次更新」恢復備份。");
        var files = AppReleasePackage.VerifyPayload(source, version);
        var paths = files.Select(f => f.Path).Append("install-manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        string oldManifest = Path.Combine(target, "install-manifest.json");
        if (File.Exists(oldManifest)) {
            if (new FileInfo(oldManifest).Length > 1024 * 1024) throw new IOException("既有安裝清單無效。");
            foreach (var file in JsonSerializer.Deserialize<InstallFile[]>(File.ReadAllText(oldManifest), Json.Options) ?? []) {
                _ = AppReleasePackage.SafePath(target, file.Path);
                if (!AppReleasePackage.IsApplicationFile(file.Path)) throw new IOException("既有安裝清單含非程式檔案，已停止。");
                paths.Add(file.Path);
            }
        }
        var states = new List<UpdateFileState>();
        foreach (string path in paths.Order(StringComparer.OrdinalIgnoreCase)) {
            string destination = AppReleasePackage.SafePath(target, path), input = AppReleasePackage.SafePath(source, path);
            AppReleasePackage.RejectLinks(destination);
            bool existed = File.Exists(destination);
            if (existed && File.Exists(input) && AppReleasePackage.Hash(destination) == AppReleasePackage.Hash(input)) continue;
            // Detect occupied files before changing any file; never kill another program to release a handle.
            if (existed) { using var handle = File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            states.Add(new(path, existed, existed ? AppReleasePackage.Hash(destination) : ""));
        }
        string backup = Path.Combine(target, ".xa-sk5-backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup);
        var transaction = new FileUpdateTransaction(backup, new(target, version, "preparing", states.ToArray()));
        foreach (var file in states.Where(f => f.Existed)) {
            string destination = AppReleasePackage.SafePath(Path.Combine(backup, "files"), file.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(AppReleasePackage.SafePath(target, file.Path), destination);
            if (AppReleasePackage.Hash(destination) != file.OriginalHash) throw new IOException("備份完整性驗證失敗，尚未替換程式。");
        }
        transaction.SetPhase("prepared"); return transaction;
    }
    public static FileUpdateTransaction Open(string backup)
    {
        backup = Path.GetFullPath(backup); AppReleasePackage.RejectLinks(backup);
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(backup, "update-journal.json")), Json.Options) ?? throw new IOException("回復紀錄無效。");
        string parent = Path.GetDirectoryName(backup)!;
        if (!parent.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(journal.Target)), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(backup).StartsWith(".xa-sk5-backup-", StringComparison.Ordinal) || journal.Files.Length > 4000)
            throw new IOException("回復紀錄與程式位置不符。");
        foreach (var file in journal.Files) {
            if (!AppReleasePackage.IsApplicationFile(file.Path)) throw new IOException("回復紀錄含非程式檔案。");
            _ = AppReleasePackage.SafePath(parent, file.Path);
            if (file.Existed && !AppReleasePackage.ValidHash(file.OriginalHash)) throw new IOException("回復完整性資料無效。");
        }
        return new(backup, journal);
    }
    public static string[] Pending(string target) => Directory.Exists(target) ? Directory.EnumerateDirectories(target, ".xa-sk5-backup-*").Where(p => File.Exists(Path.Combine(p, "update-journal.json")) &&
        (Open(p).journal.Phase is not ("committed" or "rolledBack") || (Open(p).journal.Phase == "rolledBack" && File.Exists(Path.Combine(p, "service-recovery.json")) && !File.Exists(Path.Combine(p, "service-recovery-complete"))))).ToArray() : [];
    public void Apply(string source, Action<int>? afterFile = null, Action? beforeFile = null)
    {
        if (journal.Phase != "prepared") throw new IOException("更新不是已準備狀態。");
        AppReleasePackage.VerifyPayload(source, journal.Version); SetPhase("applying");
        int count = 0;
        foreach (var file in journal.Files) {
            beforeFile?.Invoke();
            string input = AppReleasePackage.SafePath(source, file.Path), target = AppReleasePackage.SafePath(journal.Target, file.Path);
            AppReleasePackage.RejectLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(input)) {
                // A same-directory rename avoids leaving a truncated destination on power loss.
                string temporary = target + ".xa-update-tmp"; AppReleasePackage.RejectLinks(temporary);
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)) {
                    using var stream = File.OpenRead(input); stream.CopyTo(output); output.Flush(true);
                }
                File.Move(temporary, target, true);
            } else if (File.Exists(target)) File.Delete(target);
            afterFile?.Invoke(++count);
        }
        AppReleasePackage.VerifyPayload(journal.Target, journal.Version, installed: true); SetPhase("applied");
    }
    public void Commit() { if (journal.Phase != "applied") throw new IOException("更新尚未完成，無法提交。"); SetPhase("committed"); }
    public void Rollback()
    {
        if (journal.Phase == "rolledBack") return;
        // Validate every backup before restoring any file.
        foreach (var file in journal.Files.Where(f => f.Existed)) {
            string backup = AppReleasePackage.SafePath(Path.Combine(BackupDirectory, "files"), file.Path); AppReleasePackage.RejectLinks(backup);
            if (AppReleasePackage.Hash(backup) != file.OriginalHash) throw new IOException("回復備份損毀；請保留備份並重新安裝原版本。");
        }
        SetPhase("rollingBack");
        foreach (var file in journal.Files.Reverse()) {
            string target = AppReleasePackage.SafePath(journal.Target, file.Path); AppReleasePackage.RejectLinks(target);
            if (file.Existed) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(AppReleasePackage.SafePath(Path.Combine(BackupDirectory, "files"), file.Path), target, true); }
            else if (File.Exists(target)) File.Delete(target);
            string temporary = target + ".xa-update-tmp"; AppReleasePackage.RejectLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary);
        }
        SetPhase("rolledBack");
    }
    private void SetPhase(string phase)
    {
        journal = journal with { Phase = phase }; string temporary = JournalPath + ".tmp";
        AppReleasePackage.RejectLinks(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { JsonSerializer.Serialize(stream, journal, Json.Options); stream.Flush(true); }
        File.Move(temporary, JournalPath, true);
    }
}

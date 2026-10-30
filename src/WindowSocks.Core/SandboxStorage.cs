using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WindowSocks.Core;

public sealed record SandboxStorageEntry(SandboxInstanceProfile? Profile, string Path, long? Bytes, bool Registered, string Error)
{
    public string Name => Profile?.Name ?? System.IO.Path.GetFileName(Path);
    public string Size => Bytes is long size ? $"{size / 1073741824d:F2} GB（檔案大小）" : "無法完整計算";
    public string State => Error.Length > 0 ? Error : Registered ? Profile!.RemovalPending ? "清理未完成，可重試" : "已保存實例" : "保留資料／未完成副本";
    public bool CanRemove => Profile is not null && Error.Length == 0;
}

// Operates as the interactive owner. Never elevates a recursive filesystem delete.
public sealed class SandboxStorage
{
    public string Root { get; }
    public SandboxStorage() : this(SandboxInstances.StorageRoot) { }
    internal SandboxStorage(string root) => Root = System.IO.Path.GetFullPath(root);
    public string InstanceRoot(SandboxInstanceProfile p)
    {
        SandboxInstances.Validate(p);
        string expected = System.IO.Path.Combine(Root, p.Id.ToString("N"));
        if (!System.IO.Path.GetFullPath(p.WorkingDirectory).Equals(System.IO.Path.Combine(expected, "Game"), StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFullPath(p.FileRoot).Equals(System.IO.Path.Combine(expected, "Box"), StringComparison.OrdinalIgnoreCase) ||
            SandboxInstances.Within(p.SourceLauncherPath, Root)) throw new IOException("副本路徑不是目前使用者的專用沙盒目錄，拒絕清理。");
        AppReleasePackage.RejectLinks(expected);
        AppReleasePackage.RejectLinks(p.SourceLauncherPath);
        return expected;
    }
    private string Records => System.IO.Path.Combine(Root, "RemovalRecords");
    public void Remember(SandboxInstanceProfile profile)
    {
        _ = InstanceRoot(profile); AppReleasePackage.RejectLinks(Records); Directory.CreateDirectory(Records);
        WriteJson(System.IO.Path.Combine(Records, profile.Id.ToString("N") + ".json"), profile);
    }
    public void Forget(SandboxInstanceProfile profile)
    {
        _ = InstanceRoot(profile); AppReleasePackage.RejectLinks(Records);
        string path = System.IO.Path.Combine(Records, profile.Id.ToString("N") + ".json");
        AppReleasePackage.RejectLinks(path); File.Delete(path);
    }
    internal static void WriteJson<T>(string path, T data)
    {
        AppReleasePackage.RejectLinks(path); AppReleasePackage.RejectLinks(path + ".tmp");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, Json.Options)); File.Move(path + ".tmp", path, true);
    }
    private static T ReadJson<T>(string file)
    {
        AppReleasePackage.RejectLinks(file);
        using var stream = File.OpenRead(file);
        if (stream.Length > 128 * 1024) throw new IOException("沙盒紀錄過大。");
        return JsonSerializer.Deserialize<T>(stream, Json.Options) ?? throw new IOException("沙盒紀錄無效。");
    }
    public SandboxStorageEntry Inspect(SandboxInstanceProfile profile, bool registered, CancellationToken ct)
    {
        string path = System.IO.Path.GetDirectoryName(profile.WorkingDirectory) ?? "";
        try { path = InstanceRoot(profile); return new(profile, path, Measure(path, ct), registered, ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception) { return new(profile, path, null, registered, ex.Message); }
    }
    public SandboxStorageEntry[] Scan(SandboxInstanceProfile[] registered, CancellationToken ct)
    {
        AppReleasePackage.RejectLinks(Root);
        var profiles = registered.ToDictionary(p => p.Id); var errors = new List<SandboxStorageEntry>();
        if (!Directory.Exists(Root)) return registered.Select(p => Inspect(p, true, ct)).ToArray();
        void ReadProfile(string file) {
            try { var p = ReadJson<SandboxInstanceProfile>(file); _ = InstanceRoot(p); profiles.TryAdd(p.Id, p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { errors.Add(new(null, file, null, false, "紀錄無法驗證，未允許刪除：" + ex.Message)); }
        }
        if (Directory.Exists(Records)) { AppReleasePackage.RejectLinks(Records); foreach (string file in Directory.EnumerateFiles(Records, "*.json")) { ct.ThrowIfCancellationRequested(); ReadProfile(file); } }
        string preparation = System.IO.Path.Combine(Root, "Preparation");
        if (Directory.Exists(preparation)) {
            AppReleasePackage.RejectLinks(preparation);
            foreach (string file in Directory.EnumerateFiles(preparation, "*.json")) {
                ct.ThrowIfCancellationRequested();
                try { foreach (var p in ReadJson<SandboxInstanceProfile[]>(file)) if (Directory.Exists(InstanceRoot(p))) profiles.TryAdd(p.Id, p); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { errors.Add(new(null, file, null, false, "準備紀錄無法驗證：" + ex.Message)); }
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(Root)) {
            ct.ThrowIfCancellationRequested(); string name = System.IO.Path.GetFileName(dir);
            if (name is "Preparation" or "RemovalRecords") continue;
            if (Guid.TryParseExact(name, "N", out var id) && profiles.ContainsKey(id)) continue;
            try {
                AppReleasePackage.RejectLinks(dir);
                string manifest = new[] { "instance.json", "prepared.json" }.Select(f => System.IO.Path.Combine(dir, f)).FirstOrDefault(File.Exists) ?? "";
                if (manifest.Length == 0) throw new IOException("找不到歸屬紀錄；請人工確認，未允許自動刪除。");
                var p = ReadJson<SandboxInstanceProfile>(manifest);
                if (!InstanceRoot(p).Equals(dir, StringComparison.OrdinalIgnoreCase)) throw new IOException("紀錄與目錄不符。");
                profiles.TryAdd(p.Id, p);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { errors.Add(new(null, dir, null, false, ex.Message)); }
        }
        var saved = registered.Select(p => p.Id).ToHashSet();
        return profiles.Values.Select(p => Inspect(p, saved.Contains(p.Id), ct)).Concat(errors).OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }
    private static long Measure(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AppReleasePackage.RejectLinks(path);
        if (!Directory.Exists(path)) return 0;
        using var pinned = LockDirectory(path);
        long size = 0;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos()) {
            ct.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("發現連結或接合點，請先人工確認：" + entry.FullName);
            size = checked(size + (entry is DirectoryInfo ? Measure(entry.FullName, ct) : ((FileInfo)entry).Length));
        }
        return size;
    }
    public void DeleteFiles(SandboxInstanceProfile profile, IProgress<string>? progress, CancellationToken ct)
    {
        string path = InstanceRoot(profile);
        _ = Measure(path, ct); // Reject an unsafe/inaccessible tree before deleting any part of it.
        if (!Directory.Exists(path)) return;
        using var parent = PinAncestors(Root);
        DeleteDirectory(path, progress, ct);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("仍有資料未清除，請重新掃描並重試。");
    }
    private static void DeleteDirectory(string path, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (var pinned = LockDirectory(path)) {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos()) {
                ct.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("清理途中出現連結，已停止：" + entry.FullName);
                if (entry is DirectoryInfo) DeleteDirectory(entry.FullName, progress, ct);
                else File.Delete(entry.FullName);
            }
        }
        // The parent remains pinned; non-recursive deletion never traverses a replacement link.
        Directory.Delete(path, recursive: false); progress?.Report("已清除 " + System.IO.Path.GetFileName(path));
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    private sealed class PinnedPath(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    }
    private static PinnedPath PinAncestors(string path)
    {
        var parents = new Stack<string>();
        for (DirectoryInfo? dir = new(path); dir is not null; dir = dir.Parent) parents.Push(dir.FullName);
        var handles = new List<SafeFileHandle>(); var pinned = new PinnedPath(handles);
        try { while (parents.Count > 0) handles.Add(LockDirectory(parents.Pop())); return pinned; }
        catch { pinned.Dispose(); throw; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
    private static SafeFileHandle LockDirectory(string path)
    {
        var handle = CreateFileW(path, 0x80, 3, 0, 3, 0x02000000 | 0x00200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("無法鎖定清理目錄：" + path, new Win32Exception(Marshal.GetLastWin32Error())); }
        if (!GetFileInformationByHandleEx(handle, 9, out var info, 8) || (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) { handle.Dispose(); throw new IOException("清理目錄身分變更或是重新解析點，已停止。"); }
        return handle;
    }
}

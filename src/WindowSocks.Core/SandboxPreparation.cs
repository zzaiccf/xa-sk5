using System.Text.Json;

namespace WindowSocks.Core;

public static class SandboxPreparation
{
    public static void CheckCapacity(SandboxInstanceProfile[] profiles, CancellationToken ct)
    {
        var required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles) {
            SandboxInstances.Validate(profile); ct.ThrowIfCancellationRequested();
            if (!File.Exists(profile.SourceLauncherPath)) throw new IOException("找不到原始遊戲啟動器。");
            if (File.Exists(Path.Combine(Path.GetDirectoryName(profile.WorkingDirectory)!, "prepared.json")) && File.Exists(profile.LauncherPath)) continue;
            string source = Path.GetDirectoryName(profile.SourceLauncherPath)!;
            if (SandboxInstances.Within(profile.WorkingDirectory, source) || SandboxInstances.Within(source, profile.WorkingDirectory)) throw new IOException("請選原始遊戲，不能選沙盒副本。");
            string drive = Path.GetPathRoot(profile.WorkingDirectory)!;
            required.TryAdd(drive, 2L * 1024 * 1024 * 1024);
            foreach (string file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })) {
                ct.ThrowIfCancellationRequested();
                string destination = Path.Combine(profile.WorkingDirectory, Path.GetRelativePath(source, file));
                var info = new FileInfo(file);
                if (!File.Exists(destination) || new FileInfo(destination).Length != info.Length || File.GetLastWriteTimeUtc(destination) != info.LastWriteTimeUtc)
                    required[drive] += info.Length;
            }
        }
        foreach (var (drive, bytes) in required) if (new DriveInfo(drive).AvailableFreeSpace < bytes)
            throw new IOException($"{drive} 空間不足，兩個副本尚需約 {Math.Ceiling(bytes / 1073741824d)} GB（含預留空間）。");
    }
    public static SandboxInstanceProfile[] ResumePair(string source, Guid a, Guid b, SandboxInstanceProfile[] existing)
    {
        string folder = Path.Combine(SandboxInstances.StorageRoot, "Preparation"); Directory.CreateDirectory(folder);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())));
        string path = Path.Combine(folder, key + ".json");
        var pending = File.Exists(path) ? JsonSerializer.Deserialize<SandboxInstanceProfile[]>(File.ReadAllText(path), Json.Options) ?? [] : [];
        var combined = (existing.Length == 2 ? existing : existing.Concat(pending)).Where(p => p.SourceLauncherPath.Equals(source, StringComparison.OrdinalIgnoreCase)).DistinctBy(p => p.Id).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        string name = Path.GetFileName(Path.GetDirectoryName(source)) ?? "遊戲";
        if (combined.Count == 0) combined.Add(SandboxInstances.Create(name + " A", source, a));
        if (combined.Count == 1) combined.Add(SandboxInstances.Create(name + (combined[0].Name.EndsWith(" B", StringComparison.Ordinal) ? " A" : " B"), source, b));
        if (combined.Count != 2) throw new InvalidOperationException("此遊戲的沙盒實例超過兩個，請先整理規則。");
        var result = combined.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        result[0] = result[0] with { ProxyId = a }; result[1] = result[1] with { ProxyId = b };
        foreach (var profile in result) SandboxInstances.Validate(profile);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(result, Json.Options)); File.Move(path + ".tmp", path, true);
        return result;
    }
    public static async Task Prepare(string sourceLauncher, SandboxInstanceProfile profile, IProgress<string>? progress, CancellationToken ct)
    {
        SandboxInstances.Validate(profile);
        string source = Path.GetDirectoryName(Path.GetFullPath(sourceLauncher))!;
        if (!File.Exists(sourceLauncher) || SandboxInstances.Within(profile.WorkingDirectory, source) || SandboxInstances.Within(source, profile.WorkingDirectory))
            throw new InvalidOperationException("請選擇原始遊戲啟動器，副本不可位於來源目錄內。");
        if (!SandboxInstances.Within(profile.WorkingDirectory, SandboxInstances.StorageRoot) || !SandboxInstances.Within(profile.FileRoot, SandboxInstances.StorageRoot))
            throw new InvalidOperationException("副本必須位於 xa sk5 專用沙盒目錄。");
        if (new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("來源目錄不可使用重新解析點。");
        AppReleasePackage.RejectLinks(profile.WorkingDirectory); AppReleasePackage.RejectLinks(profile.FileRoot);
        string manifest = Path.Combine(Path.GetDirectoryName(profile.WorkingDirectory)!, "prepared.json");
        if (File.Exists(manifest) && File.Exists(profile.LauncherPath)) {
            progress?.Report(profile.Name + " · 保留已準備的副本與遊戲更新，套用設定…");
            await SandboxieRuntime.Configure(profile, ct, progress);
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(profile, Json.Options), ct);
            return;
        }
        var files = Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).ToArray();
        long total = files.Sum(f => new FileInfo(f).Length), done = 0; int lastPercent = -1;
        if (new DriveInfo(Path.GetPathRoot(profile.WorkingDirectory)!).AvailableFreeSpace < total + 2L * 1024 * 1024 * 1024 && !Directory.Exists(profile.WorkingDirectory))
            throw new IOException("磁碟空間不足，請至少預留遊戲大小加 2 GB。");
        foreach (string file in files) {
            ct.ThrowIfCancellationRequested(); var info = new FileInfo(file);
            string destination = Path.Combine(profile.WorkingDirectory, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            AppReleasePackage.RejectLinks(destination);
            if (!File.Exists(destination) || new FileInfo(destination).Length != info.Length || File.GetLastWriteTimeUtc(destination) != info.LastWriteTimeUtc) {
                string temporary = destination + ".xa-copying";
                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    await input.CopyToAsync(output, ct);
                File.Move(temporary, destination, true); File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
            }
            done += info.Length; int percent = (int)(done * 100 / Math.Max(1, total));
            if (percent != lastPercent) { lastPercent = percent; progress?.Report($"{profile.Name} · 複製 {percent}% · {Path.GetFileName(file)}"); }
        }
        Directory.CreateDirectory(profile.FileRoot);
        if (File.Exists(manifest)) File.Copy(manifest, manifest + ".previous", true);
        await SandboxieRuntime.Configure(profile, ct, progress);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(profile, Json.Options), ct);
    }
}

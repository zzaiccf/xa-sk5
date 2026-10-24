using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowSocks.Core;

public static class UpdateHelperPackage
{
    public static int Stage(string source, string destination, CancellationToken ct = default)
    {
        AppReleasePackage.RejectLinks(source); AppReleasePackage.RejectLinks(destination);
        string manifest = Path.Combine(source, "install-manifest.json"); AppReleasePackage.RejectLinks(manifest);
        using var manifestStream = File.OpenRead(manifest);
        if (manifestStream.Length > 1024 * 1024) throw new InvalidDataException("原程式安裝清單無效。");
        var entries = JsonSerializer.Deserialize<InstallFile[]>(manifestStream, Json.Options) ?? throw new InvalidDataException("原程式安裝清單無效。");
        if (entries.Length is < 1 or > 4000 || entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length) throw new InvalidDataException("原程式安裝清單無效。");
        foreach (var entry in entries) {
            if (!AppReleasePackage.IsApplicationFile(entry.Path) || !AppReleasePackage.ValidHash(entry.Sha256)) throw new InvalidDataException("原程式安裝清單無效。");
            _ = AppReleasePackage.SafePath(source, entry.Path);
        }
        var files = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "WindowSocks.Updater.exe", "WindowSocks.Updater.dll", "WindowSocks.Updater.deps.json", "WindowSocks.Updater.runtimeconfig.json", "WindowSocks.Core.dll"
        };
        // Keep the native runtime pieces. Managed references are followed from the actual
        // updater/Core metadata instead of copying WPF, tools, drivers and every translation.
        var references = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => !e.Path.Contains('/') && e.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) {
            ct.ThrowIfCancellationRequested(); string path = AppReleasePackage.SafePath(source, entry.Path); AppReleasePackage.RejectLinks(path);
            using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
            if (!pe.HasMetadata) { selected.Add(entry.Path); continue; }
            var metadata = pe.GetMetadataReader();
            references[entry.Path] = metadata.AssemblyReferences.Select(r => metadata.GetString(metadata.GetAssemblyReference(r).Name) + ".dll").ToArray();
        }
        var pending = new Queue<string>(selected);
        while (pending.TryDequeue(out string? name)) if (references.TryGetValue(name, out var dependencies))
            foreach (string dependency in dependencies) if (files.ContainsKey(dependency) && selected.Add(dependency)) pending.Enqueue(dependency);
        if (selected.Any(name => !files.ContainsKey(name))) throw new IOException("缺少獨立更新程式或其執行元件，請重新解壓完整可攜版。");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("更新程式暫存目錄必須為空。");
        Directory.CreateDirectory(destination);
        byte[] buffer = new byte[256 * 1024];
        foreach (string name in selected) {
            ct.ThrowIfCancellationRequested(); string input = AppReleasePackage.SafePath(source, name), output = AppReleasePackage.SafePath(destination, name);
            AppReleasePackage.RejectLinks(input); AppReleasePackage.RejectLinks(output);
            using var read = File.OpenRead(input); using var write = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int count;
            while ((count = read.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); write.Write(buffer, 0, count); }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(files[name].Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新程式元件完整性驗證失敗：" + name);
        }
        return selected.Count;
    }
}

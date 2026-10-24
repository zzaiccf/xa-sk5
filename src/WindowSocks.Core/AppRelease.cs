using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowSocks.Core;

public sealed record ReleaseAsset(string Url, long Size, string Sha256);
public sealed record AppRelease(int SchemaVersion, string Version, DateTimeOffset PublishedAt, string Notes, ReleaseAsset Portable);
public sealed record UpdateProgress(string Message, long Received = 0, long Total = 0)
{
    public bool CanCancel { get; init; }
}

public static class AppReleasePackage
{
    public const long MaximumArchiveSize = 300L * 1024 * 1024;
    public static string CurrentVersion => typeof(AppReleasePackage).Assembly.GetName().Version!.ToString(3);
    public static Version ParseVersion(string value)
    {
        // Stable releases only. Do not silently turn a prerelease or malformed value into a stable version.
        if (!System.Text.RegularExpressions.Regex.IsMatch(value ?? "", @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) || !Version.TryParse(value, out var version))
            throw new InvalidDataException("發布版本必須為三段數字，例如 0.1.15。");
        return version;
    }
    public static bool IsNewer(string candidate, string current) => ParseVersion(candidate) > ParseVersion(current);
    public static AppRelease Read(string json)
    {
        if (json.Length > 128 * 1024) throw new InvalidDataException("版本資訊超過大小限制。");
        var release = JsonSerializer.Deserialize<AppRelease>(json, Json.Options) ?? throw new InvalidDataException("版本資訊為空。");
        _ = ParseVersion(release.Version);
        if (release.SchemaVersion != 1 || release.Notes is null || release.Notes.Length > 16000 || release.PublishedAt == default || release.Portable is not { } asset ||
            asset.Size is < 1 or > MaximumArchiveSize || asset.Url is null || asset.Url.Length > 2048 || !ValidHash(asset.Sha256))
            throw new InvalidDataException("版本資訊或下載完整性資料無效。");
        return release;
    }
    public static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static void VerifyArchive(string path, ReleaseAsset asset)
    {
        if (new FileInfo(path).Length != asset.Size || !Hash(path).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下載大小或 SHA-256 不符，沒有安裝任何檔案。請重新檢查版本後再試。");
    }
    public static string SafePath(string root, string relative)
    {
        string normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (normalized.Length > 240 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("更新套件包含不合法或有歧義的路徑。");
        return ComponentPackage.ContainedPath(root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }
    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路徑包含符號連結或重新解析點，已停止。");
    }
    public static bool IsApplicationFile(string relative)
    {
        string path = relative.Replace('\\', '/');
        if (path.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase)) return path.Count(c => c == '/') == 1 && Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)/[^/]+\.resources\.dll$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        if (path.Contains('/')) return false;
        if (path.Equals("update-source.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (path is "LICENSE" or "THIRD-PARTY-NOTICES.txt" or "操作說明.html" or "install-manifest.json") return true;
        return new[] { ".dll", ".exe", ".pdb", ".sys" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
            path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }
    public static InstallFile[] VerifyPayload(string root, string version, bool installed = false)
    {
        _ = ParseVersion(version); RejectLinks(root);
        string manifest = Path.Combine(root, "install-manifest.json"); RejectLinks(manifest);
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException("更新套件缺少有效安裝清單。");
        var preliminary = JsonSerializer.Deserialize<InstallFile[]>(File.ReadAllText(manifest), Json.Options) ?? throw new InvalidDataException("更新套件清單無效。");
        if (preliminary.Length is < 1 or > 4000) throw new InvalidDataException("更新套件清單大小無效。");
        foreach (var file in preliminary) {
            if (file is null || file.Path is null || !ValidHash(file.Sha256) || !IsApplicationFile(file.Path)) throw new InvalidDataException("更新檔案清單內容無效。");
            RejectLinks(SafePath(root, file.Path));
        }
        var files = ComponentPackage.VerifyManifest(root);
        foreach (var file in files) {
            if (!IsApplicationFile(file.Path) || file.Path.Equals("install-manifest.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新套件含非程式檔案：" + file.Path);
            RejectLinks(SafePath(root, file.Path));
        }
        foreach (string path in installed ? Array.Empty<string>() : EnumerateSafeFiles(root)) {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative != "install-manifest.json" && !files.Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("更新套件含未列入清單的檔案。");
        }
        foreach (string name in new[] { "WindowSocks.dll", "WindowSocks.Core.dll", "WindowSocks.Service.dll", "WindowSocks.Maintenance.dll", "WindowSocks.Updater.dll", "WindowSocks.Tools.dll" }) {
            if (!files.Any(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase)) || AssemblyName.GetAssemblyName(Path.Combine(root, name)).Version!.ToString(3) != version)
                throw new InvalidDataException("更新套件元件版本不一致：" + name);
        }
        if (!files.Any(f => f.Path.Equals("WindowSocks.Updater.exe", StringComparison.OrdinalIgnoreCase)) || !files.Any(f => f.Path.Equals("xa sk5.exe", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("更新套件缺少介面或更新程式。");
        if (ComponentPackage.DriverFiles.Any(name => File.Exists(Path.Combine(root, name)))) {
            foreach (string name in ComponentPackage.DriverFiles) ComponentPackage.RequireX64(Path.Combine(root, name));
            DriverTrust.VerifySignature(Path.Combine(root, "WinDivert64.sys"));
        }
        return files;
    }
    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(root)) {
            RejectLinks(path);
            if (Directory.Exists(path)) { foreach (string file in EnumerateSafeFiles(path)) yield return file; }
            else yield return path;
        }
    }
    public static void Extract(string archivePath, string destination, string version, CancellationToken ct = default, IProgress<UpdateProgress>? progress = null)
    {
        if (Directory.Exists(destination)) throw new IOException("解壓目錄必須為新的空目錄。");
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count is < 1 or > 4000) throw new InvalidDataException("更新壓縮包項目數超出限制。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        // Validate the entire directory before extracting any bytes.
        foreach (var entry in zip.Entries) {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) { _ = SafePath(destination, entry.FullName.TrimEnd('/')); continue; }
            _ = SafePath(destination, entry.FullName);
            if (!names.Add(entry.FullName.Replace('\\', '/')) || !IsApplicationFile(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.Length > MaximumArchiveSize)
                throw new InvalidDataException("更新壓縮包含重複、連結或非程式檔案。");
            total = checked(total + entry.Length); if (total > 1024L * 1024 * 1024) throw new InvalidDataException("更新解壓大小超出限制。");
        }
        Directory.CreateDirectory(destination); RejectLinks(destination);
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).ToArray();
        int workers = Math.Min(entries.Length, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        long extracted = 0;
        // Each worker has its own ZIP stream; ZipArchive itself is not safe for concurrent reads.
        Parallel.For(0, workers, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers }, worker => {
            using var reader = ZipFile.OpenRead(archivePath); byte[] buffer = new byte[256 * 1024];
            for (int index = worker; index < entries.Length; index += workers) {
                ct.ThrowIfCancellationRequested(); string target = SafePath(destination, entries[index]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); RejectLinks(target);
                using var input = reader.GetEntry(entries[index])!.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length);
                int read; while ((read = input.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); long count = Interlocked.Add(ref extracted, read); progress?.Report(new("解壓更新…", count, total) { CanCancel = true }); }
            }
        });
        ct.ThrowIfCancellationRequested(); progress?.Report(new("驗證檔案與元件版本…") { CanCancel = true });
        VerifyPayload(destination, version);
    }
}

public sealed class ReleaseClient : IDisposable
{
    public const string DefaultSource = "https://api.github.com/repos/zzaiccf/xa-sk5/releases/latest";
    public const string LegacyDefaultSource = "https://github.com/zzaiccf/xa-sk5/releases/latest/download/latest.json";
    private readonly HttpClient client;
    public ReleaseClient(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("xa-sk5/" + AppReleasePackage.CurrentVersion);
    }
    public static Uri SourceUri(string source)
    {
        if (!Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) || !AllowedHttps(uri)) throw new InvalidDataException("請設定有效的 HTTPS 版本資訊網址；目前尚未設定線上發布來源。");
        return uri;
    }
    private static bool AllowedHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Fragment.Length == 0;
    public static Uri AssetUri(Uri source, string url)
    {
        var uri = new Uri(source, url); if (!AllowedLocation(source, uri)) throw new InvalidDataException("下載網址不在此發布來源的允許主機。"); return uri;
    }
    private static string? GitHubRepository(Uri source)
    {
        var parts = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return source.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 5 && parts[0] == "repos" && parts[3] == "releases" && parts[4] == "latest"
            ? parts[1] + "/" + parts[2] : null;
    }
    public static bool AllowedLocation(Uri source, Uri uri)
    {
        if (!AllowedHttps(uri)) return false;
        if (uri.Host.Equals(source.Host, StringComparison.OrdinalIgnoreCase)) return true;
        string? repository = GitHubRepository(source);
        if (repository is not null && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase);
        return (source.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || repository is not null) &&
            new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
    private static AppRelease ReadGitHubRelease(string json)
    {
        try {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("此發布尚未公開或屬於測試版。");
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string version = tag.StartsWith('v') ? tag[1..] : tag; _ = AppReleasePackage.ParseVersion(version);
            string filename = $"xa-sk5-{version}-win-x64.zip";
            var assets = root.GetProperty("assets").EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == filename).ToArray();
            if (assets.Length != 1 || assets[0].GetProperty("state").GetString() != "uploaded") throw new InvalidDataException("發布缺少完整的可攜更新檔案。");
            var asset = assets[0]; string digest = asset.GetProperty("digest").GetString() ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub 發布缺少 SHA-256，沒有下載或安裝。");
            var release = new AppRelease(1, version, root.GetProperty("published_at").GetDateTimeOffset(), root.GetProperty("body").GetString() ?? "",
                new(asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64(), digest[7..]));
            return AppReleasePackage.Read(JsonSerializer.Serialize(release, Json.Options));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { throw new InvalidDataException("GitHub 發布資訊不完整，請稍後重新檢查。", ex); }
    }
    private async Task<HttpResponseMessage> Get(Uri source, Uri uri, CancellationToken ct)
    {
        for (int i = 0; i < 6; i++) {
            if (!AllowedLocation(source, uri)) throw new IOException("發布下載重新導向到不允許的主機，已停止。");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { uri = new Uri(uri, location); response.Dispose(); continue; }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new IOException("發布下載重新導向次數過多。");
    }
    public async Task<AppRelease> CheckAsync(Uri source, CancellationToken ct)
    {
        using var response = await Get(source, source, ct);
        if (response.Content.Headers.ContentLength is > 128 * 1024) throw new InvalidDataException("版本資訊超過大小限制。");
        using var data = new MemoryStream(); await using var input = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[16384];
        while (true) { int read = await input.ReadAsync(buffer, ct); if (read == 0) break; if (data.Length + read > 128 * 1024) throw new InvalidDataException("版本資訊超過大小限制。"); data.Write(buffer, 0, read); }
        string json = System.Text.Encoding.UTF8.GetString(data.ToArray()).TrimStart('\uFEFF');
        var release = GitHubRepository(source) is not null ? ReadGitHubRelease(json) : AppReleasePackage.Read(json);
        _ = AssetUri(source, release.Portable.Url); return release;
    }
    public async Task DownloadAsync(Uri source, AppRelease release, string destination, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        bool created = false;
        try {
            using var response = await Get(source, AssetUri(source, release.Portable.Url), ct);
            if (response.Content.Headers.ContentLength is { } size && size != release.Portable.Size) throw new InvalidDataException("發布下載大小與版本資訊不符。");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0; var clock = System.Diagnostics.Stopwatch.StartNew(); long reportedAt = -100;
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true)) {
                created = true; await using var input = await response.Content.ReadAsStreamAsync(ct); byte[] buffer = new byte[256 * 1024];
                while (true) {
                    int read = await input.ReadAsync(buffer, ct); if (read == 0) break; total += read;
                    if (total > release.Portable.Size) throw new InvalidDataException("發布下載超過預期大小。");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (clock.ElapsedMilliseconds - reportedAt >= 100 || total == release.Portable.Size) {
                        reportedAt = clock.ElapsedMilliseconds;
                        progress?.Report(new("下載更新…", total, release.Portable.Size) { CanCancel = true });
                    }
                }
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            if (total != release.Portable.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Portable.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下載大小或 SHA-256 不符，沒有安裝任何檔案。請重新檢查版本後再試。");
        }
        catch { if (created) File.Delete(destination); throw; }
    }
    public void Dispose() => client.Dispose();
}

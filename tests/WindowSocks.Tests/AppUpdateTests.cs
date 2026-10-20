using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-sk5-update-test-" + Guid.NewGuid().ToString("N"));
    public AppUpdateTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Theory]
    [InlineData("0.1.15", "0.1.14", true)] [InlineData("0.1.10", "0.1.9", true)] [InlineData("0.2.0", "0.1.99", true)] [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.1.15", "0.1.15", false)] [InlineData("0.1.9", "0.1.15", false)]
    public void VersionsUseNumericComparison(string release, string current, bool expected) => Assert.Equal(expected, AppReleasePackage.IsNewer(release, current));
    [Theory]
    [InlineData("0.1.15-beta")] [InlineData("v0.1.15")] [InlineData("01.1.15")] [InlineData("0.1")] [InlineData("0.1.15.1")] [InlineData("999999999999999999.0.0")]
    public void InvalidVersionsAreRejected(string version) => Assert.Throws<InvalidDataException>(() => AppReleasePackage.ParseVersion(version));
    [Theory]
    [InlineData("../escape.exe")] [InlineData("..\\escape.exe")] [InlineData("C:\\escape.exe")] [InlineData("/escape.exe")] [InlineData("a/../../escape.exe")]
    [InlineData("test.exe:stream")] [InlineData("a/CON.txt")] [InlineData("a/NUL")] [InlineData("a /test.exe")] [InlineData("a./test.exe")] [InlineData("./test.exe")] [InlineData("a//test.exe")]
    public void AmbiguousAndEscapingPathsAreRejected(string path) => Assert.Throws<InvalidDataException>(() => AppReleasePackage.SafePath(root, path));
    [Fact] public void HttpsAndHostConstraintsApplyToRedirectsAndAssets()
    {
        var source = ReleaseClient.SourceUri("https://github.com/example/xa-sk5/releases/latest/download/latest.json");
        Assert.True(ReleaseClient.AllowedLocation(source, new("https://release-assets.githubusercontent.com/file")));
        foreach (string url in new[] { "http://github.com/file", "https://github.com.evil.example/file", "https://user:password@github.com/file", "https://github.com:8443/file", "file:///c:/file" }) Assert.False(ReleaseClient.AllowedLocation(source, new(url)));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.SourceUri(""));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.AssetUri(new("https://updates.example/latest.json"), "https://evil.example/update.zip"));
    }
    [Fact] public void ManifestRejectsMissingIntegrityAndUnsupportedSchema()
    {
        var release = Release();
        foreach (var bad in new[] { release with { SchemaVersion = 2 }, release with { Portable = release.Portable with { Sha256 = "bad" } }, release with { Portable = release.Portable with { Size = 0 } } })
            Assert.Throws<InvalidDataException>(() => AppReleasePackage.Read(JsonSerializer.Serialize(bad, Json.Options)));
    }
    [Fact] public async Task CheckAndDownloadVerifyActualBytes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic release package");
        var release = Release(bytes);
        using var client = new ReleaseClient(new Handler(uri => uri.AbsolutePath.EndsWith(".json") ? new(JsonSerializer.Serialize(release, Json.Options)) : new(bytes)));
        var read = await client.CheckAsync(new("https://updates.example/latest.json"), default); Assert.Equal(release, read);
        string destination = Path.Combine(root, "download.zip"); await client.DownloadAsync(new("https://updates.example/latest.json"), release, destination, null, default); Assert.Equal(bytes, File.ReadAllBytes(destination));
    }
    [Fact] public async Task TamperedDownloadIsRemoved()
    {
        byte[] bytes = [1, 2, 3]; var release = Release([3, 2, 1]);
        using var client = new ReleaseClient(new Handler(_ => new(bytes))); string destination = Path.Combine(root, "bad.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(new("https://updates.example/latest.json"), release, destination, null, default)); Assert.False(File.Exists(destination));
    }
    [Fact] public async Task TruncatedDownloadIsRemoved()
    {
        using var client = new ReleaseClient(new Handler(_ => new([1, 2]))); string destination = Path.Combine(root, "bad.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(new("https://updates.example/latest.json"), Release([1, 2, 3]), destination, null, default)); Assert.False(File.Exists(destination));
    }
    [Fact] public async Task CancellationDoesNotChangeExistingFiles()
    {
        using var client = new ReleaseClient(new Handler(_ => new([1, 2, 3]))); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        string destination = Path.Combine(root, "cancel.zip"); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(new("https://updates.example/latest.json"), Release([1, 2, 3]), destination, null, cancel.Token)); Assert.False(File.Exists(destination));
    }
    [Fact] public async Task CancellationDuringDownloadRemovesPartialFile()
    {
        byte[] bytes = new byte[200000]; using var cancel = new CancellationTokenSource();
        using var client = new ReleaseClient(new Handler(_ => new(bytes))); string destination = Path.Combine(root, "partial.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(new("https://updates.example/latest.json"), Release(bytes), destination, new CancelProgress(cancel), cancel.Token));
        Assert.False(File.Exists(destination));
    }
    [Fact] public async Task NetworkFailureAndUnsafeRedirectsFailClearly()
    {
        using var client = new ReleaseClient(new Handler(_ => new() { Status = HttpStatusCode.Redirect, Location = "https://evil.example/latest.json" }));
        await Assert.ThrowsAsync<IOException>(() => client.CheckAsync(new("https://updates.example/latest.json"), default));
        using var offline = new ReleaseClient(new Handler(_ => throw new HttpRequestException("synthetic network outage"))); await Assert.ThrowsAsync<HttpRequestException>(() => offline.CheckAsync(new("https://updates.example/latest.json"), default));
    }
    [Fact] public async Task GitHubReleaseProvidesVersionDigestAndPortableWithoutJsonAsset()
    {
        byte[] bytes = [1, 2, 3];
        using var client = new ReleaseClient(new Handler(uri => uri.Host == "api.github.com" ? new(GitHubRelease(bytes)) : new(bytes)));
        var source = ReleaseClient.SourceUri(ReleaseClient.DefaultSource);
        var release = await client.CheckAsync(source, default); Assert.Equal("0.1.16", release.Version); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), release.Portable.Sha256);
        string destination = Path.Combine(root, "github.zip"); await client.DownloadAsync(source, release, destination, null, default); Assert.Equal(bytes, File.ReadAllBytes(destination));
    }
    [Theory] [InlineData(null)] [InlineData("md5:123")] [InlineData("sha256:bad")]
    public async Task GitHubReleaseRequiresSha256(string? digest)
    {
        using var client = new ReleaseClient(new Handler(_ => new(GitHubRelease([1, 2, 3], digest, overrideDigest: true))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(ReleaseClient.SourceUri(ReleaseClient.DefaultSource), default));
    }
    [Theory] [InlineData(true, false)] [InlineData(false, true)]
    public async Task GitHubDraftsAndPrereleasesAreRejected(bool draft, bool prerelease)
    {
        using var client = new ReleaseClient(new Handler(_ => new(GitHubRelease([1, 2, 3], draft: draft, prerelease: prerelease))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(ReleaseClient.SourceUri(ReleaseClient.DefaultSource), default));
    }
    [Fact] public void GitHubApiSourceRestrictsDownloadRepositoryAndTransport()
    {
        var source = ReleaseClient.SourceUri(ReleaseClient.DefaultSource);
        Assert.True(ReleaseClient.AllowedLocation(source, new("https://github.com/zzaiccf/xa-sk5/releases/download/v0.1.16/update.zip")));
        Assert.True(ReleaseClient.AllowedLocation(source, new("https://release-assets.githubusercontent.com/file")));
        foreach (string url in new[] { "https://github.com/other/xa-sk5/releases/download/v0.1.16/update.zip", "https://github.com/zzaiccf/other/releases/download/v0.1.16/update.zip", "http://github.com/zzaiccf/xa-sk5/releases/download/v0.1.16/update.zip", "https://evil.example/file" }) Assert.False(ReleaseClient.AllowedLocation(source, new(url)));
    }
    [Theory] [InlineData("../outside.exe")] [InlineData("settings.dpapi")] [InlineData("owner.sid")] [InlineData("nested/test.exe")]
    public void InvalidZipIsRejectedBeforeExtraction(string name)
    {
        string zip = Path.Combine(root, "bad.zip"), destination = Path.Combine(root, "extract");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("bad"); }
        Assert.Throws<InvalidDataException>(() => AppReleasePackage.Extract(zip, destination, AppReleasePackage.CurrentVersion)); Assert.False(Directory.Exists(destination));
    }
    [Fact] public void ZipRejectsCaseInsensitiveDuplicatesAndLinks()
    {
        string zip = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { archive.CreateEntry("test.dll"); archive.CreateEntry("TEST.dll"); }
        Assert.Throws<InvalidDataException>(() => AppReleasePackage.Extract(zip, Path.Combine(root, "dup"), AppReleasePackage.CurrentVersion));
        string link = Path.Combine(root, "link.zip");
        using (var archive = ZipFile.Open(link, ZipArchiveMode.Create)) { var entry = archive.CreateEntry("test.dll"); entry.ExternalAttributes = unchecked((int)0xa1ff0000); }
        Assert.Throws<InvalidDataException>(() => AppReleasePackage.Extract(link, Path.Combine(root, "link"), AppReleasePackage.CurrentVersion));
    }
    [Fact] public void TransactionCommitsAndPreservesAllOpaqueSettings()
    {
        string source = Payload("new"), target = Target(); byte[] settings = [10, 11, 12, 13]; File.WriteAllBytes(Path.Combine(target, "settings.dpapi"), settings);
        var transaction = FileUpdateTransaction.Prepare(source, target, AppReleasePackage.CurrentVersion); transaction.Apply(source); transaction.Commit();
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "WindowSocks.exe"))); Assert.Equal(settings, File.ReadAllBytes(Path.Combine(target, "settings.dpapi"))); Assert.Empty(FileUpdateTransaction.Pending(target));
    }
    [Theory] [InlineData("FixedEmulators")] [InlineData("SequentialEmulators")]
    public void EncryptedCredentialsDatesAndEveryUserPreferenceSurviveUpdateAndRollback(string kind)
    {
        string source = Payload("new"), target = Target(); var id = Guid.NewGuid();
        var config = new SavedConfig {
            Proxies = [new() { Id = id, Host = "proxy.invalid", Port = 1080, Username = "synthetic-user", Password = "SYNTHETIC-TEST-ONLY", ExpiresOn = new(2027, 1, 11) }],
            AutoProxy = new() { Enabled = true, Kind = kind, EmulatorKind = "MuMu", PerProxy = 2, EmulatorAssignments = [new("MuMu", @"C:\Synthetic\MuMuManager.exe", 3, "synthetic", id)] },
            PendingEmulatorRule = new() { Kind = "FixedEmulators", EmulatorAssignments = [new("LDPlayer", @"C:\Synthetic\ldconsole.exe", 1, "synthetic", id)] },
            GlobalProxy = new() { Enabled = true, ProxyId = id, BypassRemoteControl = true, RemoteControlExecutables = [@"C:\Synthetic\Remote.exe"] },
            ServiceProxyExpirations = new() { [id] = new(2027, 1, 11) },
            Bindings = [new() { TargetKey = "synthetic", ProxyId = id, Enabled = true, SharedProxy = true }]
        };
        var store = new ConfigStore(target, DataProtectionScope.CurrentUser); store.Save(config);
        byte[] before = File.ReadAllBytes(Path.Combine(target, "settings.dpapi"));
        var transaction = FileUpdateTransaction.Prepare(source, target, AppReleasePackage.CurrentVersion); transaction.Apply(source);
        Assert.Equal(JsonSerializer.Serialize(config, Json.Options), JsonSerializer.Serialize(store.Load(), Json.Options));
        transaction.Rollback(); Assert.Equal(before, File.ReadAllBytes(Path.Combine(target, "settings.dpapi")));
        Assert.Equal(JsonSerializer.Serialize(config, Json.Options), JsonSerializer.Serialize(store.Load(), Json.Options));
    }
    [Fact] public void InjectedFailureRestoresOriginalFilesAndRemovesNewFiles()
    {
        string source = Payload("new"), target = Target(); var transaction = FileUpdateTransaction.Prepare(source, target, AppReleasePackage.CurrentVersion);
        Assert.Throws<IOException>(() => transaction.Apply(source, _ => throw new IOException("synthetic power interruption")));
        Assert.Single(FileUpdateTransaction.Pending(target)); FileUpdateTransaction.Open(transaction.BackupDirectory).Rollback();
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "WindowSocks.exe"))); Assert.False(File.Exists(Path.Combine(target, "WindowSocks.Updater.dll"))); Assert.Empty(FileUpdateTransaction.Pending(target));
    }
    [Fact] public void InterruptedAppliedUpdateCanBeRecoveredByNewHelper()
    {
        string source = Payload("new"), target = Target(); var transaction = FileUpdateTransaction.Prepare(source, target, AppReleasePackage.CurrentVersion); transaction.Apply(source);
        Assert.Throws<IOException>(() => FileUpdateTransaction.Prepare(source, target, AppReleasePackage.CurrentVersion));
        FileUpdateTransaction.Open(transaction.BackupDirectory).Rollback(); Assert.Equal("old", File.ReadAllText(Path.Combine(target, "WindowSocks.exe")));
    }
    [Fact] public void LockedFilePreventsAnyReplacement()
    {
        string source = Payload("new"), target = Target(); using var locked = new FileStream(Path.Combine(target, "WindowSocks.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() => FileUpdateTransaction.Prepare(source, target, AppReleasePackage.CurrentVersion)); Assert.Equal("old", File.ReadAllText(Path.Combine(target, "WindowSocks.exe")));
    }
    [Fact] public void CorruptedBackupDoesNotPartiallyRestore()
    {
        string source = Payload("new"), target = Target(); var transaction = FileUpdateTransaction.Prepare(source, target, AppReleasePackage.CurrentVersion); transaction.Apply(source);
        File.WriteAllText(Path.Combine(transaction.BackupDirectory, "files", "WindowSocks.exe"), "corrupt"); Assert.Throws<IOException>(() => FileUpdateTransaction.Open(transaction.BackupDirectory).Rollback()); Assert.Equal("new", File.ReadAllText(Path.Combine(target, "WindowSocks.exe")));
    }
    [Fact] public void MismatchedComponentVersionAndUnlistedFilesAreRejected()
    {
        string source = Payload("new"); Assert.Throws<InvalidDataException>(() => AppReleasePackage.VerifyPayload(source, "99.0.0"));
        File.WriteAllText(Path.Combine(source, "settings.dpapi"), "must never ship"); Assert.Throws<InvalidDataException>(() => AppReleasePackage.VerifyPayload(source, AppReleasePackage.CurrentVersion));
    }
    private string Payload(string text)
    {
        string path = Path.Combine(root, "source"); Directory.CreateDirectory(path);
        foreach (string name in new[] { "WindowSocks.dll", "WindowSocks.Core.dll", "WindowSocks.Service.dll", "WindowSocks.Maintenance.dll", "WindowSocks.Updater.dll", "WindowSocks.Tools.dll" }) File.Copy(typeof(AppReleasePackage).Assembly.Location, Path.Combine(path, name));
        foreach (string name in new[] { "WindowSocks.exe", "xa sk5.exe", "WindowSocks.Service.exe", "WindowSocksNative.dll", "WindowSocks.Maintenance.exe", "WindowSocks.Updater.exe" }) File.WriteAllText(Path.Combine(path, name), text);
        var files = Directory.EnumerateFiles(path).Select(file => new InstallFile(Path.GetFileName(file), AppReleasePackage.Hash(file))).ToArray(); File.WriteAllText(Path.Combine(path, "install-manifest.json"), JsonSerializer.Serialize(files, Json.Options)); return path;
    }
    private string Target() { string path = Path.Combine(root, "target"); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "WindowSocks.exe"), "old"); return path; }
    private static AppRelease Release(byte[]? bytes = null) { bytes ??= [1, 2, 3]; return new(1, "0.1.15", DateTimeOffset.Parse("2026-10-20T00:00:00Z"), "Synthetic update", new("update.zip", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))); }
    private static string GitHubRelease(byte[] bytes, string? digest = null, bool overrideDigest = false, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new {
        tag_name = "v0.1.16", published_at = DateTimeOffset.Parse("2026-10-20T00:00:00Z"), body = "Two binary assets", draft, prerelease,
        assets = new object[] {
            new { name = "xa-sk5-0.1.16-win-x64.zip", state = "uploaded", digest = overrideDigest ? digest : "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), size = bytes.Length, browser_download_url = "https://github.com/zzaiccf/xa-sk5/releases/download/v0.1.16/xa-sk5-0.1.16-win-x64.zip" },
            new { name = "xa-sk5-Setup-0.1.16.exe", state = "uploaded" }
        }
    });
    private sealed record Content
    {
        public byte[] Bytes { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? Location { get; init; }
        public Content() { }
        public Content(byte[] bytes) => Bytes = bytes;
        public Content(string text) => Bytes = Encoding.UTF8.GetBytes(text);
    }
    private sealed class Handler(Func<Uri, Content> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var content = reply(request.RequestUri!); var response = new HttpResponseMessage(content.Status) { Content = new ByteArrayContent(content.Bytes) };
            if (content.Location is not null) response.Headers.Location = new(content.Location); return Task.FromResult(response);
        }
    }
    private sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<UpdateProgress> { public void Report(UpdateProgress value) { if (value.Received > 0) cancel.Cancel(); } }
}

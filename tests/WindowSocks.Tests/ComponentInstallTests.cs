using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ComponentInstallTests
{
    private static byte[] Pe(ushort architecture = 0x8664)
    {
        var bytes = new byte[128]; BitConverter.GetBytes((ushort)0x5a4d).CopyTo(bytes, 0); BitConverter.GetBytes(64).CopyTo(bytes, 0x3c);
        BitConverter.GetBytes(0x4550).CopyTo(bytes, 64); BitConverter.GetBytes(architecture).CopyTo(bytes, 68); return bytes;
    }
    private static MemoryStream Archive(params (string Name, byte[] Bytes)[] entries)
    {
        var stream = new MemoryStream(); using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) foreach (var (name, bytes) in entries) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
        stream.Position = 0; return stream;
    }
    private static string Temporary() { string path = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

    [Fact] public void ExtractsOnlyPairedX64DriverFiles()
    {
        string directory = Temporary();
        try {
            using var archive = Archive(("WinDivert-2.2.2-A/x64/WinDivert.dll", Pe()), ("WinDivert-2.2.2-A/x64/WinDivert64.sys", Pe()), ("WinDivert-2.2.2-A/x86/ignored.exe", [1,2,3]));
            ComponentPackage.ExtractDriver(archive, directory); Assert.Equal(2, Directory.GetFiles(directory).Length);
        } finally { Directory.Delete(directory, true); }
    }
    [Theory] [InlineData("../outside.dll")] [InlineData("C:/outside.dll")] [InlineData("safe/file.dll:stream")]
    public void RejectsUnsafeArchivePathsBeforeWriting(string unsafeName)
    {
        string directory = Temporary();
        try { using var archive = Archive((unsafeName, Pe()), ("WinDivert-2.2.2-A/x64/WinDivert.dll", Pe()), ("WinDivert-2.2.2-A/x64/WinDivert64.sys", Pe())); Assert.Throws<InvalidDataException>(() => ComponentPackage.ExtractDriver(archive, directory)); Assert.Empty(Directory.GetFiles(directory)); }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public void RejectsMissingAndDuplicateDriverFiles()
    {
        string directory = Temporary();
        try {
            using var missing = Archive(("WinDivert-2.2.2-A/x64/WinDivert.dll", Pe())); Assert.Throws<InvalidDataException>(() => ComponentPackage.ExtractDriver(missing, directory));
            using var duplicate = Archive(("WinDivert-2.2.2-A/x64/WinDivert.dll", Pe()), ("WinDivert-2.2.2-A/x64/WinDivert.dll", Pe()), ("WinDivert-2.2.2-A/x64/WinDivert64.sys", Pe())); Assert.Throws<InvalidDataException>(() => ComponentPackage.ExtractDriver(duplicate, directory));
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public void RejectsX86AndUnsignedDriver()
    {
        string directory = Temporary(); string file = Path.Combine(directory, "fake.sys");
        try { File.WriteAllBytes(file, Pe(0x14c)); Assert.Throws<InvalidDataException>(() => ComponentPackage.RequireX64(file)); File.WriteAllBytes(file, Pe()); Assert.Throws<InvalidDataException>(() => DriverTrust.VerifySignature(file)); }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public void ManifestDetectsTamperingAndMissingRequiredComponents()
    {
        string directory = Temporary();
        try {
            var entries = new[] { "WindowSocks.exe", "WindowSocks.Service.exe", "WindowSocksNative.dll", "WindowSocks.Maintenance.exe" }.Select(name => { File.WriteAllBytes(Path.Combine(directory, name), Pe()); return new InstallFile(name, Convert.ToHexString(SHA256.HashData(Pe()))); }).ToArray();
            File.WriteAllText(Path.Combine(directory, "install-manifest.json"), JsonSerializer.Serialize(entries, Json.Options)); Assert.Equal(4, ComponentPackage.VerifyManifest(directory).Length);
            File.AppendAllText(Path.Combine(directory, entries[0].Path), "changed"); Assert.Throws<InvalidDataException>(() => ComponentPackage.VerifyManifest(directory));
        } finally { Directory.Delete(directory, true); }
    }
    [Theory] [InlineData("http://github.com/test")] [InlineData("https://github.com.evil.example/test")] [InlineData("https://127.0.0.1/test")] [InlineData("https://github.com:444/test")]
    public void DownloadDoesNotFollowUntrustedRedirects(string url) => Assert.False(ComponentPackage.AllowedDownloadUri(new Uri(url)));
    [Fact] public void DownloadAllowsOnlyPinnedOfficialHttpsHosts()
    {
        Assert.True(ComponentPackage.AllowedDownloadUri(new Uri(ComponentPackage.DriverUrl)));
        Assert.True(ComponentPackage.AllowedDownloadUri(new Uri("https://release-assets.githubusercontent.com/test")));
        Assert.Contains("安全性", ComponentPackage.DriverError(225)); Assert.Contains("取消", ComponentPackage.DriverError(1223));
    }
}

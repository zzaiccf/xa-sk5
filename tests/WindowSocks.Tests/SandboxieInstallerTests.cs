using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxieInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [11, 23, 45, 67, 89];
    private static ReleaseAsset FixturePackage => SandboxieInstaller.Package with { Size = Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests++; return send(request, ct); }
    }
    private static Handler Download(byte[]? bytes = null) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Bytes) }));
    [Fact] public async Task VerifiedDownloadRunsOnceAndRechecksBeforeAndAfterInstallation()
    {
        bool installed = false; int inspections = 0, signatures = 0, runs = 0;
        using var handler = Download();
        var setup = new SandboxieInstaller(() => { inspections++; return new(installed, !installed, "fixture"); }, file => {
            Assert.Equal(Bytes, File.ReadAllBytes(file)); Assert.Equal(1, signatures); runs++; installed = true; return Task.FromResult(0);
        }, _ => signatures++, FixturePackage, handler);
        await setup.EnsureInstalled(root, null, default);
        Assert.Equal(3, inspections); Assert.Equal(1, runs); Assert.Equal(1, handler.Requests);
        await setup.EnsureInstalled(root, null, default); Assert.Equal(1, runs); Assert.Equal(1, handler.Requests);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task WrongHashOrUntrustedSignatureNeverRunsInstaller(bool wrongHash)
    {
        using var handler = Download(); int runs = 0;
        var setup = new SandboxieInstaller(() => new(false, true, "missing"), _ => { runs++; return Task.FromResult(0); },
            _ => { if (!wrongHash) throw new InvalidDataException("untrusted"); },
            FixturePackage with { Sha256 = wrongHash ? new string('0', 64) : FixturePackage.Sha256 }, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.EnsureInstalled(root, null, default));
        Assert.Equal(0, runs);
    }
    [Fact] public async Task RedirectToUnapprovedHostAndOversizeDownloadCannotExecute()
    {
        int runs = 0;
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.test/malware.exe") } }));
        var setup = new SandboxieInstaller(() => new(false, true, ""), _ => { runs++; return Task.FromResult(0); }, _ => { }, FixturePackage, handler);
        await Assert.ThrowsAsync<IOException>(() => setup.EnsureInstalled(root, null, default));
        Assert.Equal(1, handler.Requests); Assert.Equal(0, runs);
        using var oversized = Download([.. Bytes, 99]);
        setup = new(() => new(false, true, ""), _ => { runs++; return Task.FromResult(0); }, _ => { }, FixturePackage, oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.EnsureInstalled(root, null, default)); Assert.Equal(0, runs);
    }
    [Theory] [InlineData(3010)] [InlineData(1)] [InlineData(0)] [InlineData(1223)]
    public async Task RebootCancellationFailureOrMissingPostInstallFilesCannotReportReady(int code)
    {
        using var handler = Download();
        var setup = new SandboxieInstaller(() => new(false, true, "missing"), _ => code == 1223 ? throw new Win32Exception(1223) : Task.FromResult(code), _ => { }, FixturePackage, handler);
        var error = await Assert.ThrowsAsync<IOException>(() => setup.EnsureInstalled(root, null, default));
        Assert.Contains(code switch { 3010 => "重新啟動", 1223 => "取消管理員", 0 => "尚未通過", _ => "退出碼 1" }, error.Message);
    }
    [Fact] public async Task ExistingInstallationIsPreservedAndConcurrentChangesAreRechecked()
    {
        using var handler = Download(); int runs = 0, checks = 0;
        var setup = new SandboxieInstaller(() => new(false, false, "existing"), _ => { runs++; return Task.FromResult(0); }, _ => { }, FixturePackage, handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.EnsureInstalled(root, null, default));
        Assert.Equal(0, handler.Requests); Assert.False(Directory.Exists(root));
        setup = new(() => new(false, ++checks == 1, "changed"), _ => { runs++; return Task.FromResult(0); }, _ => { }, FixturePackage, handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.EnsureInstalled(root, null, default)); Assert.Equal(0, runs);
    }
    [Fact] public async Task CancelDuringDownloadDoesNotExecuteAndCancelDuringInstallWaitsForOfficialInstaller()
    {
        using var ct = new CancellationTokenSource(); int runs = 0;
        using var slow = new Handler(async (_, token) => { ct.Cancel(); await Task.Delay(1000, token); return new(HttpStatusCode.OK); });
        var setup = new SandboxieInstaller(() => new(false, true, ""), _ => { runs++; return Task.FromResult(0); }, _ => { }, FixturePackage, slow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.EnsureInstalled(root, null, ct.Token)); Assert.Equal(0, runs);
        using var handler = Download(); using var closing = new CancellationTokenSource(); bool installed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup = new(() => new(installed, !installed, ""), async _ => { entered.SetResult(); int result = await exit.Task; installed = true; return result; }, _ => { }, FixturePackage, handler);
        var work = setup.EnsureInstalled(root, null, closing.Token);
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); closing.Cancel(); Assert.False(work.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => setup.EnsureInstalled(root, null, default));
        } finally { exit.TrySetResult(0); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.True(installed);
    }
    [Fact] public void SetupArgumentsUseElevationWithoutForcedRestartPortableModeOrExtraTasks()
    {
        var start = SandboxieInstaller.StartInfo(Path.Combine(root, "setup.exe"));
        Assert.True(start.UseShellExecute); Assert.Equal("runas", start.Verb);
        Assert.Contains("/NORESTART", start.ArgumentList); Assert.Contains("/RESTARTEXITCODE=3010", start.ArgumentList);
        Assert.Contains("/PORTABLE=0", start.ArgumentList); Assert.Contains("/TASKS=", start.ArgumentList);
        Assert.Contains("/DIR=" + SandboxieRuntime.InstallDirectory, start.ArgumentList);
        Assert.DoesNotContain("/SUPPRESSMSGBOXES", start.ArgumentList);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

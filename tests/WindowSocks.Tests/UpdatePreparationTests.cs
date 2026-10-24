using System.Net;
using System.Text;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class UpdatePreparationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-update-order-" + Guid.NewGuid().ToString("N"));
    public UpdatePreparationTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static UpdateDownload Download => new(new(1, AppReleasePackage.CurrentVersion, DateTimeOffset.UtcNow, "test", new("update.zip", 3, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })))), "https://updates.example/latest.json");
    [Fact] public async Task DownloadCannotStartUntilProxyInterruptionCompletes()
    {
        bool stopped = false; int requests = 0;
        using var client = new ReleaseClient(new Handler(() => { Assert.True(stopped); requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }; }));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePreparation.PrepareAsync(Download, AppReleasePackage.CurrentVersion, root, _ => { stopped = true; return Task.CompletedTask; }, null, default, client));
        Assert.Equal(1, requests); Assert.True(stopped);
    }
    [Fact] public async Task FailedOrCancelledProxyInterruptionNeverDownloads()
    {
        int requests = 0; using var client = new ReleaseClient(new Handler(() => { requests++; throw new Exception("Must not download"); }));
        await Assert.ThrowsAsync<IOException>(() => UpdatePreparation.PrepareAsync(Download, AppReleasePackage.CurrentVersion, root, _ => throw new IOException("stop failed"), null, default, client));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdatePreparation.PrepareAsync(Download, AppReleasePackage.CurrentVersion, root, _ => Task.CompletedTask, null, cancel.Token, client));
        Assert.Equal(0, requests); Assert.False(File.Exists(Path.Combine(root, "update.zip")));
    }
    [Fact] public async Task InvalidDownloadMetadataNeverInterruptsTheProxy()
    {
        bool interrupted = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePreparation.PrepareAsync(Download with { Source = null }, AppReleasePackage.CurrentVersion, root, _ => { interrupted = true; return Task.CompletedTask; }, null, default));
        Assert.False(interrupted);
    }
    [Fact] public async Task ColdServiceRetriesAndRejectsWrongVersions()
    {
        int attempts = 0;
        Snapshot State(string version) => new([], [], [], [], [], new(true, true, true, false, version, "", []));
        var result = await ServiceReadiness.WaitAsync("0.1.19", _ => { if (++attempts == 1) throw new IOException("starting"); return Task.FromResult(State("0.1.19")); }, timeout: TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts); Assert.Equal("0.1.19", result.Diagnostics.Version);
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceReadiness.WaitAsync("0.1.19", _ => Task.FromResult(State("0.1.18"))));
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(response()); }
    }
}

using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxLaunchFixTests
{
    [Fact] public async Task MonitorFailureAfterAcknowledgedStartRetriesWithoutReportingLaunchFailure()
    {
        int reads = 0, failures = 0, retries = 0; bool started = false;
        await SandboxBootstrapSession.Run(() => { started = true; return Task.CompletedTask; }, () => {
            Assert.True(started); if (++reads == 1) throw new InvalidOperationException("temporary pipe failure"); return Task.FromResult(false);
        }, _ => { failures++; return Task.CompletedTask; }, _ => retries++, delay: _ => Task.CompletedTask);
        Assert.Equal(0, failures); Assert.Equal(2, reads); Assert.Equal(1, retries);
    }
    [Fact] public async Task CompletedLaunchDescribesLauncherAndManualLogin()
    {
        var result = await new SandboxLaunchQueue().RunAsync([Guid.NewGuid()], 10, (_, _) => Task.CompletedTask, null, default);
        Assert.Equal("已開啟啟動器，請在啟動器按開始遊戲並登入", Assert.Single(result).Detail);
    }
    // Frozen 0.1.32/0.1.34 service wire contract; unknown JSON fields are ignored
    // by the same Json.Options used in those versions.
    private sealed record LegacyLaunchReport(Guid Id, bool Started, string Detail);
    [Theory] [InlineData(true)] [InlineData(false)]
    public void OldServiceReadsNewHelperWithoutRequiringDetailedFields(bool started)
    {
        Guid id = Guid.NewGuid();
        var modern = SandboxLaunchErrors.Report(id, new InvalidOperationException("service rejected request: concrete reason"), "synthetic-error.txt") with { Started = started };
        var legacy = System.Text.Json.JsonSerializer.Deserialize<LegacyLaunchReport>(System.Text.Json.JsonSerializer.Serialize(modern, Json.Options), Json.Options)!;
        Assert.Equal(id, legacy.Id); Assert.Equal(started, legacy.Started); Assert.Equal(modern.Detail, legacy.Detail);
        Assert.Contains("concrete reason", legacy.Detail);
    }
    [Fact] public void ErrorReportPreservesReasonAndStackButBoundsSummaryAndRemovesSecrets()
    {
        var error = new InvalidOperationException("request rejected: token=hidden " + new string('x', 500));
        var report = SandboxLaunchErrors.Report(Guid.NewGuid(), error, "synthetic-error.txt");
        Assert.Equal(300, report.Detail.Length); Assert.Contains("request rejected", report.Detail);
        Assert.Contains("InvalidOperationException", report.ErrorDetail); Assert.True(report.ErrorDetail!.Length > 300);
        Assert.DoesNotContain("hidden", report.Detail); Assert.DoesNotContain("hidden", report.ErrorDetail);
    }
    [Fact] public void LauncherSuccessUsesStructuredStateAndLegacyVerifiedProcessNotProse()
    {
        var p = SandboxInstances.Create("Synthetic", @"C:\Synthetic\Launcher.exe", Guid.NewGuid());
        var process = new ProcessIdentity(123, 456);
        var status = new SandboxInstanceStatus(p.Id, "代理執行中", "啟動器已建立", [process]) { LauncherStarted = false };
        Assert.False(SandboxLauncherState.IsStarted(status, p, _ => p.LauncherPath, _ => process));
        Assert.True(SandboxLauncherState.IsStarted(status with { Detail = "unrelated text", LauncherStarted = true }, p, _ => "", _ => null));
        var legacy = status with { Detail = "unrelated text", LauncherStarted = null, Network = new(p.Id, "192.0.2.1", "192.0.2.1", true) };
        Assert.True(SandboxLauncherState.IsStarted(legacy, p, _ => p.LauncherPath, _ => process));
        Assert.False(SandboxLauncherState.IsStarted(legacy, p, _ => p.LauncherPath, _ => new ProcessIdentity(123, 789)));
        Assert.False(SandboxLauncherState.IsStarted(legacy, p, _ => @"C:\Tools\WindowSocks.Tools.exe", _ => process));
        Assert.False(SandboxLauncherState.IsStarted(status with { LauncherStarted = true, State = "出口異常 · 已封鎖" }, p, _ => p.LauncherPath, _ => process));
    }
    [Fact] public async Task RunningAThenSelectedABRetainsAAndReportsSkippedSeparately()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(); var running = new HashSet<Guid>(); var starts = new List<Guid>();
        Task<SandboxStartResult> Launch(Guid id, CancellationToken ct) { if (!running.Add(id)) return Task.FromResult(SandboxStartResult.AlreadyRunning); starts.Add(id); return Task.FromResult(SandboxStartResult.Started); }
        var queue = new SandboxLaunchQueue();
        await queue.RunAsync([a], 10, Launch, null, default);
        int monitors = 0, failed = 0;
        var monitoring = SandboxBootstrapSession.Run(() => Task.CompletedTask, async () => {
            if (++monitors == 1) { await queue.RunAsync([a, b], 10, Launch, null, default, (_, _) => Task.CompletedTask); throw new IOException("transient monitor failure"); }
            return false;
        }, _ => { failed++; return Task.CompletedTask; }, _ => { }, delay: _ => Task.CompletedTask);
        await monitoring; Assert.Equal(0, failed); Assert.Equal(new[] { a, b }, starts); Assert.Equal(2, monitors);
        var results = await queue.RunAsync([a, b], 10, Launch, null, default, (_, _) => Task.CompletedTask);
        Assert.All(results, r => { Assert.Equal(SandboxStartResult.AlreadyRunning, r.Result); Assert.Equal("已在執行，略過", r.Detail); });
    }
}

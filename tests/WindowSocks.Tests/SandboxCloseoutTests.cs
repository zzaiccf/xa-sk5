using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

internal sealed class SandboxTestClock : TimeProvider
{
    private long milliseconds;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => milliseconds;
    public void Advance(TimeSpan time) => milliseconds += (long)time.TotalMilliseconds;
    public Task Delay(TimeSpan time, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Advance(time); return Task.CompletedTask; }
}

public sealed class SandboxCloseoutTests
{
    [Fact] public async Task C03_ContinuousMonitorFailureExitsQuietlyWithinFiveMinutes()
    {
        var clock = new SandboxTestClock(); int polls = 0, failed = 0, logged = 0;
        await SandboxBootstrapSession.Run(() => Task.CompletedTask, () => {
            // Safeguard makes the old infinite-retry implementation fail promptly.
            if (++polls > 600) return Task.FromResult(false);
            throw new IOException("synthetic service unavailable");
        }, _ => { failed++; return Task.CompletedTask; }, _ => logged++,
            delay: ct => clock.Delay(TimeSpan.FromSeconds(1), ct), clock: clock);
        Assert.Equal(0, failed); Assert.InRange(polls, 300, 301); Assert.Equal(polls, logged);
    }
    [Fact] public async Task C03_RecoveryRestartsConsecutiveFailureBudget()
    {
        var clock = new SandboxTestClock(); int polls = 0, failed = 0;
        await SandboxBootstrapSession.Run(() => Task.CompletedTask, () => {
            ++polls;
            if (polls == 200) return Task.FromResult(true);
            if (polls > 900) return Task.FromResult(false);
            throw new IOException("synthetic transport failure");
        }, _ => { failed++; return Task.CompletedTask; }, _ => { },
            delay: ct => clock.Delay(TimeSpan.FromSeconds(1), ct), clock: clock);
        Assert.Equal(0, failed); Assert.InRange(polls, 500, 501);
    }
    [Fact] public async Task C03_CancellingPostSuccessDelayDoesNotBecomeStartupFailure()
    {
        using var ct = new CancellationTokenSource(); int failed = 0;
        await SandboxBootstrapSession.Run(() => { ct.Cancel(); return Task.CompletedTask; }, () => Task.FromResult(true),
            _ => { failed++; return Task.CompletedTask; }, _ => { }, ct.Token);
        Assert.Equal(0, failed);
    }
    [Fact] public async Task C03_PendingRetryCannotExtendTheFailureDeadline()
    {
        var clock = new SandboxTestClock(); int polls = 0, failed = 0;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = SandboxBootstrapSession.Run(() => Task.CompletedTask, () => {
            if (++polls == 1) throw new IOException("synthetic disconnect");
            return pending.Task;
        }, _ => { failed++; return Task.CompletedTask; }, _ => { },
            delay: ct => clock.Delay(TimeSpan.FromMilliseconds(polls == 0 ? 1000 : 299980), ct), clock: clock);
        try { await run.WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { pending.TrySetResult(false); await run; }
        Assert.Equal(2, polls); Assert.Equal(0, failed);
    }
    [Fact] public async Task C04_PathIsResolvedOnceAcrossNestedRemovalOperationsButNotAcrossRuns()
    {
        int queries = 0;
        string Resolve() => SandboxConfiguration.ResolvePath(() => { queries++; return @"C:\Synthetic\Sandboxie.ini"; });
        var p = SandboxInstances.Create("Synthetic", @"C:\Synthetic\Launcher.exe", Guid.NewGuid());
        var box = new SandboxBoxRemoval(_ => { Resolve(); return Task.FromResult(Array.Empty<string>()); },
            (_, _, _) => Task.FromResult("GlobalSettings\n"), _ => { Resolve(); });
        await SandboxConfiguration.Run(async () => { await box.Disable(p, default); await box.Remove(p, default); }, default);
        Assert.Equal(1, queries);
        await SandboxConfiguration.Run(() => { Resolve(); return Task.CompletedTask; }, default);
        Assert.Equal(2, queries);
    }
}

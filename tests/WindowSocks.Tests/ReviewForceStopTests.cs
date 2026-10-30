using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;
public class ReviewForceStopTests
{
    [Fact] public async Task R9_ExplicitRecoveryCanRecoverUnresponsiveService()
    {
        bool running = true, offered = false;
        await ServiceExit.StopAsync(_ => Task.CompletedTask, () => running, timeout: TimeSpan.FromMilliseconds(10),
            explicitRecovery: _ => { offered = true; running = false; return Task.CompletedTask; });
        Assert.True(offered);
        Assert.False(running);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task R9_PinnedTerminationAndRiskReviewPrecedeScm(bool approve)
    {
        var trace = new List<string>();
        var work = ForceServiceStop.ExecuteAsync(null, "test", report => {
            trace.Add("confirm"); Assert.Contains("未知", report.Warning); return Task.FromResult(approve);
        }, (_, _) => { trace.Add("pin-and-terminate"); return Task.FromResult<ProcessCloseOutcome[]>([]); },
            () => { trace.Add("scm"); return Task.CompletedTask; });
        if (approve) await work; else await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        Assert.Equal(approve ? ["pin-and-terminate", "confirm", "scm"] : new[] { "pin-and-terminate", "confirm" }, trace);
    }
}

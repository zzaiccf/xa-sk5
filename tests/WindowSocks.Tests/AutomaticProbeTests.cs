using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class AutomaticProbeTests
{
    private readonly DateTimeOffset now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    [Fact] public void StartupQueuesEveryNodeOnceAndSnapshotsCannotDuplicateActiveOrPendingWork()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule(1);
        Assert.True(schedule.Synchronize("local", [a, b, a], now)); Assert.Equal(2, schedule.PendingCount);
        Assert.True(schedule.TryStart(out var job)); Assert.Equal(a, job.ProxyId);
        Assert.False(schedule.TryStart(out _));
        for (int i = 0; i < 5; i++) schedule.Synchronize("local", [a, b], now.AddSeconds(i));
        Assert.Equal(1, schedule.PendingCount); Assert.True(schedule.IsActive(a));
        schedule.Complete(job, true, now);
        schedule.Synchronize("local", [a, b], now.AddSeconds(5));
        Assert.True(schedule.TryStart(out job)); Assert.Equal(b, job.ProxyId);
        schedule.Complete(job, true, now);
        Assert.False(schedule.TryStart(out _));
    }
    [Fact] public void ImportedNodesAreAutomaticallyDiscoveredAndCanMoveAheadOfExistingBatch()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), imported = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule(1);
        schedule.Synchronize("service", [a, b], now); schedule.TryStart(out var first);
        schedule.Synchronize("service", [a, b, imported], now); schedule.Request([imported, imported]);
        Assert.Equal(2, schedule.PendingCount);
        schedule.Complete(first, true, now);
        Assert.True(schedule.TryStart(out var next)); Assert.Equal(imported, next.ProxyId);
        schedule.Complete(next, true, now);
        Assert.True(schedule.TryStart(out next)); Assert.Equal(b, next.ProxyId);
    }
    [Fact] public void FailedNodeWaitsFiveMinutesAndSuccessfulNodeWaitsThirtyMinutes()
    {
        Guid bad = Guid.NewGuid(), good = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule(1);
        schedule.Synchronize("local", [bad, good], now); schedule.TryStart(out var job); schedule.Complete(job, false, now);
        Assert.True(schedule.TryStart(out job)); Assert.Equal(good, job.ProxyId); schedule.Complete(job, true, now);
        schedule.Synchronize("local", [bad, good], now.AddMinutes(4).AddSeconds(59)); Assert.Equal(0, schedule.PendingCount);
        schedule.Synchronize("local", [bad, good], now.AddMinutes(5)); Assert.Equal(1, schedule.PendingCount);
        schedule.TryStart(out job); Assert.Equal(bad, job.ProxyId); schedule.Complete(job, true, now.AddMinutes(5));
        schedule.Synchronize("local", [bad, good], now.AddMinutes(29).AddSeconds(59)); Assert.Equal(0, schedule.PendingCount);
        schedule.Synchronize("local", [bad, good], now.AddMinutes(30));
        Assert.True(schedule.TryStart(out job)); Assert.Equal(good, job.ProxyId);
    }
    [Fact] public void BackendChangeRequiresFreshChecksAndRejectsLateResultsFromOldBackend()
    {
        Guid id = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule(1);
        schedule.Synchronize("local", [id], now); schedule.TryStart(out var oldJob);
        Assert.True(schedule.Synchronize("service", [id], now)); Assert.False(schedule.IsCurrent(oldJob));
        schedule.Complete(oldJob, true, now);
        Assert.True(schedule.TryStart(out var newJob)); Assert.NotEqual(oldJob.Generation, newJob.Generation);
        schedule.Complete(oldJob, true, now); Assert.True(schedule.IsActive(id));
        schedule.Complete(newJob, true, now); Assert.Equal(0, schedule.ActiveCount);
    }
    [Fact] public void DeletedQueuedAndActiveNodesAreRemovedWithoutReappearingFromLateCompletion()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("local", [a, b], now); schedule.TryStart(out var job);
        Assert.True(schedule.Synchronize("local", [], now)); schedule.Complete(job, true, now);
        Assert.Equal(0, schedule.ActiveCount); Assert.Equal(0, schedule.PendingCount); Assert.False(schedule.TryStart(out _));
        schedule.Synchronize("local", [a], now); Assert.True(schedule.TryStart(out _));
    }
    [Fact] public void ManualOrImportRequestsRunWhilePausedWithoutResumingAutomaticChecks()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule(1);
        schedule.Synchronize("local", [a, b], now); schedule.TryStart(out var job); schedule.Stop();
        Assert.True(schedule.Paused); Assert.True(schedule.IsActive(a)); schedule.Complete(job, true, now);
        schedule.Synchronize("local", [a, b], now.AddHours(1)); Assert.False(schedule.TryStart(out _));
        schedule.Request([b]); Assert.True(schedule.Paused); Assert.True(schedule.TryStart(out job)); Assert.Equal(b, job.ProxyId);
        schedule.Complete(job, true, now.AddHours(1));
        schedule.Synchronize("local", [a, b], now.AddHours(2)); Assert.False(schedule.TryStart(out _));
        schedule.Resume(); schedule.Synchronize("local", [a, b], now.AddHours(2)); Assert.False(schedule.Paused); Assert.True(schedule.TryStart(out _));
    }
    [Fact] public void ManualRequestBypassesFreshnessDelayAndRepeatedClicksDoNotDuplicateWork()
    {
        Guid id = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("local", [id], now); schedule.TryStart(out var job); schedule.Complete(job, true, now);
        schedule.Synchronize("local", [id], now.AddSeconds(1)); Assert.False(schedule.TryStart(out _));
        schedule.Request([id]); schedule.Request([id]); Assert.Equal(1, schedule.PendingCount);
        Assert.True(schedule.TryStart(out job)); schedule.Request([id]); Assert.Equal(0, schedule.PendingCount);
        schedule.Complete(job, true, now.AddMinutes(1)); schedule.Synchronize("local", [id], now.AddMinutes(30)); Assert.False(schedule.TryStart(out _));
        schedule.Synchronize("local", [id], now.AddMinutes(31)); Assert.True(schedule.TryStart(out _));
    }
    [Fact] public void FiveJobsRunTogetherAndSlotsRefillWithoutDuplicatingRequests()
    {
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("local", ids, now); var jobs = new List<ProbeJob>();
        while (schedule.TryStart(out var job)) jobs.Add(job);
        Assert.Equal(5, jobs.Count); Assert.Equal(5, schedule.ActiveCount); Assert.Equal(3, schedule.PendingCount);
        schedule.Request(ids); schedule.Request(ids); Assert.Equal(3, schedule.PendingCount);
        Assert.All(jobs, job => Assert.True(schedule.IsCurrent(job)));
        schedule.Complete(jobs[2], true, now); Assert.True(schedule.TryStart(out var next)); Assert.Equal(ids[5], next.ProxyId);
        Assert.Equal(5, schedule.ActiveCount); Assert.False(schedule.TryStart(out _));
        schedule.Complete(jobs[2], false, now); Assert.Equal(5, schedule.ActiveCount);
    }
    [Fact] public void LateCompletionCannotReplaceReaddedNodeOrNewBackendBatch()
    {
        var id = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("local", [id], now); schedule.TryStart(out var deleted);
        schedule.Synchronize("local", [], now); schedule.Synchronize("local", [id], now); schedule.TryStart(out var readded);
        Assert.NotEqual(deleted, readded); schedule.Complete(deleted, false, now); Assert.True(schedule.IsCurrent(readded));
        schedule.Synchronize("service", [id], now); schedule.TryStart(out var service);
        schedule.Complete(readded, false, now); Assert.True(schedule.IsCurrent(service));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchingSupplementUpdatesRetryFrequencyWithoutAcceptingAnOldCheck(bool udpPassed)
    {
        Guid id = Guid.NewGuid(), check = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("service", [id], now); schedule.TryStart(out var job);
        schedule.Complete(job, true, now, check, supplementalPending: true);
        var final = new ProbeResult(id, true, true, true, udpPassed, "203.0.113.1", null, 100, "fixture", now.AddSeconds(2)) { CheckId = check };
        schedule.ObserveResults([final with { CheckId = Guid.NewGuid(), Udp = false }]);
        schedule.Synchronize("service", [id], now.AddMinutes(5)); Assert.False(schedule.TryStart(out _));
        schedule.ObserveResults([final]);
        var due = final.At.Add(ProbeTiming.NextInterval(udpPassed));
        schedule.Synchronize("service", [id], due.AddMilliseconds(-1)); Assert.False(schedule.TryStart(out _));
        schedule.Synchronize("service", [id], due); Assert.True(schedule.TryStart(out _));
    }
}

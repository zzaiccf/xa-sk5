using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class AutomaticProbeTests
{
    private readonly DateTimeOffset now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    [Fact] public void StartupQueuesEveryNodeOnceAndSnapshotsCannotDuplicateActiveOrPendingWork()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        Assert.True(schedule.Synchronize("local", [a, b, a], now)); Assert.Equal(2, schedule.PendingCount);
        Assert.True(schedule.TryStart(out var job)); Assert.Equal(a, job.ProxyId);
        Assert.False(schedule.TryStart(out _));
        for (int i = 0; i < 5; i++) schedule.Synchronize("local", [a, b], now.AddSeconds(i));
        Assert.Equal(1, schedule.PendingCount); Assert.Equal(a, schedule.Active);
        schedule.Complete(job, true, now);
        schedule.Synchronize("local", [a, b], now.AddSeconds(5));
        Assert.True(schedule.TryStart(out job)); Assert.Equal(b, job.ProxyId);
        schedule.Complete(job, true, now);
        Assert.False(schedule.TryStart(out _));
    }
    [Fact] public void ImportedNodesAreAutomaticallyDiscoveredAndCanMoveAheadOfExistingBatch()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), imported = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
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
        Guid bad = Guid.NewGuid(), good = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
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
        Guid id = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("local", [id], now); schedule.TryStart(out var oldJob);
        Assert.True(schedule.Synchronize("service", [id], now)); Assert.False(schedule.IsCurrent(oldJob));
        schedule.Complete(oldJob, true, now);
        Assert.True(schedule.TryStart(out var newJob)); Assert.NotEqual(oldJob.Generation, newJob.Generation);
        schedule.Complete(oldJob, true, now); Assert.Equal(id, schedule.Active);
        schedule.Complete(newJob, true, now); Assert.Null(schedule.Active);
    }
    [Fact] public void DeletedQueuedAndActiveNodesAreRemovedWithoutReappearingFromLateCompletion()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("local", [a, b], now); schedule.TryStart(out var job);
        Assert.True(schedule.Synchronize("local", [], now)); schedule.Complete(job, true, now);
        Assert.Null(schedule.Active); Assert.Equal(0, schedule.PendingCount); Assert.False(schedule.TryStart(out _));
        schedule.Synchronize("local", [a], now); Assert.True(schedule.TryStart(out _));
    }
    [Fact] public void ManualOrImportRequestsRunWhilePausedWithoutResumingAutomaticChecks()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("local", [a, b], now); schedule.TryStart(out var job); schedule.Stop();
        Assert.True(schedule.Paused); Assert.Equal(a, schedule.Active); schedule.Complete(job, true, now);
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
}

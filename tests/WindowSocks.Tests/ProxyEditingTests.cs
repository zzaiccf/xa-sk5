using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ProxyEditingTests
{
    [Fact] public void EditingRetainsIdentityAndSecretUnlessExplicitlyChanged()
    {
        var original = new ProxyProfile { Name = "original", Host = "198.51.100.1", Port = 1080, Username = "fixture", Password = "old-secret", ExpiresOn = new(2027, 1, 1) };
        var request = new EditProxyRequest(original.Id, "new name", original.Host, original.Port, original.Username, null, null);
        var next = ProxyEditing.Prepare([original], request);
        Assert.Equal(original.Id, next.Id); Assert.Equal("old-secret", next.Password); Assert.Null(next.ExpiresOn);
        Assert.Equal("original", original.Name); Assert.NotNull(original.ExpiresOn);
        Assert.Empty(ProxyEditing.Prepare([original], request with { Password = "" }).Password);
        Assert.Throws<InvalidOperationException>(() => ProxyEditing.Prepare([original], request with { Port = 0 }));
        var duplicate = original with { Id = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => ProxyEditing.Prepare([original, duplicate], request));
    }
    [Fact] public void ReplacingAnActiveScheduledNodeDiscardsLateCompletions()
    {
        var id = Guid.NewGuid(); var schedule = new AutomaticProbeSchedule();
        schedule.Synchronize("test", [id], DateTimeOffset.UtcNow); Assert.True(schedule.TryStart(out var old));
        schedule.Invalidate(id); schedule.Request([id]); Assert.True(schedule.TryStart(out var current));
        Assert.False(schedule.IsCurrent(old)); schedule.Complete(old, true, DateTimeOffset.UtcNow);
        Assert.True(schedule.IsCurrent(current)); Assert.Equal(1, schedule.ActiveCount);
    }
    [Fact] public async Task EditingDuringAProbeRejectsOldResultAndRetainsTheNewRunningCheck()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xa-edit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var entered = System.Threading.Channels.Channel.CreateUnbounded<int>();
        TaskCompletionSource[] releases = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        int calls = 0;
        try {
            using var service = new Controller(directory, async (p, _, ct) => {
                int call = Interlocked.Increment(ref calls); entered.Writer.TryWrite(call); await releases[call - 1].Task.WaitAsync(ct);
                return new(p.Id, true, true, true, true, "203.0.113.10", "203.0.113.10", 5, "synthetic", DateTimeOffset.UtcNow);
            });
            async Task<Snapshot> State() => Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.True((await service.Dispatch(Command.Create("import", new ImportRequest("198.51.100.1/1080/fixture/old-secret/2027-01-01")), default)).Ok);
            var profile = (await State()).Proxies.Single();
            var old = service.Dispatch(Command.Create("probe", new ProxyRequest(profile.Id)), default);
            await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var edit = new EditProxyRequest(profile.Id, "edited", "198.51.100.2", 1081, profile.Username, null, null);
            Assert.True((await service.Dispatch(Command.Create("editProxy", edit), default)).Ok);
            var next = service.Dispatch(Command.Create("probe", new ProxyRequest(profile.Id)), default);
            await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            releases[0].TrySetResult(); Assert.False((await old).Ok);
            var joined = service.Dispatch(Command.Create("probe", new ProxyRequest(profile.Id)), default);
            Assert.Equal(2, Volatile.Read(ref calls)); Assert.False(joined.IsCompleted);
            releases[1].TrySetResult(); Assert.True((await next).Ok); Assert.True((await joined).Ok);
            var state = await State(); Assert.True(state.SupportsProxyEditing); Assert.Single(state.Proxies); Assert.Single(state.Probes);
            Assert.Equal("edited", state.Proxies[0].Name); Assert.Equal(profile.Id, state.Proxies[0].Id); Assert.Null(state.Proxies[0].ExpiresOn); Assert.Empty(state.Proxies[0].Password);
            var saved = new ConfigStore(directory).Load(); Assert.Equal("old-secret", saved.Proxies.Single().Password);
        } finally { foreach (var release in releases) release.TrySetResult(); Directory.Delete(directory, true); }
    }
}

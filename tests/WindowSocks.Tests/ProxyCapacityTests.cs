using System.Security.Principal;
using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ProxyCapacityTests
{
    private static TargetInstance Target(int n) => new($"pid:{n}", $"test{n}", "Process", @"C:\SyntheticGame\Login.exe", 0, true, [new(n, n)]);
    [Fact] public void DifferentLineCountsAndZeroAreUsedByAutomaticAssignment()
    {
        ProxyProfile[] pool = [new(), new(), new()];
        var quotas = new Dictionary<Guid, int> { [pool[0].Id] = 0, [pool[1].Id] = 2, [pool[2].Id] = 3 };
        var items = AutoProxyPlanner.Additions(Enumerable.Range(1, 8).Select(Target), [], pool, 1, [], capacities: quotas);
        Assert.Equal(5, items.Length); Assert.DoesNotContain(items, i => i.ProxyId == pool[0].Id);
        Assert.Equal(2, items.Count(i => i.ProxyId == pool[1].Id)); Assert.Equal(3, items.Count(i => i.ProxyId == pool[2].Id));
    }
    [Fact] public void LoweringQuotaRetainsBindingsAndOnlyNewTargetsUseRemainingSlots()
    {
        ProxyProfile[] pool = [new(), new()];
        var existing = AssignmentPlanner.Prepare([], pool, new([new(Target(1), pool[0].Id), new(Target(2), pool[0].Id)]));
        var quotas = new Dictionary<Guid, int> { [pool[0].Id] = 1, [pool[1].Id] = 2 };
        var items = AutoProxyPlanner.Additions(Enumerable.Range(1, 5).Select(Target), existing, pool, 1, [], capacities: quotas);
        Assert.Equal(2, items.Length); Assert.All(items, i => Assert.Equal(pool[1].Id, i.ProxyId));
        Assert.All(existing, b => Assert.Equal(pool[0].Id, b.ProxyId));
        var released = AutoProxyPlanner.Additions([Target(3)], [], pool, 1, [], capacities: quotas);
        Assert.Equal(pool[0].Id, Assert.Single(released).ProxyId);
    }
    [Fact] public void CountsIncludeManualUseAndDoNotOverrideExclusiveBindingsOrTotalLimit()
    {
        ProxyProfile[] pool = [new(), new()];
        var existing = AssignmentPlanner.Prepare([], pool, new([new(Target(1), pool[0].Id, false)]));
        var quotas = pool.ToDictionary(p => p.Id, _ => 10);
        var items = AutoProxyPlanner.Additions(Enumerable.Range(1, 20).Select(Target), existing, pool, 1, [], capacities: quotas);
        Assert.Equal(9, items.Length); Assert.All(items, i => Assert.Equal(pool[1].Id, i.ProxyId));
    }
    [Fact] public void LegacyUniformCapacityAndNewLineFallbackSurviveJson()
    {
        var rule = Json.Read<AutoProxyRule>(JsonSerializer.SerializeToElement(new { Kind = "Process", Pattern = "Login.exe", PerProxy = 2 }, Json.Options));
        Assert.Empty(rule.ProxyCapacities); AutoProxyPlanner.Validate(rule);
        ProxyProfile[] pool = [new(), new()];
        var items = AutoProxyPlanner.Additions(Enumerable.Range(1, 6).Select(Target), [], pool, rule.PerProxy, [], capacities: rule.ProxyCapacities);
        Assert.Equal(4, items.Length);
        var custom = rule with { ProxyCapacities = new() { [pool[0].Id] = 0 } };
        var restored = Json.Read<AutoProxyRule>(JsonSerializer.SerializeToElement(custom, Json.Options));
        Assert.Equal(0, restored.ProxyCapacities[pool[0].Id]); Assert.Equal(2, ProxyCapacity.For(pool[1].Id, restored.PerProxy, restored.ProxyCapacities));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void InvalidQuotaIsRejectedBeforeAssignment(int count)
        => Assert.Throws<InvalidOperationException>(() => AutoProxyPlanner.Validate(new() { Kind = "Process", Pattern = "Login.exe", ProxyCapacities = new() { [Guid.NewGuid()] = count } }));
    [Fact] public void NullAndEmptyIdentitiesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => ProxyCapacity.Validate(1, null));
        Assert.Throws<InvalidOperationException>(() => ProxyCapacity.Validate(1, new Dictionary<Guid, int> { [Guid.Empty] = 1 }));
    }
    [Fact] public async Task DisabledSequenceCountsPersistAcrossServiceRestartAndDeletedLinesArePruned()
    {
        string root = Path.Combine(Path.GetTempPath(), "xa-capacity-tests-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var first = new ProxyProfile { Host = "127.0.0.1", Port = 1080 }; var second = new ProxyProfile { Host = "127.0.0.1", Port = 1081 };
        var store = new ConfigStore(root); store.Save(new() { Proxies = [first, second] });
        try {
            var rule = new AutoProxyRule { Kind = "EmulatorSequence", Enabled = false, ConsolePath = @"C:\SyntheticEmulator\ldconsole.exe", ProxyCapacities = new() { [first.Id] = 0, [second.Id] = 3 } };
            using (var service = new Controller(root)) {
                var response = await service.Dispatch(Command.Create("setAutoProxy", rule), default); Assert.True(response.Ok, response.Error);
            }
            using (var service = new Controller(root)) {
                var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.True(snapshot.SupportsProxyCapacities); Assert.Equal(3, snapshot.AutoProxy.ProxyCapacities[second.Id]); Assert.False(snapshot.Diagnostics.EngineRunning);
                var invalid = await service.Dispatch(Command.Create("setAutoProxy", rule with { ProxyCapacities = new() { [Guid.NewGuid()] = 5 } }), default);
                Assert.False(invalid.Ok); Assert.Equal(2, store.Load().AutoProxy.ProxyCapacities.Count);
                Assert.True((await service.Dispatch(Command.Create("deleteProxy", new ProxyRequest(first.Id)), default)).Ok);
                Assert.DoesNotContain(first.Id, store.Load().AutoProxy.ProxyCapacities.Keys);
                Assert.True((await service.Dispatch(Command.Create("clearProxyPool", new { }), default)).Ok);
                Assert.Empty(store.Load().AutoProxy.ProxyCapacities);
            }
        } finally { Directory.Delete(root, true); }
    }
}

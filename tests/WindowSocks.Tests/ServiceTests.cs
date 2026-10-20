using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ServiceTests
{
    [Fact] public async Task TcpQueueFinishesWhileSupplementsAreBlockedAndOldChecksCannotOverwriteNewOnes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        var concurrency = new System.Collections.Concurrent.ConcurrentBag<int>(); int active = 0;
        try {
            using var service = new Controller(directory,
                measureTcp: (profile, _, _) => Task.FromResult(new ProbeResult(profile.Id, true, true, true, false, "203.0.113.10", null, 100, "TCP first", DateTimeOffset.UtcNow) { UdpPending = true, UdpChecked = false, CheckId = Guid.NewGuid(), ConnectLatencyMs = 8 }),
                supplementProxy: async (profile, tcp, _, ct) => {
                    concurrency.Add(Interlocked.Increment(ref active)); entered.Writer.TryWrite(profile.Id);
                    try { await release.Task.WaitAsync(ct); return tcp with { Udp = true, UdpPending = false, UdpChecked = true, At = DateTimeOffset.UtcNow }; }
                    finally { Interlocked.Decrement(ref active); }
                });
            try {
                string batch = string.Join('\n', Enumerable.Range(1, 7).Select(i => $"198.51.100.4/{25000+i}/fixture-{i}/synthetic"));
                Assert.True((await service.Dispatch(Command.Create("import", new ImportRequest(batch)), default)).Ok);
                async Task<Snapshot> Snapshot() => Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                var ids = (await Snapshot()).Proxies.Select(p => p.Id).ToArray();
                var replies = await Task.WhenAll(ids.Select(id => service.Dispatch(Command.Create("probeTcp", new ProxyRequest(id)), default))).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.All(replies, reply => { Assert.True(reply.Ok, reply.Error); var tcp = Json.Read<ProbeResult>(reply.Data); Assert.True(tcp.Tcp); Assert.True(tcp.UdpPending); Assert.False(tcp.Ready); });
                var first = await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(2, Volatile.Read(ref active));
                var state = await Snapshot(); Assert.True(state.SupportsTcpFirstProbes); Assert.Equal(7, state.Probes.Length);
                Assert.All(state.Probes, probe => Assert.True(probe.UdpPending));
                var newer = Json.Read<ProbeResult>((await service.Dispatch(Command.Create("probeTcp", new ProxyRequest(first)), default)).Data);
                var deleted = ids.First(id => id != first);
                Assert.True((await service.Dispatch(Command.Create("deleteProxy", new ProxyRequest(deleted)), default)).Ok);
                release.TrySetResult();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                do { state = await Snapshot(); if (state.Probes.All(p => !p.UdpPending)) break; await Task.Delay(10, deadline.Token); } while (true);
                Assert.Equal(6, state.Probes.Length); Assert.DoesNotContain(state.Probes, p => p.ProxyId == deleted);
                Assert.Equal(newer.CheckId, state.Probes.Single(p => p.ProxyId == first).CheckId);
                Assert.All(state.Probes, probe => Assert.True(probe.Ready)); Assert.Equal(2, concurrency.Max());
            } finally { release.TrySetResult(); }
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task FiveDistinctProbesShareNoNetworkLockAndDuplicateRequestsShareOneMeasurement()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var entered = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        var releases = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource>();
        var concurrency = new System.Collections.Concurrent.ConcurrentBag<int>(); int active = 0, measured = 0;
        try {
            using var service = new Controller(directory, async (profile, _, ct) => {
                var release = releases.GetOrAdd(profile.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                concurrency.Add(Interlocked.Increment(ref active)); Interlocked.Increment(ref measured); entered.Writer.TryWrite(profile.Id);
                try { await release.Task.WaitAsync(ct); return new(profile.Id, true, true, true, true, "203.0.113.10", "203.0.113.10", 10, "Synthetic", DateTimeOffset.UtcNow); }
                finally { Interlocked.Decrement(ref active); }
            });
            Task<Reply>[] work = [];
            try {
                string batch = string.Join('\n', Enumerable.Range(1, 6).Select(i => $"198.51.100.3/{23000 + i}/fixture-{i}/synthetic"));
                Assert.True((await service.Dispatch(Command.Create("import", new ImportRequest(batch)), default)).Ok);
                var ids = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data).Proxies.Select(p => p.Id).ToArray();
                work = ids.Select(id => service.Dispatch(Command.Create("probe", new ProxyRequest(id)), default)).ToArray();
                for (int i = 0; i < 5; i++) await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(5, Volatile.Read(ref measured)); Assert.Equal(5, Volatile.Read(ref active));
                var duplicate = service.Dispatch(Command.Create("probe", new ProxyRequest(ids[0])), default);
                using var cancelJoin = new CancellationTokenSource();
                var cancelled = service.Dispatch(Command.Create("probe", new ProxyRequest(ids[0])), cancelJoin.Token); cancelJoin.Cancel();
                Assert.False((await cancelled).Ok); Assert.False(work[0].IsCompleted);
                Assert.True((await service.Dispatch(Command.Create("snapshot", new { }), default).WaitAsync(TimeSpan.FromSeconds(3))).Ok);
                releases[ids[0]].TrySetResult();
                Assert.Equal(ids[5], await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True((await duplicate).Ok); Assert.Equal(6, Volatile.Read(ref measured));
                foreach (var release in releases.Values) release.TrySetResult();
                Assert.All(await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(3)), reply => Assert.True(reply.Ok, reply.Error));
                Assert.Equal(5, concurrency.Max());
            } finally { foreach (var release in releases.Values) release.TrySetResult(); await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(5)); }
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task SlowProbeDoesNotDelayBatchImportAndDeletedNodeCannotReturnAsHealthy()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            using var service = new Controller(directory, async (profile, _, ct) => {
                started.TrySetResult(); await completion.Task.WaitAsync(ct);
                return new(profile.Id, true, true, true, true, "203.0.113.10", "203.0.113.10", 10, "Synthetic slow probe", DateTimeOffset.UtcNow);
            });
            Assert.True((await service.Dispatch(Command.Create("import", new ImportRequest("198.51.100.1/1080/test/fixture")), default)).Ok);
            var original = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data).Proxies.Single();
            var probe = service.Dispatch(Command.Create("probe", new ProxyRequest(original.Id)), default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            string batch = string.Join("\r\n", Enumerable.Range(1, 5).Select(i => $"198.51.100.2/{21000 + i}/fixture-{i}/synthetic/2027-01-20"));
            var imported = await service.Dispatch(Command.Create("import", new ImportRequest(batch)), default).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(imported.Ok, imported.Error); Assert.False(probe.IsCompleted);
            var state = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default).WaitAsync(TimeSpan.FromSeconds(3))).Data);
            Assert.Equal(6, state.Proxies.Length); Assert.All(state.Proxies.Where(p => p.Id != original.Id), p => Assert.Equal(new DateOnly(2027, 1, 20), p.ExpiresOn));
            Assert.True((await service.Dispatch(Command.Create("deleteProxy", new ProxyRequest(original.Id)), default)).Ok);
            completion.TrySetResult(); Assert.False((await probe).Ok);
            state = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.Equal(5, state.Proxies.Length); Assert.DoesNotContain(state.Probes, p => p.ProxyId == original.Id);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.False((await service.Dispatch(Command.Create("probe", new ProxyRequest(state.Proxies[0].Id)), cancelled.Token)).Ok);
            Assert.True((await service.Dispatch(Command.Create("probe", new ProxyRequest(state.Proxies[0].Id)), default).WaitAsync(TimeSpan.FromSeconds(3))).Ok);
        } finally { completion.TrySetResult(); Directory.Delete(directory, true); }
    }
    [Fact] public async Task ServicePersistsSecretsRedactsSnapshotsAndRejectsUnavailableEngine()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        try {
            using (var service = new Controller(directory)) {
                var imported = await service.Dispatch(Command.Create("import", new ImportRequest("127.0.0.1:1080:u:TEST-SECRET")), default); Assert.True(imported.Ok, imported.Error);
                var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                var profile = Assert.Single(snapshot.Proxies); Assert.Empty(profile.Password); Assert.False(snapshot.Diagnostics.EngineRunning);
                var backup = await service.Dispatch(Command.Create("backup", new { }), default); Assert.True(backup.Ok);
                Assert.DoesNotContain("TEST-SECRET", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Json.Read<string>(backup.Data))));
                var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
                var target = new TargetInstance("test", "test", "Process", Environment.ProcessPath!, -1, true, [identity]);
                Assert.True((await service.Dispatch(Command.Create("syncTargets", new[] { target }), default)).Ok);
                var bind = await service.Dispatch(Command.Create("bind", new BindRequest(target.Key, profile.Id)), default);
                Assert.False(bind.Ok); // Test output intentionally contains no WinDivert binaries.
                var after = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.Empty(after.Bindings);
                Assert.False((await service.Dispatch(Command.Create("unknown", new { }), default)).Ok);
            }
            using var reopened = new Controller(directory);
            var persisted = Json.Read<Snapshot>((await reopened.Dispatch(Command.Create("snapshot", new { }), default)).Data); Assert.Single(persisted.Proxies);
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task NamedPipeRoundTripUsesCurrentUserAcl()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string pipeName = "WindowSocks.Tests." + Guid.NewGuid().ToString("N");
        using var server = PipeTransport.CreateServer(WindowsIdentity.GetCurrent().User!.Value, pipeName);
        var accept = server.WaitForConnectionAsync(timeout.Token);
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token); await accept;
        await PipeTransport.WriteAsync(client, Command.Create("ping", "hello"), timeout.Token);
        var command = await PipeTransport.ReadAsync<Command>(server, timeout.Token); Assert.Equal("ping", command.Action);
        await PipeTransport.WriteAsync(server, Reply.Failure("測試錯誤"), timeout.Token);
        Assert.False((await PipeTransport.ReadAsync<Reply>(client, timeout.Token)).Ok);
    }
}

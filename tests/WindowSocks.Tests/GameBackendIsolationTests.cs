using System.Diagnostics;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class GameBackendIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneFailedProxyQuarantinesOnlyItsOwnTreeAndRestartRequiresFreshHealth(bool udpOnlyFailure)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        using var first = StartIdle(); using var second = StartIdle();
        ProcessIdentity[] identities = [LdPlayer.Identity(first.Id)!.Value, LdPlayer.Identity(second.Id)!.Value];
        ProxyProfile[] proxies = [new() { Host = "127.0.0.1", Port = 1080 }, new() { Host = "127.0.0.1", Port = 1081 }];
        var store = new ConfigStore(directory);
        var bindings = identities.Select((p, i) => new InstanceBinding {
            TargetKey = $"tree-{i}", Kind = "Process", Path = Path.Combine(Environment.SystemDirectory, "ping.exe"), Processes = [p],
            ProxyId = proxies[i].Id, IncludeChildren = true, Enabled = true, SharedProxy = false
        }).ToList();
        store.Save(new() { Proxies = proxies.ToList(), Bindings = bindings });
        bool failed = false, duplicate = false;
        Task<ProbeResult> Measure(ProxyProfile profile, bool _, CancellationToken ct)
        {
            bool firstProxy = profile.Id == proxies[0].Id, failure = failed && firstProxy;
            string exit = firstProxy || duplicate ? "198.51.100.1" : "198.51.100.2";
            return Task.FromResult(new ProbeResult(profile.Id, true, true, !failure || udpOnlyFailure, !failure,
                !failure || udpOnlyFailure ? exit : null, failure ? null : exit, 1, "Synthetic health; no driver/network", DateTimeOffset.UtcNow));
        }
        static uint Route(ProcessIdentity p, int expected)
        {
            Assert.Equal(expected, NativeEngine.Core_RouteForTest((uint)p.Pid, (ulong)p.CreatedFileTime, 0, out uint proxy));
            return proxy;
        }
        try {
            using (var service = new Controller(directory, measureProxy: Measure)) {
                async Task Probe(int index) {
                    var reply = await service.Dispatch(Command.Create("probe", new ProxyRequest(proxies[index].Id)), default);
                    Assert.True(reply.Ok, reply.Error);
                }
                foreach (var p in identities) Route(p, 2); // Disk health is never trusted on startup.
                await Probe(0); uint firstProxy = Route(identities[0], 1); Route(identities[1], 2);
                await Probe(1); uint secondProxy = Route(identities[1], 1); Assert.NotEqual(firstProxy, secondProxy);
                failed = true; await Probe(0); Assert.Equal(firstProxy, Route(identities[0], 1)); // One-check grace stays on the same upstream.
                await Probe(0); Route(identities[0], 2); Assert.Equal(secondProxy, Route(identities[1], 1));
                failed = false; await Probe(0); Assert.Equal(firstProxy, Route(identities[0], 1));
                duplicate = true; await Probe(1); foreach (var p in identities) Route(p, 2);
                duplicate = false; await Probe(1); Assert.Equal(firstProxy, Route(identities[0], 1)); Assert.Equal(secondProxy, Route(identities[1], 1));
                Assert.True((await service.Dispatch(Command.Create("pause", new TargetRequest(bindings[0].TargetKey)), default)).Ok);
                await Probe(0); Route(identities[0], 2); Assert.Equal(secondProxy, Route(identities[1], 1));
                foreach (var p in identities) Assert.Equal(2, NativeEngine.Core_RouteForTest((uint)p.Pid, (ulong)p.CreatedFileTime, 1, out _));
                var saved = store.Load();
                Assert.Equal(proxies.Select(p => p.Id), saved.Bindings.Select(b => b.ProxyId)); Assert.False(saved.Bindings[0].Enabled);
            }
            using (var restarted = new Controller(directory, measureProxy: Measure)) {
                foreach (var p in identities) Route(p, 2);
                Assert.True((await restarted.Dispatch(Command.Create("probe", new ProxyRequest(proxies[1].Id)), default)).Ok);
                Route(identities[0], 2); Route(identities[1], 1);
                Assert.Equal(proxies.Select(p => p.Id), store.Load().Bindings.Select(b => b.ProxyId));
            }
        } finally {
            NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
            foreach (var process in new[] { first, second }) { if (!process.HasExited) process.Kill(); process.WaitForExit(); }
            Directory.Delete(directory, true);
        }
    }
    private static Process StartIdle() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe")) {
        Arguments = "-n 120 127.0.0.1", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
    })!;
}

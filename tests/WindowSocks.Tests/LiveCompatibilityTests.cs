using System.Buffers.Binary;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class LiveCompatibilityTests
{
    [Theory]
    [InlineData(6, 5555, 1)]
    [InlineData(17, 9000, 1)]
    [InlineData(6, 53, 0)]
    [InlineData(17, 53, 0)]
    public void LocalControlPassesButLocalDnsRemainsManaged(int protocol, int port, int expected)
    {
        var packet = new byte[28]; packet[0] = 0x45; packet[9] = (byte)protocol; packet[16] = 127; packet[19] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)port);
        Assert.Equal(expected, NativeEngine.Core_LocalCommunicationForTest(packet, (uint)packet.Length));
        packet[16] = 192; Assert.Equal(0, NativeEngine.Core_LocalCommunicationForTest(packet, (uint)packet.Length));
        Assert.Equal(0, NativeEngine.Core_LocalCommunicationForTest(packet, 10));
    }
    [Fact] public void OnlyIpv6LoopbackControlCanBypassIpv6Block()
    {
        var packet = new byte[48]; packet[0] = 0x60; packet[6] = 6; packet[39] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42), 5555);
        Assert.Equal(1, NativeEngine.Core_LocalCommunicationForTest(packet, 48));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42), 53);
        Assert.Equal(0, NativeEngine.Core_LocalCommunicationForTest(packet, 48));
        packet[24] = 0x20; packet[42] = 1;
        Assert.Equal(0, NativeEngine.Core_LocalCommunicationForTest(packet, 48));
    }
    [Fact] public async Task ExitedManualProcessReleasesCapacityWithoutEnablingAutoRule()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        var profile = new ProxyProfile { Host = "127.0.0.1", Port = 1080 };
        var alive = LdPlayer.Identity(Environment.ProcessId)!.Value;
        var store = new ConfigStore(directory);
        store.Save(new() { Proxies = [profile], Bindings = [
            new() { TargetKey = "expired", Kind = "Process", ProxyId = profile.Id, Processes = [new(int.MaxValue, 1)] },
            new() { TargetKey = "alive-paused", Kind = "Process", ProxyId = profile.Id, Processes = [alive], Enabled = false }
        ] });
        try {
            using var service = new Controller(directory);
            await service.Tick(default);
            var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.Equal("alive-paused", Assert.Single(snapshot.Bindings).TargetKey);
            Assert.False(snapshot.AutoProxy.Enabled); Assert.False(snapshot.Bindings[0].Enabled);
            Assert.Equal("alive-paused", Assert.Single(store.Load().Bindings).TargetKey);
        } finally { Directory.Delete(directory, true); }
    }
}

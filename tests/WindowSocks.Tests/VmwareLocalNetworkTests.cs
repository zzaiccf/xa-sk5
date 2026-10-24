using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class VmwareLocalNetworkTests : IDisposable
{
    const string A = @"C:\fixtures\a.vmx", B = @"C:\fixtures\b.vmx", C = @"C:\fixtures\c.vmx";
    static uint Ip(string ip) => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes());
    public void Dispose() => NativeEngine.Core_ReplaceGatewayRules([], 0);
    [Fact] public void FixedLocalAndProxyChoicesPersistAndDoNotShareProxyCapacity()
    {
        var proxy = new ProxyProfile();
        var settings = new VmwareSettings { Machines = [new(A,"192.168.70.20",Guid.Empty) { UseLocalNetwork = true }, new(B,"192.168.70.21",proxy.Id)] };
        var restored = JsonSerializer.Deserialize<VmwareSettings>(JsonSerializer.Serialize(settings))!;
        var rows = VmwarePlanner.Assign(restored,[proxy],new Dictionary<string,ProcessIdentity?> { [A]=new(101,1), [B]=new(102,2) },[]);
        Assert.True(rows.Single(r=>r.VmxPath==A).UseLocalNetwork); Assert.Equal(Guid.Empty,rows.Single(r=>r.VmxPath==A).ProxyId);
        Assert.False(rows.Single(r=>r.VmxPath==B).UseLocalNetwork); Assert.Equal(proxy.Id,rows.Single(r=>r.VmxPath==B).ProxyId);
        VmwarePlanner.Validate(settings with { Machines = [settings.Machines[0]] },[]);
        Assert.Throws<InvalidOperationException>(()=>VmwarePlanner.Validate(settings with { Machines = [settings.Machines[0] with { ProxyId = proxy.Id }] },[proxy]));
    }
    [Fact] public void StartupOverflowIsOptInAndExplicitLocalDoesNotConsumeSlots()
    {
        var proxy = new ProxyProfile();
        var settings = new VmwareSettings { ByStartup=true, Machines=[new(A,"192.168.70.20",Guid.Empty){UseLocalNetwork=true},new(B,"192.168.70.21",Guid.Empty),new(C,"192.168.70.22",Guid.Empty)] };
        var live = new Dictionary<string,ProcessIdentity?> { [A]=new(101,1),[B]=new(102,2),[C]=new(103,3) };
        var blocked = VmwarePlanner.Assign(settings,[proxy],live,[]);
        Assert.True(blocked.Single(r=>r.VmxPath==A).UseLocalNetwork);
        Assert.Equal(proxy.Id,blocked.Single(r=>r.VmxPath==B).ProxyId);
        Assert.False(blocked.Single(r=>r.VmxPath==C).UseLocalNetwork);
        var local = VmwarePlanner.Assign(settings with { OverflowToLocal=true },[proxy],live,blocked);
        Assert.True(local.Single(r=>r.VmxPath==C).UseLocalNetwork);
        // Proxy health is separate: a reserved proxy is never reclassified as capacity overflow.
        Assert.Equal(proxy.Id,local.Single(r=>r.VmxPath==B).ProxyId); Assert.False(local.Single(r=>r.VmxPath==B).UseLocalNetwork);
        var stamp=DateTimeOffset.UtcNow;
        local=local.Select(r=>r.VmxPath==C?r with {ConnectedAt=stamp}:r).ToArray();
        Assert.Equal(stamp,VmwarePlanner.Assign(settings with { OverflowToLocal=true },[proxy],live,local).Single(r=>r.VmxPath==C).ConnectedAt);
        live[B]=null;
        var promoted=VmwarePlanner.Assign(settings with { OverflowToLocal=true },[proxy],live,local);
        Assert.Equal(proxy.Id,promoted.Single(r=>r.VmxPath==C).ProxyId); Assert.False(promoted.Single(r=>r.VmxPath==C).UseLocalNetwork);
        Assert.All(VmwarePlanner.Assign(settings with { OverflowToLocal=true },[],live,[]).Where(r=>r.Process.HasValue),r=>Assert.True(r.UseLocalNetwork));
    }
    [Fact] public void LoweringCapacityOrDisablingOverflowReplacesLocalRoutesWithoutChangingFixedLocal()
    {
        var proxy = new ProxyProfile();
        var settings = new VmwareSettings { ByStartup=true, PerProxy=2, OverflowToLocal=true, Machines=[new(A,"192.168.70.20",Guid.Empty),new(B,"192.168.70.21",Guid.Empty),new(C,"192.168.70.22",Guid.Empty){UseLocalNetwork=true}] };
        var live=new Dictionary<string,ProcessIdentity?> { [A]=new(101,1),[B]=new(102,2),[C]=new(103,3) };
        var initial=VmwarePlanner.Assign(settings,[proxy],live,[]);
        var lowered=VmwarePlanner.Assign(settings with {PerProxy=1},[proxy],live,initial);
        Assert.Single(lowered,r=>r.ProxyId==proxy.Id); Assert.Equal(2,lowered.Count(r=>r.UseLocalNetwork));
        var disabled=VmwarePlanner.Assign(settings with {PerProxy=1,OverflowToLocal=false},[proxy],live,lowered);
        Assert.False(disabled.Single(r=>r.VmxPath==B).UseLocalNetwork); Assert.True(disabled.Single(r=>r.VmxPath==C).UseLocalNetwork);
        var legacy=JsonSerializer.Deserialize<VmwareSettings>("{\"ByStartup\":true}")!; Assert.False(legacy.OverflowToLocal);
    }
    [Fact] public void NativeLocalRouteRequiresExplicitChoiceAndCurrentVmIdentity()
    {
        Assert.True(NativeEngine.SupportsVmwareLocalNetwork);
        var identity=LdPlayer.Identity(Environment.ProcessId)!.Value;
        var rule=new NativeEngine.GatewayRule { Source=Ip("192.168.70.20"),Pid=(uint)identity.Pid,Created=(ulong)identity.CreatedFileTime,Blocked=2 };
        Assert.Equal(1,NativeEngine.Core_ReplaceGatewayRules([rule],1));
        Assert.Equal(3,NativeEngine.Core_GatewayRouteForTest(rule.Source,out var proxy)); Assert.Equal(0u,proxy);
        Assert.Equal(2,NativeEngine.Core_GatewayRouteForTest(rule.Source+1,out _));
        Assert.Equal(0,NativeEngine.Core_ReplaceGatewayRules([withProxy(1)],1));
        rule.Created++; Assert.Equal(1,NativeEngine.Core_ReplaceGatewayRules([rule],1)); Assert.Equal(2,NativeEngine.Core_GatewayRouteForTest(rule.Source,out _));
        rule.Created--; rule.Blocked=1; Assert.Equal(1,NativeEngine.Core_ReplaceGatewayRules([rule],1)); Assert.Equal(2,NativeEngine.Core_GatewayRouteForTest(rule.Source,out _));
        rule.Blocked=0; Assert.Equal(0,NativeEngine.Core_ReplaceGatewayRules([rule],1));
        NativeEngine.GatewayRule withProxy(uint value) {var copy=rule;copy.Proxy=value;return copy;}
    }
    [Fact] public async Task NativeLocalTcpConnectsToTheDestinationWithoutSocksNegotiation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        try {
            var accepted=listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            nuint handle=NativeEngine.Core_GatewayOpenLocalForTest(Ip("127.0.0.1"),(ushort)((IPEndPoint)listener.LocalEndpoint).Port,0);
            Assert.NotEqual(nuint.MaxValue,handle);
            using var client=new Socket(new SafeSocketHandle((nint)handle,true)); using var server=await accepted;
            byte[] payload="GET /local HTTP/1.0\r\n\r\n"u8.ToArray(), received=new byte[payload.Length];
            await client.SendAsync(payload,SocketFlags.None,timeout.Token); await server.GetStream().ReadExactlyAsync(received,timeout.Token);
            Assert.Equal(payload,received);
        } finally {listener.Stop();}
    }
    [Fact] public void LocalDnsPreservesGuestResolversAndReplacesTheDedicatedGateway()
    {
        uint host=Ip("192.168.70.1"),dns=Ip("9.9.9.9");
        Assert.Equal(dns,NativeEngine.Core_GatewayDestinationForTest(host,dns,53,1));
        Assert.Equal(Ip("1.1.1.1"),NativeEngine.Core_GatewayDestinationForTest(host,dns,53,0));
        Assert.NotEqual(host,NativeEngine.Core_GatewayDestinationForTest(host,host,53,1));
        Assert.Equal(dns,NativeEngine.Core_GatewayDestinationForTest(host,dns,443,1));
    }
    [Fact] public async Task NativeLocalUdpUsesTheDestinationWithoutAProxyAssociation()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        nuint handle=NativeEngine.Core_GatewayOpenLocalForTest(Ip("127.0.0.1"),(ushort)((IPEndPoint)server.Client.LocalEndPoint!).Port,1);
        Assert.NotEqual(nuint.MaxValue,handle); using var client=new Socket(new SafeSocketHandle((nint)handle,true));
        byte[] payload=[0x11,0x22,0x33,0x44]; await client.SendAsync(payload,SocketFlags.None,timeout.Token);
        var received=await server.ReceiveAsync(timeout.Token); Assert.Equal(payload,received.Buffer);
        await server.SendAsync(received.Buffer,received.RemoteEndPoint,timeout.Token);
        byte[] response=new byte[32]; int count=await client.ReceiveAsync(response,SocketFlags.None,timeout.Token); Assert.Equal(payload,response[..count]);
    }
}

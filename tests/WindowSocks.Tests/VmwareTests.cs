using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class VmwareTests : IDisposable
{
    private const string Vmx = ".encoding = \"UTF-8\"\r\ndisplayName = \"Test Windows\"\r\nuuid.bios = \"fixture-uuid\"\r\nethernet0.present = \"TRUE\"\r\nethernet0.connectionType = \"nat\"\r\nethernet0.addressType = \"generated\"\r\nethernet0.generatedAddress = \"00:0c:29:12:34:56\"\r\nscsi0:0.fileName = \"keep-this-disk.vmdk\"\r\n";
    private const string Dhcp = "# untouched comment\r\nsubnet 192.168.70.0 netmask 255.255.255.0 {\r\nrange 192.168.70.128 192.168.70.254;\r\noption domain-name-servers 192.168.70.1;\r\n}\r\nsubnet 192.168.71.0 netmask 255.255.255.0 { range 192.168.71.128 192.168.71.254; }\r\n";
    public void Dispose() { if (NativeEngine.SupportsVmwareGateway) NativeEngine.Core_ReplaceGatewayRules([], 0); }
    [Fact] public void VmxConfigurationPreservesIdentityDiskAndMacAndRejectsExtraNics()
    {
        var before = VmwareVmx.Describe("fixture.vmx", VmwareVmx.Parse(Vmx));
        string result = VmwareVmx.Configure(Vmx, "VMnet2", out string mac);
        var values = VmwareVmx.Parse(result); var after = VmwareVmx.Describe("fixture.vmx", values);
        Assert.Equal(before.Uuid, after.Uuid); Assert.Equal(before.Mac, mac); Assert.Equal(before.Mac, after.Mac);
        Assert.Equal("custom", after.Connection); Assert.Equal("VMnet2", after.Vnet); Assert.Equal("keep-this-disk.vmdk", values["scsi0:0.fileName"]);
        Assert.Throws<InvalidDataException>(() => VmwareVmx.Configure(Vmx, "VMnet8", out _));
        Assert.Throws<InvalidDataException>(() => VmwareVmx.Configure(Vmx + "ethernet1.present = \"TRUE\"\n", "VMnet2", out _));
        Assert.Throws<InvalidDataException>(() => VmwareVmx.Parse(Vmx + "ethernet0.present = \"FALSE\"\n"));
        string withoutMac = Vmx.Replace("ethernet0.generatedAddress = \"00:0c:29:12:34:56\"", "");
        Assert.Equal(VmwareVmx.Configure(withoutMac, "VMnet2", out string first), VmwareVmx.Configure(withoutMac, "VMnet2", out string second)); Assert.Equal(first, second); Assert.StartsWith("00:50:56:", first);
    }
    [Fact] public void DhcpOnlyChangesTheChosenSubnetAndRefusesCollisions()
    {
        (string Ip, string Mac)[] guests = [("192.168.70.20", "00:0c:29:12:34:56"), ("192.168.70.21", "00:0c:29:12:34:57")];
        string result = VmwareDhcp.Configure(Dhcp, "192.168.70.1", guests);
        Assert.Contains("option routers 192.168.70.1;", result); Assert.Contains("fixed-address 192.168.70.20;", result); Assert.Contains(Dhcp[Dhcp.IndexOf("subnet 192.168.71")..], result);
        Assert.Throws<InvalidDataException>(() => VmwareDhcp.Configure(Dhcp, "192.168.70.1", [("192.168.70.130", guests[0].Mac)]));
        Assert.Throws<InvalidDataException>(() => VmwareDhcp.Configure(Dhcp, "192.168.70.1", [("192.168.71.20", guests[0].Mac)]));
        Assert.Throws<InvalidDataException>(() => VmwareDhcp.Configure(Dhcp, "192.168.70.1", [guests[0], guests[0]]));
        Assert.Throws<InvalidDataException>(() => VmwareDhcp.Configure(result, "192.168.70.1", guests));
    }
    [Fact] public void StartupOrderReservesLiveAssignmentsBeforeNewArrivalsAndSupportsSharing()
    {
        var proxies = new[] { new ProxyProfile(), new ProxyProfile() };
        string a = @"C:\fixtures\a.vmx", b = @"C:\fixtures\b.vmx", c = @"C:\fixtures\c.vmx";
        var settings = new VmwareSettings { ByStartup = true, Machines = [new(a, "192.168.70.20", Guid.Empty), new(b, "192.168.70.21", Guid.Empty), new(c, "192.168.70.22", Guid.Empty)] };
        var live = new Dictionary<string, ProcessIdentity?> { [a] = new(10001, 20), [b] = new(10002, 10), [c] = null };
        var first = VmwarePlanner.Assign(settings, proxies, live, []);
        Assert.Equal(proxies[0].Id, first.Single(r => r.VmxPath == b).ProxyId); Assert.Equal(proxies[1].Id, first.Single(r => r.VmxPath == a).ProxyId);
        live[b] = null; live[c] = new(10003, 30);
        var second = VmwarePlanner.Assign(settings, proxies, live, first);
        Assert.Equal(proxies[1].Id, second.Single(r => r.VmxPath == a).ProxyId); Assert.Equal(proxies[0].Id, second.Single(r => r.VmxPath == c).ProxyId);
        live[b] = new(10004, 40); var full = VmwarePlanner.Assign(settings, proxies, live, second); Assert.Equal(Guid.Empty, full.Single(r => r.VmxPath == b).ProxyId);
        var shared = VmwarePlanner.Assign(settings with { PerProxy = 2 }, [proxies[0]], live, []); Assert.Equal(2, shared.Count(r => r.ProxyId == proxies[0].Id)); Assert.Single(shared, r => r.ProxyId == Guid.Empty);
        var fixedRule = settings with { ByStartup = false, Machines = [new(a, "192.168.70.20", proxies[0].Id), new(b, "192.168.70.21", proxies[0].Id)] };
        Assert.All(VmwarePlanner.Assign(fixedRule, proxies, live, []), row => Assert.Equal(proxies[0].Id, row.ProxyId));
    }
    [Fact] public void NativeGatewayQuarantinesUnknownStoppedAndChangedSourcesWithoutAffectingHostPolicy()
    {
        Assert.True(NativeEngine.SupportsVmwareGateway); Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<NativeEngine.GatewayRule>());
        Assert.Equal(1, NativeEngine.Core_SetProxy(901, "127.0.0.1", 1080, "", ""));
        var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        uint source = BitConverter.ToUInt32(System.Net.IPAddress.Parse("192.168.70.20").GetAddressBytes());
        NativeEngine.GatewayRule[] rules = [new() { Source = source, Pid = (uint)identity.Pid, Created = (ulong)identity.CreatedFileTime, Proxy = 901 }];
        Assert.Equal(1, NativeEngine.Core_ReplaceGatewayRules(rules, 1)); Assert.Equal(1, NativeEngine.Core_GatewayRouteForTest(source, out uint proxy)); Assert.Equal(901u, proxy);
        Assert.Equal(2, NativeEngine.Core_GatewayRouteForTest(source + 1, out _));
        Assert.Equal(0, NativeEngine.Core_ReplaceGatewayRules([rules[0], rules[0]], 2)); Assert.Equal(1, NativeEngine.Core_GatewayRouteForTest(source, out _));
        rules[0].Created++; Assert.Equal(1, NativeEngine.Core_ReplaceGatewayRules(rules, 1)); Assert.Equal(2, NativeEngine.Core_GatewayRouteForTest(source, out _));
        rules[0].Blocked = 1; Assert.Equal(1, NativeEngine.Core_ReplaceGatewayRules(rules, 1)); Assert.Equal(2, NativeEngine.Core_GatewayRouteForTest(source, out _));
        Assert.Equal(0, NativeEngine.Core_GatewayRunning());
    }
    [Fact] public void GuestTcpRedirectAndReturnKeepTheGuestAndRemoteEndpointsSeparated()
    {
        byte[] packet = new byte[40]; packet[0] = 0x45; packet[9] = 6;
        byte[] guest = System.Net.IPAddress.Parse("192.168.70.20").GetAddressBytes(), remote = System.Net.IPAddress.Parse("203.0.113.5").GetAddressBytes(), host = System.Net.IPAddress.Parse("192.168.70.1").GetAddressBytes();
        guest.CopyTo(packet, 12); remote.CopyTo(packet, 16);
        Assert.Equal(1, NativeEngine.Core_GatewayTranslationForTest(packet, 40, BitConverter.ToUInt32(guest), BitConverter.ToUInt32(remote), BitConverter.ToUInt32(host), 17, 0, out uint outbound));
        Assert.Equal(0u, outbound); Assert.Equal(guest, packet[12..16]); Assert.Equal(host, packet[16..20]);
        Assert.Equal(25000, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20))); Assert.Equal(18000, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(22)));
        host.CopyTo(packet, 12); guest.CopyTo(packet, 16);
        Assert.Equal(1, NativeEngine.Core_GatewayTranslationForTest(packet, 40, BitConverter.ToUInt32(guest), BitConverter.ToUInt32(remote), BitConverter.ToUInt32(host), 17, 1, out outbound));
        Assert.Equal(1u, outbound); Assert.Equal(remote, packet[12..16]); Assert.Equal(guest, packet[16..20]);
        Assert.Equal(443, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20))); Assert.Equal(41000, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(22)));
    }
    [Fact] public void DistinctProfilesWithTheSameExitAreBlockedWhileIntentionalSharingIsAllowed()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var checks = new Dictionary<Guid, ProbeResult> {
            [a] = new(a, true, true, true, true, "203.0.113.10", "203.0.113.10", 5, "fixture", DateTimeOffset.UtcNow),
            [b] = new(b, true, true, true, true, "203.0.113.10", "203.0.113.10", 5, "fixture", DateTimeOffset.UtcNow)
        };
        VmwareRuntime[] shared = [new("a.vmx", new(10001, 1), a, ""), new("b.vmx", new(10002, 2), a, "")];
        Assert.False(VmwarePlanner.HasDuplicateExit(a, shared, checks));
        shared[1] = shared[1] with { ProxyId = b }; Assert.True(VmwarePlanner.HasDuplicateExit(a, shared, checks)); Assert.True(VmwarePlanner.HasDuplicateExit(b, shared, checks));
        shared[1] = shared[1] with { Process = null }; Assert.False(VmwarePlanner.HasDuplicateExit(a, shared, checks));
    }
}

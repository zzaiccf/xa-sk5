using System.Text;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class VmwareDiscoveryTests
{
    [Fact] public void DiscoveryKeepsUnicodePathsDeduplicatesAndDoesNotInterpretOrderAsIdentity()
    {
        string path = @"C:\虛擬機\Windows 10\Windows.vmx";
        Assert.Equal([path], VmwareDiscovery.ParseRunningPaths("Total running VMs: 3\r\n" + path + "\r\n" + path.ToUpperInvariant() + "\r\nrelative.vmx\n"));
    }
    [Fact] public void Big5VmxPreservesChineseAndEncodingAcrossNetworkEditAndRestore()
    {
        string text = ".encoding = \"Big5\"\r\ndisplayName = \"測試視窗\"\r\nuuid.bios = \"fixture\"\r\nethernet0.present = \"TRUE\"\r\nethernet0.connectionType = \"nat\"\r\nethernet0.generatedAddress = \"00:0c:29:12:34:56\"\r\n";
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vmx");
        try {
            byte[] original = VmwareVmx.TextEncoding(text).GetBytes(text); File.WriteAllBytes(path, original);
            Assert.Equal("測試視窗", VmwareVmx.Read(path).Name);
            string configured = VmwareVmx.Configure(VmwareVmx.ReadText(path), "VMnet1", out _);
            File.WriteAllBytes(path, VmwareVmx.TextEncoding(configured).GetBytes(configured));
            Assert.Equal("測試視窗", VmwareVmx.Read(path).Name); Assert.Equal("VMnet1", VmwareVmx.Read(path).Vnet);
            File.WriteAllBytes(path, VmwareVmx.TextEncoding(text).GetBytes(text)); Assert.Equal(original, File.ReadAllBytes(path));
        } finally { File.Delete(path); }
    }
    [Fact] public void ServiceRestartRetainsReservationsButReopenedVmMustRejoinCapacity()
    {
        ProxyProfile[] proxies = [new(), new()];
        string a = @"C:\fixtures\a.vmx", b = @"C:\fixtures\b.vmx", c = @"C:\fixtures\c.vmx";
        var settings = new VmwareSettings { ByStartup = true, Machines = [new(a, "192.168.70.20", Guid.Empty), new(b, "192.168.70.21", Guid.Empty), new(c, "192.168.70.22", Guid.Empty)] };
        var live = new Dictionary<string, ProcessIdentity?> { [a] = new(100, 10), [b] = new(101, 20), [c] = null };
        var initial = VmwarePlanner.Assign(settings, proxies, live, []);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<VmwareRuntime[]>(System.Text.Json.JsonSerializer.Serialize(initial))!;
        Assert.Equal(initial, VmwarePlanner.Assign(settings, proxies, live, persisted));
        live[a] = null; live[c] = new(102, 30);
        var next = VmwarePlanner.Assign(settings, proxies, live, persisted);
        Assert.Equal(proxies[0].Id, next.Single(r => r.VmxPath == c).ProxyId);
        live[a] = new(100, 40); // PID reuse must not restore the old reservation.
        Assert.Equal(Guid.Empty, VmwarePlanner.Assign(settings, proxies, live, next).Single(r => r.VmxPath == a).ProxyId);
    }
}

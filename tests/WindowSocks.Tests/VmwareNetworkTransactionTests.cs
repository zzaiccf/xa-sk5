using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class VmwareNetworkTransactionTests : IDisposable
{
    [Theory]
    [InlineData("192.168.70.128/25", true)] [InlineData("192.168.0.0/16", true)] [InlineData("192.168.71.0/24", false)] [InlineData("0.0.0.0/0", true)]
    public void NatOverlapIsRejectedForBothSmallerAndLargerPrefixes(string prefix, bool overlaps) =>
        Assert.Equal(overlaps, VmwareNetwork.OverlapsSubnet(prefix, VmwareDhcp.Number("192.168.70.0")));
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-vmware-transaction-" + Guid.NewGuid().ToString("N"));
    private const string Vmx = "uuid.bios = \"fixture\"\nethernet0.present = \"TRUE\"\nethernet0.connectionType = \"nat\"\n";
    public VmwareNetworkTransactionTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private (VmwareNetworkBackup Backup, string Journal) Plan()
    {
        string vmx = Path.Combine(root, "guest.vmx"), dhcp = Path.Combine(root, "dhcp.conf");
        string applied = VmwareVmx.Configure(Vmx, "VMnet2", out _); File.WriteAllText(vmx, Vmx); File.WriteAllText(dhcp, "old DHCP fixture");
        return (new("fixture-adapter", 17, false, [new(vmx, Vmx, applied), new(dhcp, "old DHCP fixture", "new DHCP fixture")], "prepared"), Path.Combine(root, "journal.json"));
    }
    [Fact] public void DhcpRestartFailureRestoresAllFilesForwardingAndDurableJournal()
    {
        var (backup, journal) = Plan(); int restarts = 0; var forwarding = new List<bool>();
        Assert.Throws<IOException>(() => VmwareNetwork.Apply(backup, journal, forwarding.Add, () => { if (++restarts == 1) throw new IOException("synthetic DHCP failure"); }));
        Assert.Equal(new[] { true, false }, forwarding); Assert.Equal(2, restarts);
        Assert.All(backup.Files, f => Assert.Equal(f.Original, File.ReadAllText(f.Path)));
        Assert.Equal("restored", JsonSerializer.Deserialize<VmwareNetworkBackup>(File.ReadAllText(journal), Json.Options)!.Phase);
    }
    [Fact] public void ExternalChangesAreNeverOverwrittenOrPartiallyRestored()
    {
        var (backup, journal) = Plan(); VmwareNetwork.Apply(backup, journal, _ => { }, () => { });
        File.WriteAllText(backup.Files[1].Path, "external DHCP edit");
        Assert.Throws<IOException>(() => VmwareNetwork.RestoreFiles(backup, journal, _ => throw new Exception("must not change forwarding"), () => throw new Exception("must not restart")));
        Assert.Equal(backup.Files[0].Applied, File.ReadAllText(backup.Files[0].Path));
        Assert.Equal("external DHCP edit", File.ReadAllText(backup.Files[1].Path));
        Assert.Equal("applied", JsonSerializer.Deserialize<VmwareNetworkBackup>(File.ReadAllText(journal), Json.Options)!.Phase);
    }
    [Fact] public void VmxChangesAfterPlanningAreRejectedBeforeAnyMutation()
    {
        var (backup, journal) = Plan(); File.AppendAllText(backup.Files[0].Path, "displayName = \"external edit\"\n");
        Assert.Throws<IOException>(() => VmwareNetwork.Apply(backup, journal, _ => throw new Exception("must not change forwarding"), () => throw new Exception("must not restart")));
        Assert.Equal(backup.Files[1].Original, File.ReadAllText(backup.Files[1].Path)); Assert.False(File.Exists(journal));
    }
}

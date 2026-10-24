using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class VmwareQuickSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vmware-quick-" + Guid.NewGuid());
    private const string Vmx = ".encoding = \"Big5\"\ndisplayName = \"測試虛擬機\"\nuuid.bios = \"fixture\"\nethernet0.present = \"TRUE\"\nethernet0.generatedAddress = \"00:0c:29:12:34:56\"\nethernet0.connectionType = \"nat\"\n";
    public VmwareQuickSetupTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string Machine() { string path = Path.Combine(root, "測試.vmx"); File.WriteAllBytes(path, VmwareVmx.TextEncoding(Vmx).GetBytes(Vmx)); return path; }
    [Fact] public void RegisteredInventoryFindsPoweredOffUnicodeVmsAndSkipsDeletedEntries()
    {
        string path = Machine(), inventory = Path.Combine(root, "inventory.vmls");
        string text = ".encoding = \"Big5\"\nvmlist1.config = \"" + path + "\"\nvmlist2.config = \"" + path.ToUpperInvariant() + "\"\nvmlist3.config = \"C:\\missing\\deleted.vmx\"\n";
        File.WriteAllBytes(inventory, VmwareVmx.TextEncoding(text).GetBytes(text));
        var result = Assert.Single(VmwareDiscovery.Registered(inventory)); Assert.Equal("測試虛擬機", result.Machine.Name); Assert.Null(result.Process);
    }
    [Fact] public void SavedPendingSetupWaitsForShutdownAndSurvivesRestartWithoutChangingVmx()
    {
        string path = Machine(); string lockPath = path + ".lck"; Directory.CreateDirectory(lockPath);
        var vm = VmwareVmx.Read(path);
        var pending = new VmwareSettings { Enabled = true, PendingPreparation = true, ByStartup = true, Machines = [new(path, "192.168.70.20", Guid.Empty) { Uuid = vm.Uuid, Mac = vm.Mac }] };
        var restored = JsonSerializer.Deserialize<VmwareSettings>(JsonSerializer.Serialize(pending))!;
        Assert.True(restored.PendingPreparation); Assert.False(VmwareSetupReadiness.CanPrepare(restored, out var waiting)); Assert.Contains("尚未代理", waiting);
        Assert.Equal(Vmx, VmwareVmx.ReadText(path)); Directory.Delete(lockPath);
        Assert.True(VmwareSetupReadiness.CanPrepare(restored, out _)); Assert.Equal(Vmx, VmwareVmx.ReadText(path));
    }
    [Fact] public void SuspendedOrChangedMachinesNeverPrepareInBackground()
    {
        string path = Machine(); var vm = VmwareVmx.Read(path);
        var settings = new VmwareSettings { Machines = [new(path, "192.168.70.20", Guid.Empty) { Uuid = vm.Uuid, Mac = vm.Mac }] };
        File.WriteAllBytes(path, VmwareVmx.TextEncoding(Vmx).GetBytes(Vmx + "checkpoint.vmState = \"suspended.vmss\"\n"));
        Assert.False(VmwareSetupReadiness.CanPrepare(settings, out _)); Assert.Throws<InvalidOperationException>(() => VmwareNetwork.RequirePoweredOff(path));
        File.WriteAllBytes(path, VmwareVmx.TextEncoding(Vmx).GetBytes(Vmx.Replace("fixture", "other")));
        Assert.Throws<InvalidOperationException>(() => VmwareSetupReadiness.CanPrepare(settings, out _));
    }
    [Fact] public void AutomaticIpSelectionAvoidsHostDynamicLeasesAndExistingReservations()
    {
        const string text = "subnet 192.168.70.0 netmask 255.255.255.0 { range 192.168.70.23 192.168.70.254; }\nhost other { hardware ethernet 00:0c:29:aa:bb:cc; fixed-address 192.168.70.20; }\n";
        Assert.Equal(["192.168.70.21", "192.168.70.22"], VmwareDhcp.AvailableAddresses(text, "192.168.70.1", ["00:0c:29:12:34:56", "00:0c:29:12:34:57"]));
        Assert.Throws<InvalidOperationException>(() => VmwareDhcp.AvailableAddresses(text, "192.168.70.1", ["00:0c:29:aa:bb:cc"]));
    }
    [Fact] public async Task PendingSetupPersistsThroughServiceRestartAndPauseCancelsItWithoutNetworkChanges()
    {
        string path = Machine(); Directory.CreateDirectory(path + ".lck"); var vm = VmwareVmx.Read(path);
        File.WriteAllText(Path.Combine(root, "owner.sid"), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        var store = new ConfigStore(root);
        store.Save(new SavedConfig { Vmware = new() { PendingPreparation = true, Machines = [new(path, "192.168.70.20", Guid.Empty) { Uuid = vm.Uuid, Mac = vm.Mac }] } });
        using (var controller = new Controller(root)) {
            await controller.Tick(default);
            var snapshot = Json.Read<Snapshot>((await controller.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.True(snapshot.Vmware!.Settings.PendingPreparation); Assert.Contains("尚未代理", snapshot.Vmware.Detail);
            Assert.True((await controller.Dispatch(Command.Create("pauseVmware", new { }), default)).Ok);
        }
        Assert.False(store.Load().Vmware.PendingPreparation);
        using (var restarted = new Controller(root)) {
            await restarted.Tick(default);
            var snapshot = Json.Read<Snapshot>((await restarted.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            Assert.False(snapshot.Vmware!.Settings.PendingPreparation); Assert.False(snapshot.Vmware.Settings.Enabled);
        }
        Assert.Equal(Vmx, VmwareVmx.ReadText(path)); Assert.False(File.Exists(Path.Combine(root, "vmware-network-backup.json")));
    }
}

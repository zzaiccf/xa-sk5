namespace WindowSocks.Core;

public static class VmwareSetupReadiness
{
    public static bool CanPrepare(VmwareSettings settings, out string detail)
    {
        foreach (var mapping in settings.Machines) {
            var vm = VmwareVmx.Read(mapping.VmxPath);
            if (vm.Uuid != mapping.Uuid || vm.Mac != mapping.Mac) throw new InvalidOperationException("虛擬機身分在儲存後變更，請重新選取。");
            if (VmwareVmx.Parse(VmwareVmx.ReadText(vm.Path)).GetValueOrDefault("checkpoint.vmState") is { Length: > 0 }) {
                detail = "已儲存；請將暫停的 VM 恢復後正常關機一次，目前尚未代理。"; return false;
            }
            if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(vm.Path)!, "*.lck").Any()) {
                detail = "已儲存；等待所選 VM 正常關機一次，目前尚未代理。"; return false;
            }
        }
        detail = ""; return true;
    }
}

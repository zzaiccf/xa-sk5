namespace WindowSocks.Core;

public static class SandboxCompatibility
{
    public static string PreferLauncher(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.Equals("gameclient.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("YongOrigin.exe", StringComparison.OrdinalIgnoreCase)) return path;
        string launcher = Path.Combine(Path.GetDirectoryName(path) ?? "", "Launcher.exe");
        return File.Exists(launcher) && IsYongOrigin(launcher) ? launcher : path;
    }
    public static bool IsYongOrigin(string launcher)
    {
        if (!Path.IsPathFullyQualified(launcher)) return false;
        string root = Path.GetDirectoryName(launcher)!;
        return Path.GetFileName(launcher).Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(root, "gameclient.exe")) && File.Exists(Path.Combine(root, "YongOrigin.EXE"));
    }
    public static Dictionary<string, string[]> Settings(SandboxInstanceProfile p)
    {
        SandboxInstances.Validate(p);
        var values = new Dictionary<string, string[]> {
            ["Enabled"] = ["y"], ["ConfigLevel"] = ["10"], ["FileRootPath"] = [p.FileRoot],
            ["HideOtherBoxes"] = ["y"], ["AutoDelete"] = ["n"],
            ["OpenPipePath"] = [@"WindowSocks.Tools.exe,\Device\NamedPipe\WindowSocks.Sandbox.v1"],
            ["FakeAdminRights"] = [p.SimulateAdmin ? "y" : "n"], ["BoxNameTitle"] = ["y"], ["BoxAlias"] = [p.Name],
            ["BorderColor"] = [p.Name.EndsWith(" B", StringComparison.Ordinal) ? "#E88B24,on,3" : "#4070FF,on,3"]
        };
        // Null preserves older manually configured boxes until the user saves a preset.
        if (p.IsolateDeviceIdentity == true) {
            string id = p.Id.ToString("N").ToUpperInvariant();
            values["DiskSerialNumber"] = [id[..4] + "-" + id[4..8]];
            values["NetworkAdapterMAC"] = Enumerable.Range(0, 32).Select(i => $"{i},02-{id[..2]}-{id[2..4]}-{id[4..6]}-{id[6..8]}-{i:X2}").ToArray();
            values["HideDiskSerialNumber"] = ["y"]; values["HideNetworkAdapterMAC"] = ["y"];
        } else if (p.IsolateDeviceIdentity == false) {
            values["HideDiskSerialNumber"] = ["n"]; values["HideNetworkAdapterMAC"] = ["n"];
        }
        return values;
    }
}

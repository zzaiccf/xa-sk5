namespace WindowSocks.Core;

public static class SandboxCompatibility
{
    private static readonly string[] BorderColors = ["#4070FF", "#E88B24", "#229954", "#9B59B6", "#E74C3C", "#00A6A6", "#D4AC0D", "#C75498", "#607D8B", "#795548"];
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
            ["BorderColor"] = [BorderColors[p.Name.Length >= 2 && p.Name[^2] == ' ' && p.Name[^1] is >= 'A' and <= 'J' ? p.Name[^1] - 'A' : 0] + ",on,3"]
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

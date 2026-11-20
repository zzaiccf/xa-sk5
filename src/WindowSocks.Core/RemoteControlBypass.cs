using System.Net;
using System.Net.Sockets;

namespace WindowSocks.Core;

public static class RemoteControlBypass
{
    public static IReadOnlyList<string> ExecutableNames { get; } = Array.AsReadOnly(new[] {
        "AnyDesk.exe", "remoting_host.exe", "remote_assistance_host.exe", "DeskIn.exe", "DeskInService.exe", "RdViewer.exe", "RdServer.exe",
        "TeamViewer.exe", "TeamViewer_Service.exe", "TeamViewer_Desktop.exe", "TeamViewer_Host.exe", "TeamViewerQS.exe",
        "rustdesk.exe", "RDxaomao.exe", "RDxaomaoHost.exe", "RDxaomaoControl.exe", "HopToDesk.exe", "ToDesk.exe", "ToDesk_Service.exe", "ToDesk_Session.exe", "ToDesk_Lite.exe",
        "SunloginClient.exe", "SunloginClient_Desktop.exe", "SunloginRemote.exe", "SunloginClientLite.exe", "AweSun.exe",
        "mstsc.exe", "msrdc.exe", "RemoteDesktop.exe", "WindowsApp.exe", "parsecd.exe", "parsec.exe",
        "SRManager.exe", "SRServer.exe", "SRService.exe", "strwinclt.exe", "SplashtopSOS.exe",
        "winvnc.exe", "vncserver.exe", "vncviewer.exe", "tvnserver.exe", "tvnviewer.exe",
        "ScreenConnect.ClientService.exe", "ScreenConnect.WindowsClient.exe", "UltraViewer_Desktop.exe", "UltraViewer_Service.exe"
    });
    public static string[] NormalizeCustom(IEnumerable<string> paths)
    {
        var input = paths.ToArray();
        if (input.Length > 32) throw new InvalidOperationException("自訂遠端程式最多 32 個。");
        foreach (var path in input)
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.IndexOfAny(['*', '?', '\0', '\r', '\n']) >= 0 ||
                !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自訂遠端程式請選擇本機 .exe 的完整路徑，不支援萬用字元。");
        return input.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private const string RdHost = "RDxaomaoHost.exe";
    public static string NativeImages(IEnumerable<string> customPaths) =>
        PolicyImages(new() { RemoteControlExecutables = customPaths.ToArray() });

    public static string PolicyImages(GlobalProxySettings settings)
    {
        var paths = NormalizeCustom(settings.RemoteControlExecutables);
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.BypassRemoteControl)
            images.UnionWith(ExecutableNames.Concat(paths).Where(p => !Path.GetFileName(p).Equals(RdHost, StringComparison.OrdinalIgnoreCase)));
        if (settings.RdHostDirect) {
            images.Add(RdHost);
            // The host ships its own broker. Never exempt the Windows broker or unrelated copies.
            var installs = paths.Where(p => Path.GetFileName(p).Equals(RdHost, StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetDirectoryName(p)!)
                .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDxaomaoHost"));
            foreach (string dir in installs) images.Add(Path.Combine(dir, "RuntimeBroker_rustdesk.exe"));
        }
        return string.Join('\0', images) + "\0\0";
    }
    public static string? PinnedAddressForPolicyChange(GlobalProxySettings previous, GlobalProxySettings next)
    {
        bool changed = previous.BypassLan != next.BypassLan || previous.BypassRemoteControl != next.BypassRemoteControl || previous.RdHostDirect != next.RdHostDirect ||
            !previous.RemoteControlExecutables.SequenceEqual(next.RemoteControlExecutables, StringComparer.OrdinalIgnoreCase);
        return previous.Enabled && previous.ProxyId == next.ProxyId && changed &&
            IPAddress.TryParse(previous.ResolvedHost, out var address) && address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : null;
    }
}

using System.Text.RegularExpressions;

namespace WindowSocks.Core;

public sealed record SandboxBrokerRequest(string Action, string Box = "", int Pid = 0, SandboxInstanceProfile? Profile = null, string OwnerSid = "")
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
}
public sealed record SandboxBrokerReply(Guid RequestId, Reply Reply);

public sealed record SandboxInstanceProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string BoxName { get; init; } = "";
    public string LauncherPath { get; init; } = "";
    public string SourceLauncherPath { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string FileRoot { get; init; } = "";
    public Guid ProxyId { get; init; }
    public bool Enabled { get; init; }
    public bool SimulateAdmin { get; init; }
    public bool? IsolateDeviceIdentity { get; init; }
    public string TargetKey => "sandbox:" + Id.ToString("N");
}
public sealed record SandboxInstanceStatus(Guid Id, string State, string Detail, ProcessIdentity[] Processes)
{
    public SandboxNetworkReport? Network { get; init; }
}
public sealed record SandboxRequest(Guid Id);
public sealed record SandboxLaunchTicket(SandboxInstanceProfile Instance, string Token);
public sealed record SandboxRegistration(Guid Id, string Token, ProcessIdentity Process);
public sealed record SandboxLaunchReport(Guid Id, bool Started, string Detail);
public sealed record SandboxNetworkReport(Guid Id, string? TcpExit, string? UdpExit, bool DirectDns);
public sealed record SandboxLaunchPermission(string LauncherPath, string WorkingDirectory, string FileRoot);
public sealed record SandboxProcess(ProcessIdentity Identity, string BoxName, string OwnerSid, uint SessionId);

public static class SandboxInstances
{
    // MSIX-hosted shells can redirect LocalAppData into a private package cache, invisible to the service.
    public static string StorageRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WindowSocksGames", "SandboxGames");
    public static SandboxInstanceProfile Create(string name, string launcher, Guid proxy)
    {
        var id = Guid.NewGuid(); string root = Path.Combine(StorageRoot, id.ToString("N"));
        return new() { Id = id, Name = name, BoxName = "Xa_" + id.ToString("N")[..24], ProxyId = proxy,
            SourceLauncherPath = Path.GetFullPath(launcher), LauncherPath = Path.Combine(root, "Game", Path.GetFileName(launcher)), WorkingDirectory = Path.Combine(root, "Game"), FileRoot = Path.Combine(root, "Box") };
    }
    public static bool Within(string file, string root) => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void Validate(SandboxInstanceProfile profile)
    {
        if (profile.Id == Guid.Empty || profile.ProxyId == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Name.Any(char.IsControl))
            throw new InvalidOperationException("沙盒名稱或線路無效。");
        if (!Regex.IsMatch(profile.BoxName, "^Xa_[0-9a-f]{24}$") || profile.BoxName != "Xa_" + profile.Id.ToString("N")[..24])
            throw new InvalidOperationException("沙盒身分不正確。");
        foreach (string path in new[] { profile.LauncherPath, profile.SourceLauncherPath, profile.WorkingDirectory, profile.FileRoot })
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.Contains('"') || path.Contains('\n') || path.Contains('\r') || path.Contains('%') || path.Length > 230)
                throw new InvalidOperationException("沙盒路徑必須是有效的完整本機路徑（最多 230 字）。");
        string root = Path.GetDirectoryName(Path.GetFullPath(profile.WorkingDirectory))!;
        if (Path.GetFileName(root) != profile.Id.ToString("N") || !Path.GetFullPath(profile.WorkingDirectory).Equals(Path.Combine(root, "Game"), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(profile.FileRoot).Equals(Path.Combine(root, "Box"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("沙盒副本必須使用此實例專屬的 Game 與 Box 目錄。");
        if (!Within(profile.LauncherPath, profile.WorkingDirectory) || !new[] { ".exe", ".bin" }.Contains(Path.GetExtension(profile.LauncherPath), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("啟動器必須位於沙盒遊戲副本內。");
        if (Within(profile.FileRoot, profile.WorkingDirectory) || Within(profile.WorkingDirectory, profile.FileRoot) || profile.WorkingDirectory.Equals(profile.FileRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("遊戲副本與沙盒寫入目錄必須分開。");
    }
    public static ProcessIdentity[] VerifiedMembers(SandboxInstanceProfile profile, IEnumerable<SandboxProcess> observed, string sid,
        Func<int, ProcessIdentity?> identity) => observed.Where(p => p.BoxName.Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase) &&
            p.OwnerSid.Equals(sid, StringComparison.OrdinalIgnoreCase) && identity(p.Identity.Pid) == p.Identity).Select(p => p.Identity).Distinct().ToArray();
    public static bool ExitMatches(SandboxNetworkReport report, ProbeResult? probe) => probe is { Ready: true } &&
        !string.IsNullOrEmpty(report.TcpExit) && !string.IsNullOrEmpty(report.UdpExit) && report.TcpExit == probe.TcpExit && report.UdpExit == probe.UdpExit;
}

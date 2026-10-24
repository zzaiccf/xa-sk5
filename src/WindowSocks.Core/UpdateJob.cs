namespace WindowSocks.Core;

public sealed record UpdateJob(string Payload, string Target, string Version, string RestartExe, int ParentPid, long ParentStartTime, bool IncludeService, bool Recover = false)
{
    public UpdateDownload? Download { get; init; }
    public bool PauseService { get; init; }
}
public sealed record UpdateDownload(AppRelease Release, string? Source = null, string? LocalArchive = null);
public sealed record UpdateResult(bool Success, bool Recovered, string Message, string[] Backups, DateTimeOffset At);
public static class UpdatePaths
{
    public static string Installed => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowSocks");
    public static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks");
    public static string ServiceData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowSocks");
    public static string Preferences => Path.Combine(UserData, "update-preferences.json");
    public static bool ServiceInstalled => File.Exists(Path.Combine(Installed, "WindowSocks.Service.exe"));
    public static bool ServiceRegistered { get { using var service = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WindowSocks"); return service is not null; } }
}

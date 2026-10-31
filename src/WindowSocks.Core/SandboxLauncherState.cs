namespace WindowSocks.Core;

public static class SandboxLauncherState
{
    public static bool IsStarted(SandboxInstanceStatus status, SandboxInstanceProfile profile,
        Func<int, string> imagePath, Func<int, ProcessIdentity?> identity)
    {
        if (status.State != "代理執行中") return false;
        if (status.LauncherStarted.HasValue) return status.LauncherStarted.Value;
        // Legacy service has no acknowledgement field. Its verified members plus
        // an actual launcher/game executable form the fallback, never UI prose.
        return status.Network is not null && status.Processes.Any(p => {
            if (identity(p.Pid) != p) return false;
            string path = imagePath(p.Pid);
            return path.Length > 0 && (SandboxInstances.Within(path, profile.WorkingDirectory) || SandboxInstances.Within(path, profile.FileRoot));
        });
    }
}

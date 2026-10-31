namespace WindowSocks.Core;

public static class SandboxNetworkRules
{
    public static NativeEngine.Rule Build(SandboxInstanceProfile profile, ProcessIdentity process, uint proxy,
        bool enabled, bool conflicted, bool proxyReady) => new() {
        Pid = (uint)process.Pid, Created = (ulong)process.CreatedFileTime, Proxy = profile.UseLocalNetwork ? 0 : proxy,
        // Explicit direct (2) bypasses the global fallback; guard exemption (3) does not.
        // Retain ancestry, no-Job and driver-health flags for BOTH network modes.
        Blocked = !enabled || conflicted ? 1u : profile.UseLocalNetwork ? profile.ProxyId == Guid.Empty ? 2u : 1u : proxyReady && proxy != 0 ? 0u : 1u, Reserved = 13
    };
}

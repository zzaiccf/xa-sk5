namespace WindowSocks.Core;

public static class ProxyHealth
{
    public static bool CanRoute(ProbeResult? latest, DateTimeOffset? lastSuccess, int failures, DateTimeOffset now)
    {
        if (latest is null || now - latest.At >= ProbeTiming.HealthValidity) return false;
        if (latest.Ready) return true;
        // Grace protects an already verified session; a first failed check must never open quarantine.
        return lastSuccess is { } success && now - success < ProbeTiming.HealthValidity && failures < 2;
    }
}

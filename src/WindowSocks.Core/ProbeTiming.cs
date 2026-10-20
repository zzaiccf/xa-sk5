namespace WindowSocks.Core;

public static class ProbeTiming
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    // Keep verified routes alive through one failed scheduled check and its retry, without a direct fallback.
    public static readonly TimeSpan HealthValidity = RefreshInterval + RetryInterval + TimeSpan.FromMinutes(2);
    public static TimeSpan NextInterval(bool ready) => ready ? RefreshInterval : RetryInterval;
    public static bool IsStale(DateTimeOffset at, DateTimeOffset now) => now - at >= RefreshInterval;
}

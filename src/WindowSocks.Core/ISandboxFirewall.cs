namespace WindowSocks.Core;

public sealed record SandboxFirewallStatus(uint Blocks, uint Permits, string Detail);
public interface ISandboxFirewall : IDisposable
{
    void ReplaceImages(string[] images);
    void WithdrawPermit();
    void AllowWhileHealthy();
    SandboxFirewallStatus Inspect();
    void Clear();
}

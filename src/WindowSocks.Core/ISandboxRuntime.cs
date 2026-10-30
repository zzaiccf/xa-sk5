namespace WindowSocks.Core;

public interface ISandboxRuntime : IDisposable
{
    SandboxProcess[] Processes(string box);
    SandboxProcess? Query(int pid);
    void TerminateOwned(SandboxInstanceProfile profile, string ownerSid);
    void ValidateSettings(SandboxInstanceProfile profile);
    void VerifyCompatibility();
    bool IdentityAvailable { get; }
}

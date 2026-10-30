using System.Reflection;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ReviewGlobalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-review-global-" + Guid.NewGuid());
    private Controller? controller;
    private SavedConfig config = new();
    private readonly ProxyProfile global = new() { Host = "127.0.0.1", Port = 1080 };
    private readonly ProxyProfile assigned = new() { Host = "127.0.0.1", Port = 1081 };
    private object? Invoke(string method, params object[] args) => typeof(Controller).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, args);
    private T Field<T>(string name) => (T)typeof(Controller).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
    private void Setup(bool sandbox, bool sameExit = false)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        config = new() { Proxies = [global, assigned], GlobalProxy = new() { Enabled = true, ProxyId = global.Id, ResolvedHost = "127.0.0.1" } };
        if (sandbox) config.SandboxInstances = [SandboxInstances.Create("fixture", @"C:\SyntheticGame\Launcher.exe", assigned.Id)];
        else config.Bindings = [new() { TargetKey = "later", Kind = "Process", ProxyId = assigned.Id }];
        new ConfigStore(root).Save(config);
        controller = new(root, sandboxRuntime: new EmptyRuntime());
        config = Field<SavedConfig>("config");
        Field<Dictionary<Guid, ProbeResult>>("probes")[global.Id] = Probe(global.Id, "192.0.2.1");
        Field<Dictionary<Guid, ProbeResult>>("probes")[assigned.Id] = Probe(assigned.Id, sameExit ? "192.0.2.1" : "192.0.2.2");
    }
    private static ProbeResult Probe(Guid id, string exit) => new(id, true, true, true, true, exit, exit, 1, "fixture", DateTimeOffset.UtcNow);
    [Fact] public void R1_PreexistingGlobalAndDifferentSandboxNodeKeepGlobalEnabled()
    {
        Setup(true);
        Assert.Equal(1u, Invoke("GlobalPolicyState"));
        var ledger = ProxyOwnershipLedger.From(config);
        ledger.Require(global.Id, "global", "Global");
        ledger.Require(assigned.Id, config.SandboxInstances[0].TargetKey, "Sandbox");
        Assert.Throws<InvalidOperationException>(() => ledger.Require(assigned.Id, "global", "Global"));
        Assert.Throws<InvalidOperationException>(() => ledger.Require(global.Id, "new-box", "Sandbox"));
    }
    [Fact] public void R2_LaterBindingWithSameExitCannotDisableGlobalTraffic()
    {
        Setup(false, sameExit: true);
        Assert.Equal(1u, Invoke("GlobalPolicyState"));
        Assert.Equal(true, Invoke("DuplicateExit", assigned.Id));
        Assert.Equal(false, Invoke("DuplicateExit", global.Id));
    }
    [Fact] public async Task R2_EnablingConflictingGlobalIsRejectedBeforeChangingExistingPolicy()
    {
        Setup(false, sameExit: true);
        config.GlobalProxy = config.GlobalProxy with { Enabled = false };
        var response = await controller!.Dispatch(Command.Create("setGlobalProxy", config.GlobalProxy with { Enabled = true }), default);
        Assert.False(response.Ok);
        Assert.Contains("出口", response.Error);
        Assert.False(config.GlobalProxy.Enabled);
    }
    public void Dispose()
    {
        controller?.Dispose();
        NativeEngine.Core_ReplacePolicy([], 0, "\0\0");
        Directory.Delete(root, true);
    }
    private sealed class EmptyRuntime : ISandboxRuntime
    {
        public SandboxProcess[] Processes(string box) => [];
        public SandboxProcess? Query(int pid) => null;
        public void ValidateSettings(SandboxInstanceProfile profile) { }
        public void VerifyCompatibility() { }
        public bool IdentityAvailable => true;
        public void TerminateOwned(SandboxInstanceProfile profile, string sid) { }
        public void Dispose() { }
    }
}

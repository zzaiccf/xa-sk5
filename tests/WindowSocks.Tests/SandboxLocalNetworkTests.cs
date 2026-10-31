using System.Text.Json;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxLocalNetworkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xa-local-tests-" + Guid.NewGuid());
    private static SandboxInstanceProfile Local(string name = "A") => SandboxInstances.Create(name, @"C:\Synthetic\Launcher.exe", Guid.Empty) with { UseLocalNetwork = true };
    [Fact] public void ExplicitLocalNeedsNoProxyAndMultipleLocalsDoNotConflict()
    {
        SandboxPreparation.ValidateBatch([Local(), Local("B"), SandboxInstances.Create("C", @"C:\Synthetic\Launcher.exe", Guid.NewGuid())]);
    }
    [Fact] public void MissingProxyNeverImplicitlyMeansLocalAndLegacyProfilesRemainProxied()
    {
        var missing = Local() with { UseLocalNetwork = false };
        Assert.Throws<InvalidOperationException>(() => SandboxInstances.Validate(missing));
        var legacy = JsonSerializer.Deserialize<SandboxInstanceProfile>("{\"proxyId\":\"11111111-1111-1111-1111-111111111111\"}", Json.Options)!;
        Assert.False(legacy.UseLocalNetwork);
        Assert.Throws<InvalidOperationException>(() => SandboxInstances.Validate(Local() with { ProxyId = Guid.NewGuid() }));
    }
    [Fact] public void ResumeMixedModesRetainsIdentitiesAndReleasesLocalProxyReservation()
    {
        var a = SandboxInstances.Create("A", @"C:\Synthetic\Launcher.exe", Guid.NewGuid());
        var b = SandboxInstances.Create("B", a.SourceLauncherPath, Guid.NewGuid());
        var profiles = SandboxPreparation.ResumeNetwork(a.SourceLauncherPath, [new SandboxNetworkChoice(Guid.Empty, true), new(b.ProxyId)], [b, a], root);
        Assert.Equal(a.Id, profiles[0].Id); Assert.True(profiles[0].UseLocalNetwork); Assert.Equal(Guid.Empty, profiles[0].ProxyId);
        Assert.Equal(b.Id, profiles[1].Id); Assert.False(profiles[1].UseLocalNetwork); Assert.Equal(b.ProxyId, profiles[1].ProxyId);
        var config = new SavedConfig { SandboxInstances = profiles.ToList() };
        Assert.DoesNotContain(ProxyOwnershipLedger.From(config).Entries, p => p.TargetKey == a.TargetKey);
        var restored = SandboxPreparation.ResumeNetwork(a.SourceLauncherPath, [new SandboxNetworkChoice(a.ProxyId), new(b.ProxyId)], profiles, root);
        Assert.False(restored[0].UseLocalNetwork); Assert.Equal(a.Id, restored[0].Id); Assert.Equal(a.ProxyId, restored[0].ProxyId);
    }
    [Fact] public void LocalRuleIsExplicitDirectButRetainsBirthAndSandboxGuardFlags()
    {
        var rule = SandboxNetworkRules.Build(Local(), new(123, 456), 0, true, false, false);
        Assert.Equal(2u, rule.Blocked); Assert.Equal(0u, rule.Proxy); Assert.Equal(13u, rule.Reserved);
        Assert.Equal(123u, rule.Pid); Assert.Equal(456ul, rule.Created);
        Assert.Equal(1u, SandboxNetworkRules.Build(Local(), new(123, 456), 0, false, false, false).Blocked);
        Assert.Equal(1u, SandboxNetworkRules.Build(Local(), new(123, 456), 0, true, true, false).Blocked);
    }
    [Fact] public void ProxyFailureNeverFallsBackToLocal()
    {
        var proxy = Local() with { UseLocalNetwork = false, ProxyId = Guid.NewGuid() };
        Assert.Equal(1u, SandboxNetworkRules.Build(proxy, new(123, 456), 7, true, false, false).Blocked);
        Assert.Equal(0u, SandboxNetworkRules.Build(proxy, new(123, 456), 7, true, false, true).Blocked);
    }
    [Fact] public void LocalLauncherAcknowledgementIsNotLabeledAsProxy()
    {
        var profile = Local(); var status = new SandboxInstanceStatus(profile.Id, "本地網路執行中", "", []) { LauncherStarted = true };
        Assert.True(SandboxLauncherState.IsStarted(status, profile, _ => "", _ => null));
        Assert.False(SandboxLauncherState.IsStarted(status with { State = "已暫停 · 已封鎖" }, profile, _ => "", _ => null));
        var snapshot = new Snapshot([], [], [], [], [], new(false, false, false, false, "fixture", "", [])) { SandboxInstances = [profile] };
        var row = Assert.Single(ProxyRules.List(snapshot)); Assert.Contains("本地網路", row.Allocation); Assert.DoesNotContain("專用 IP", row.Capacity);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

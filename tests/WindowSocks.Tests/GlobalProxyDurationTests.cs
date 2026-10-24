using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Desktop;
using Xunit;

namespace WindowSocks.Tests;

public sealed class GlobalProxyDurationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid First = Guid.NewGuid(), Second = Guid.NewGuid();

    [Fact] public void SameRouteRetainsStartAndChangingRouteStartsAnotherSession()
    {
        var tracker = new GlobalProxySessionTracker();
        tracker.Synchronize(null, Start); Assert.Null(tracker.ConnectedAt);
        tracker.Synchronize(First, Start);
        tracker.Synchronize(First, Start.AddHours(2)); Assert.Equal(Start, tracker.ConnectedAt);
        tracker.Synchronize(Second, Start.AddHours(3)); Assert.Equal(Start.AddHours(3), tracker.ConnectedAt);
    }
    [Fact] public void AnyRoutingInterruptionClearsStartAndRestorationStartsFresh()
    {
        var tracker = new GlobalProxySessionTracker();
        tracker.Synchronize(First, Start); tracker.Synchronize(null, Start.AddSeconds(15));
        Assert.Null(tracker.ConnectedAt);
        tracker.Synchronize(First, Start.AddMinutes(1)); Assert.Equal(Start.AddMinutes(1), tracker.ConnectedAt);
        Assert.Null(new GlobalProxySessionTracker().ConnectedAt);
    }
    private static Snapshot Active() => new([], [], [], [], [], new(true, true, true, true, "0.1.24", "", [])) {
        GlobalProxy = new() { Enabled = true, ProxyId = First }, GlobalProxyRouting = true, GlobalProxyConnectedAt = Start
    };
    [Fact] public void ServiceTimestampSurvivesSerializationAndDesktopReopening()
    {
        var snapshot = Json.Read<Snapshot>(JsonSerializer.SerializeToElement(Active(), Json.Options));
        Assert.Equal(Start, snapshot.GlobalProxyConnectedAt);
        Assert.Equal("01:02:03", GlobalProxyDisplay.Duration(snapshot, true, Start.AddHours(1).AddMinutes(2).AddSeconds(3)));
        Assert.Equal("02:00:00", GlobalProxyDisplay.Duration(snapshot, true, Start.AddHours(2)));
        Assert.Equal("1 天 01:00:00", GlobalProxyDisplay.Duration(snapshot, true, Start.AddHours(25)));
        Assert.Equal("00:00:00", GlobalProxyDisplay.Duration(snapshot, true, Start.AddMinutes(-1)));
    }
    [Fact] public void DisconnectionBlockedRoutingAndStoppedCoreCannotKeepCountingCachedTimestamp()
    {
        Assert.Equal("未確認", GlobalProxyDisplay.Duration(Active(), false, Start.AddHours(2)));
        Assert.Equal("未確認", GlobalProxyDisplay.Duration(Active() with { Diagnostics = Active().Diagnostics with { ServiceOnline = false } }, true, Start));
        Assert.Equal("—", GlobalProxyDisplay.Duration(Active() with { GlobalProxyRouting = false }, true, Start));
        Assert.Equal("—", GlobalProxyDisplay.Duration(Active() with { Diagnostics = Active().Diagnostics with { EngineRunning = false } }, true, Start));
        Assert.Equal("—", GlobalProxyDisplay.Duration(Active() with { GlobalProxy = new() }, true, Start));
    }
    [Fact] public void OlderServicesDoNotFabricateAnInterfaceLocalStartTime()
    {
        Assert.Equal("待服務提供", GlobalProxyDisplay.Duration(Active() with { GlobalProxyConnectedAt = null }, true, Start));
        Assert.Equal("待服務提供", GlobalProxyDisplay.Duration(Active() with { GlobalProxyRouting = null }, true, Start));
        var legacy = Json.Read<Snapshot>(JsonSerializer.SerializeToElement(new Snapshot([], [], [], [], [], new(true, true, true, true, "0.1.23", "", [])), Json.Options));
        Assert.Null(legacy.GlobalProxyConnectedAt);
    }
}

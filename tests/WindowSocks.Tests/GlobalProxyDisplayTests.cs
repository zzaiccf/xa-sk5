using WindowSocks.Core;
using WindowSocks.Desktop;
using Xunit;

namespace WindowSocks.Tests;

public sealed class GlobalProxyDisplayTests
{
    private readonly ProxyProfile profile = new() { Name = "測試線路", Host = "192.0.2.10", Port = 1080, Username = "demo" };
    private Snapshot State(bool engine = true, bool? routing = true) => new([profile], [], [], [], [], new(true, true, true, engine, "0.1.18", "", [])) {
        GlobalProxy = new() { Enabled = true, ProxyId = profile.Id }, GlobalProxyRouting = routing,
        SupportsRemoteControlBypass = true,
        GlobalProxyStatus = $"全局：{profile.Name} · 代理執行中 · IPv4 TCP／UDP"
    };

    [Fact] public void AnEnabledSettingWithAStoppedCoreNeverAppearsActive()
    {
        var state = GlobalProxyDisplay.From(State(engine: false), true);
        Assert.True(state.ShowBanner); Assert.True(state.CanStop);
        Assert.Equal("全局尚未生效", state.Title);
        Assert.Contains("待核心啟用", state.RemotePolicy);
    }
    [Fact] public void ServiceRoutingStateOverridesOldStatusTextAndTheLatestIncompleteProbe()
    {
        Assert.Equal("全局外網已封鎖", GlobalProxyDisplay.From(State(routing: false), true).Title);
        var grace = State() with { Probes = [new(profile.Id, true, true, true, false, "203.0.113.20", null, 20, "補測中", DateTimeOffset.UtcNow) { UdpPending = true }] };
        Assert.Equal("全局代理中", GlobalProxyDisplay.From(grace, true).Title);
    }
    [Fact] public void DisconnectShowsUnknownAndClearlyLabelsCachedDetailsUntilReconnected()
    {
        var previous = State() with { Probes = [new(profile.Id, true, true, true, true, "203.0.113.20", "203.0.113.20", 20, "", DateTimeOffset.UtcNow)] };
        var offline = new Snapshot([], [], [], [], [], new(false, false, false, false, "", "", []));
        var display = GlobalProxyDisplay.From(offline, false, previous);
        Assert.Equal("全局狀態未確認", display.Title); Assert.True(display.ShowBanner); Assert.True(display.ShowNode); Assert.False(display.CanStop);
        Assert.Equal("上次實際 IP", display.ExitLabel); Assert.Equal("203.0.113.20", display.ExitIp);
        Assert.Contains("上次", display.RemotePolicy);
        var coldOffline = GlobalProxyDisplay.From(offline, false);
        Assert.Equal("全局狀態未確認", coldOffline.Title); Assert.False(coldOffline.ShowBanner);
        var disabled = State() with { GlobalProxy = new() };
        var connected = GlobalProxyDisplay.From(disabled, true, previous);
        Assert.Equal("全局未開啟", connected.Title); Assert.False(connected.ShowBanner); Assert.False(connected.CanStop);
    }
    [Fact] public void OldServicesRemainSupportedButNodeNamesAndUnknownMessagesCannotPretendToBeActive()
    {
        Assert.Equal("全局代理中", GlobalProxyDisplay.From(State(routing: null), true).Title);
        var blocked = State(routing: null) with { GlobalProxyStatus = $"全局：{profile.Name} · 代理未通過檢測，外網保持封鎖 · IPv4 TCP／UDP" };
        Assert.Equal("全局外網已封鎖", GlobalProxyDisplay.From(blocked, true).Title);
        var unknown = State(routing: null) with { GlobalProxyStatus = "未知狀態：節點名稱是代理執行中" };
        Assert.Equal("全局狀態未確認", GlobalProxyDisplay.From(unknown, true).Title);
        Assert.Null(Json.Read<Snapshot>(System.Text.Json.JsonSerializer.SerializeToElement(unknown, Json.Options)).GlobalProxyRouting);
    }
    [Fact] public void RemoteBypassIsNeverReportedAsWorkingWithoutCoreSupport()
    {
        Assert.Contains("待更新核心", GlobalProxyDisplay.From(State() with { SupportsRemoteControlBypass = false }, true).RemotePolicy);
        Assert.Contains("關閉", GlobalProxyDisplay.From(State() with { GlobalProxy = new() { Enabled = true, ProxyId = profile.Id, BypassRemoteControl = false } }, true).RemotePolicy);
    }
    [Fact] public void WarningsEscalateAtThreeDaysTomorrowAndExpiryAndOnlyConfirmLocalAfterServiceCutover()
    {
        var date = new DateOnly(2030, 4, 8);
        profile.ExpiresOn = date;
        var state = State() with { SupportsGlobalExpiryFallback = true };
        ProxyExpiryNotice Notice(int remaining) => Assert.Single(ProxyExpiryNotice.Collect(state, date.AddDays(-remaining)));
        Assert.Empty(ProxyExpiryNotice.Collect(state, date.AddDays(-4)));
        Assert.Equal(Notice(3).Stage, Notice(2).Stage);
        Assert.NotEqual(Notice(1).Stage, Notice(2).Stage);
        Assert.Contains("00:00", Notice(1).Message);
        Assert.Contains("等候", Notice(0).Message);
        state = state with { SupportsGlobalExpiryFallback = false };
        Assert.Contains("請停用", Notice(0).Message);
        state = state with { GlobalProxy = state.GlobalProxy with { Enabled = false, ExpiryFallbackOn = date } };
        Assert.Contains("已切換本地網路", Notice(0).Message);
        state = state with { GlobalProxy = new() };
        Assert.DoesNotContain("切換本地網路", Notice(0).Message);
        profile.ExpiresOn = null;
        Assert.Empty(ProxyExpiryNotice.Collect(state, date));
    }
}

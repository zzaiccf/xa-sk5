using System.Text.Json;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class IdleTcpProbeTests
{
    [Fact] public async Task SameProxyTunnelCanEchoAgainAfterIdle()
    {
        await using var server = new SocksFixture(echoTcp: true);
        var result = await IdleTcpProbe.RunAsync(server.Profile, 1, "198.51.100.8", 4242);
        Assert.True(result.Connected); Assert.True(result.InitialEcho); Assert.True(result.IdleCompleted);
        Assert.True(result.FinalEcho, result.Message); Assert.False(result.UpstreamEof); Assert.Equal("Passed", result.Stage);
        Assert.True(result.IdleObservedMs >= 900);
        string json = JsonSerializer.Serialize(result, Json.Options);
        Assert.DoesNotContain(server.Profile.Password, json); Assert.DoesNotContain(server.Profile.Username, json);
    }
    [Fact] public async Task EofDuringIdleIsReportedWithoutBlamingEitherEndpoint()
    {
        await using var server = new SocksFixture(echoTcp: true, closeEchoAfterFirst: true);
        var result = await IdleTcpProbe.RunAsync(server.Profile, 2, "198.51.100.8", 4242);
        Assert.True(result.InitialEcho); Assert.True(result.UpstreamEof); Assert.False(result.IdleCompleted); Assert.False(result.FinalEcho);
        Assert.Contains("無法單獨區分", result.Message);
    }
    [Fact] public async Task FailedAuthenticationCannotPassOrExposeCredentials()
    {
        await using var server = new SocksFixture(echoTcp: true);
        var profile = server.Profile with { Password = "private-wrong-idle-password" };
        var result = await IdleTcpProbe.RunAsync(profile, 1, "198.51.100.8", 4242);
        Assert.False(result.Connected); Assert.False(result.FinalEcho);
        Assert.DoesNotContain(profile.Password, JsonSerializer.Serialize(result, Json.Options));
    }
}

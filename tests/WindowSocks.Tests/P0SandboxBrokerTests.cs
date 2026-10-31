using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;
namespace WindowSocks.Tests;
public sealed class P0SandboxBrokerTests
{
    [Fact] public void C01_ValidationUsesResidentBrokerAndAcceptsSlowReplyWithoutRestart()
    {
        int requests = 0, closed = 0;
        var profile = SandboxInstances.Create("Synthetic", @"C:\Synthetic\Launcher.exe", Guid.NewGuid());
        using var runtime = new SandboxieServiceRuntime(request => {
            Assert.Equal("validate", request.Action); Assert.Equal(profile, request.Profile);
            if (++requests == 1) Thread.Sleep(1300);
            return JsonSerializer.Serialize(new SandboxBrokerReply(request.RequestId, Reply.Success(true)), Json.Options);
        }, () => closed++);
        runtime.ValidateSettings(profile); runtime.ValidateSettings(profile);
        Assert.Equal(2, requests); Assert.Equal(0, closed);
        Assert.Equal(TimeSpan.FromSeconds(20), SandboxieServiceRuntime.ResponseTimeout("validate", true));
        Assert.Equal(TimeSpan.FromSeconds(10), SandboxieServiceRuntime.ResponseTimeout("validate", false));
        Assert.Equal(TimeSpan.FromSeconds(20), SandboxieServiceRuntime.ResponseTimeout("list", true));
        Assert.Equal(TimeSpan.FromSeconds(5), SandboxieServiceRuntime.ResponseTimeout("list", false));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void C01_BrokerValidationRejectionCannotAuthorizeLaunch(bool rejection)
    {
        using var runtime = new SandboxieServiceRuntime(request => JsonSerializer.Serialize(new SandboxBrokerReply(request.RequestId,
            rejection ? Reply.Failure("synthetic invalid settings") : Reply.Success(false)), Json.Options));
        Assert.ThrowsAny<Exception>(() => runtime.ValidateSettings(SandboxInstances.Create("Synthetic", @"C:\Synthetic\Launcher.exe", Guid.NewGuid())));
    }
    [Fact] public void P02_BusinessErrorKeepsBrokerAvailableForNextRequest()
    {
        int requests=0,closed=0;
        using var runtime=new SandboxieServiceRuntime(request=>JsonSerializer.Serialize(new SandboxBrokerReply(request.RequestId,++requests==1?Reply.Failure("fixture business rejection"):Reply.Success(Array.Empty<SandboxProcess>())),Json.Options),()=>closed++);
        Assert.ThrowsAny<InvalidOperationException>(()=>runtime.Processes("fixture"));Assert.Empty(runtime.Processes("fixture"));Assert.Equal(2,requests);Assert.Equal(0,closed);
    }
    [Fact] public void P02_StaleCorrelationIdIsRejected()
    {
        int closed=0;
        using var runtime=new SandboxieServiceRuntime(_=>JsonSerializer.Serialize(new SandboxBrokerReply(Guid.NewGuid(),Reply.Success(Array.Empty<SandboxProcess>())),Json.Options),()=>closed++);
        Assert.Throws<IOException>(()=>runtime.Processes("fixture"));Assert.Equal(1,closed);
    }
    [Fact] public void P02_MissingBrokerBacksOffAcrossAllBoxes()
    {
        int attempts=0;var now=DateTimeOffset.UtcNow;
        using var runtime=new SandboxieServiceRuntime(_=>{attempts++;throw new IOException("fixture unavailable");},clock:()=>now);
        for(int i=0;i<10;i++)Assert.Throws<IOException>(()=>runtime.Processes("fixture"+i));Assert.Equal(1,attempts);
        now=now.AddMilliseconds(500);Assert.Throws<IOException>(()=>runtime.Processes("fixture"));Assert.Equal(2,attempts);
        now=now.AddMilliseconds(500);Assert.Throws<IOException>(()=>runtime.Processes("fixture"));Assert.Equal(2,attempts);
        now=now.AddMilliseconds(500);Assert.Throws<IOException>(()=>runtime.Processes("fixture"));Assert.Equal(3,attempts);
    }
}

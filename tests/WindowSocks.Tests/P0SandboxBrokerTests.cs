using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;
namespace WindowSocks.Tests;
public sealed class P0SandboxBrokerTests
{
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

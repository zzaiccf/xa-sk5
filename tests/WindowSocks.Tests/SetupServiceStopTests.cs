using System.Security.Principal;
using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SetupServiceStopTests
{
    [Fact]
    public async Task StoppedServiceSkipsRecoverySnapshot()
    {
        var snapshot = await ForceServiceStop.TrySnapshotAsync(() => false,
            _ => throw new Exception("A stopped service has no control pipe."));
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task MissingPipeDuringRecoverySnapshotDoesNotPreventGracefulStop()
    {
        string pipe = "xa-setup-snapshot-test-" + Guid.NewGuid();
        var snapshot = await ForceServiceStop.TrySnapshotAsync(() => true,
            token => PipeTransport.CallAsync<Snapshot>(Command.Create("snapshot", new { }), token, pipe));
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GracefulStopCanConnectAfterOldPipeTimeout()
    {
        string pipe = "xa-setup-stop-test-" + Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var stop = PipeTransport.CallAsync<bool>(Command.Create("stopService", new { }), timeout.Token, pipe);
        await Task.Delay(2100, timeout.Token);
        // Surface an early client failure immediately instead of waiting on an unused listener.
        if (stop.IsCompleted) await stop;
        await using var server = PipeTransport.CreateServer(WindowsIdentity.GetCurrent().User!.Value, pipe);
        await server.WaitForConnectionAsync(timeout.Token);
        var request = await PipeTransport.ReadAsync<Command>(server, timeout.Token);
        Assert.Equal("stopService", request.Action);
        await PipeTransport.WriteAsync(server, Reply.Success(true), timeout.Token);
        Assert.True(await stop);
    }
}

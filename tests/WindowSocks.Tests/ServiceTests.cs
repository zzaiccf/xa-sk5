using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ServiceTests
{
    [Fact] public async Task ServicePersistsSecretsRedactsSnapshotsAndRejectsUnavailableEngine()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        try {
            using (var service = new Controller(directory)) {
                var imported = await service.Dispatch(Command.Create("import", new ImportRequest("127.0.0.1:1080:u:TEST-SECRET")), default); Assert.True(imported.Ok, imported.Error);
                var snapshot = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                var profile = Assert.Single(snapshot.Proxies); Assert.Empty(profile.Password); Assert.False(snapshot.Diagnostics.EngineRunning);
                var backup = await service.Dispatch(Command.Create("backup", new { }), default); Assert.True(backup.Ok);
                Assert.DoesNotContain("TEST-SECRET", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Json.Read<string>(backup.Data))));
                var identity = LdPlayer.Identity(Environment.ProcessId)!.Value;
                var target = new TargetInstance("test", "test", "Process", Environment.ProcessPath!, -1, true, [identity]);
                Assert.True((await service.Dispatch(Command.Create("syncTargets", new[] { target }), default)).Ok);
                var bind = await service.Dispatch(Command.Create("bind", new BindRequest(target.Key, profile.Id)), default);
                Assert.False(bind.Ok); // Test output intentionally contains no WinDivert binaries.
                var after = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                Assert.Empty(after.Bindings);
                Assert.False((await service.Dispatch(Command.Create("unknown", new { }), default)).Ok);
            }
            using var reopened = new Controller(directory);
            var persisted = Json.Read<Snapshot>((await reopened.Dispatch(Command.Create("snapshot", new { }), default)).Data); Assert.Single(persisted.Proxies);
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task NamedPipeRoundTripUsesCurrentUserAcl()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string pipeName = "WindowSocks.Tests." + Guid.NewGuid().ToString("N");
        using var server = PipeTransport.CreateServer(WindowsIdentity.GetCurrent().User!.Value, pipeName);
        var accept = server.WaitForConnectionAsync(timeout.Token);
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token); await accept;
        await PipeTransport.WriteAsync(client, Command.Create("ping", "hello"), timeout.Token);
        var command = await PipeTransport.ReadAsync<Command>(server, timeout.Token); Assert.Equal("ping", command.Action);
        await PipeTransport.WriteAsync(server, Reply.Failure("測試錯誤"), timeout.Token);
        Assert.False((await PipeTransport.ReadAsync<Reply>(client, timeout.Token)).Ok);
    }
}

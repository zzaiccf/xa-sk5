using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed class PipeWorker(Controller controller, ILogger<PipeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new HashSet<Task>();
        try {
            while (!stoppingToken.IsCancellationRequested)
            {
                var pipe = PipeTransport.CreateServer(controller.OwnerSid);
                try { await pipe.WaitForConnectionAsync(stoppingToken); }
                catch { pipe.Dispose(); throw; }
                var task = Handle(pipe, stoppingToken); pending.RemoveWhere(t => t.IsCompleted); pending.Add(task);
                if (pending.Count >= 7) await Task.WhenAny(pending);
            }
        } finally { await Task.WhenAll(pending); }
    }
    private async Task Handle(System.IO.Pipes.NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(TimeSpan.FromSeconds(115));
            try { var command = await PipeTransport.ReadAsync<Command>(pipe, timeout.Token); var response = await controller.Dispatch(command, timeout.Token); await PipeTransport.WriteAsync(pipe, response, timeout.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException) { logger.LogDebug("Pipe ended: {Type}", ex.GetType().Name); }
        }
    }
}
public sealed class MonitorWorker(Controller controller, ILogger<MonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken)) {
            // One failed tick (probe timeout, WMI hiccup) must never stop rule enforcement for good:
            // a dead monitor leaves stale bindings and unassigned groups behind.
            if (stoppingToken.IsCancellationRequested) break;
            try { await controller.Tick(stoppingToken); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { logger.LogWarning(ex, "Monitoring tick failed; continuing."); }
        }
    }
}

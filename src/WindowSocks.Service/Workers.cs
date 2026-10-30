using WindowSocks.Core;

namespace WindowSocks.Service;

public sealed class PipeWorker(Controller controller, ILogger<PipeWorker> logger, IHostApplicationLifetime lifetime, string? pipeName = null) : BackgroundService
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new HashSet<Task>();
        try {
            while (!stoppingToken.IsCancellationRequested)
            {
                var pipe = PipeTransport.CreateServer(controller.OwnerSid, pipeName ?? PipeTransport.Name);
                try { await pipe.WaitForConnectionAsync(stoppingToken); }
                catch { pipe.Dispose(); throw; }
                var task = Task.Run(() => Handle(pipe, stoppingToken)); pending.RemoveWhere(t => t.IsCompleted); pending.Add(task);
                if (pending.Count >= 7) await Task.WhenAny(pending);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowSocks", "pipe-worker-error.log"), DateTimeOffset.Now.ToString("O") + Environment.NewLine + ex + Environment.NewLine);
            throw;
        } finally { await Task.WhenAll(pending); }
    }
    private async Task Handle(System.IO.Pipes.NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(TimeSpan.FromSeconds(115));
            bool stop = false;
            try { var command = await PipeTransport.ReadAsync<Command>(pipe, timeout.Token); int? caller = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? checked((int)pid) : null; var response = pipeName == PipeTransport.SandboxName && command.Action is not ("registerSandbox" or "refreshSandboxes" or "reportSandboxLaunch" or "reportSandboxNetwork") ? Reply.Failure("沙盒通道不允許管理設定。") : await controller.Dispatch(command, timeout.Token, caller); stop = command.Action == "stopService" && response.Ok; await PipeTransport.WriteAsync(pipe, response, timeout.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException) { logger.LogDebug("Pipe ended: {Type}", ex.GetType().Name); }
            finally { if (stop) lifetime.StopApplication(); }
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

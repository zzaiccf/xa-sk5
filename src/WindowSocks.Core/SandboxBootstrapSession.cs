namespace WindowSocks.Core;

// Test boundary around the helper lifecycle; the caller supplies process/IPC IO.
public static class SandboxBootstrapSession
{
    public static async Task Run(Func<Task> start, Func<Task<bool>> monitor, Func<Exception, Task> failure,
        Action<Exception> monitorError, CancellationToken ct = default, Func<CancellationToken, Task>? delay = null)
    {
        delay ??= token => Task.Delay(1000, token);
        try { await start(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { await failure(ex); return; }
        while (true) {
            await delay(ct);
            try { if (!await monitor()) return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                // Service health/identity/exit policy remains authoritative. A
                // transport failure here is never another launch report.
                try { monitorError(ex); } catch (Exception logError) when (logError is not OutOfMemoryException) { }
            }
        }
    }
}

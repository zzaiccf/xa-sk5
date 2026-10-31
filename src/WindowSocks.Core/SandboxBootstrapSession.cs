namespace WindowSocks.Core;

// Test boundary around the helper lifecycle; the caller supplies process/IPC IO.
public static class SandboxBootstrapSession
{
    public static async Task Run(Func<Task> start, Func<Task<bool>> monitor, Func<Exception, Task> failure,
        Action<Exception> monitorError, CancellationToken ct = default, Func<CancellationToken, Task>? delay = null,
        TimeProvider? clock = null)
    {
        delay ??= token => Task.Delay(1000, token);
        clock ??= TimeProvider.System;
        long? failuresSince = null;
        try { await start(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { await failure(ex); return; }
        while (true) {
            try { await delay(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            if (failuresSince is long since && clock.GetElapsedTime(since) >= TimeSpan.FromMinutes(5)) return;
            long attempt = clock.GetTimestamp();
            Task<bool>? pending = null;
            try {
                pending = monitor();
                var remaining = TimeSpan.FromMinutes(5) - clock.GetElapsedTime(failuresSince ?? attempt);
                if (remaining <= TimeSpan.Zero || !await pending.WaitAsync(remaining, ct)) return;
                failuresSince = null;
            }
            catch (TimeoutException) when (pending is { IsCompleted: false }) { return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                // Service health/identity/exit policy remains authoritative. A
                // transport failure here is never another launch report.
                failuresSince ??= attempt;
                try { monitorError(ex); } catch (Exception logError) when (logError is not OutOfMemoryException) { }
                if (clock.GetElapsedTime(failuresSince.Value) >= TimeSpan.FromMinutes(5)) return;
            }
        }
    }
}

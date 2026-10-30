namespace WindowSocks.Core;

// Timed-out callers do not spawn additional stuck discovery workers.
public sealed class SingleFlightQuery<T>(Func<T> query)
{
    private readonly object gate = new();
    private Task<T>? pending;
    public Task<T> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task<T> task;
        lock (gate) {
            if (pending is null || pending.IsCompleted) {
                pending = Task.Run(query);
                _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            task = pending;
        }
        return task.WaitAsync(timeout, ct);
    }
}

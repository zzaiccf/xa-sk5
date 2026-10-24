namespace WindowSocks.Core;

public static class ServiceExit
{
    public static async Task StopAsync(Func<CancellationToken, Task> requestStop, Func<bool> isRunning, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (!isRunning()) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        Exception? requestError = null;
        try { await requestStop(deadline.Token); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { requestError = ex; }
        try {
            while (isRunning()) await Task.Delay(150, deadline.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new IOException("背景服務尚未停止，請重試關閉。", requestError);
        }
    }
}

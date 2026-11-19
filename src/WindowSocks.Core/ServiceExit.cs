namespace WindowSocks.Core;

public static class ServiceExit
{
    public static async Task StopAsync(Func<CancellationToken, Task> requestStop, Func<bool> isRunning, CancellationToken ct = default, TimeSpan? timeout = null, Func<Exception, Task>? explicitRecovery = null)
    {
        if (explicitRecovery is not null) {
            try { await StopAsync(requestStop, isRunning, ct, timeout); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or TimeoutException or InvalidOperationException) {
                await explicitRecovery(ex);
                if (isRunning()) throw new IOException("強制停止尚未完成，服務仍在執行。", ex);
            }
            return;
        }
        if (!isRunning()) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        Exception? requestError = null;
        try { await requestStop(deadline.Token); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { requestError = ex; }
        try {
            while (isRunning()) await Task.Delay(150, deadline.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            string message = requestError is TimeoutException
                ? "無法連上背景服務的控制管道，Windows 仍回報服務執行中。請正常關閉 xa sk5 與受管程式後重試。"
                : "Windows 尚未確認背景服務已停止，請查看服務紀錄後重試。";
            throw new IOException(message, requestError);
        }
    }
}

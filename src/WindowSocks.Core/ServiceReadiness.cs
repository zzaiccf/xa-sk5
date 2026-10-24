namespace WindowSocks.Core;

public static class ServiceReadiness
{
    public static async Task<Snapshot> WaitAsync(string expectedVersion, Func<CancellationToken, Task<Snapshot>>? read = null, Action<string>? report = null, TimeSpan? timeout = null)
    {
        read ??= ct => PipeTransport.CallAsync<Snapshot>(Command.Create("snapshot", new { }), ct);
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(75));
        var clock = System.Diagnostics.Stopwatch.StartNew(); Exception? last = null;
        while (!deadline.IsCancellationRequested) {
            try {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); attempt.CancelAfter(TimeSpan.FromSeconds(5));
                var snapshot = await read(attempt.Token);
                if (snapshot.Diagnostics.Version.Split(' ')[0] != expectedVersion) throw new InvalidDataException("背景服務回報的版本不符。");
                return snapshot;
            } catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) {
                last = ex; report?.Invoke($"等候背景服務就緒（{(int)clock.Elapsed.TotalSeconds} 秒）…");
                try { await Task.Delay(250, deadline.Token); } catch (OperationCanceledException) { break; }
            }
        }
        throw new IOException("背景服務未能在等候時間內完成連線驗證。", last);
    }
}

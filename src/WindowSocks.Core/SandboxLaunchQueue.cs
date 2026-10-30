namespace WindowSocks.Core;

public sealed record SandboxLaunchOutcome(Guid Id, bool Success, string Detail);
public sealed class SandboxLaunchQueue
{
    private int running;
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public async Task<SandboxLaunchOutcome[]> RunAsync(IEnumerable<Guid> ids, int intervalSeconds,
        Func<Guid, CancellationToken, Task> launch, IProgress<string>? progress, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (intervalSeconds is < 1 or > 300) throw new InvalidOperationException("啟動間隔請輸入 1–300 秒。");
        var ordered = ids.Distinct().ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException("請先選取已準備的沙盒實例。");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("已有啟動工作進行中，請等候或取消該工作。");
        delay ??= (duration, token) => Task.Delay(duration, token);
        var results = new List<SandboxLaunchOutcome>();
        try {
            for (int i = 0; i < ordered.Length; i++) {
                if (i > 0) for (int remaining = intervalSeconds; remaining > 0; remaining--) {
                    progress?.Report($"{remaining} 秒後啟動下一個（{i + 1}/{ordered.Length}）…");
                    await delay(TimeSpan.FromSeconds(1), ct);
                }
                ct.ThrowIfCancellationRequested(); progress?.Report($"正在核驗並啟動 {i + 1}/{ordered.Length}…");
                try { await launch(ordered[i], ct); results.Add(new(ordered[i], true, "已啟動／已在執行")); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { results.Add(new(ordered[i], false, ex.Message)); }
            }
            return results.ToArray();
        } finally { Volatile.Write(ref running, 0); }
    }
}

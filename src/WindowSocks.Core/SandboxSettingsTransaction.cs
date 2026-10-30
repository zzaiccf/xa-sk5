namespace WindowSocks.Core;

public static class SandboxSettingsTransaction
{
    public static async Task Apply(Dictionary<string, string[]> settings,
        Func<string, CancellationToken, Task<string[]>> read,
        Func<string, string[], CancellationToken, Task> write,
        Func<CancellationToken, Task> reload, CancellationToken ct)
    {
        var before = new Dictionary<string, string[]>();
        foreach (string key in settings.Keys) before[key] = await read(key, ct);
        var changed = new List<string>();
        try {
            foreach (var (key, values) in settings) {
                ct.ThrowIfCancellationRequested();
                if (before[key].SequenceEqual(values, StringComparer.OrdinalIgnoreCase)) continue;
                changed.Add(key); await write(key, values, ct);
                if (!(await read(key, ct)).SequenceEqual(values, StringComparer.OrdinalIgnoreCase)) throw new IOException("沙盒設定核對失敗：" + key);
            }
            if (changed.Count > 0) await reload(ct);
        } catch (Exception failure) {
            try {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                foreach (string key in changed.AsEnumerable().Reverse()) {
                    await write(key, before[key], rollback.Token);
                    if (!(await read(key, rollback.Token)).SequenceEqual(before[key], StringComparer.OrdinalIgnoreCase)) throw new IOException("沙盒設定回復核對失敗：" + key);
                }
                if (changed.Count > 0) await reload(rollback.Token);
            } catch (Exception restore) { throw new IOException("沙盒設定未完成，回復亦未完成；請保留 ConfigBackups 後重試。", new AggregateException(failure, restore)); }
            throw;
        }
    }
}

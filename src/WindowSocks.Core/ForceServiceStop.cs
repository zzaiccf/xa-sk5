namespace WindowSocks.Core;

public sealed record ForceStopReport(ProcessCloseOutcome[] Outcomes, string[] Remaining)
{
    public string Warning => "強制停止會失去代理保護，仍在執行的程式可能直接連線。\n" +
        "已先使用釘選 handle 核對 PID、建立時間與擁有者，嘗試結束最後已核驗的沙盒成員。\n" +
        string.Join("\n", Outcomes.Select(p => $"PID {p.Identity.Pid}：{p.Message}")) + "\n仍可能連線的程式：\n" +
        string.Join("\n", Remaining) + "\n服務沒有回應，清單可能不完整，後續產生的子程序也可能未列出。確認強制停止背景服務？";
}

public static class ForceServiceStop
{
    public static async Task<Snapshot?> TrySnapshotAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { return await PipeTransport.CallAsync<Snapshot>(Command.Create("snapshot", new { }), timeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { return null; }
    }
    // Called only after the user selects the explicit force-stop option. A second
    // confirmation covers incomplete inventory and failures before SCM is touched.
    public static async Task ExecuteAsync(Snapshot? snapshot, string ownerSid,
        Func<ForceStopReport, Task<bool>> confirm,
        Func<ProcessIdentity[], string, Task<ProcessCloseOutcome[]>>? terminate = null,
        Func<Task>? stop = null)
    {
        var identities = snapshot?.Bindings.Where(b => b.Kind == "Sandbox").SelectMany(b => b.Processes.Concat(b.Descendants))
            .Where(p => p.Pid > 4 && p.CreatedFileTime > 0).Distinct().ToArray() ?? [];
        var outcomes = await (terminate ?? ManagedProcessTerminator.CloseAsync)(identities, ownerSid);
        var closed = outcomes.Where(p => p.State is "closed" or "exited").Select(p => p.Identity).ToHashSet();
        var remaining = snapshot?.Bindings.SelectMany(b => b.Processes.Concat(b.Descendants)
            .Where(p => !closed.Contains(p)).Select(p => $"{b.Name} · PID {p.Pid}（最後已知身分，可能已退出或被重用）")).Distinct().ToList() ?? [];
        remaining.Add(snapshot is null ? "無法取得服務清單，存活程序未知。" : "全局代理涵蓋的程式與未知新程序無法完整列舉。");
        if (!await confirm(new(outcomes, remaining.ToArray()))) throw new OperationCanceledException("已取消強制停止，背景服務保持執行。");
        await (stop ?? StopScmAsync)();
    }
    private static async Task StopScmAsync()
    {
        WindowsProxyService.RequestStop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { while (WindowsProxyService.Running()) await Task.Delay(150, timeout.Token); }
        catch (OperationCanceledException) { throw new IOException("SCM 停止仍未完成；未宣稱服務已停止，也未覆寫程式檔案。"); }
    }
}

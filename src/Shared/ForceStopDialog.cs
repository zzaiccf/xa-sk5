using System.Security.Principal;
using WindowSocks.Core;

namespace WindowSocks.Recovery;

internal static class ForceStopDialog
{
    public static void Stop() => Task.Run(StopAsync).GetAwaiter().GetResult();
    public static async Task StopAsync()
    {
        var snapshot = await ForceServiceStop.TrySnapshotAsync();
        await WindowsProxyService.StopGracefullyAsync(explicitRecovery: async error => {
            if (MessageBox.Show("一般停止未完成：" + error.Message + "\n\n選擇強制停止，將先嘗試結束已核驗的沙盒程序，再顯示仍可能直連的清單供確認。是否繼續？",
                "強制停止（會失去保護）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                throw new OperationCanceledException("已取消強制停止。");
            string ownerFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowSocks", "owner.sid");
            string ownerSid = File.Exists(ownerFile) ? File.ReadAllText(ownerFile).Trim() : WindowsIdentity.GetCurrent().User!.Value;
            await ForceServiceStop.ExecuteAsync(snapshot, ownerSid, report => Task.FromResult(MessageBox.Show(report.Warning,
                "確認強制停止（會失去保護）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes),
                terminate: (ids,sid) => Task.Run(()=>ManagedProcessTerminator.CloseAsync(ids,sid)));
        });
    }
}

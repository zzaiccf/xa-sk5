using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class Backend
{
    public virtual async Task<SandboxStorageEntry[]> SandboxStorageSnapshot(CancellationToken ct)
    {
        var state = await SandboxSnapshot(ct).ConfigureAwait(false);
        return await Task.Run(() => new SandboxStorage().Scan(state.SandboxInstances, ct), ct).ConfigureAwait(false);
    }
    public virtual async Task RemoveSandboxData(SandboxInstanceProfile profile, bool deleteFiles, IProgress<string>? progress, CancellationToken ct)
    {
        if (SandboxLaunchBusy || !await sandboxPreparationGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new IOException("啟動或準備工作尚未完成，請稍後重試移除。");
        try {
            await Task.Run(() => new SandboxRemoval(new SandboxStorage(), new RemovalHost(this)).Run(profile, deleteFiles, progress, ct), ct).ConfigureAwait(false);
        } finally { sandboxPreparationGate.Release(); }
    }
    private sealed class RemovalHost(Backend backend) : ISandboxRemovalHost
    {
        private bool registered;
        private readonly SandboxBoxRemoval box = new();
        public async Task Reserve(SandboxInstanceProfile profile, CancellationToken ct)
        {
            var snapshot = await backend.SandboxSnapshot(ct).ConfigureAwait(false);
            if (!snapshot.SupportsSandboxRemoval) throw new IOException("安全移除需要 0.1.33 以上背景服務；請先完整更新主程式與服務。");
            var current = snapshot.SandboxInstances.FirstOrDefault(p => p.Id == profile.Id);
            if (current is not null && (current.WorkingDirectory != profile.WorkingDirectory || current.FileRoot != profile.FileRoot || current.BoxName != profile.BoxName)) throw new IOException("實例設定已變更，請重新掃描。");
            if (snapshot.SandboxInstances.Any(p => p.Id != profile.Id && (p.BoxName == profile.BoxName || p.WorkingDirectory.Equals(profile.WorkingDirectory, StringComparison.OrdinalIgnoreCase)))) throw new IOException("資料仍由其他實例使用，未清理。");
            registered = current is not null;
            if (registered) await backend.Call<SandboxInstanceProfile>(Command.Create("beginSandboxRemoval", new SandboxRequest(profile.Id)), ct).ConfigureAwait(false);
        }
        public Task DisableStoppedBox(SandboxInstanceProfile profile, CancellationToken ct) => box.Disable(profile, ct);
        public async Task RemoveRule(SandboxInstanceProfile profile, bool deleteFiles, CancellationToken ct)
        {
            if (registered) await backend.Call<bool>(Command.Create("finishSandboxRemoval", new SandboxRemovalRequest(profile.Id, deleteFiles)), ct).ConfigureAwait(false);
            else {
                var current = await backend.SandboxSnapshot(ct).ConfigureAwait(false);
                if (current.SandboxInstances.Any(p => p.Id == profile.Id || p.BoxName == profile.BoxName)) throw new IOException("清理期間實例已重新登記，請重新掃描並完成移除。");
            }
        }
        public Task RemoveBoxSettings(SandboxInstanceProfile profile, CancellationToken ct) => box.Remove(profile, ct);
    }
}

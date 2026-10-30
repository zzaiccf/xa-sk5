namespace WindowSocks.Core;

public interface ISandboxRemovalHost
{
    Task Reserve(SandboxInstanceProfile profile, CancellationToken ct);
    Task DisableStoppedBox(SandboxInstanceProfile profile, CancellationToken ct);
    Task RemoveRule(SandboxInstanceProfile profile, bool deleteFiles, CancellationToken ct);
    Task RemoveBoxSettings(SandboxInstanceProfile profile, CancellationToken ct);
}

public sealed class SandboxRemoval(SandboxStorage storage, ISandboxRemovalHost host)
{
    public async Task Run(SandboxInstanceProfile profile, bool deleteFiles, IProgress<string>? progress, CancellationToken ct)
    {
        var inspected = storage.Inspect(profile, false, ct);
        if (!inspected.CanRemove) throw new IOException(inspected.Error);
        await host.Reserve(profile, ct);
        storage.Remember(profile); // Outside the directory being deleted; survives cancellation and partial failure.
        SandboxPreparation.ForgetDraft(profile, Path.Combine(storage.Root, "Preparation"));
        progress?.Report("正在確認遊戲已停止並停用此沙盒…");
        await host.DisableStoppedBox(profile, ct);
        ct.ThrowIfCancellationRequested();
        if (deleteFiles) { progress?.Report("正在永久刪除選取實例的副本與沙盒資料…"); storage.DeleteFiles(profile, progress, ct); }
        await host.RemoveBoxSettings(profile, ct);
        // Keep the durable service reservation until Sandboxie settings are gone.
        await host.RemoveRule(profile, deleteFiles, ct);
        if (deleteFiles) storage.Forget(profile);
        progress?.Report(deleteFiles ? "此實例的設定與副本已清除。" : "設定已移除；保留資料已列入儲存空間清單，可日後清理。");
    }
}

public sealed class SandboxBoxRemoval
{
    private readonly Func<CancellationToken, Task<string[]>> read;
    private readonly Func<string, string[], CancellationToken, Task<string>> command;
    private readonly Action<string> requireStopped;
    public SandboxBoxRemoval() : this(ReadInstalled, (exe, args, ct) => SandboxieRuntime.Command(exe, args, ct), box => {
        if (!SandboxieInstaller.Inspect().Ready) throw new IOException("請先確認支援的 Sandboxie 1.18.8 已完整安裝，再清理。");
        using var runtime = new SandboxieRuntime(); if (runtime.Processes(box).Length != 0) throw new IOException("沙盒仍有程序，未刪除資料；請先正常關閉遊戲並停止實例。");
    }) { }
    internal SandboxBoxRemoval(Func<CancellationToken, Task<string[]>> read, Func<string, string[], CancellationToken, Task<string>> command, Action<string> requireStopped)
    { this.read = read; this.command = command; this.requireStopped = requireStopped; }
    private static async Task<string[]> ReadInstalled(CancellationToken ct)
    {
        string ini = new[] { Path.Combine(SandboxieRuntime.InstallDirectory, "Sandboxie.ini"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sandboxie.ini") }.FirstOrDefault(File.Exists)
            ?? throw new IOException("無法確認 Sandboxie 設定，未進行清理。");
        if (new FileInfo(ini).Length > 1024 * 1024) throw new IOException("Sandboxie 設定過大。");
        return await File.ReadAllLinesAsync(ini, ct);
    }
    private static bool Exists(string[] lines, string box) => lines.Any(l => l.Trim().Equals("[" + box + "]", StringComparison.OrdinalIgnoreCase));
    private static void OwnSection(string[] lines, SandboxInstanceProfile p)
    {
        SandboxInstances.Validate(p);
        var roots = SandboxIniSettings.Read(lines, p.BoxName).GetValueOrDefault("FileRootPath", []);
        if (roots.Length != 1 || !roots[0].Equals(p.FileRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Sandboxie 的 FileRootPath 已變更，拒絕清理非本實例資料。");
    }
    public async Task Disable(SandboxInstanceProfile profile, CancellationToken ct)
    {
        requireStopped(profile.BoxName);
        var lines = await read(ct); if (!Exists(lines, profile.BoxName)) return;
        OwnSection(lines, profile);
        await command("SbieIni.exe", ["set", profile.BoxName, "Enabled", "n"], ct);
        await command("Start.exe", ["/reload"], ct);
        var updated = await read(ct); OwnSection(updated, profile);
        string? active = (await command("SbieIni.exe", ["query", profile.BoxName, "Enabled"], ct)).Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!SandboxIniSettings.Read(updated, profile.BoxName).GetValueOrDefault("Enabled", []).SequenceEqual(["n"], StringComparer.OrdinalIgnoreCase) || !string.Equals(active, "n", StringComparison.OrdinalIgnoreCase))
            throw new IOException("未能確認沙盒已停用，未刪除資料。");
        requireStopped(profile.BoxName);
    }
    public async Task Remove(SandboxInstanceProfile profile, CancellationToken ct)
    {
        var lines = await read(ct); if (!Exists(lines, profile.BoxName)) return;
        OwnSection(lines, profile); requireStopped(profile.BoxName);
        // Official persistent section deletion; never use Start.exe's broad phase-2 content cleanup.
        await command("SbieIni.exe", ["set", profile.BoxName, "*"], ct);
        await command("Start.exe", ["/reload"], ct);
        if (Exists(await read(ct), profile.BoxName) || (await command("SbieIni.exe", ["query", "*"], ct)).Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Any(s => s.Trim().Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Sandboxie 設定尚未移除；清理紀錄保留，可重新掃描後重試。");
    }
}

using System.Diagnostics;
using WindowSocks.Core;

internal static class SandboxBootstrap
{
    public static async Task Run(Guid id, string token)
    {
        SandboxLaunchPermission permission = null!;
        var grace = DateTimeOffset.UtcNow;
        await SandboxBootstrapSession.Run(async () => {
        var identity = LdPlayer.Identity(Environment.ProcessId) ?? throw new InvalidOperationException("無法確認啟動程序。");
        permission = await PipeTransport.CallAsync<SandboxLaunchPermission>(Command.Create("registerSandbox", new SandboxRegistration(id, token, identity) { SupportsLocalNetwork = true }), pipeName: PipeTransport.SandboxName);
        if (!permission.UseLocalNetwork) {
        var network = await TransparentTrafficProbe.RunAsync();
        await File.WriteAllTextAsync(Path.Combine(permission.WorkingDirectory, "xa-sandbox-network.json"), System.Text.Json.JsonSerializer.Serialize(network, Json.Options));
        await PipeTransport.CallAsync<bool>(Command.Create("reportSandboxNetwork", new SandboxNetworkReport(id, network.TcpExit, network.UdpExit, network.Dns)), pipeName: PipeTransport.SandboxName);
        }
        using var game = Process.Start(new ProcessStartInfo(permission.LauncherPath) { WorkingDirectory = permission.WorkingDirectory, UseShellExecute = false })
            ?? throw new IOException("無法啟動遊戲。");
        await PipeTransport.CallAsync<bool>(Command.Create("reportSandboxLaunch", new SandboxLaunchReport(id, true, "")), pipeName: PipeTransport.SandboxName);
        grace = DateTimeOffset.UtcNow;
        }, async () => {
            var statuses = await PipeTransport.CallAsync<SandboxInstanceStatus[]>(Command.Create("refreshSandboxes", new { }), pipeName: PipeTransport.SandboxName);
            var status = statuses.SingleOrDefault(s => s.Id == id);
            if (status is null || status.State.Contains("暫停")) return false;
            bool active = status.Processes.Where(p => p.Pid != Environment.ProcessId).Any(p => {
                string path = ProcessCatalog.ImagePath(p.Pid);
                return path.Length > 0 && (SandboxInstances.Within(path, permission.WorkingDirectory) || SandboxInstances.Within(path, permission.FileRoot));
            });
            if (active) grace = DateTimeOffset.UtcNow;
            else if (DateTimeOffset.UtcNow - grace > TimeSpan.FromSeconds(15)) return false;
            return true;
        }, ex => Task.FromException(ex), ex => {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "sandbox-monitor-error.txt"), DateTimeOffset.Now.ToString("O") + Environment.NewLine + SandboxLaunchErrors.Clean(ex.ToString()));
        });
    }
}

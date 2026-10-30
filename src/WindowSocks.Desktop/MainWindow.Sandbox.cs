using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class MainWindow
{
    private SandboxLaunchWindow? sandboxLaunchWindow;
    private async Task OpenSandbox(string initialPath = "")
    {
        var dialog = new SandboxWindow(backend, snapshot ?? backend.EditorState, initialPath) { Owner = this };
        dialog.ShowDialog(); await Refresh(false, skipDiscovery: true);
    }
    private async void SandboxClick(object sender, RoutedEventArgs e)
    {
        if (closingService || quitting || ruleDialogOpen) return;
        ruleDialogOpen = true;
        string source = sender is not System.Windows.Controls.Button && RuleGrid.SelectedItem is ProxyRuleItem rule
            ? snapshot?.SandboxInstances.FirstOrDefault(p => p.TargetKey == rule.Key)?.SourceLauncherPath ?? "" : "";
        try { await OpenSandbox(source); } catch (Exception ex) { StatusText.Text = ex.Message; } finally { ruleDialogOpen = false; }
    }
    private void OpenSandboxLaunch(SandboxInstanceProfile[] profiles) {
        if (closingService || quitting) return;
        if (sandboxLaunchWindow is { IsVisible: true, Finished: false }) { sandboxLaunchWindow.Activate(); return; }
        if (profiles.Length == 0) { StatusText.Text = "請先從桌面沙盒多開準備遊戲環境。"; return; }
        sandboxLaunchWindow?.Close();
        sandboxLaunchWindow = new SandboxLaunchWindow(backend, profiles, 10) { Owner = this };
        sandboxLaunchWindow.Show();
    }
    private void StartRuleClick(object sender, RoutedEventArgs e) {
        var profile = snapshot?.SandboxInstances.FirstOrDefault(p => p.TargetKey == contextRuleKey);
        if (profile is not null) OpenSandboxLaunch([profile]);
    }
    private void StartRuleGroupClick(object sender, RoutedEventArgs e) {
        var profile = snapshot?.SandboxInstances.FirstOrDefault(p => p.TargetKey == contextRuleKey);
        if (profile is not null) OpenSandboxLaunch(snapshot!.SandboxInstances.Where(p => p.SourceLauncherPath.Equals(profile.SourceLauncherPath, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray());
    }
    private void StartAllSandboxesClick(object sender, RoutedEventArgs e) => OpenSandboxLaunch(snapshot?.SandboxInstances.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray() ?? []);
    private async void StopSandboxRuleClick(object sender, RoutedEventArgs e) {
        var profile = snapshot?.SandboxInstances.FirstOrDefault(p => p.TargetKey == contextRuleKey);
        if (profile is null || backend.SandboxLaunchBusy) return;
        await Run("正在停止 " + profile.Name + "…", async () => { await backend.StopSandbox(profile.Id); await Refresh(false, skipDiscovery: true); });
    }
}

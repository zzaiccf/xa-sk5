using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class SandboxWindow : Window
{
    private readonly Backend backend;
    private Snapshot state;
    private bool busy, refreshing;
    private bool ready, compatibilityChoiceMade, applyingSource;
    private CancellationTokenSource? sourceInspection;
    private string completionText = "操作完成。";
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    public sealed record LineOption(Guid Id, string Label);
    public sealed record InstanceRow(SandboxInstanceProfile Profile, string Line, string State, int Members, string Detail);
    public SandboxWindow(Backend backend, Snapshot state, string initialPath = "")
    {
        this.backend = backend; this.state = state; InitializeComponent();
        var lines = state.Proxies.Select(p => new LineOption(p.Id, p.Name + " · " + p.Endpoint)).ToArray();
        LineA.ItemsSource = lines; LineB.ItemsSource = lines;
        LineA.SelectedIndex = lines.Length > 0 ? 0 : -1; LineB.SelectedIndex = lines.Length > 1 ? 1 : -1;
        SourcePath.Text = Path.IsPathFullyQualified(initialPath) ? initialPath : state.SandboxInstances.FirstOrDefault()?.SourceLauncherPath ?? "";
        var pair = CurrentPair(); if (pair.Length > 0) LineA.SelectedValue = pair[0].ProxyId; if (pair.Length > 1) LineB.SelectedValue = pair[1].ProxyId;
        SimulateAdmin.IsChecked = pair.Any(p => p.SimulateAdmin);
        YongCompatibility.IsChecked = pair.Any(p => p.IsolateDeviceIdentity == true);
        Render(); timer.Tick += async (_, _) => { if (!busy) await RefreshState(); }; timer.Start();
        ready = true;
        Loaded += async (_, _) => { await CheckEnvironmentState(); await RefreshState(); await InspectSource(); };
        Closed += (_, _) => { lifetime.Cancel(); sourceInspection?.Cancel(); operation?.Cancel(); timer.Stop(); };
    }
    private SandboxInstanceProfile[] CurrentPair() => state.SandboxInstances.Where(p => p.SourceLauncherPath.Equals(ProgramRuleInput.Normalize(SourcePath.Text), StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    private void Render()
    {
        Guid? selected = (Instances.SelectedItem as InstanceRow)?.Profile.Id;
        var rows = CurrentPair().Select(p => {
            var status = state.SandboxStatuses.FirstOrDefault(s => s.Id == p.Id); var line = state.Proxies.FirstOrDefault(n => n.Id == p.ProxyId);
            string exit = status?.Network?.TcpExit ?? "待沙盒內驗證";
            return new InstanceRow(p, $"{line?.Name} · {line?.Port} · {exit}", status?.State ?? "未啟動", status?.Processes.Length ?? 0,
                status?.Detail + (status?.Network is { } network ? $"\n沙盒 TCP：{network.TcpExit} · UDP：{network.UdpExit} · 直接 DNS：{(network.DirectDns ? "通過" : "未通過")}" : ""));
        }).ToArray();
        Instances.ItemsSource = rows; Instances.SelectedItem = rows.FirstOrDefault(r => r.Profile.Id == selected) ?? rows.FirstOrDefault();
    }
    private async Task RefreshState()
    {
        if (refreshing || lifetime.IsCancellationRequested) return; refreshing = true;
        try {
            var fresh = await backend.SandboxSnapshot(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            state = fresh;
            Guid? a = LineA.SelectedValue as Guid?, b = LineB.SelectedValue as Guid?;
            var lines = state.Proxies.Select(p => new LineOption(p.Id, p.Name + " · " + p.Endpoint)).ToArray();
            LineA.ItemsSource = lines; LineB.ItemsSource = lines;
            LineA.SelectedValue = a; LineB.SelectedValue = b;
            if (LineA.SelectedIndex < 0 && lines.Length > 0) LineA.SelectedIndex = 0;
            if (LineB.SelectedIndex < 0 && lines.Length > 1) LineB.SelectedIndex = 1;
            if (SourcePath.Text.Length == 0 && state.SandboxInstances.FirstOrDefault() is { } first) SourcePath.Text = first.SourceLauncherPath;
            Render();
        }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) ProgressText.Text = ex.Message; }
        finally { refreshing = false; }
    }
    private async Task Work(Func<CancellationToken, Task> action)
    {
        if (busy) return; busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        completionText = "操作完成。"; sourceInspection?.Cancel();
        PrepareButton.IsEnabled = StartAButton.IsEnabled = StartBButton.IsEnabled = StartBothButton.IsEnabled = StopButton.IsEnabled = false;
        SourcePath.IsEnabled = LineA.IsEnabled = LineB.IsEnabled = SimulateAdmin.IsEnabled = YongCompatibility.IsEnabled = LaunchInterval.IsEnabled = false;
        CancelWork.IsEnabled = true;
        try { await Dispatcher.Yield(DispatcherPriority.Background); await action(operation.Token); if (!lifetime.IsCancellationRequested) ProgressText.Text = completionText; }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) ProgressText.Text = "已取消未完成的工作；已完成的副本與先前啟動的實例保留。"; }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) ProgressText.Text = ex.Message; }
        finally { operation.Dispose(); operation = null; busy = false; if (!lifetime.IsCancellationRequested) {
            PrepareButton.IsEnabled = StartAButton.IsEnabled = StartBButton.IsEnabled = StartBothButton.IsEnabled = StopButton.IsEnabled = true; CancelWork.IsEnabled = false;
            SourcePath.IsEnabled = LineA.IsEnabled = LineB.IsEnabled = SimulateAdmin.IsEnabled = YongCompatibility.IsEnabled = LaunchInterval.IsEnabled = true;
            await RefreshState();
        } }
    }
    private async void Prepare(object sender, RoutedEventArgs e) => await Work(async ct => {
        string input = SourcePath.Text;
        string source = await Task.Run(() => SandboxCompatibility.PreferLauncher(ProgramFileImport.Resolve([input])), ct); SourcePath.Text = source;
        if (LineA.SelectedValue is not Guid a || LineB.SelectedValue is not Guid b || a == b) throw new InvalidOperationException("請選擇兩條不同線路。");
        var existing = CurrentPair();
        var pair = await Task.Run(() => SandboxPreparation.ResumePair(source, a, b, existing), ct);
        if (!compatibilityChoiceMade && existing.All(p => p.IsolateDeviceIdentity is null) && await backend.DetectYongOrigin(source, ct)) {
            applyingSource = true; YongCompatibility.IsChecked = true; applyingSource = false;
        }
        var progress = new Progress<string>(s => { if (!lifetime.IsCancellationRequested) ProgressText.Text = s; });
        bool compatibility = YongCompatibility.IsChecked == true, admin = compatibility || SimulateAdmin.IsChecked == true;
        ProgressText.Text = "正在檢查線路與環境…";
        await backend.PrepareSandboxPair(pair.Select(p => p with { SimulateAdmin = admin, IsolateDeviceIdentity = compatibility }).ToArray(), progress, ct);
        completionText = "環境已準備好。可按「啟動 A／B」或回到規則管理右鍵啟動；下次不必重新準備。";
    });
    private async Task Launch(int index, CancellationToken ct) {
        var pair = CurrentPair(); if (pair.Length <= index) throw new InvalidOperationException("請先準備兩個沙盒實例。");
        await LaunchProfiles([pair[index]], ct);
    }
    private async Task LaunchProfiles(SandboxInstanceProfile[] profiles, CancellationToken ct) {
        if (!int.TryParse(LaunchInterval.Text, out int seconds)) throw new InvalidOperationException("啟動間隔請輸入 1–300 秒。");
        var progress = new Progress<string>(s => { if (!lifetime.IsCancellationRequested) ProgressText.Text = s; });
        var outcomes = await backend.StartSandboxes(profiles.Select(p => p.Id).ToArray(), seconds, progress, ct);
        completionText = string.Join("\n", outcomes.Select(r => profiles.Single(p => p.Id == r.Id).Name + "：" + r.Detail)) + "\n遊戲帳密請在遊戲視窗操作。";
    }
    private async void StartA(object sender, RoutedEventArgs e) => await Work(ct => Launch(0, ct));
    private async void StartB(object sender, RoutedEventArgs e) => await Work(ct => Launch(1, ct));
    private async void StartBoth(object sender, RoutedEventArgs e) => await Work(ct => LaunchProfiles(CurrentPair(), ct));
    private async void StopSelected(object sender, RoutedEventArgs e) { if (Instances.SelectedItem is InstanceRow row) await Work(_ => backend.StopSandbox(row.Profile.Id)); }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshState();
    private void CancelPreparation(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private async void Browse(object sender, RoutedEventArgs e) {
        if (busy) return; var dialog = new OpenFileDialog { Filter = "遊戲或捷徑|*.exe;*.bin;*.lnk", DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true) await ImportFile([dialog.FileName]);
    }
    private async Task ImportFile(string[] paths) { try { string path = await Task.Run(() => SandboxCompatibility.PreferLauncher(ProgramFileImport.Resolve(paths)), lifetime.Token); if (!lifetime.IsCancellationRequested) SourcePath.Text = path; } catch (Exception ex) { if (!lifetime.IsCancellationRequested) ProgressText.Text = ex.Message; } }
    private async void SourceChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (ready && !busy) { compatibilityChoiceMade = false; await InspectSource(); } }
    private async Task InspectSource() {
        sourceInspection?.Cancel();
        using var check = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); sourceInspection = check;
        string path = ProgramRuleInput.Normalize(SourcePath.Text);
        try {
            await Task.Delay(250, check.Token);
            bool yong = await backend.DetectYongOrigin(path, check.Token);
            if (check.IsCancellationRequested || busy) return;
            var pair = CurrentPair();
            if (pair.Length > 0) LineA.SelectedValue = pair[0].ProxyId;
            if (pair.Length > 1) LineB.SelectedValue = pair[1].ProxyId;
            if (!compatibilityChoiceMade) {
                applyingSource = true;
                YongCompatibility.IsChecked = pair.Any(p => p.IsolateDeviceIdentity == true) || yong && pair.All(p => p.IsolateDeviceIdentity is null);
                SimulateAdmin.IsChecked = YongCompatibility.IsChecked == true || pair.Any(p => p.SimulateAdmin);
                applyingSource = false;
            }
            CompatibilityHint.Text = yong ? "已辨識 YongOrigin：建議套用相容設定，請使用原始 Launcher.exe。" : "一般遊戲可保留預設；YongOrigin 請選原始 Launcher.exe 並套用相容設定。";
            Render();
        } catch (OperationCanceledException) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) CompatibilityHint.Text = "遊戲辨識未完成：" + ex.Message; }
        finally { if (ReferenceEquals(sourceInspection, check)) sourceInspection = null; }
    }
    private void CompatibilityChanged(object sender, RoutedEventArgs e) {
        if (ready && !applyingSource) compatibilityChoiceMade = true;
        if (SimulateAdmin is not null && YongCompatibility.IsChecked == true) SimulateAdmin.IsChecked = true;
    }
    private async Task CheckEnvironmentState() {
        try { bool installed = await backend.SandboxInstalled(lifetime.Token); if (!lifetime.IsCancellationRequested) EnvironmentText.Text = installed ? "Sandboxie 已安裝 · 可準備環境" : "尚未安裝 Sandboxie：請先開啟官方安裝頁，安裝後按重新檢查。"; }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) EnvironmentText.Text = "環境檢查未完成：" + ex.Message; }
    }
    private async void CheckEnvironment(object sender, RoutedEventArgs e) => await CheckEnvironmentState();
    private void OpenSandboxDownload(object sender, RoutedEventArgs e) {
        try { Process.Start(new ProcessStartInfo("https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.8") { UseShellExecute = true }); }
        catch (Exception ex) { ProgressText.Text = "無法開啟官方安裝頁：" + ex.Message; }
    }
    private void DragOverFile(object sender, DragEventArgs e) { if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; e.Handled = true; e.Effects = !busy && ProgramFileImport.CanImport(e.Data.GetData(DataFormats.FileDrop) as string[]) ? DragDropEffects.Copy : DragDropEffects.None; }
    private async void DropFile(object sender, DragEventArgs e) { if (busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return; e.Handled = true; await ImportFile(paths); }
}

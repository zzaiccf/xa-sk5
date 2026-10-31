using System;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    public sealed class LineAssignment(string name, Guid proxyId, LineOption[] options, bool useLocalNetwork = false, Guid? instanceId = null) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public Guid? InstanceId { get; } = instanceId;
        public Guid ProxyId { get; set; } = proxyId;
        public LineOption[] Options { get; } = options;
        private bool local = useLocalNetwork;
        public bool UseLocalNetwork { get => local; set { if (local == value) return; local = value; PropertyChanged?.Invoke(this, new(nameof(UseLocalNetwork))); PropertyChanged?.Invoke(this, new(nameof(CanSelectProxy))); } }
        public bool CanSelectProxy => !UseLocalNetwork;
        public SandboxNetworkChoice Choice => new(UseLocalNetwork ? Guid.Empty : ProxyId, UseLocalNetwork);
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly ObservableCollection<LineAssignment> assignments = new();
    private string assignmentSource = "";
    public sealed record InstanceRow(SandboxInstanceProfile Profile, string Line, string State, int Members, string Detail);
    public SandboxWindow(Backend backend, Snapshot state, string initialPath = "")
    {
        this.backend = backend; this.state = state; InitializeComponent();
        LineAssignments.ItemsSource = assignments;
        SourcePath.Text = Path.IsPathFullyQualified(initialPath) ? initialPath : state.SandboxInstances.FirstOrDefault()?.SourceLauncherPath ?? "";
        var pair = CurrentProfiles(); LoadAssignments();
        SimulateAdmin.IsChecked = pair.Any(p => p.SimulateAdmin);
        YongCompatibility.IsChecked = pair.Any(p => p.IsolateDeviceIdentity == true);
        Render(); timer.Tick += async (_, _) => { if (!busy) await RefreshState(); }; timer.Start();
        ready = true;
        Loaded += async (_, _) => { await CheckEnvironmentState(); await RefreshState(); await InspectSource(); };
        Closed += (_, _) => { lifetime.Cancel(); sourceInspection?.Cancel(); operation?.Cancel(); timer.Stop(); };
    }
    private SandboxInstanceProfile[] CurrentProfiles() => state.SandboxInstances.Where(p => p.SourceLauncherPath.Equals(ProgramRuleInput.Normalize(SourcePath.Text), StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id).ToArray();
    private LineOption[] Lines() => state.Proxies.Select(p => new LineOption(p.Id, p.Name + " · " + p.Endpoint)).ToArray();
    private int SlotCount() => state.Bindings.Select(b => b.TargetKey).Concat(state.SandboxInstances.Select(p => p.TargetKey)).Distinct(StringComparer.OrdinalIgnoreCase).Count() + Math.Max(0, assignments.Count - CurrentProfiles().Length);
    private void UpdateAssignmentButtons()
    {
        InstanceCountText.Text = $"此遊戲 {assignments.Count} 個 · 已用名額 {SlotCount()}/10";
        AddInstanceButton.IsEnabled = !busy && assignments.Count < SandboxPreparation.MaximumInstances && SlotCount() < SandboxPreparation.MaximumInstances;
        RemoveDraftButton.IsEnabled = !busy && assignments.Count > Math.Max(1, CurrentProfiles().Length);
    }
    private void AppendAssignment()
    {
        var lines = Lines();
        var used = assignments.Select(p => p.ProxyId).Concat(state.SandboxInstances.Select(p => p.ProxyId)).Concat(state.Bindings.Select(p => p.ProxyId)).ToHashSet();
        if (state.GlobalProxy.Enabled) used.Add(state.GlobalProxy.ProxyId);
        Guid proxy = lines.FirstOrDefault(p => !used.Contains(p.Id))?.Id ?? Guid.Empty;
        string source = Path.IsPathFullyQualified(assignmentSource) ? assignmentSource : @"C:\遊戲\Launcher.exe";
        assignments.Add(new(SandboxPreparation.NextName(source, assignments.Select(p => p.Name)), proxy, lines));
        UpdateAssignmentButtons();
    }
    private void LoadAssignments()
    {
        assignmentSource = ProgramRuleInput.Normalize(SourcePath.Text);
        assignments.Clear(); var lines = Lines();
        foreach (var profile in CurrentProfiles()) assignments.Add(new(profile.Name, profile.ProxyId, lines, profile.UseLocalNetwork, profile.Id));
        if (assignments.Count == 0) { AppendAssignment(); if (SlotCount() < SandboxPreparation.MaximumInstances) AppendAssignment(); }
        UpdateAssignmentButtons();
    }
    private void AddInstance(object sender, RoutedEventArgs e)
    {
        if (!busy && AddInstanceButton.IsEnabled) AppendAssignment();
    }
    private void RemoveDraft(object sender, RoutedEventArgs e)
    {
        if (!busy && RemoveDraftButton.IsEnabled) { assignments.RemoveAt(assignments.Count - 1); UpdateAssignmentButtons(); }
    }
    private void Render()
    {
        Guid? selected = (Instances.SelectedItem as InstanceRow)?.Profile.Id;
        var rows = CurrentProfiles().Select(p => {
            var status = state.SandboxStatuses.FirstOrDefault(s => s.Id == p.Id); var line = state.Proxies.FirstOrDefault(n => n.Id == p.ProxyId);
            string exit = status?.Network?.TcpExit ?? "待沙盒內驗證";
            return new InstanceRow(p, p.UseLocalNetwork ? "本地網路 · 共用本機出口 · 略過全局代理" : $"{line?.Name} · {line?.Port} · {exit}", status?.State ?? "未啟動", status?.Processes.Length ?? 0,
                status?.Detail + (status?.Network is { } network ? $"\n沙盒 TCP：{network.TcpExit} · UDP：{network.UdpExit} · 直接 DNS：{(network.DirectDns ? "通過" : "未通過")}" : ""));
        }).ToArray();
        Instances.ItemsSource = rows; Instances.SelectedItem = rows.FirstOrDefault(r => r.Profile.Id == selected) ?? rows.FirstOrDefault();
        UpdateAssignmentButtons();
    }
    private async Task RefreshState()
    {
        if (refreshing || lifetime.IsCancellationRequested) return; refreshing = true;
        try {
            var fresh = await backend.SandboxSnapshot(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            state = fresh;
            var lines = Lines();
            if (assignments.Any(row => !row.Options.SequenceEqual(lines))) {
                var choices = assignments.Select(row => new LineAssignment(row.Name, row.ProxyId, lines, row.UseLocalNetwork, row.InstanceId)).ToArray();
                assignments.Clear(); foreach (var row in choices) assignments.Add(row);
            }
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
        PrepareButton.IsEnabled = SaveNetworkButton.IsEnabled = StartSelectedButton.IsEnabled = StartAllButton.IsEnabled = StopButton.IsEnabled = false;
        InstallSandboxButton.IsEnabled = false;
        SourcePath.IsEnabled = LineAssignments.IsEnabled = SimulateAdmin.IsEnabled = YongCompatibility.IsEnabled = LaunchInterval.IsEnabled = false;
        UpdateAssignmentButtons();
        CancelWork.IsEnabled = true;
        try { await Dispatcher.Yield(DispatcherPriority.Background); await action(operation.Token); if (!lifetime.IsCancellationRequested) ProgressText.Text = completionText; }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) ProgressText.Text = "已取消未完成的工作；已完成的副本與先前啟動的實例保留。"; }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) ProgressText.Text = ex.Message; }
        finally { operation.Dispose(); operation = null; busy = false; if (!lifetime.IsCancellationRequested) {
            PrepareButton.IsEnabled = SaveNetworkButton.IsEnabled = StartSelectedButton.IsEnabled = StartAllButton.IsEnabled = StopButton.IsEnabled = true; CancelWork.IsEnabled = false;
            SourcePath.IsEnabled = LineAssignments.IsEnabled = SimulateAdmin.IsEnabled = YongCompatibility.IsEnabled = LaunchInterval.IsEnabled = true;
            await RefreshState();
            await CheckEnvironmentState();
        } }
    }
    private async void Prepare(object sender, RoutedEventArgs e) => await Work(async ct => {
        string input = SourcePath.Text;
        string source = await Task.Run(() => SandboxCompatibility.PreferLauncher(ProgramFileImport.Resolve([input])), ct); SourcePath.Text = source;
        // A shortcut/client path can resolve to another game; never carry the previous game's rows across.
        if (!source.Equals(assignmentSource, StringComparison.OrdinalIgnoreCase)) LoadAssignments();
        var existing = CurrentProfiles();
        var selectedLines = assignments.Select(row => row.Choice).ToArray();
        var pair = await backend.ResumeSandboxNetworkPreparation(source, selectedLines, existing, ct);
        if (!compatibilityChoiceMade && existing.All(p => p.IsolateDeviceIdentity is null) && await backend.DetectYongOrigin(source, ct)) {
            applyingSource = true; YongCompatibility.IsChecked = true; applyingSource = false;
        }
        var progress = new Progress<string>(s => { if (!lifetime.IsCancellationRequested) ProgressText.Text = s; });
        bool compatibility = YongCompatibility.IsChecked == true, admin = compatibility || SimulateAdmin.IsChecked == true;
        await EnsureEnvironment(ct);
        ProgressText.Text = "正在檢查線路與環境…";
        await backend.PrepareSandboxes(pair.Select(p => p with { SimulateAdmin = admin, IsolateDeviceIdentity = compatibility }).ToArray(), progress, ct);
        await RefreshState(); LoadAssignments();
        completionText = $"{pair.Length} 個環境已準備好。可選取實例啟動、批量啟動此遊戲，或回到規則管理右鍵啟動。";
    });
    private async void SaveNetwork(object sender, RoutedEventArgs e) => await Work(async ct => {
        var saved = CurrentProfiles();
        var changes = assignments.Where(row => row.InstanceId.HasValue).Select(row => {
                var p = saved.Single(old => old.Id == row.InstanceId);
                return p with { UseLocalNetwork = row.UseLocalNetwork, ProxyId = row.Choice.ProxyId };
            })
            .Where(p => saved.Any(old => old.Id == p.Id && (old.UseLocalNetwork != p.UseLocalNetwork || old.ProxyId != p.ProxyId))).ToArray();
        if (changes.Length == 0) { completionText = "既有實例的網路選擇沒有變更；新實例請使用準備環境。"; return; }
        await backend.SaveSandboxNetworks(changes, new Progress<string>(s => ProgressText.Text = s), ct);
        await RefreshState(); LoadAssignments();
        completionText = $"已保存 {changes.Length} 個實例的網路選擇，重新啟動後使用新設定。";
    });
    private async Task LaunchProfiles(SandboxInstanceProfile[] profiles, CancellationToken ct) {
        if (!int.TryParse(LaunchInterval.Text, out int seconds)) throw new InvalidOperationException("啟動間隔請輸入 1–300 秒。");
        var progress = new Progress<string>(s => { if (!lifetime.IsCancellationRequested) ProgressText.Text = s; });
        var outcomes = await backend.StartSandboxes(profiles.Select(p => p.Id).ToArray(), seconds, progress, ct);
        completionText = string.Join("\n", outcomes.Select(r => profiles.Single(p => p.Id == r.Id).Name + "：" + r.Detail)) + "\n遊戲帳密請在遊戲視窗操作。";
    }
    private async void StartSelected(object sender, RoutedEventArgs e) {
        if (Instances.SelectedItem is InstanceRow row) await Work(ct => LaunchProfiles([row.Profile], ct));
        else ProgressText.Text = "請先準備環境，並從下方列表選取實例。";
    }
    private async void StartAll(object sender, RoutedEventArgs e) => await Work(ct => LaunchProfiles(CurrentProfiles(), ct));
    private async void StopSelected(object sender, RoutedEventArgs e) { if (Instances.SelectedItem is InstanceRow row) await Work(_ => backend.StopSandbox(row.Profile.Id)); }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshState();
    private async void ManageStorage(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var before = CurrentProfiles().Select(p => p.Id).ToArray();
        var dialog = new SandboxStorageWindow(backend, (Instances.SelectedItem as InstanceRow)?.Profile.Id) { Owner = this };
        dialog.ShowDialog(); await RefreshState();
        if (!before.SequenceEqual(CurrentProfiles().Select(p => p.Id))) LoadAssignments();
    }
    private void CancelPreparation(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private async void Browse(object sender, RoutedEventArgs e) {
        if (busy) return; var dialog = new OpenFileDialog { Filter = "遊戲或捷徑|*.exe;*.bin;*.lnk", DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true) await ImportFile([dialog.FileName]);
    }
    private async Task ImportFile(string[] paths) { try { string path = await Task.Run(() => SandboxCompatibility.PreferLauncher(ProgramFileImport.Resolve(paths)), lifetime.Token); if (!lifetime.IsCancellationRequested) SourcePath.Text = path; } catch (Exception ex) { if (!lifetime.IsCancellationRequested) ProgressText.Text = ex.Message; } }
    private async void SourceChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (ready && !busy) { compatibilityChoiceMade = false; LoadAssignments(); Render(); await InspectSource(); } }
    private async Task InspectSource() {
        sourceInspection?.Cancel();
        using var check = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); sourceInspection = check;
        string path = ProgramRuleInput.Normalize(SourcePath.Text);
        try {
            await Task.Delay(250, check.Token);
            bool yong = await backend.DetectYongOrigin(path, check.Token);
            if (check.IsCancellationRequested || busy) return;
            var pair = CurrentProfiles();
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
        try { var environment = await backend.SandboxEnvironment(lifetime.Token); if (!lifetime.IsCancellationRequested) { EnvironmentText.Text = environment.Detail; InstallSandboxButton.IsEnabled = !busy && environment.CanInstall; } }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) EnvironmentText.Text = "環境檢查未完成：" + ex.Message; }
    }
    private Task EnsureEnvironment(CancellationToken ct) => backend.EnsureSandboxInstalled(new Progress<UpdateProgress>(p => {
        if (!lifetime.IsCancellationRequested) { ProgressText.Text = p.Message; CancelWork.IsEnabled = p.CanCancel; }
    }), ct);
    private async void InstallEnvironment(object sender, RoutedEventArgs e) => await Work(async ct => {
        await EnsureEnvironment(ct); completionText = "Sandboxie 已安裝並通過檔案版本檢查。選好遊戲與線路後，按「準備環境／保存」。";
    });
    private async void CheckEnvironment(object sender, RoutedEventArgs e) => await CheckEnvironmentState();
    private void OpenSandboxDownload(object sender, RoutedEventArgs e) {
        try { Process.Start(new ProcessStartInfo(SandboxieInstaller.OfficialPage) { UseShellExecute = true }); }
        catch (Exception ex) { ProgressText.Text = "無法開啟官方安裝頁：" + ex.Message; }
    }
    private void DragOverFile(object sender, DragEventArgs e) { if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; e.Handled = true; e.Effects = !busy && ProgramFileImport.CanImport(e.Data.GetData(DataFormats.FileDrop) as string[]) ? DragDropEffects.Copy : DragDropEffects.None; }
    private async void DropFile(object sender, DragEventArgs e) { if (busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return; e.Handled = true; await ImportFile(paths); }
}

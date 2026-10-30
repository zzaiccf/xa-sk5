using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class AutoProxyWindow : Window
{
    private readonly string consolePath;
    private readonly string programConsolePath;
    private readonly ProxyProfile[] profiles;
    private readonly ProxyOption[] options;
    private readonly ObservableCollection<MappingRow> mappings = new();
    private readonly ObservableCollection<CapacityRow> capacities = new();
    private readonly Dictionary<string, TargetInstance> discovered = new();
    private string mumuPath;
    private string ldPath;
    private string vendorKind = "LDPlayer";
    private bool ready;
    private bool importingProgram;
    private readonly CancellationTokenSource lifetime = new();
    public TargetInstance[] DiscoveredTargets => discovered.Values.ToArray();
    public string MuMuConsolePath => vendorKind == "MuMu" ? EmulatorPath.Text.Trim() : mumuPath;
    public string LdConsolePath => vendorKind == "LDPlayer" ? EmulatorPath.Text.Trim() : ldPath;
    public AutoProxyRule? Rule { get; private set; }
    public bool VmwareRequested { get; private set; }
    public bool SandboxRequested { get; private set; }
    public string SandboxLauncherPath => ProgramRuleInput.Normalize(Pattern.Text);
    private void OpenSandboxMode(object sender, RoutedEventArgs e) { SandboxRequested = true; DialogResult = true; }
    public AutoProxyWindow(AutoProxyRule rule, string consolePath, int proxyCount, ProxyProfile[]? profiles = null,
        ProbeResult[]? probes = null, TargetInstance[]? targets = null, string mumuConsole = "", InstanceBinding[]? bindings = null)
    {
        this.consolePath = consolePath; programConsolePath = rule.ConsolePath; ldPath = consolePath; mumuPath = mumuConsole; this.profiles = profiles ?? [];
        options = new[] { new ProxyOption(Guid.Empty, "未指定 · 不代理") }.Concat(this.profiles.Select(p => {
            var node = new ProxyNodeDisplay(p, probes?.FirstOrDefault(r => r.ProxyId == p.Id));
            return new ProxyOption(p.Id, $"{node.ExitIp} · {node.AccountPort}");
        })).ToArray();
        InitializeComponent(); MappingGrid.ItemsSource = mappings;
        CapacityGrid.ItemsSource = capacities;
        foreach (var profile in this.profiles) {
            var node = new ProxyNodeDisplay(profile, probes?.FirstOrDefault(p => p.ProxyId == profile.Id));
            capacities.Add(new(profile.Id, $"{profile.Name} · {node.ExitIp} · {node.AccountPort}",
                ProxyCapacity.For(profile.Id, rule.PerProxy, rule.ProxyCapacities), bindings?.Count(b => b.ProxyId == profile.Id) ?? 0));
        }
        CapacityHint.Text = $"未個別設定的線路預設 {rule.PerProxy} 個；0＝不再分配新目標。降低數量會保留既有窗口 IP，待名額釋放後再分配。目前最多同時管理 10 個目標。";
        PoolSummary.Text = $"{proxyCount} 個節點 · 儲存後依規則分配 IP。";
        foreach (var item in rule.EmulatorAssignments) {
            var rowOptions = options.Any(o => o.Id == item.ProxyId) ? options : options.Append(new ProxyOption(item.ProxyId, "節點未移交或已移除 · 請重新指定")).ToArray();
            mappings.Add(new(item.Kind, item.ConsolePath, item.Index, item.Name, item.ProxyId, rowOptions));
            if (item.Kind == "MuMu" && mumuPath.Length == 0) mumuPath = item.ConsolePath;
        }
        MergeTargets(targets ?? [], bindings ?? []);
        Mode.SelectedIndex = rule.Kind is "LDPlayer" or "Emulator" or "EmulatorSequence" ? 0 : 1;
        AllocationMode.SelectedIndex = rule.Kind == "Emulator" ? 1 : 0;
        if (rule.Kind == "EmulatorSequence") {
            vendorKind = rule.EmulatorKind;
            if (vendorKind == "MuMu") mumuPath = rule.ConsolePath; else ldPath = rule.ConsolePath;
            Vendor.SelectedIndex = vendorKind == "MuMu" ? 1 : 0;
        } else if (rule.Kind == "LDPlayer") ldPath = rule.ConsolePath;
        Pattern.Text = rule.Pattern; ClientPath.Text = rule.ClientPath; Limit.Text = rule.PerProxy.ToString();
        SpecifyClient.IsChecked = rule.Kind == "GameGroup"; ProgramAdvanced.IsExpanded = SpecifyClient.IsChecked == true;
        UpdateMode();
        EmulatorPath.Text = vendorKind == "MuMu" ? mumuPath : ldPath;
        if (rule.Kind is "Emulator" or "EmulatorSequence" or "LDPlayer") EnableRule.IsChecked = rule.Enabled;
        ready = true;
        Closed += (_, _) => lifetime.Cancel();
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e) {
        if (ready && Mode.SelectedIndex == 2) { VmwareRequested = true; DialogResult = true; return; }
        if (ClientPanel is not null) UpdateMode();
    }
    private void AllocationChanged(object sender, SelectionChangedEventArgs e) { if (ClientPanel is not null) UpdateMode(); }
    private void ProgramChanged(object sender, RoutedEventArgs e) { if (ClientPanel is not null) UpdateMode(); }
    private void UpdateMode()
    {
        bool fixedMode = AllocationMode?.SelectedIndex == 1;
        bool paired = SpecifyClient.IsChecked == true;
        ProgramPanel.Visibility = Mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        Pattern.Tag = paired ? "登入器完整執行檔位置" : "例如 Login.exe 或完整路徑";
        if (EmulatorPanel is not null) EmulatorPanel.Visibility = Mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EnableRule is not null) EnableRule.Visibility = Mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (FixedActions is not null) FixedActions.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        if (FixedMappingPanel is not null) FixedMappingPanel.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        if (FixedLimitPanel is not null) FixedLimitPanel.Visibility = Mode.SelectedIndex == 0 && fixedMode ? Visibility.Visible : Visibility.Collapsed;
        if (LineCapacityPanel is not null) LineCapacityPanel.Visibility = Mode.SelectedIndex == 0 && fixedMode ? Visibility.Collapsed : Visibility.Visible;
        PatternLabel.Text = paired ? "登入器完整路徑" : "登入器或程式";
        string target = ProgramRuleInput.Normalize(Pattern.Text);
        MatchHint.Text = paired ? "指定搭配遊戲時，登入器與遊戲都使用完整路徑辨識。"
            : target.Length == 0 ? "填檔名或完整路徑即可，自動辨識匹配方式。"
            : ProgramRuleInput.Match(target) == TargetMatch.ExecutablePath ? "依完整路徑辨識 · 只匹配指定位置的程式。" : "依檔名辨識 · 不同資料夾的同名程式都會匹配。";
        ClientPanel.Visibility = paired ? Visibility.Visible : Visibility.Collapsed;
        if (Children is not null) { Children.Visibility = Mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; Children.Text = paired ? "登入器與指定遊戲 · 共用相同 IP" : "登入後自動接續遊戲與子程序 · 沿用相同 IP"; }
        if (ModeNote is not null) ModeNote.Text = Mode.SelectedIndex switch {
            0 when !fixedMode => "先啟動的窗口先取得清單前方 IP；已有分配保留，關閉後釋放名額，重開重新排入。總計最多 10 個窗口；MuMu 首次辨識新窗口須保持介面開啟。",
            0 => "雷電與 MuMu 可同時配置。使用原始實例編號，未啟動也保留；總計最多 10 個窗口。僅支援可辨識的 NAT 網路程序。",
            1 when paired => "登入器與指定遊戲視為一組，共用同一個 IP 與名額。關閉進階指定後，改為自動接續登入器啟動的遊戲與子程序。",
            _ => "先儲存規則，再開啟登入器或程式。登入後自動接續它啟動的遊戲與子程序，共用同一個 IP 與名額。"
        };
    }
    private void BrowseProgram(object sender, RoutedEventArgs e) => BrowseExecutable(Pattern);
    private void CapacityGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The outer scroll viewer measures the auto-height table before its viewport settles.
        // Give the line label the available width instead of leaving its star column at minimum width.
        if (CapacityGrid.Columns.Count > 1) CapacityGrid.Columns[1].Width = new DataGridLength(Math.Max(180, CapacityGrid.ActualWidth - 48 - 75 - 110 - 20));
    }
    private void SelectAllCapacities(object sender, RoutedEventArgs e) { foreach (var row in capacities) row.Selected = true; }
    private void ClearCapacitySelection(object sender, RoutedEventArgs e) { foreach (var row in capacities) row.Selected = false; }
    private void ApplyCapacityBatch(object sender, RoutedEventArgs e)
    {
        try {
            if (!int.TryParse(BatchCapacity.Text, out int count) || count is < 0 or > 10) throw new InvalidOperationException("批量數量請填 0–10。");
            var selected = capacities.Where(c => c.Selected).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("請先勾選要調整的線路。");
            foreach (var row in selected) row.CountText = count.ToString();
            Error.Text = "";
        } catch (InvalidOperationException ex) { Error.Text = ex.Message; }
    }
    private void BrowseClient(object sender, RoutedEventArgs e) => BrowseExecutable(ClientPath);
    private async void BrowseExecutable(TextBox target)
    {
        var dialog = new OpenFileDialog { Title = "選擇遊戲執行檔或捷徑", Filter = "程式、遊戲與捷徑|*.exe;*.bin;*.lnk|所有檔案|*.*", DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true) await ImportProgramFiles([dialog.FileName], target);
    }
    private void ProgramDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = !importingProgram && ProgramFileImport.CanImport(e.Data.GetData(DataFormats.FileDrop) as string[]) &&
            (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }
    private async void ProgramDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // TextBox handles its own drag events; the window preview receives Explorer drops first.
        var source = e.OriginalSource as DependencyObject;
        bool client = source is not null && (ReferenceEquals(source, ClientPath) || ClientPath.IsAncestorOf(source));
        if (ProgramFileImport.CanImport(paths) && (e.AllowedEffects & DragDropEffects.Copy) != 0) e.Effects = DragDropEffects.Copy;
        await ImportProgramFiles(paths, client ? ClientPath : Pattern);
    }
    private async Task<bool> ImportProgramFiles(string[] paths, TextBox target)
    {
        if (importingProgram) return false;
        importingProgram = true; SaveRuleButton.IsEnabled = false; Error.Text = "正在讀取遊戲位置…";
        try {
            string path = await Task.Run(() => ProgramFileImport.Resolve(paths), lifetime.Token);
            if (lifetime.IsCancellationRequested) return false;
            Mode.SelectedIndex = 1; target.Text = path; Error.Text = ""; return true;
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) Error.Text = ex.Message; return false; }
        finally { importingProgram = false; if (!lifetime.IsCancellationRequested) SaveRuleButton.IsEnabled = true; }
    }
    private void VendorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (vendorKind == "MuMu") mumuPath = EmulatorPath.Text.Trim(); else ldPath = EmulatorPath.Text.Trim();
        vendorKind = Vendor.SelectedIndex == 1 ? "MuMu" : "LDPlayer";
        EmulatorPath.Text = vendorKind == "MuMu" ? mumuPath : ldPath;
    }
    private void BrowseEmulator(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = vendorKind == "MuMu" ? "MuMu 管理程式|MuMuManager.exe" : "雷電管理程式|ldconsole.exe;dnconsole.exe" };
        if (dialog.ShowDialog(this) == true) EmulatorPath.Text = dialog.FileName;
    }
    private void MergeTargets(IEnumerable<TargetInstance> targets, InstanceBinding[]? bindings = null)
    {
        foreach (var target in targets.Where(t => EmulatorCatalog.IsEmulator(t.Kind)).OrderBy(t => t.Kind).ThenBy(t => t.Path).ThenBy(t => t.Index)) {
            discovered[target.Key] = target;
            var row = mappings.FirstOrDefault(m => m.TargetKey == target.Key);
            if (row is null) mappings.Add(new(target.Kind, target.Path, target.Index, target.Name, bindings?.FirstOrDefault(b => b.TargetKey == target.Key)?.ProxyId ?? Guid.Empty, options));
            else row.Name = target.Name;
        }
    }
    private async void ReadEmulators(object sender, RoutedEventArgs e)
    {
        if (!ReadInstances.IsEnabled) return;
        var ct = lifetime.Token; string kind = vendorKind, path = EmulatorPath.Text.Trim();
        ReadInstances.IsEnabled = false; Error.Text = "正在讀取窗口…";
        try { var found = await System.Threading.Tasks.Task.Run(() => EmulatorCatalog.DiscoverAsync(kind, path, ct), ct); if (!ct.IsCancellationRequested) { MergeTargets(found); Error.Text = ""; } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Error.Text = ex.Message; }
        finally { if (!ct.IsCancellationRequested) ReadInstances.IsEnabled = true; }
    }
    private void AddEmulator(object sender, RoutedEventArgs e)
    {
        try {
            string path = EmulatorPath.Text.Trim(); EmulatorCatalog.ValidateConsole(vendorKind, path);
            if (!int.TryParse(WindowIndex.Text, out int index) || index < 0) throw new InvalidOperationException("請填寫模擬器原始窗口編號（可從 0 開始）。");
            MergeTargets([new(EmulatorCatalog.Key(vendorKind, path, index), $"{EmulatorCatalog.Name(vendorKind)} {index}", vendorKind, path, index, false, [])]); Error.Text = "";
        } catch (Exception ex) { Error.Text = ex.Message; }
    }
    private void DistributeEmulators(object sender, RoutedEventArgs e)
    {
        try {
            if (!int.TryParse(Limit.Text, out int count) || count is < 1 or > 10) throw new InvalidOperationException("每個節點請填 1–10 個窗口。");
            if (mappings.Count is < 1 or > 10) throw new InvalidOperationException("請先讀取或加入 1–10 個窗口；更多窗口可在表格逐條指定。");
            if (mappings.Count > profiles.Length * count) throw new InvalidOperationException("節點容量不足，請增加每個節點的窗口數或匯入更多節點。");
            var sorted = mappings.OrderBy(m => m.Kind).ThenBy(m => m.ConsolePath, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Index).ToArray();
            for (int i = 0; i < sorted.Length; i++) sorted[i].ProxyId = profiles[i / count].Id;
            Error.Text = "";
        } catch (InvalidOperationException ex) { Error.Text = ex.Message; }
    }
    private void Confirm(object sender, RoutedEventArgs e)
    {
        try {
            if (!int.TryParse(Limit.Text, out int count)) throw new InvalidOperationException("請輸入 1–10 的數字。");
            bool sequential = Mode.SelectedIndex == 0 && AllocationMode.SelectedIndex == 0;
            var rule = Mode.SelectedIndex == 1 ? ProgramRuleInput.Create(Pattern.Text, SpecifyClient.IsChecked == true, ClientPath.Text, count) with { ConsolePath = programConsolePath } : new AutoProxyRule { Enabled = EnableRule.IsChecked == true, Kind = sequential ? "EmulatorSequence" : "Emulator", PerProxy = count, ConsolePath = sequential ? EmulatorPath.Text.Trim() : consolePath, EmulatorKind = vendorKind,
                EmulatorAssignments = Mode.SelectedIndex == 0 && !sequential ? mappings.Where(m => m.ProxyId != Guid.Empty).Select(m => new EmulatorAssignment(m.Kind, m.ConsolePath, m.Index, m.Name, m.ProxyId)).ToArray() : [] };
            if (rule.Kind == "Emulator" && rule.EmulatorAssignments.Length == 0) rule = rule with { Enabled = false };
            if (rule.Kind != "Emulator") {
                var counts = new Dictionary<Guid, int>();
                foreach (var row in capacities) {
                    if (!int.TryParse(row.CountText, out int capacity) || capacity is < 0 or > 10)
                        throw new InvalidOperationException($"線路「{row.Label}」的可分配數量請填 0–10。");
                    if (capacity != count) counts.Add(row.ProxyId, capacity);
                }
                rule = rule with { ProxyCapacities = counts };
            }
            AutoProxyPlanner.Validate(rule); Rule = rule; DialogResult = true;
        } catch (InvalidOperationException ex) { Error.Text = ex.Message; }
    }
    public sealed record ProxyOption(Guid Id, string Label);
    public sealed class CapacityRow(Guid proxyId, string label, int count, int used) : INotifyPropertyChanged
    {
        public Guid ProxyId => proxyId;
        public string Label => label;
        public int Used => used;
        private string text = count.ToString();
        public string CountText { get => text; set { text = value; PropertyChanged?.Invoke(this, new(nameof(CountText))); } }
        private bool selected;
        public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed class MappingRow(string kind, string consolePath, int index, string name, Guid proxyId, ProxyOption[] options) : INotifyPropertyChanged
    {
        public string Kind => kind;
        public string ConsolePath => consolePath;
        public int Index => index;
        public string TargetKey => EmulatorCatalog.Key(kind, consolePath, index);
        public string Identity => $"{EmulatorCatalog.Name(kind)} / #{index}";
        private string displayName = name;
        public string Name { get => displayName; set { displayName = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        private Guid selectedProxy = proxyId;
        public Guid ProxyId { get => selectedProxy; set { selectedProxy = value; PropertyChanged?.Invoke(this, new(nameof(ProxyId))); PropertyChanged?.Invoke(this, new(nameof(SelectedLabel))); } }
        public ProxyOption[] Options => options;
        public string SelectedLabel => options.FirstOrDefault(p => p.Id == selectedProxy)?.Label ?? "節點已移除，請重新指定";
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

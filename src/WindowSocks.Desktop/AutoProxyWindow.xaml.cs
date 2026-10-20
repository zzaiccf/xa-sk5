using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class AutoProxyWindow : Window
{
    private readonly string consolePath;
    private readonly ProxyProfile[] profiles;
    private readonly ProxyOption[] options;
    private readonly ObservableCollection<MappingRow> mappings = new();
    private readonly Dictionary<string, TargetInstance> discovered = new();
    private string mumuPath;
    private string ldPath;
    private string vendorKind = "LDPlayer";
    private bool ready;
    public TargetInstance[] DiscoveredTargets => discovered.Values.ToArray();
    public string MuMuConsolePath => vendorKind == "MuMu" ? EmulatorPath.Text.Trim() : mumuPath;
    public string LdConsolePath => vendorKind == "LDPlayer" ? EmulatorPath.Text.Trim() : ldPath;
    public AutoProxyRule? Rule { get; private set; }
    public AutoProxyWindow(AutoProxyRule rule, string consolePath, int proxyCount, ProxyProfile[]? profiles = null,
        ProbeResult[]? probes = null, TargetInstance[]? targets = null, string mumuConsole = "", InstanceBinding[]? bindings = null)
    {
        this.consolePath = consolePath; ldPath = consolePath; mumuPath = mumuConsole; this.profiles = profiles ?? [];
        options = new[] { new ProxyOption(Guid.Empty, "未指定 · 不代理") }.Concat(this.profiles.Select(p => {
            var node = new ProxyNodeDisplay(p, probes?.FirstOrDefault(r => r.ProxyId == p.Id));
            return new ProxyOption(p.Id, $"{node.ExitIp} · {node.AccountPort}");
        })).ToArray();
        InitializeComponent(); MappingGrid.ItemsSource = mappings;
        PoolSummary.Text = $"{proxyCount} 個節點 · 可依啟動順序或固定窗口編號配置。";
        foreach (var item in rule.EmulatorAssignments) {
            var rowOptions = options.Any(o => o.Id == item.ProxyId) ? options : options.Append(new ProxyOption(item.ProxyId, "節點未移交或已移除 · 請重新指定")).ToArray();
            mappings.Add(new(item.Kind, item.ConsolePath, item.Index, item.Name, item.ProxyId, rowOptions));
            if (item.Kind == "MuMu" && mumuPath.Length == 0) mumuPath = item.ConsolePath;
        }
        MergeTargets(targets ?? [], bindings ?? []);
        Mode.SelectedIndex = rule.Kind is "LDPlayer" or "Emulator" or "EmulatorSequence" ? 0 : rule.Kind == "GameGroup" ? 3 : rule.Match == TargetMatch.ExecutablePath ? 2 : 1;
        AllocationMode.SelectedIndex = rule.Kind is "LDPlayer" or "EmulatorSequence" ? 1 : 0;
        if (rule.Kind == "EmulatorSequence") {
            vendorKind = rule.EmulatorKind;
            if (vendorKind == "MuMu") mumuPath = rule.ConsolePath; else ldPath = rule.ConsolePath;
            Vendor.SelectedIndex = vendorKind == "MuMu" ? 1 : 0;
        } else if (rule.Kind == "LDPlayer") ldPath = rule.ConsolePath;
        Pattern.Text = rule.Pattern; ClientPath.Text = rule.ClientPath; Children.IsChecked = rule.IncludeChildren; Limit.Text = rule.PerProxy.ToString(); UpdateMode();
        EmulatorPath.Text = vendorKind == "MuMu" ? mumuPath : ldPath;
        if (rule.Kind is "Emulator" or "EmulatorSequence" or "LDPlayer") EnableRule.IsChecked = rule.Enabled;
        ready = true;
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e) { if (Pattern is not null) UpdateMode(); }
    private void AllocationChanged(object sender, SelectionChangedEventArgs e) { if (Pattern is not null) UpdateMode(); }
    private void UpdateMode()
    {
        bool fixedMode = AllocationMode?.SelectedIndex != 1;
        Pattern.IsEnabled = Mode.SelectedIndex != 0;
        Pattern.Visibility = Mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        Pattern.Tag = Mode.SelectedIndex == 1 ? "例如 game.exe" : "完整執行檔位置";
        if (EmulatorPanel is not null) EmulatorPanel.Visibility = Mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EnableRule is not null) EnableRule.Visibility = Mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (FixedActions is not null) FixedActions.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        if (FixedMappingPanel is not null) FixedMappingPanel.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        if (LimitLabel is not null) { LimitLabel.Text = Mode.SelectedIndex == 0 && fixedMode ? "依編號配置時，每個節點的窗口數" : "每個節點的目標數"; LimitHint.Text = Mode.SelectedIndex == 0 && fixedMode ? "僅供快速配置；表格可逐條調整" : "1–10 個窗口或遊戲組"; }
        if (PatternLabel is not null) {
            PatternLabel.Text = Mode.SelectedIndex == 3 ? "登入器完整路徑" : Mode.SelectedIndex == 2 ? "執行檔完整路徑" : "程序檔名";
            PatternLabel.Visibility = Pattern.Visibility;
        }
        if (ClientPanel is not null) ClientPanel.Visibility = Mode.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (Children is not null) { Children.IsEnabled = Mode.SelectedIndex is 1 or 2; Children.Visibility = Children.IsEnabled ? Visibility.Visible : Visibility.Collapsed; }
        if (ModeNote is not null) ModeNote.Text = Mode.SelectedIndex switch {
            0 when !fixedMode => "先啟動的窗口先取得清單前方 IP；已有分配保留，關閉後釋放名額，重開重新排入。總計最多 10 個窗口；MuMu 首次辨識新窗口須保持介面開啟。",
            0 => "雷電與 MuMu 可同時配置。使用原始實例編號，未啟動也保留；總計最多 10 個窗口。僅支援可辨識的 NAT 網路程序。",
            1 => "檔名採完全匹配；探索前可能直連，需啟動保護時請改用完整路徑。",
            2 => "儲存後先攔截指定路徑的新連線，再分配代理；既有連線需重新建立。",
            _ => "同組登入器與遊戲共用出口，僅接管這兩個指定程式。"
        };
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
        ReadInstances.IsEnabled = false; Error.Text = "正在讀取窗口…";
        try { MergeTargets(await EmulatorCatalog.DiscoverAsync(vendorKind, EmulatorPath.Text.Trim(), CancellationToken.None)); Error.Text = ""; }
        catch (Exception ex) { Error.Text = ex.Message; }
        finally { ReadInstances.IsEnabled = true; }
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
            bool sequential = Mode.SelectedIndex == 0 && AllocationMode.SelectedIndex == 1;
            var rule = new AutoProxyRule { Enabled = Mode.SelectedIndex != 0 || EnableRule.IsChecked == true, Kind = Mode.SelectedIndex == 0 ? sequential ? "EmulatorSequence" : "Emulator" : Mode.SelectedIndex == 3 ? "GameGroup" : "Process", Match = Mode.SelectedIndex >= 2 ? TargetMatch.ExecutablePath : TargetMatch.ExecutableName, Pattern = Pattern.Text.Trim(), ClientPath = Mode.SelectedIndex == 3 ? ClientPath.Text.Trim() : "", IncludeChildren = Children.IsEnabled && Children.IsChecked == true, PerProxy = count, ConsolePath = sequential ? EmulatorPath.Text.Trim() : consolePath, EmulatorKind = vendorKind,
                EmulatorAssignments = Mode.SelectedIndex == 0 && !sequential ? mappings.Where(m => m.ProxyId != Guid.Empty).Select(m => new EmulatorAssignment(m.Kind, m.ConsolePath, m.Index, m.Name, m.ProxyId)).ToArray() : [] };
            if (rule.Kind == "Emulator" && rule.EmulatorAssignments.Length == 0) rule = rule with { Enabled = false };
            AutoProxyPlanner.Validate(rule); Rule = rule; DialogResult = true;
        } catch (InvalidOperationException ex) { Error.Text = ex.Message; }
    }
    public sealed record ProxyOption(Guid Id, string Label);
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

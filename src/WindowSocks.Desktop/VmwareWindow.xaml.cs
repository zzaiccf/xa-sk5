using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class VmwareWindow : Window
{
    private readonly Backend backend;
    private readonly ProxyProfile[] proxies;
    private readonly ObservableCollection<Row> rows = new();
    private readonly Option[] options;
    private VmwareAdapter[] adapters;
    private readonly CancellationTokenSource lifetime = new();
    private VmwareState? currentState;
    private readonly bool quickSetup;
    private readonly bool localNetworkSupported;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool working, capturing, refreshing, hasBackup, adaptersReady;
    private VmwareDiscovered[] discovered = [];
    public bool ConfigurationSaved { get; private set; }
    public VmwareWindow(Backend backend, Snapshot snapshot, VmwareAdapter[] adapters, Func<CancellationToken, Task<VmwareAdapter[]>>? loadAdapters = null)
    {
        this.backend = backend; this.adapters = adapters; proxies = snapshot.Proxies; quickSetup = snapshot.SupportsVmwareQuickSetup; localNetworkSupported = snapshot.SupportsVmwareLocalNetwork;
        options = new[] { new Option("blocked", "未指定 · 已封鎖"), new Option("local", "本地網路") }.Where(o => o.Id != "local" || localNetworkSupported).Concat(proxies.Select(p => { var node = new ProxyNodeDisplay(p, snapshot.Probes.FirstOrDefault(r => r.ProxyId == p.Id)); return new Option(p.Id.ToString(), node.ExitIp + " · " + node.AccountPort); })).ToArray();
        InitializeComponent(); Machines.ItemsSource = rows;
        Adapter.ItemsSource = new[] { new NetworkOption("", "自動選擇（建議）") }.Concat(adapters.Select(a => new NetworkOption(a.Id, a.Name + " · " + a.Address))).ToArray();
        var settings = snapshot.Vmware?.Settings ?? new();
        Adapter.SelectedValue = settings.AdapterId;
        if (Adapter.SelectedIndex < 0) Adapter.SelectedIndex = 0;
        Allocation.SelectedIndex = settings.Machines.Length == 0 || settings.ByStartup ? 1 : 0; PerProxy.Text = settings.PerProxy.ToString();
        OverflowToLocal.IsEnabled = localNetworkSupported;
        OverflowToLocal.IsChecked = localNetworkSupported && (settings.Machines.Length == 0 || settings.OverflowToLocal);
        if (!localNetworkSupported) OverflowToLocal.ToolTip = "請安裝新版私人測試安裝包以使用本地網路。";
        foreach (var mapping in settings.Machines) {
            rows.Add(new(mapping.VmxPath, string.IsNullOrWhiteSpace(mapping.Name) ? Path.GetFileNameWithoutExtension(mapping.VmxPath) : mapping.Name, mapping.ProxyId, options) { GuestIp = mapping.GuestIp, UseLocalNetwork = mapping.UseLocalNetwork, ByStartup = Allocation.SelectedIndex == 1 });
        }
        adaptersReady = loadAdapters is null;
        RenderState(snapshot.Vmware);
        timer.Tick += async (_, _) => {
            if (working || capturing || refreshing) return;
            refreshing = true;
            try { var state = await backend.Snapshot([]).WaitAsync(lifetime.Token); if (!lifetime.IsCancellationRequested) RenderState(state.Vmware); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch { if (!lifetime.IsCancellationRequested) Status.Text = "服務暫時未連線，代理狀態未確認。"; }
            finally { refreshing = false; }
        };
        Loaded += async (_, _) => {
            timer.Start();
            if (loadAdapters is not null) _ = LoadAdapters(loadAdapters, settings.AdapterId);
            await Capture();
        };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); };
        Closing += (_, e) => { if (working) e.Cancel = true; };
    }
    private async Task LoadAdapters(Func<CancellationToken, Task<VmwareAdapter[]>> load, string savedId)
    {
        try {
            var found = await load(lifetime.Token).WaitAsync(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            string selected = Adapter.SelectedValue as string ?? "";
            adapters = found;
            Adapter.ItemsSource = new[] { new NetworkOption("", "自動選擇（建議）") }.Concat(found.Select(a => new NetworkOption(a.Id, a.Name + " · " + a.Address))).ToArray();
            Adapter.SelectedValue = selected.Length > 0 ? selected : savedId;
            if (Adapter.SelectedIndex < 0) Adapter.SelectedIndex = 0;
            adaptersReady = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) CaptureSummary.Text = "網路清單讀取未完成：" + ex.Message; }
        finally { if (!lifetime.IsCancellationRequested) RenderState(currentState); }
    }
    private void RenderState(VmwareState? state)
    {
        currentState = state;
        hasBackup = state?.HasBackup == true;
        Save.IsEnabled = quickSetup && state is not null && !capturing && adaptersReady; Restore.IsEnabled = hasBackup && !capturing;
        Pause.IsEnabled = state is not null && !capturing && (state.Settings.Enabled || state.Settings.PendingPreparation);
        Adapter.IsEnabled = Add.IsEnabled = Remove.IsEnabled = !hasBackup;
        Status.Text = !quickSetup ? "請安裝新版私人測試安裝檔，啟用簡化設定流程。" : state?.Detail ?? "服務尚未連線。";
        var network = adapters.FirstOrDefault(a => a.Id == state?.Settings.AdapterId);
        NetworkSummary.Text = network is null ? "由程式檢查並選擇可用網路，自動分配客體保留 IP。" : "代理網路：" + network.Name + " · " + network.Address + "；" + string.Join("、", state!.Settings.Machines.Select(m => Path.GetFileNameWithoutExtension(m.VmxPath) + " → " + m.GuestIp));
        foreach (var row in rows) {
            var runtime = state?.Machines.FirstOrDefault(m => m.VmxPath.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
            var current = discovered.FirstOrDefault(v => v.Machine.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
            row.State = state?.Settings.PendingPreparation == true && state.Settings.Machines.Any(m => m.VmxPath.Equals(row.Path, StringComparison.OrdinalIgnoreCase)) ? state.Settings.PreparationError.Length > 0 ? "已儲存 · 準備未完成" : "已儲存 · 等待網路準備" : runtime?.State ?? current?.Detail ?? "尚未配置";
            row.Assigned = runtime?.UseLocalNetwork == true ? "本地網路" : runtime?.ProxyId is { } id && id != Guid.Empty ? options.FirstOrDefault(o => o.Id == id.ToString())?.Label ?? "—" : "—";
            row.CanSelect = !hasBackup;
        }
    }
    private async void CaptureClick(object sender, RoutedEventArgs e) => await Capture();
    private async Task Capture()
    {
        if (working || capturing || lifetime.IsCancellationRequested) return;
        capturing = true; CaptureButton.IsEnabled = false; Save.IsEnabled = false;
        var ct = lifetime.Token;
        try {
            discovered = await Task.Run(async () => {
                VmwareDiscovered[] live;
                try { live = await backend.DiscoverVmware(ct); }
                catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested) { live = await VmwareDiscovery.Running(ct); }
                ct.ThrowIfCancellationRequested();
                return live.Concat(VmwareDiscovery.Registered()).DistinctBy(v => v.Machine.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            }, ct);
            if (ct.IsCancellationRequested) return;
            if (!hasBackup) foreach (var item in discovered) AddMachine(item.Machine, discovered.Length == 1);
            CaptureSummary.Text = hasBackup ? $"已儲存 {rows.Count} 台虛擬機，可調整連線方式與分配數量。" : discovered.Length == 0 ? "尚未找到虛擬機，可在進階設定手動加入 VMX。" : $"找到 {discovered.Length} 台虛擬機，勾選要代理的視窗。";
            var state = await backend.Snapshot([]).WaitAsync(ct);
            if (!ct.IsCancellationRequested) RenderState(state.Vmware);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) CaptureSummary.Text = "尋找未完成：" + ex.Message; }
        finally { capturing = false; if (!ct.IsCancellationRequested) { CaptureButton.IsEnabled = true; RenderState(currentState); } }
    }
    private void AddMachine(VmwareMachine machine, bool selected = true)
    {
        var existing = rows.FirstOrDefault(r => r.Path.Equals(machine.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { existing.Name = machine.Name; return; }
        if (rows.Count >= 10) throw new InvalidOperationException("最多管理 10 台虛擬機。");
        rows.Add(new(machine.Path, machine.Name, Guid.Empty, options) { Selected = selected, ByStartup = Allocation.SelectedIndex == 1 });
    }
    private void AllocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PerProxy is not null) PerProxy.IsEnabled = Allocation.SelectedIndex == 1;
        if (OverflowToLocal is not null) OverflowToLocal.Visibility = Allocation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in rows) row.ByStartup = Allocation.SelectedIndex == 1;
        if (Machines is not null) { Machines.CommitEdit(); Machines.CommitEdit(); Machines.Items.Refresh(); }
    }
    private VmwareSettings Settings()
    {
        Machines.CommitEdit(); Machines.CommitEdit();
        if (!int.TryParse(PerProxy.Text, out int count) || count is < 1 or > 10) throw new InvalidOperationException("每個 IP 請設定 1–10 個視窗。");
        var selected = rows.Where(r => r.Selected).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("請至少勾選一台虛擬機。");
        return new VmwareSettings { Enabled = true, ByStartup = Allocation.SelectedIndex == 1, PerProxy = count, OverflowToLocal = localNetworkSupported && OverflowToLocal.IsChecked == true, AdapterId = Adapter.SelectedValue as string ?? "",
            Machines = selected.Select(r => new VmwareMapping(r.Path, r.GuestIp, r.UseLocalNetwork ? Guid.Empty : r.ProxyId) { UseLocalNetwork = r.UseLocalNetwork, Name = r.Name }).ToArray() };
    }
    private async Task<bool> Work(string message, Func<Task> action)
    {
        if (working || capturing) return false; working = true; Status.Text = message; IsEnabled = false;
        try { await action(); RenderState((await backend.Snapshot([])).Vmware); return true; }
        catch (Exception ex) { Status.Text = ex.Message; return false; }
        finally { working = false; IsEnabled = true; }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!adaptersReady) return;
        if (await Work("正在儲存並自動設定，請稍候…", () => backend.SetupVmware(Settings()))) { ConfigurationSaved = true; Close(); }
    }
    private async void PauseClick(object sender, RoutedEventArgs e) => await Work("正在暫停…", backend.PauseVmware);
    private async void RestoreClick(object sender, RoutedEventArgs e) => await Work("正在回復原網路…", backend.RestoreVmware);
    private void RemoveClick(object sender, RoutedEventArgs e) { if (hasBackup) return; foreach (var row in Machines.SelectedItems.Cast<Row>().ToArray()) rows.Remove(row); }
    private void AddClick(object sender, RoutedEventArgs e)
    {
        if (hasBackup) return;
        var dialog = new OpenFileDialog { Filter = "VMware 虛擬機|*.vmx", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        try { foreach (string path in dialog.FileNames) AddMachine(VmwareVmx.Read(path)); } catch (Exception ex) { Status.Text = ex.Message; }
    }
    public sealed record Option(string Id, string Label);
    public sealed record NetworkOption(string Id, string Label);
    public sealed class Row(string path, string name, Guid proxyId, Option[] options) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        private string displayName = name;
        public string Name { get => displayName; set { displayName = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        public bool Selected { get; set; } = true;
        public string GuestIp { get; set; } = ""; public Guid ProxyId { get; private set; } = proxyId;
        public bool UseLocalNetwork { get; set; }
        private bool byStartup;
        private readonly Option[] automaticOptions = new[] { new Option("auto", "自動分配 IP") }.Concat(options.Where(o => o.Id == "local")).ToArray();
        public bool ByStartup { get => byStartup; set { byStartup = value; PropertyChanged?.Invoke(this, new(nameof(Options))); PropertyChanged?.Invoke(this, new(nameof(RouteChoice))); } }
        public Option[] Options => ByStartup ? automaticOptions : options;
        public string RouteChoice {
            get => UseLocalNetwork ? "local" : ByStartup ? "auto" : ProxyId == Guid.Empty ? "blocked" : ProxyId.ToString();
            set {
                if (string.IsNullOrEmpty(value)) return;
                UseLocalNetwork = value == "local";
                if (Guid.TryParse(value, out Guid id)) ProxyId = id;
                else if (value == "blocked") ProxyId = Guid.Empty;
                PropertyChanged?.Invoke(this, new(nameof(RouteChoice)));
            }
        }
        private bool canSelect = true;
        private string state = "尚未配置", assigned = "—";
        public bool CanSelect { get => canSelect; set { canSelect = value; PropertyChanged?.Invoke(this, new(nameof(CanSelect))); } }
        public string Assigned { get => assigned; set { assigned = value; PropertyChanged?.Invoke(this, new(nameof(Assigned))); } }
        public string State { get => state; set { state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

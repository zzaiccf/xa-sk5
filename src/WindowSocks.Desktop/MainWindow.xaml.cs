using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class MainWindow : Window
{
    private readonly Backend backend;
    private readonly IDesktopStartupController startup;
    private readonly bool integrateShell;
    private readonly IProxyServiceSession serviceSession;
    private bool closingService, exitCleaned;
    private DateTimeOffset lastBackgroundHint = DateTimeOffset.MinValue;
    private readonly Action<string, string, int>? backgroundNotification;
    private bool startupChanging;
    private Snapshot? snapshot;
    private Snapshot? lastConnectedSnapshot;
    private TargetInstance[] targets = [];
    private VmwareDiscovered[] vmwareDiscovered = [];
    private TargetRow[] windowRows = [];
    private bool busy, quitting, picking;
    private bool ruleDialogOpen, vmwareDialogOpen;
    private bool bulkActionPending;
    private readonly Func<string, string, bool> confirmBulkAction;
    private readonly AutomaticProbeSchedule probeSchedule = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource startupRequest;
    private Task serviceStart = Task.CompletedTask;
    private readonly Dictionary<ProbeJob, CancellationTokenSource> probeRequests = [];
    private bool probeWorkerRunning;
    private bool probeStatusPending, probeStatusIncomplete;
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer durationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset lastDiscovery = DateTimeOffset.MinValue;
    private Task? discovery;
    private bool taskbarReading, taskbarRequesting;
    private readonly List<TargetInstance> extraTargets = [];
    private Guid[] contextProxyIds = [];
    private bool contextFromPointer, contextOnRow;
    private string? contextRuleKey;
    private bool ruleContextFromPointer, ruleContextOnRow;
    public MainWindow() : this(new Backend(), new DesktopStartupController(), true) { }
    public MainWindow(Backend backend, IDesktopStartupController? startup = null, bool integrateShell = false, IProxyServiceSession? serviceSession = null,
        Func<string, string, bool>? confirmBulkAction = null, Action<string, string, int>? backgroundNotification = null)
    {
        this.integrateShell = integrateShell;
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        startupRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        this.backgroundNotification = backgroundNotification;
        this.confirmBulkAction = confirmBulkAction ?? ((message, title) => MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        this.startup = startup ?? new PreviewStartupController();
        this.serviceSession = serviceSession ?? (integrateShell ? new DesktopProxySession(backend) : new PreviewProxySession());
        InitializeComponent(); LdPath.Text = backend.ConsolePath; ProductVersionText.Text = AboutVersionText.Text = AppReleasePackage.CurrentVersion + " · 預覽版";
        RenderStartup();
        Activated += async (_, _) => { if (this.integrateShell && !taskbarRequesting) await RefreshTaskbar(); };
        trayIcon = LoadTrayIcon();
        tray = new() { Icon = trayIcon, Visible = integrateShell, Text = "xa sk5" };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("開啟 xa sk5", null, (_, _) => Dispatcher.Invoke(Restore));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(RequestExit));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
        Loaded += async (_, _) => {
            if (closingService || quitting) return;
            if (this.integrateShell) try { DesktopTaskbar.RegisterExit(DesktopExecutable); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { ShellHint.Text = "工作列選單未完成，仍可從右下角圖示選擇退出。"; }
            await Run("正在啟動代理元件並讀取節點…", async () => {
                serviceStart = this.serviceSession.StartAsync(startupRequest.Token); await serviceStart;
                if (!closingService && !quitting) await Refresh(false, skipDiscovery: true);
            });
            if (closingService || quitting || lifetime.IsCancellationRequested) return;
            if (!lifetime.IsCancellationRequested) { discovery = DiscoverTargets(); _ = RefreshAfterDiscovery(discovery); }
            if (this.integrateShell) {
                try {
                    WindowsShortcuts.Initialize(Registry.CurrentUser, DesktopExecutable);
                    _ = RefreshTaskbar();
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { ShellHint.Text = "捷徑設定未完成：" + ex.Message; }
            }
            string[] arguments = Environment.GetCommandLineArgs(); int index = Array.IndexOf(arguments, "--update-result");
            if (index >= 0 && index + 1 < arguments.Length) try {
                string path = Path.GetFullPath(arguments[index + 1]);
                if (path.StartsWith(Path.Combine(UpdatePaths.UserData, "Updates") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length < 32768) {
                    var result = System.Text.Json.JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), Json.Options);
                    if (result is not null) { StatusText.Text = result.Message; StatusText.ToolTip = result.Message + "\n備份：\n" + string.Join("\n", result.Backups); if (!result.Success) MessageBox.Show(this, result.Message, "xa sk5 更新未完成"); }
                }
            } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { StatusText.Text = "已重新開啟；無法讀取更新結果，請查看更新紀錄。"; }
        };
        timer.Tick += async (_, _) => { if (!busy) await Run("", () => Refresh(false), false); }; timer.Start();
        durationTimer.Tick += (_, _) => { foreach (var row in TargetGrid.Items.OfType<TargetRow>()) row.UpdateDuration(); UpdateGlobalDuration(); }; durationTimer.Start();
        Closing += OnClosing;
        StateChanged += (_, _) => {
            if (WindowState == WindowState.Minimized && !closingService && !quitting) ShowBackgroundHint();
            else if (WindowState != WindowState.Minimized) lastBackgroundHint = DateTimeOffset.MinValue;
        };
    }
    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void RenderStartup()
    {
        if (startupChanging) return;
        var state = startup.Read(); StartWithWindows.IsChecked = state.Enabled; StartupHint.Text = state.Message;
        StartBackgroundService.Visibility = state.ServiceInstalled && !backend.Online ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void StartWithWindowsClick(object sender, RoutedEventArgs e)
    {
        bool enabled = StartWithWindows.IsChecked == true;
        await Run("正在更新開機啟動設定…", async () => {
            startupChanging = true; StartWithWindows.IsEnabled = false;
            try { await startup.SetEnabledAsync(enabled); }
            finally { startupChanging = false; StartWithWindows.IsEnabled = true; RenderStartup(); }
        });
    }
    private async void StartBackgroundServiceClick(object sender, RoutedEventArgs e) => await Run("正在啟動背景服務…", async () => {
        StartBackgroundService.IsEnabled = false;
        try { await startup.StartServiceAsync(); await Refresh(false); }
        finally { StartBackgroundService.IsEnabled = true; RenderStartup(); }
    });
    private void CheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new UpdateWindow { Owner = this };
        dialog.ShowDialog();
        if (dialog.ReadyToExit) { quitting = true; Close(); }
    }
    private static string DesktopExecutable => Path.Combine(AppContext.BaseDirectory, File.Exists(Path.Combine(AppContext.BaseDirectory, "xa sk5.exe")) ? "xa sk5.exe" : "WindowSocks.exe");
    private void CreateDesktopShortcutClick(object sender, RoutedEventArgs e)
    {
        try { WindowsShortcuts.CreateDesktop(DesktopExecutable); ShellHint.Text = "已建立桌面捷徑。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { ShellHint.Text = "無法建立捷徑：" + ex.Message; }
    }
    private async Task RefreshTaskbar()
    {
        if (taskbarReading || taskbarRequesting) return;
        taskbarReading = true;
        try {
            var state = await TaskbarPinning.ReadAsync();
            PinTaskbar.IsEnabled = state.Supported && state.Allowed && !state.Pinned;
            PinTaskbar.Content = state.Pinned ? "已釘選工作列" : "釘選工作列";
            ShellHint.Text = state.Pinned ? "已釘選工作列；桌面捷徑預設建立一次。" : state.Supported && state.Allowed ? "桌面捷徑預設建立；工作列釘選需在 Windows 提示中確認。" : "桌面捷徑已就緒；請在工作列圖示按右鍵，選擇「釘選到工作列」。";
        } catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or TimeoutException) {
            PinTaskbar.IsEnabled = false; ShellHint.Text = "請在工作列圖示按右鍵，選擇「釘選到工作列」。";
        }
        finally { taskbarReading = false; }
    }
    private async void PinTaskbarClick(object sender, RoutedEventArgs e)
    {
        if (taskbarRequesting) return;
        taskbarRequesting = true;
        PinTaskbar.IsEnabled = false;
        try {
            bool pinned = await TaskbarPinning.RequestAsync();
            taskbarRequesting = false;
            await RefreshTaskbar();
            if (!pinned) ShellHint.Text = "尚未釘選；可再次按下釘選，或在工作列圖示按右鍵操作。";
        } catch (Exception ex) when (ex is COMException or NotSupportedException or TimeoutException) { ShellHint.Text = "請在工作列圖示按右鍵，選擇「釘選到工作列」。"; }
        finally { taskbarRequesting = false; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (quitting) { CleanupExit(); return; }
        e.Cancel = true;
        if (closingService) return;
        if (WindowState != WindowState.Minimized) WindowState = WindowState.Minimized;
        else ShowBackgroundHint();
    }
    private void ShowBackgroundHint()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastBackgroundHint < TimeSpan.FromSeconds(3)) return;
        lastBackgroundHint = now;
        const string title = "xa sk5 已縮小，代理繼續運作";
        const string message = "可從工作列還原視窗；右鍵工作列或右下角 xa sk5 圖示，選擇「退出」才會停止代理並結束程式。";
        tray.BalloonTipTitle = title; tray.BalloonTipText = message; tray.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        if (backgroundNotification is not null) backgroundNotification(title, message, 3000);
        else if (integrateShell) tray.ShowBalloonTip(3000);
    }
    public void RequestExit()
    {
        if (quitting || closingService) return;
        Restore();
        closingService = true; IsEnabled = false; timer.Stop(); durationTimer.Stop(); backend.StopProbes();
        startupRequest.Cancel();
        StatusText.Text = "正在停止代理與背景服務…";
        Dispatcher.BeginInvoke(new Action(async () => await CloseWithService()));
    }
    private async Task CloseWithService()
    {
        try {
            // A stop sent before an outstanding start completes can leave a new
            // background service behind after the UI exits. Settle startup first.
            try { await serviceStart; } catch (Exception) { }
            StatusText.Text = "正在停止代理與背景服務…";
            await serviceSession.StopAsync(CancellationToken.None); quitting = true; Close();
        }
        catch (Exception ex) {
            closingService = false; IsEnabled = true; timer.Start(); durationTimer.Start();
            StatusText.Text = "尚未完成關閉：" + ex.Message;
            Restore();
        }
    }
    private void CleanupExit()
    {
        if (exitCleaned) return; exitCleaned = true;
        timer.Stop(); durationTimer.Stop(); probeSchedule.Stop(); lifetime.Cancel(); startupRequest.Dispose(); backend.StopProbes();
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose();
    }
    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/WindowSocks;component/Assets/Brand/xa-sk5.ico"));
        using var stream = resource.Stream;
        using var original = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)original.Clone();
    }
    private async Task Run(string message, Func<Task> action, bool showError = true)
    {
        if (busy) {
            if (!showError) return; // Background refresh may be skipped; a user action must not disappear.
            StatusText.Text = "正在等候上一項操作…";
            while (busy) await Task.Delay(50);
        }
        busy = true; if (message.Length > 0) StatusText.Text = message;
        try { await action(); if (message.Length > 0 && !closingService && !quitting) StatusText.Text = "完成 · " + DateTime.Now.ToString("HH:mm:ss") + (probeSchedule.ActiveCount == 0 ? "" : $" · 實際 IP 自動檢測中（{probeSchedule.ActiveCount} 條並行）"); }
        catch (Exception ex) { if (!closingService && !quitting) StatusText.Text = ex.Message; if (showError && !closingService && !quitting) MessageBox.Show(this, ex.Message, "xa sk5", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { busy = false; }
    }
    private async Task Refresh(bool discover, bool skipDiscovery = false)
    {
        if (!skipDiscovery && (discover || DateTimeOffset.Now - lastDiscovery > TimeSpan.FromSeconds(20)))
        {
            if (discovery is null || discovery.IsCompleted) {
                discovery = DiscoverTargets();
                if (!discover) _ = RefreshAfterDiscovery(discovery);
            }
            if (discover) await discovery;
        }
        var previous = snapshot;
        snapshot = await backend.Snapshot(targets);
        if (previous is not null && previous.Diagnostics.ServiceOnline == snapshot.Diagnostics.ServiceOnline) {
            var latest = previous.Probes.Concat(snapshot.Probes).GroupBy(p => p.ProxyId).ToDictionary(group => group.Key, group => group.MaxBy(p => p.At)!);
            snapshot = snapshot with { Probes = snapshot.Proxies.Where(p => latest.ContainsKey(p.Id)).Select(p => latest[p.Id]).ToArray() };
        }
        probeSchedule.ObserveResults(snapshot.Probes);
        if (probeSchedule.Synchronize(backend.Online ? "service" : "local", snapshot.Proxies.Select(p => p.Id), DateTimeOffset.UtcNow))
            foreach (var request in probeRequests.Where(pair => !probeSchedule.IsCurrent(pair.Key)).ToArray()) request.Value.Cancel();
        RenderSnapshot(); StartProbeWorker();
    }
    private async Task RefreshAfterDiscovery(Task pending)
    {
        try { await pending; if (!lifetime.IsCancellationRequested) await Run("", () => Refresh(false, skipDiscovery: true), false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task DiscoverTargets()
    {
        string ldConsole = backend.ConsolePath, mumuConsole = backend.MuMuConsolePath;
        bool online = backend.Online; var extra = extraTargets.ToArray(); var ct = lifetime.Token;
        var result = await Task.Run(async () => {
            string message = "";
            TargetInstance[] ld = [];
            if (File.Exists(ldConsole))
            {
                try {
                    var output = await LdPlayer.RunAsync(ldConsole, ["list2"], ct);
                    ld = LdPlayer.Parse(output, ldConsole, LdPlayer.Identity);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested) { message = "模擬器探索未完成；繼續讀取節點與自動檢測實際 IP。"; }
            }
            TargetInstance[] mumu = [];
            if (File.Exists(mumuConsole)) {
                try { mumu = await EmulatorCatalog.DiscoverAsync("MuMu", mumuConsole, ct); }
                catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested) { message = "MuMu 清單暫時無法讀取；固定配置仍保留。"; }
            }
            var found = ld.Concat(mumu).Concat(extra.Where(t => t.Processes.All(p => LdPlayer.Identity(p.Pid) == p))).DistinctBy(t => t.Key).ToArray();
            VmwareDiscovered[] vmware;
            try { vmware = online ? await backend.DiscoverVmware(ct) : await VmwareDiscovery.Running(ct); }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested) { vmware = []; }
            return (Targets: found, Vmware: vmware, Message: message);
        }, ct);
        if (ct.IsCancellationRequested) return;
        targets = result.Targets; vmwareDiscovered = result.Vmware; lastDiscovery = DateTimeOffset.Now;
        if (result.Message.Length > 0 && !ruleDialogOpen) StatusText.Text = result.Message;
    }
    private void RenderSnapshot()
    {
        if (snapshot is null) return;
        string? selectedRule = (RuleGrid.SelectedItem as ProxyRuleItem)?.Key;
        RuleGrid.ItemsSource = ProxyRules.List(snapshot);
        RuleGrid.SelectedItem = RuleGrid.Items.Cast<ProxyRuleItem>().FirstOrDefault(r => r.Key == selectedRule);
        RenderStartup();
        var selectedProxy = ProxyGrid.SelectedItems.Cast<ProxyRow>().Select(x => x.Profile.Id).ToHashSet();
        windowRows = BuildWindowRows(snapshot);
        RenderWindowList();
        var query = ProxySearch.Text.Trim();
        var rows = snapshot.Proxies.Select(p => new ProxyRow(p, snapshot.Probes.FirstOrDefault(x => x.ProxyId == p.Id), snapshot.Bindings.Count(b => b.ProxyId == p.Id) + windowRows.Count(r => r.Target.Kind == "VMware" && r.Target.Running && r.Node?.Id == p.Id), string.Join("、", snapshot.Bindings.Where(b => b.ProxyId == p.Id).Select(b => b.Name).Concat(windowRows.Where(r => r.Target.Kind == "VMware" && r.Target.Running && r.Node?.Id == p.Id).Select(r => r.Name))), probeSchedule.IsActive(p.Id), probeSchedule.IsQueued(p.Id))).ToArray();
        ProxyGrid.ItemsSource = rows.Where(r => query.Length == 0 || new[] { r.Name, r.EntryIp, r.Profile.Username, r.Probe?.TcpExit, r.Probe?.UdpExit }.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        foreach (ProxyRow row in ProxyGrid.Items) if (selectedProxy.Contains(row.Profile.Id)) ProxyGrid.SelectedItems.Add(row);
        UpdateProxyDetails();
        ProxyCount.Text = snapshot.Proxies.Length.ToString();
        ReadyCount.Text = snapshot.Probes.Count(p => p.Ready && !ProbeTiming.IsStale(p.At, DateTimeOffset.UtcNow) && snapshot.Proxies.Any(profile => profile.Id == p.ProxyId)).ToString();
        ReadyCount.ToolTip = "最近 30 分鐘內 TCP 與 UDP 皆實測通過的節點數。";
        ModeText.Text = !backend.Online ? "離線模式" : "服務已連線";
        ModeBadge.ToolTip = !backend.Online ? "可匯入與檢測節點；安裝背景服務後才能代理窗口。" : snapshot.Diagnostics.EngineRunning ? "代理規則已啟用，實際出口請從目標程式內確認。" : snapshot.Diagnostics.Detail;
        ModeIndicator.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(!backend.Online ? "#D89A38" : snapshot.Diagnostics.EngineRunning ? "#218552" : "#3563E9"));
        ServiceValue.Text = backend.Online ? "已連線" : "未連線";
        CoreValue.Text = snapshot.Diagnostics.CorePresent ? "已就緒" : "未安裝";
        DriverValue.Text = snapshot.Diagnostics.DriverPresent ? "檔案就緒" : "未安裝";
        DiagnosticsText.Text = $"背景服務：{(backend.Online ? "已連線" : "未連線")}\n原生核心：{(snapshot.Diagnostics.CorePresent ? "已就緒" : "缺少檔案")}\nWinDivert：{(snapshot.Diagnostics.DriverPresent ? "檔案存在，載入結果以核心狀態為準" : "缺少官方驅動檔案")}\n{snapshot.Diagnostics.Detail}\n\nWindows x64／IPv4 TCP、UDP：雷電 NAT、一般程序與子程序、全局代理。\n不同遊戲及模擬器仍需逐一驗收。核心及驅動崩潰時不保證零直連。";
        Logs.Text = string.Join(Environment.NewLine, snapshot.Diagnostics.Logs);
        NetworkEvents.Text = snapshot.FlowEvents.Length == 0 ? "尚無連線事件。需要 0.1.7 以上背景服務；啟動遊戲後自動收集。" : string.Join(Environment.NewLine, snapshot.FlowEvents.TakeLast(40).Select(e => e.Label));
        AutoStatus.Text = snapshot.AutoProxy.Enabled ? "自動代理已開啟" : "手動模式";
        AutoStatus.ToolTip = snapshot.AutoProxyStatus;
        RenderGlobalProxy();
        StopProbe.IsEnabled = !probeSchedule.Paused && snapshot.Proxies.Length > 0;
        StopProbe.Visibility = probeSchedule.Paused ? Visibility.Collapsed : Visibility.Visible;
        ResumeProbe.Visibility = probeSchedule.Paused ? Visibility.Visible : Visibility.Collapsed;
        RenderWorkspaceCounters();
        ProxyEmpty.Visibility = ProxyGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProxyEmptyTitle.Text = query.Length > 0 ? "找不到符合的節點" : "匯入你的第一個節點";
        ProxyEmptyHint.Text = query.Length > 0 ? "試試其他 IP、帳號或名稱。" : "貼上代理資料，實際 IP 會自動檢測。";
        ProxyListSummary.Text = query.Length > 0 ? $"{ProxyGrid.Items.Count} / {rows.Length} 個節點" : $"{rows.Length} 個節點";
        if (ProxyGrid.SelectedItems.Count > 0) ProxyListSummary.Text += $" · 已選 {ProxyGrid.SelectedItems.Count}";
        ManualProbe.IsEnabled = snapshot.Proxies.Length > 0;
        RenderBulkActions();
        MainRefresh.ToolTip = Pages.SelectedIndex == 2 ? "立即檢測並更新節點" : "重新整理";
        int supplemental = snapshot.Probes.Count(p => p.UdpPending);
        ProbeStateText.Text = probeSchedule.ActiveCount > 0 || probeSchedule.PendingCount > 0 ? $"TCP 檢測中 · {probeSchedule.ActiveCount} 條並行 · 等待 {probeSchedule.PendingCount}" : supplemental > 0 ? $"TCP 已完成 · 背景補測 {supplemental} 條" : probeSchedule.Paused ? "自動檢測已暫停" : "每 30 分鐘複測";
        if (probeStatusPending) {
            if (probeSchedule.ActiveCount > 0 || probeSchedule.PendingCount > 0)
                StatusText.Text = $"TCP 檢測中 · {probeSchedule.ActiveCount} 條並行 · 等待 {probeSchedule.PendingCount} 條";
            else if (supplemental > 0) StatusText.Text = $"TCP 已完成 · 背景補測中（{supplemental} 條）";
            else {
                StatusText.Text = probeStatusIncomplete ? "檢測完成 · 部分節點未完成，可重新檢測。" : "檢測完成";
                probeStatusPending = false;
            }
        }
        ProbeStateText.ToolTip = backend.Online && !snapshot.SupportsLowFrequencyProbes
            ? "清單成功後每 30 分鐘複測、失敗後 5 分鐘重試。目前舊版背景服務仍有較頻繁的健康檢查，更新至 0.1.12 後會同步降低頻率。"
            : "開啟與匯入時檢測一次；成功後 30 分鐘複測，失敗後 5 分鐘重試。需要最新結果可按「立即檢測」。";
    }
    private void RenderBulkActions()
    {
        if (snapshot is null) return;
        bool supported = backend.Online && snapshot.SupportsBulkActions;
        ClearAllProxyIps.IsEnabled = !bulkActionPending && snapshot.Proxies.Length > 0 && (!backend.Online || supported);
        ClearAllProxyIps.ToolTip = backend.Online && !supported ? "請先套用 0.1.22 以上背景服務。" : "清除全部節點，不受搜尋或選取影響。使用中的節點會阻止整批清除。";
        CloseAllProxyProcesses.IsEnabled = !bulkActionPending && supported &&
            (snapshot.Bindings.Any(b => b.Processes.Length > 0 || b.Descendants.Length > 0) || snapshot.Vmware?.Machines.Any(m => m.Process.HasValue && m.ProxyId != Guid.Empty && !m.UseLocalNetwork) == true);
        CloseAllProxyProcesses.ToolTip = !supported ? "請先連線並套用 0.1.22 以上背景服務。" : "結束所有個別綁定的程式及子程序；全局代理與僅使用全局代理的其他程式不受影響。";
    }
    private async void ClearAllProxyIpsClick(object sender, RoutedEventArgs e)
    {
        if (bulkActionPending) return;
        bulkActionPending = true; RenderBulkActions();
        try {
            await Run("", async () => {
                await Refresh(false, skipDiscovery: true);
                int count = snapshot?.Proxies.Length ?? 0;
                if (count == 0) return;
                if (!confirmBulkAction($"清除全部 {count} 個 IP 節點？此操作包含目前搜尋未顯示及未選取的節點。\n\n使用中的節點會阻止整批清除，請先解除相關綁定或分配；不會關閉程式或自動停用全局代理。", "清除所有 IP")) return;
                StatusText.Text = "正在清除所有 IP…";
                try {
                    var result = await backend.ClearProxyPool();
                    foreach (Guid id in result.RemovedIds) probeSchedule.Invalidate(id);
                    await Refresh(false, skipDiscovery: true);
                    StatusText.Text = $"已清除全部 {result.RemovedIds.Length} 個 IP 節點。";
                } catch { await Refresh(false, skipDiscovery: true); throw; }
            });
        } finally { bulkActionPending = false; RenderBulkActions(); }
    }
    private async void CloseAllProxyProcessesClick(object sender, RoutedEventArgs e)
    {
        if (bulkActionPending) return;
        bulkActionPending = true; RenderBulkActions();
        try {
            await Run("", async () => {
                StatusText.Text = "正在確認個別綁定的程式與子程序…";
                var plan = await backend.PrepareCloseProxyProcesses();
                if (plan.ProcessCount == 0) { StatusText.Text = "目前沒有可關閉的個別代理進程；全局代理保持原設定。"; return; }
                string names = string.Join("\n", plan.Targets.Take(8).Select(t => "• " + t.Name));
                if (plan.Targets.Length > 8) names += $"\n…另 {plan.Targets.Length - 8} 組";
                string vmWarning = plan.Targets.Any(t => t.Key.StartsWith("vmware:", StringComparison.OrdinalIgnoreCase)) ? "VMware 程序會直接結束，請先保存虛擬機工作。" : "";
                if (!confirmBulkAction($"關閉 {plan.Targets.Length} 組個別代理程式及其子程序（目前共 {plan.ProcessCount} 個程序）？\n\n{names}\n\n未儲存的工作可能遺失。{vmWarning}全局代理及僅使用全局代理的其他程式保持運作；代理規則會保留。", "關閉所有代理進程")) { StatusText.Text = "已取消關閉代理進程。"; return; }
                StatusText.Text = "正在關閉個別代理程式與子程序…";
                var result = await backend.CloseProxyProcesses(plan.Token);
                await Refresh(false, skipDiscovery: true); StatusText.Text = result.Message;
                if (result.Unclosed > 0) MessageBox.Show(this, string.Join("\n", result.Processes.Where(p => p.State is "failed" or "skipped").Take(20).Select(p => $"PID {p.Identity.Pid}：{p.Message}")), "部分代理進程未關閉", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        } finally { bulkActionPending = false; RenderBulkActions(); }
    }
    private void RenderGlobalProxy()
    {
        if (snapshot is null) return;
        var state = GlobalProxyDisplay.From(snapshot, backend.Online, lastConnectedSnapshot);
        if (backend.Online && snapshot.Diagnostics.ServiceOnline) lastConnectedSnapshot = snapshot;
        static System.Windows.Media.Brush Brush(string color) => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        var foreground = Brush(state.Foreground); var background = Brush(state.Background); var border = Brush(state.Border);
        GlobalStatus.Text = GlobalHeading.Text = state.Title;
        GlobalStatus.Foreground = GlobalHeading.Foreground = GlobalBannerIcon.Foreground = GlobalIndicator.Fill = foreground;
        GlobalBadge.Background = GlobalBanner.Background = background;
        GlobalBadge.BorderBrush = GlobalBanner.BorderBrush = border;
        GlobalBadge.ToolTip = GlobalBanner.ToolTip = state.Detail;
        GlobalBanner.Visibility = state.ShowBanner ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(GlobalBanner, state.Title + "。" + state.Hint);
        GlobalHint.Text = state.Hint; GlobalNodeName.Text = state.NodeName;
        GlobalNodeDetails.Visibility = state.ShowNode ? Visibility.Visible : Visibility.Collapsed;
        GlobalExitLabel.Text = state.ExitLabel; GlobalExitIp.Text = state.ExitIp; GlobalExitIp.ToolTip = state.ExitHint;
        GlobalAccountPort.Text = state.AccountPort; GlobalEntryIp.Text = state.EntryIp;
        GlobalRemotePolicy.Text = state.RemotePolicy; GlobalLanPolicy.Text = state.LanPolicy;
        UpdateGlobalDuration();
        GlobalRemotePolicy.Foreground = state.RemotePolicy.Contains("待", StringComparison.Ordinal) ? Brush("#A36D14") : foreground;
        GlobalPolicies.Visibility = state.RemotePolicy.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopGlobal.IsEnabled = StopGlobalBanner.IsEnabled = state.CanStop;
        ManageGlobal.IsEnabled = backend.Online && snapshot.Proxies.Length > 0;
        GlobalProxyButton.Content = snapshot.GlobalProxy.Enabled && backend.Online ? "全局設定" : "全局代理";
        TargetEmptyTitle.Text = WindowFilter.SelectedIndex == 0 ? "目前沒有執行中的窗口" : "尚無管理中的窗口";
        TargetEmptyHint.Text = snapshot.GlobalProxy.Enabled ? "全局模式會依上方狀態處理其餘程式流量。" : "已儲存設定可在「規則管理」查看；切換「全部窗口」可看未啟動項目。";
        Title = state.ShowBanner ? "xa sk5 · " + state.Title : "xa sk5";
        tray.Text = "xa sk5 · " + state.Title;
        UpdateSummaryCards();
    }
    private void UpdateGlobalDuration() => GlobalDuration.Text = GlobalProxyDisplay.Duration(snapshot, backend.Online, DateTimeOffset.UtcNow);
    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSummaryCards();
    private void UpdateSummaryCards()
    {
        if (SummaryCards is null || Pages is null || GlobalBanner is null) return;
        SummaryCards.Visibility = Pages.SelectedIndex == 3 || GlobalBanner.Visibility == Visibility.Visible && Workspace.ActualHeight is > 0 and < 650 ? Visibility.Collapsed : Visibility.Visible;
    }
    private static string Size(long n) => n >= 1048576 ? $"{n / 1048576d:F1} MB" : $"{n / 1024d:F1} KB";
    private void WindowFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetGrid is null || RuleGrid is null || RuntimePicker is null || TargetCountLabel is null) return;
        RenderWindowList(); RenderWorkspaceCounters();
    }
    private void RenderWindowList()
    {
        var selected = TargetGrid.SelectedItems.Cast<TargetRow>().Select(r => r.Target.Key).ToHashSet();
        TargetGrid.ItemsSource = windowRows.Where(r => WindowFilter.SelectedIndex == 1 || r.Target.Running).ToArray();
        foreach (TargetRow row in TargetGrid.Items) if (selected.Contains(row.Target.Key)) TargetGrid.SelectedItems.Add(row);
        TargetEmpty.Visibility = TargetGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TargetSelectionChanged(this, null!);
    }
    private void RenderWorkspaceCounters()
    {
        bool rules = Pages.SelectedIndex == 1;
        RuleEmpty.Visibility = RuleGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TargetCountLabel.Text = rules ? "已儲存規則" : WindowFilter.SelectedIndex == 0 ? "執行中窗口" : "全部窗口";
        TargetCount.Text = (rules ? RuleGrid.Items.Count : TargetGrid.Items.Count).ToString();
        RuleListSummary.Text = $"共 {RuleGrid.Items.Count} 條規則 · 未啟動、等待準備與已暫停的規則都會保留 · 右鍵可編輯或刪除";
        AutoStatus.Text = $"{TargetGrid.Items.Count} 個窗口 · 每 5 秒更新 · VM 顯示目前分配的線路，客體實際出口仍需核對";
    }
    private void TargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnableTargetAction is null) return;
        var selected = TargetGrid.SelectedItems.Cast<TargetRow>().ToArray();
        EnableTargetAction.IsEnabled = PauseTargetAction.IsEnabled = UnbindTargetAction.IsEnabled = selected.Length > 0 && selected.All(r => r.Target.Kind != "VMware");
        RuntimeBind.IsEnabled = selected.Length > 0 && (selected.All(r => r.Target.Kind == "VMware") || selected.All(r => r.Target.Kind != "VMware"));
        RuntimeBind.Content = selected.Length > 0 && selected.All(r => r.Target.Kind == "VMware") ? "調整 VM 線路" : "分配節點";
    }
    private TargetRow[] BuildWindowRows(Snapshot state) => WindowInventory.List(state, vmwareDiscovered).Select(item => {
        var target = item.Target; var binding = state.Bindings.FirstOrDefault(b => b.TargetKey == target.Key);
        var profile = state.Proxies.FirstOrDefault(p => p.Id == item.ProxyId); var probe = state.Probes.FirstOrDefault(p => p.ProxyId == item.ProxyId);
        string line = item.UseLocalNetwork ? "本地網路" : profile is null ? item.Rule == "未設定規則" ? "未納管" : "尚未分配" : profile.Name.Length > 0 ? profile.Name : profile.Endpoint;
        if (target.Kind == "VMware" && item.State.Contains("未代理") && (profile is not null || item.UseLocalNetwork)) line = "預定 · " + line;
        return new TargetRow(target, target.Name + (EmulatorCatalog.IsEmulator(target.Kind) ? $"  #{target.Index}" : target.Kind == "GameGroup" ? $"（{target.Processes.Length} 個成員）" : binding?.IncludeChildren == true ? $"（含 {binding.Descendants.Length} 個子程序）" : ""), line,
            item.UseLocalNetwork ? "本地出口待核對" : probe?.ExitLabel ?? "未驗證", item.State, item.Sent is { } sent && item.Received is { } received ? $"{Size(sent)} / {Size(received)}" : "—", probe is null ? "未檢測" : $"{(probe.Tcp ? "通過" : "失敗")} / {(probe.Udp ? "通過" : "失敗")}") {
            ConnectedAt = item.State.Contains("未代理") || item.State.Contains("封鎖") ? null : item.ConnectedAt,
            Connections = item.TcpConnections.HasValue ? $"{item.TcpConnections} / {item.UdpAssociations}" : "—", Members = item.Detail + "\n規則：" + item.Rule,
            Node = profile is null ? null : new ProxyNodeDisplay(profile, probe), AccountPort = profile is null ? "—" : new ProxyNodeDisplay(profile, probe).AccountPort, EntryIp = profile?.Host ?? "—", InstanceDetail = item.InstanceDetail
        };
    }).ToArray();
    private void EditRuleClick(object sender, RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not ProxyRuleItem rule) return;
        if (rule.Key == "vmware") VmwareClick(sender, e);
        else if (rule.Key == "automatic") AutoProxyClick(sender, e);
        else {
            WindowFilter.SelectedIndex = 1; SelectWorkspace(0); TargetGrid.UnselectAll();
            TargetGrid.SelectedItem = TargetGrid.Items.Cast<TargetRow>().FirstOrDefault(r => rule.Key == "binding:" + r.Target.Key);
            BindClick(sender, e);
        }
    }
    private void RuleGridRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        ruleContextFromPointer = true; ruleContextOnRow = false;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != RuleGrid;) {
            if (source is DataGridRow row && row.Item is ProxyRuleItem item) {
                ruleContextOnRow = true; RuleGrid.SelectedItem = item; row.Focus(); break;
            }
            source = source is FrameworkContentElement content ? content.Parent :
                source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
    }
    private void RuleGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) ruleContextFromPointer = false;
    }
    private void RuleContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (busy || ruleContextFromPointer && !ruleContextOnRow || RuleGrid.SelectedItem is not ProxyRuleItem item) { contextRuleKey = null; e.Handled = true; }
        else { contextRuleKey = item.Key; ContextDeleteRule.IsEnabled = true; }
        ruleContextFromPointer = false;
    }
    private async void DeleteRuleClick(object sender, RoutedEventArgs e)
    {
        string? key = contextRuleKey; if (key is null) return;
        await Run("正在刪除規則…", async () => { await backend.DeleteRule(key); await Refresh(false, skipDiscovery: true); StatusText.Text = "規則已刪除。"; });
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        SelectWorkspace(int.Parse((string)((Button)sender).Tag));
    }
    private void SelectWorkspace(int index)
    {
        Pages.SelectedIndex = index;
        foreach (var button in SidebarNavigation.Children.OfType<Button>()) {
            bool active = int.Parse((string)button.Tag) == index;
            button.Background = active ? (System.Windows.Media.Brush)FindResource("SoftBlue") : System.Windows.Media.Brushes.Transparent;
            button.Foreground = (System.Windows.Media.Brush)FindResource(active ? "Accent" : "Muted");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
        PageTitle.Text = new[] { "窗口列表", "規則管理", "代理與檢測", "設定" }[index];
        PageSubtitle.Text = new[] { "正在執行的窗口、虛擬機與目前使用的 IP 線路。", "已儲存的分配規則、網路準備與啟用狀態。", "節點、實際 IP 與連線狀態。", "服務、模擬器與資料。" }[index];
        RenderWorkspaceCounters();
        UpdateSummaryCards();
        MainRefresh.ToolTip = index == 2 ? "立即檢測並更新節點" : "重新整理";
    }
    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        bool probe = Pages.SelectedIndex == 2;
        await Run("正在重新整理…", () => Refresh(!probe));
        if (probe) RequestManualProbe();
    }
    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        var text = AskText("匯入節點", "每行一組，匯入後自動檢測實際 IP。\nhost/port/user/password/到期日期（選填）", "", true);
        if (text is not null) await Run("正在匯入…", () => ImportAndProbe(text));
    }
    private async void ImportFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "代理清單|*.txt;*.csv|所有檔案|*.*" };
        if (dialog.ShowDialog(this) == true) await Run("正在匯入…", async () => { if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidOperationException("檔案超過 1 MB。"); await ImportAndProbe(await File.ReadAllTextAsync(dialog.FileName)); });
    }
    private async Task ImportAndProbe(string text)
    {
        var ids = await backend.Import(text); await Refresh(false, skipDiscovery: true);
        probeSchedule.Request(ids); RenderSnapshot(); StartProbeWorker();
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "xa-sk5-proxies.csv" };
        if (dialog.ShowDialog(this) == true) await Run("正在匯出…", async () => await File.WriteAllTextAsync(dialog.FileName, await backend.Export()));
    }
    private async void DeleteClick(object sender, RoutedEventArgs e) => await DeleteProxies(ProxyGrid.SelectedItems.Cast<ProxyRow>().Select(row => row.Profile.Id).ToArray());
    private Task DeleteProxies(Guid[] ids) => Run("正在刪除…", async () => {
        try { foreach (var id in ids) await backend.Delete(id); }
        finally { await Refresh(false, skipDiscovery: true); }
    });
    private async void ContextDeleteProxyClick(object sender, RoutedEventArgs e) => await DeleteProxies(contextProxyIds.ToArray());
    private void ContextProbeProxyClick(object sender, RoutedEventArgs e)
    {
        probeSchedule.Request(contextProxyIds); RenderSnapshot(); StartProbeWorker();
    }
    private async void ContextEditProxyClick(object sender, RoutedEventArgs e)
    {
        if (contextProxyIds.Length != 1 || snapshot?.Proxies.FirstOrDefault(p => p.Id == contextProxyIds[0]) is not { } profile) return;
        var dialog = new ProxyEditWindow(profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Request is not { } request) return;
        await Run("正在儲存節點…", async () => {
            bool changed = await backend.Edit(request);
            if (changed) {
                foreach (var pair in probeRequests.Where(pair => pair.Key.ProxyId == profile.Id).ToArray()) pair.Value.Cancel();
                probeSchedule.Invalidate(profile.Id);
                if (snapshot is not null) snapshot = snapshot with { Probes = snapshot.Probes.Where(p => p.ProxyId != profile.Id).ToArray() };
            }
            await Refresh(false, skipDiscovery: true);
            if (changed) { probeSchedule.Request([profile.Id]); RenderSnapshot(); StartProbeWorker(); }
            StatusText.Text = changed ? "節點已儲存，正在重新檢測。" : "節點已儲存。";
        });
    }
    private void StartProbeWorker()
    {
        if (probeWorkerRunning || lifetime.IsCancellationRequested || probeSchedule.PendingCount == 0) return;
        probeWorkerRunning = true;
        _ = ProcessProbeQueue();
    }
    private async Task ProcessProbeQueue()
    {
        var running = new HashSet<Task>();
        try {
            while (!lifetime.IsCancellationRequested) {
                while (running.Count < probeSchedule.MaxConcurrent && probeSchedule.TryStart(out var job)) running.Add(CheckProxy(job));
                if (running.Count == 0) break;
                var completed = await Task.WhenAny(running); running.Remove(completed); await completed;
                await Task.Yield();
            }
        }
        finally {
            await Task.WhenAll(running);
            probeWorkerRunning = false;
            StopProbe.IsEnabled = !probeSchedule.Paused && snapshot?.Proxies.Length > 0;
            if (!lifetime.IsCancellationRequested && probeSchedule.PendingCount > 0) StartProbeWorker();
        }
    }
    private async Task CheckProxy(ProbeJob job)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        probeRequests.Add(job, request);
        bool ready = false;
        ProbeResult? measured = null;
        try {
            StopProbe.IsEnabled = true;
            if (!probeStatusPending) probeStatusIncomplete = false;
            probeStatusPending = true;
            RenderSnapshot();
            var result = await backend.Probe(job.ProxyId, request.Token);
            measured = result;
            request.Token.ThrowIfCancellationRequested();
            ready = result.UdpPending ? result.Tcp : result.Ready;
            if (probeSchedule.IsCurrent(job) && snapshot is not null) {
                snapshot = snapshot with { Probes = snapshot.Probes.Where(p => p.ProxyId != job.ProxyId).Append(result).ToArray() };
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { probeStatusIncomplete = true; }
        finally {
            probeRequests.Remove(job);
            probeSchedule.Complete(job, ready, DateTimeOffset.UtcNow, measured?.CheckId ?? Guid.Empty, measured?.UdpPending ?? false);
        }
        if (!lifetime.IsCancellationRequested) RenderSnapshot();
    }
    private void StopProbeClick(object sender, RoutedEventArgs e) { probeSchedule.Stop(); RenderSnapshot(); StatusText.Text = "自動檢測已暫停；目前檢測完成後停止，仍可按「立即檢測」。"; }
    private void ResumeProbeClick(object sender, RoutedEventArgs e)
    {
        probeSchedule.Resume();
        if (snapshot is not null) probeSchedule.Synchronize(backend.Online ? "service" : "local", snapshot.Proxies.Select(p => p.Id), DateTimeOffset.UtcNow);
        RenderSnapshot(); StartProbeWorker(); StatusText.Text = "已恢復自動檢測；成功節點每 30 分鐘複測。";
    }
    private void ProxySearchChanged(object sender, TextChangedEventArgs e) { if (ProxyGrid is not null) RenderSnapshot(); }
    private void ProxyPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep a usable table above the expanded details at the minimum window size.
        if (ProxyDetailScroll is not null) ProxyDetailScroll.MaxHeight = Math.Clamp((e.NewSize.Height - 280) / 2, 60, 180);
    }
    private void ProxySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProxyDetails();
        if (ProxyListSummary is not null && snapshot is not null) {
            ProxyListSummary.Text = ProxySearch.Text.Trim().Length > 0 ? $"{ProxyGrid.Items.Count} / {snapshot.Proxies.Length} 個節點" : $"{snapshot.Proxies.Length} 個節點";
            if (ProxyGrid.SelectedItems.Count > 0) ProxyListSummary.Text += $" · 已選 {ProxyGrid.SelectedItems.Count}";
        }
    }
    private void ProxyGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Keep WPF's Ctrl/Shift multi-selection, column sorting and scrollbar input intact.
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        DataGridRow? row = null;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != ProxyGrid;) {
            if (source is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb or
                System.Windows.Controls.Primitives.DataGridColumnHeader or System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter) return;
            if (source is DataGridRow found) { row = found; break; }
            source = source is FrameworkContentElement content ? content.Parent :
                source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        if (row is null && ProxyGrid.SelectedItems.Count > 0) { ProxyGrid.UnselectAll(); e.Handled = true; }
        else if (row?.IsSelected == true) { ProxyGrid.SelectedItems.Remove(row.Item); e.Handled = true; }
    }
    private void ProxyGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) contextFromPointer = false;
        if (e.Key == Key.Escape && ProxyGrid.SelectedItems.Count > 0) { ProxyGrid.UnselectAll(); e.Handled = true; }
    }
    private void ProxyGridRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        contextFromPointer = true; contextOnRow = false;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != ProxyGrid;) {
            if (source is DataGridRow row && row.Item is ProxyRow item) {
                contextOnRow = true;
                if (!row.IsSelected) { ProxyGrid.UnselectAll(); ProxyGrid.SelectedItem = item; }
                row.Focus(); break;
            }
            source = source is FrameworkContentElement content ? content.Parent :
                source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
    }
    private void ProxyContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (busy || contextFromPointer && !contextOnRow || ProxyGrid.SelectedItems.Count == 0) { contextProxyIds = []; e.Handled = true; contextFromPointer = false; return; }
        contextProxyIds = ProxyGrid.SelectedItems.Cast<ProxyRow>().Select(row => row.Profile.Id).ToArray();
        ContextDeleteProxy.IsEnabled = ContextProbeProxy.IsEnabled = true;
        ContextEditProxy.IsEnabled = contextProxyIds.Length == 1;
        ContextEditProxy.ToolTip = contextProxyIds.Length == 1 ? "修改連入 IP、端口、帳密與到期時間" : "一次選取一個節點進行修改";
        contextFromPointer = false;
    }
    private void ClearProxySelectionClick(object sender, RoutedEventArgs e) => ProxyGrid.UnselectAll();
    private void UpdateProxyDetails()
    {
        if (ClearProxySelection is not null) ClearProxySelection.Visibility = ProxyGrid.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ProxyDetails is null || ProxyDetailCard is null) return;
        ProbeSelected.IsEnabled = DeleteSelected.IsEnabled = ProxyGrid.SelectedItems.Count > 0;
        if (ProxyGrid.SelectedItem is not ProxyRow row) { ProxyDetailCard.Visibility = Visibility.Collapsed; return; }
        ProxyDetailCard.Visibility = Visibility.Visible;
        ProxyDetailTitle.Text = $"節點詳情 · {(row.Profile.Username.Length == 0 ? row.Name : row.Profile.Username)}";
        var p = row.Probe;
        ProxyFacts.ItemsSource = new[] {
            new DetailFact("TCP 實際 IP", p?.TcpExit ?? "未驗證"), new DetailFact("UDP 實際 IP", row.UdpExit), new DetailFact("連接帳號／端口", row.AccountPort),
            new DetailFact("連入 IP", row.EntryIp), new DetailFact("到期時間", row.Expiration), new DetailFact("檢測時間", row.CheckedAt.Length == 0 ? "—" : row.CheckedAt),
            new DetailFact("連線／驗證", $"{row.Reachable} / {row.Auth}"), new DetailFact("TCP / UDP", $"{row.Tcp} / {row.Udp}"),
            new DetailFact("入口 TCP 連線", Ms(p?.ConnectLatencyMs)), new DetailFact("TCP 檢測耗時", Ms(p?.LatencyMs)), new DetailFact("UDP 往返", Ms(p?.UdpLatencyMs)),
            new DetailFact("名稱", row.Name), new DetailFact("所在地／業者", p?.Location is { } location ? $"{location.Label} · {location.Isp}" : "—"), new DetailFact("已分配窗口", row.Count == 0 ? "—" : row.Owners)
        };
        ProxyDetails.Text = p?.Message ?? "尚未檢測；此節點已加入自動檢測佇列。";
        ProxyDetails.ToolTip = p?.LocationMessage;
    }
    private static string Ms(long? value) => value.HasValue ? $"{value} ms" : "—";
    public sealed record DetailFact(string Label, string Value);
    private void RequireProcessHandoffSupport()
    {
        if (snapshot?.SupportsProcessHandoffs != true) throw new InvalidOperationException("請先安裝遊戲接續修正版，更新主程式與背景服務後再套用。原有 IP 配置會保留。");
    }
    private void RequireServiceVersion(Version minimum, string feature)
    {
        if (!backend.Online || !Version.TryParse(snapshot?.Diagnostics.Version.Split(' ')[0], out var version) || version < minimum)
            throw new InvalidOperationException($"{feature}需要 {minimum} 以上背景服務，請先安裝新版。");
    }
    private async void GlobalProxyClick(object sender, RoutedEventArgs e) => await Run("正在讀取全局代理設定…", async () => {
        await Refresh(false); RequireServiceVersion(new(0, 1, 9), "全局代理");
        if (snapshot!.Proxies.Length == 0) throw new InvalidOperationException("請先匯入代理。");
        var dialog = new GlobalProxyWindow(snapshot.Proxies, snapshot.GlobalProxy, snapshot.Probes) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Settings is null) return;
        try { await backend.Act("setGlobalProxy", dialog.Settings); }
        finally { await Refresh(false); }
    });
    private async void StopGlobalClick(object sender, RoutedEventArgs e) => await Run("正在停用全局代理…", async () => {
        if (snapshot is null) return;
        await backend.Act("setGlobalProxy", snapshot.GlobalProxy with { Enabled = false }); await Refresh(false);
    });
    private async void AutoProxyClick(object sender, RoutedEventArgs e)
    {
        if (ruleDialogOpen || closingService || quitting) return;
        ruleDialogOpen = true;
        try {
            // Opening an editor uses the last rendered state. Discovery and service health checks
            // can take seconds; neither may hold the editor behind the main-window work queue.
            if (snapshot is null) await Refresh(false, skipDiscovery: true);
            var dialog = new AutoProxyWindow(snapshot!.AutoProxy, backend.ConsolePath, snapshot.Proxies.Length,
                snapshot.Proxies, snapshot.Probes, snapshot.Targets, backend.MuMuConsolePath, snapshot.Bindings) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (dialog.VmwareRequested) { await OpenVmware(); return; }
            if (dialog.Rule is null) return;
            await Run("正在儲存自動分配規則…", async () => {
            // Refresh capabilities before applying, without waiting for emulator/VM discovery.
            await Refresh(false, skipDiscovery: true);
            if (dialog.Rule.Kind is "Emulator" or "EmulatorSequence") {
                backend.MuMuConsolePath = dialog.MuMuConsolePath;
                backend.ConsolePath = dialog.LdConsolePath;
                targets = dialog.DiscoveredTargets.Concat(targets).DistinctBy(t => t.Key).ToArray();
                snapshot = await backend.Snapshot(targets);
                bool applied = await backend.ConfigureAutomatic(dialog.Rule);
                await Refresh(false, skipDiscovery: true);
                SelectWorkspace(1);
                if (!applied) MessageBox.Show(this, $"模擬器配置已儲存在本機。請更新至 {(dialog.Rule.Kind == "EmulatorSequence" ? "0.1.13" : "0.1.10")} 背景服務，再開啟自動代理按「儲存配置」套用。現有代理繼續執行。", "配置已儲存", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (dialog.Rule.IncludeChildren) RequireProcessHandoffSupport();
            if (dialog.Rule.Kind == "GameGroup" && !Version.TryParse(snapshot.Diagnostics.Version.Split(' ')[0], out var serviceVersion)) throw new InvalidOperationException("無法確認背景服務版本，請安裝 0.1.6 以上版本。");
            if (dialog.Rule.Kind == "GameGroup" && Version.Parse(snapshot.Diagnostics.Version.Split(' ')[0]) < new Version(0, 1, 6)) throw new InvalidOperationException("遊戲組需要 0.1.6 以上背景服務，請先安裝新版。");
            if (!backend.Online || !Version.TryParse(snapshot.Diagnostics.Version.Split(' ')[0], out var automaticVersion) || automaticVersion < new Version(0, 1, 3)) throw new InvalidOperationException("自動分配需要 0.1.3 以上背景服務，請先安裝新版。");
            await backend.ConfigureAutomatic(dialog.Rule); await Refresh(false, skipDiscovery: true); SelectWorkspace(1);
            });
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { ruleDialogOpen = false; }
    }
    private async void VmwareClick(object sender, RoutedEventArgs e) {
        if (closingService || quitting) return;
        try { await OpenVmware(); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async Task OpenVmware() {
        if (vmwareDialogOpen) return;
        vmwareDialogOpen = true;
        try {
        if (snapshot is null) await Refresh(false, skipDiscovery: true);
        var state = snapshot!;
        var dialog = new VmwareWindow(backend, state, [], loadAdapters: ct => state.SupportsVmware ? backend.VmwareAdapters(ct) : Task.Run(VmwareDiscovery.Adapters, ct)) { Owner = this }; dialog.ShowDialog();
        if (dialog.ConfigurationSaved) await Run("", async () => { await Refresh(false, skipDiscovery: true); SelectWorkspace(1); RuleGrid.SelectedItem = RuleGrid.Items.Cast<ProxyRuleItem>().FirstOrDefault(r => r.Key == "vmware"); });
        } finally { vmwareDialogOpen = false; }
    }
    private async void PauseAutoClick(object sender, RoutedEventArgs e) => await Run("正在暫停自動分配…", async () => {
        if (snapshot is null) return;
        if (snapshot.HasPendingEmulatorConfiguration) throw new InvalidOperationException("模擬器配置尚未套用。請先更新背景服務並儲存配置；目前服務的既有規則不會變更。");
        await backend.Act("setAutoProxy", snapshot.AutoProxy with { Enabled = false }); await Refresh(false);
    });
    private async void AssignFromProxyClick(object sender, RoutedEventArgs e)
    {
        await Run("正在列出可分配的窗口與程序…", async () => {
            await Refresh(true);
            var proxies = snapshot!.Proxies;
            if (proxies.Length == 0) throw new InvalidOperationException("請先匯入代理。");
            var candidates = snapshot!.Targets.Concat(await Task.Run(() => ProcessCatalog.List())).DistinctBy(t => t.Key).ToArray();
            var dialog = new AssignmentWindow(proxies, candidates, backend.Online, probes: snapshot.Probes) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Request is null) return;
            if (dialog.Request.Items.Any(i => i.IncludeChildren) && backend.Online) RequireProcessHandoffSupport();
            try {
                await backend.Assign(dialog.Request);
                foreach (var item in dialog.Request.Items) if (item.Target.Kind == "Process") extraTargets.Add(item.Target);
                if (dialog.EnableImmediately) foreach (var item in dialog.Request.Items) {
                    StatusText.Text = "正在檢測並啟用：" + item.Target.Name;
                    await backend.Act("enable", new TargetRequest(item.Target.Key));
                }
            } finally { await Refresh(true); }
        });
    }
    private void ProbeClick(object sender, RoutedEventArgs e) { probeSchedule.Request(ProxyGrid.SelectedItems.Cast<ProxyRow>().Select(r => r.Profile.Id).ToArray()); RenderSnapshot(); StartProbeWorker(); }
    private void ProbeAllClick(object sender, RoutedEventArgs e) { probeSchedule.Request(snapshot?.Proxies.Select(p => p.Id) ?? []); RenderSnapshot(); StartProbeWorker(); }
    private void ManualProbeClick(object sender, RoutedEventArgs e) => RequestManualProbe();
    private void RequestManualProbe()
    {
        var ids = ProxyGrid.SelectedItems.Cast<ProxyRow>().Select(row => row.Profile.Id).ToArray();
        if (ids.Length == 0) ids = snapshot?.Proxies.Select(p => p.Id).ToArray() ?? [];
        probeSchedule.Request(ids); RenderSnapshot(); StartProbeWorker();
    }
    private async void BindClick(object sender, RoutedEventArgs e)
    {
        var selected = TargetGrid.SelectedItems.Cast<TargetRow>().Select(r => r.Target).ToArray();
        if (selected.Any(t => t.Kind == "VMware")) { await Run("正在讀取虛擬機規則…", OpenVmware); return; }
        if (selected.Length == 0) { MessageBox.Show(this, "請先選取目標，或使用「自動分配並代理」設定持續匹配規則。", "xa sk5"); return; }
        await Run("正在依數量分配並啟用…", async () => {
            await Refresh(false);
            if (snapshot!.Proxies.Length == 0) throw new InvalidOperationException("請先匯入代理。");
            var dialog = new AssignmentWindow(snapshot.Proxies, selected, backend.Online, true, snapshot.AutoProxy.PerProxy, snapshot.Probes) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Request is null) return;
            if (dialog.Request.Items.Any(i => i.IncludeChildren) && backend.Online) RequireProcessHandoffSupport();
            try {
                await backend.Assign(dialog.Request);
                if (dialog.EnableImmediately) foreach (var item in dialog.Request.Items) await backend.Act("enable", new TargetRequest(item.Target.Key));
            } finally { await Refresh(true); }
        });
    }
    private async void EnableClick(object sender, RoutedEventArgs e) => await Run("啟用前正在檢測代理…", async () => {
        var selected = TargetGrid.SelectedItems.Cast<TargetRow>().ToArray();
        foreach (var row in selected)
        {
            await backend.Act("enable", new TargetRequest(row.Target.Key));
            if (row.Target.Kind == "LDPlayer")
            {
                bool restart = row.Target.Running && MessageBox.Show(this, $"「{row.Name}」已在執行。現在重啟以建立新的代理連線？這會中斷目前遊戲。", "重新建立連線", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
                if (restart) {
                    await LdPlayer.RunAsync(row.Target.Path, ["quit", "--index", row.Target.Index.ToString()], CancellationToken.None);
                    var deadline = DateTimeOffset.Now.AddSeconds(30);
                    while (true) {
                        var output = await LdPlayer.RunAsync(row.Target.Path, ["list2"], CancellationToken.None);
                        if (!LdPlayer.Parse(output, row.Target.Path, LdPlayer.Identity).Any(t => t.Index == row.Target.Index && t.Running)) break;
                        if (DateTimeOffset.Now >= deadline) throw new InvalidOperationException("實例尚未結束，請稍後再次啟用。代理規則仍保留。");
                        await Task.Delay(500);
                    }
                }
                if (!row.Target.Running || restart) await LdPlayer.RunAsync(row.Target.Path, ["launch", "--index", row.Target.Index.ToString()], CancellationToken.None);
            }
            else if (row.Target.Kind == "MuMu" && !row.Target.Running) {
                await EmulatorCatalog.RunAsync(row.Target.Path, ["control", "-v", row.Target.Index.ToString(), "launch"], lifetime.Token);
            }
        }
        await Refresh(true);
    });
    private async void PauseClick(object sender, RoutedEventArgs e) => await Run("正在暫停…", async () => { foreach (var row in TargetGrid.SelectedItems.Cast<TargetRow>().ToArray()) await backend.Act("pause", new TargetRequest(row.Target.Key)); await Refresh(false); });
    private async void UnbindClick(object sender, RoutedEventArgs e)
    {
        if (TargetGrid.SelectedItems.Count == 0) return;
        if (MessageBox.Show(this, snapshot?.GlobalProxy.Enabled == true ? "解除後這些實例會改依全局代理規則連線。" : "解除後這些實例可使用原有網路直接連線。", "解除綁定", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Run("正在解除…", async () => { foreach (var row in TargetGrid.SelectedItems.Cast<TargetRow>().ToArray()) await backend.Act("unbind", new TargetRequest(row.Target.Key)); await Refresh(false); });
    }
    private async void ProcessClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Owner = this, Title = "選取一般程序", Width = 820, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new DataGrid { ItemsSource = ProcessCatalog.List(), Margin = new Thickness(18), AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "窗口／程序", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); grid.Columns.Add(new DataGridTextColumn { Header = "程序身分", Binding = new System.Windows.Data.Binding("Detail"), Width = 250 });
        var panel = new DockPanel(); var button = new Button { Content = "加入窗口管理", Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button); panel.Children.Add(grid); dialog.Content = panel;
        button.Click += (_, _) => { if (grid.SelectedItem is TargetInstance) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true && grid.SelectedItem is TargetInstance target) { extraTargets.Add(target); await Run("正在加入程序…", () => Refresh(true)); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    private void PickDown(object sender, MouseButtonEventArgs e) { picking = true; Mouse.Capture((Button)sender); Mouse.OverrideCursor = Cursors.Cross; e.Handled = true; }
    private async void PickUp(object sender, MouseButtonEventArgs e)
    {
        if (!picking) return; picking = false; Mouse.Capture(null); Mouse.OverrideCursor = null; GetCursorPos(out var point); GetWindowThreadProcessId(WindowFromPoint(point), out var pid);
        await Run("正在辨識窗口…", async () => { var target = ProcessCatalog.List().FirstOrDefault(t => t.Processes.Any(p => p.Pid == pid)); if (target is null) throw new InvalidOperationException("無法辨識此窗口，請改用程序清單。"); extraTargets.Add(target); await Refresh(true); });
    }
    private async void BrowseLdClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "雷電命令列|ldconsole.exe;dnconsole.exe" };
        if (dialog.ShowDialog(this) == true) await Run("正在設定路徑…", async () => { if (backend.Online) await backend.Act("setLdPath", dialog.FileName); backend.ConsolePath = dialog.FileName; LdPath.Text = dialog.FileName; await Refresh(true); });
    }
    private async void MigrateClick(object sender, RoutedEventArgs e) => await Run("正在移交設定…", async () => { await backend.Migrate(); await Refresh(true); });
    private async void RepairComponentsClick(object sender, RoutedEventArgs e)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "WindowSocks.Maintenance.exe");
        if (!File.Exists(helper)) { MessageBox.Show(this, "缺少安裝元件，請解壓縮完整的 0.1.1 以上可攜版，或使用新版安裝包。", "xa sk5"); return; }
        if (MessageBox.Show(this, "將要求 Windows 管理員授權，安裝／修復背景服務，並從官方 GitHub 下載 WinDivert。\n\n請先關閉受管遊戲／模擬器，修復時會重新啟動服務。\n\nWindows 安全性若阻擋驅動，流程會停止並顯示原因；管理員授權不會解除該攔截。", "安裝／修復代理元件", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await Run("請在 Windows 授權視窗按「是」，完成後關閉元件安裝視窗…", async () => {
            var start = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--install-components"); start.ArgumentList.Add("--owner-sid"); start.ArgumentList.Add(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
            Process? installer;
            try { installer = Process.Start(start); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException(ComponentPackage.DriverError(1223)); }
            using (installer) {
                if (installer is null) throw new IOException("無法啟動元件安裝程式。");
                await installer.WaitForExitAsync();
                await Refresh(true);
                if (installer.ExitCode != 0) throw new InvalidOperationException("元件安裝未完成。請依安裝視窗的原因處理；紀錄位於 ProgramData\\WindowSocks\\component-install.log。");
                for (int retry = 0; retry < 5 && !backend.Online; retry++) { await Task.Delay(1000); await Refresh(false); }
                if (!backend.Online) throw new InvalidOperationException("驅動載入測試通過，但尚未連上服務。請稍後重新整理並查看診斷。");
                if (backend.HasLocalProxies) { await backend.Migrate(); await Refresh(false); }
            }
        });
    }
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "操作說明.html"); if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); else MessageBox.Show(this, "請開啟交付資料夾內的 docs／操作說明.html。", "xa sk5");
    }
    private async void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "連線診斷 JSON|*.json", FileName = "xa-sk5-diagnostics.json" };
        if (dialog.ShowDialog(this) == true) await Run("正在擷取程序、窗口與代理連線診斷…", async () => {
            var report = await backend.Diagnose();
            await File.WriteAllTextAsync(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(Json.Options) { WriteIndented = true }));
        });
    }
    private async void BackupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "DPAPI 加密備份|*.dpapi", FileName = backend.Online ? "xa-sk5-service.dpapi" : "xa-sk5-local.dpapi" };
        if (dialog.ShowDialog(this) == true) await Run("正在備份加密設定…", async () => await File.WriteAllBytesAsync(dialog.FileName, await backend.Backup()));
    }
    private string? AskText(string title, string description, string initial, bool multiline)
    {
        var dialog = new Window { Owner = this, Title = title, Width = 660, Height = multiline ? 480 : 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new Grid { Margin = new Thickness(28) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(header);
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(14), Tag = "貼上代理資料…", ToolTip = "支援 host:port、host:port:user:password、host/port/user/password/日期、SOCKS5 URI，以及 host,port,username,password,name,expiresOn CSV。到期日期使用 yyyy-MM-dd，可留空。" };
        Grid.SetRow(box, 1); panel.Children.Add(box);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        footer.Children.Add(new Button { Content = "取消", IsCancel = true });
        var button = new Button { Content = "匯入節點", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Margin = new Thickness(0) };
        footer.Children.Add(button); Grid.SetRow(footer, 2); panel.Children.Add(footer); dialog.Content = panel;
        button.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true ? box.Text : null;
    }
    public sealed record TargetRow(TargetInstance Target, string Name, string Proxy, string Exit, string State, string Traffic, string Protocols) : INotifyPropertyChanged
    {
        public DateTimeOffset? ConnectedAt { get; init; }
        public string ConnectionDuration => ProxySessionTracker.Duration(ConnectedAt, DateTimeOffset.UtcNow);
        public string ConnectionHint => (ConnectedAt is null ? "尚未連入代理，或背景服務尚未提供計時。" : $"本次代理自 {ConnectedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 啟用；暫停、換 IP 或重開窗口會重新計時。") + $"\n連線 TCP / UDP：{Connections}";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void UpdateDuration() => PropertyChanged?.Invoke(this, new(nameof(ConnectionDuration)));
        public string Uploaded => "↑ " + Traffic.Split('/')[0].Trim();
        public string Downloaded => Traffic.Contains('/') ? "↓ " + Traffic.Split('/')[1].Trim() : "—";
        public string StateLabel => State.Contains("封鎖") ? "已封鎖" : State.Contains("準備未完成") ? "準備未完成" : State.Contains("等待網路準備") ? "等待準備" : State.Contains("待確認") ? "待確認" : State.Contains("本地網路已套用") ? "本地網路" : State.Contains("代理執行中") || State.Contains("代理規則已套用") ? "代理中" : State.Contains("全局") ? "全局規則" : State.Contains("等待") ? "等待啟動" : State.Contains("未啟動") ? "未啟動" : State.Contains("核心") ? "核心未啟用" : "未代理";
        public string Tone => State.Contains("封鎖") || State.Contains("異常") || State.Contains("準備未完成") ? "danger" : State.Contains("代理執行中") || State.Contains("代理規則已套用") || State.Contains("本地網路已套用") ? "success" : State.Contains("等待") || State.Contains("待確認") ? "warning" : "neutral";
        public string Connections { get; init; } = "—";
        public string Members { get; init; } = "";
        public ProxyNodeDisplay? Node { get; init; }
        public string AccountPort { get; init; } = "—";
        public string EntryIp { get; init; } = "—";
        public string InstanceDetail { get; init; } = "";
    }
    public sealed record ProxyRow(ProxyProfile Profile, ProbeResult? Probe, int Count, string Owners, bool Probing, bool Queued = false)
    {
        public override string ToString() => Name;
        public string Name => Profile.Name;
        public string Endpoint => Profile.Endpoint;
        public ProxyNodeDisplay Node => new(Profile, Probe);
        public string AccountPort => Node.AccountPort;
        public string EntryIp => Node.EntryIp;
        public string Expiration => Profile.ExpiresOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        public string Reachable => Probe is null ? "—" : Probe.Reachable ? "通過" : "失敗";
        public string Auth => Probe is null ? "—" : Probe.Authenticated ? "通過" : "未通過";
        public string UdpExit => Probe?.UdpExit ?? "未驗證";
        public string CheckedAt => Probe?.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        public string Message => Probe?.Message ?? "尚未檢測";
        public string Status => Probing ? "檢測中…" : Queued ? "檢測中…（等待空位）" : Probe is null ? "未檢測" : ProbeTiming.IsStale(Probe.At, DateTimeOffset.UtcNow) ? "待自動複測" : Probe.StatusLabel;
        public string StateLabel => Probing || Queued ? "檢測中" : Probe is null ? "未檢測" : ProbeTiming.IsStale(Probe.At, DateTimeOffset.UtcNow) ? "待複測" : Probe.UdpPending ? "TCP 通過" : Probe.Ready ? "可用" : Probe.Tcp ? "僅 TCP" : !Probe.Reachable ? "無法連線" : !Probe.Authenticated ? "驗證失敗" : "檢測失敗";
        public string Tone => Probing || Queued || Probe?.UdpPending == true ? "busy" : Probe is null ? "neutral" : ProbeTiming.IsStale(Probe.At, DateTimeOffset.UtcNow) ? "warning" : Probe.Ready ? "success" : Probe.Tcp ? "warning" : "danger";
        public string ProtocolSummary => Probe is null ? "TCP —   UDP —" : $"TCP {(Probe.Tcp ? "✓" : "×")}   UDP {(Probe.UdpPending ? "待測" : !Probe.UdpChecked ? "—" : Probe.Udp ? "✓" : "×")}";
        public string Tcp => Probe is null ? "—" : Probe.Tcp ? "通過" : "失敗";
        public string Udp => Probe is null ? "—" : Probe.UdpPending ? "背景檢測中" : !Probe.UdpChecked ? "未檢測" : Probe.Udp ? "通過" : "失敗";
        public string Exit => Probe?.TcpExit ?? "—";
        public string Location => Probe?.Location?.Label ?? "";
        public string Latency => Probing || Queued ? "—" : Probe?.LatencyLabel ?? "—";
        public string Usage => Count == 0 ? "—" : Count.ToString();
    }
}

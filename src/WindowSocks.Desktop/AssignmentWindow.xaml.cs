using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class AssignmentWindow : Window
{
    private readonly ProxyProfile[] proxies;
    private readonly ProbeResult[] probes;
    private readonly Choice[] choices;
    private readonly bool online;
    private bool ready;
    public BatchBindRequest? Request { get; private set; }
    public bool EnableImmediately => EnableAfter.IsChecked == true;
    public AssignmentWindow(ProxyProfile[] proxies, TargetInstance[] candidates, bool online, bool selectAll = false, int perProxy = 1, ProbeResult[]? probes = null)
    {
        this.proxies = proxies; this.probes = probes ?? []; this.online = online; choices = candidates.DistinctBy(t => t.Key).Select(t => new Choice(t)).ToArray();
        InitializeComponent(); Candidates.ItemsSource = choices;
        Limit.Text = perProxy.ToString();
        if (selectAll) foreach (var c in choices) c.Selected = true;
        foreach (var c in choices) c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Choice.Selected)) Update(); };
        ProxySummary.Text = $"代理池 · {proxies.Length} 個節點，依序分配至勾選的窗口。";
        EnableAfter.IsEnabled = online; EnableAfter.IsChecked = online;
        ModeHint.Text = online ? "共用節點會使用相同出口；既有連線需重新連線。" : "離線模式：僅儲存分配，尚未啟用代理。";
        ModeHint.ToolTip = online ? "其他獨占綁定不會自動改成共用；同一程序的多個窗口只算一個目標，重開程序後須重新選取。" : "不會攔截或封鎖流量。安裝服務後到設定頁移交，再啟用。";
        ready = true; Update();
    }
    private void Change(object sender, TextChangedEventArgs e) { if (ready) Update(); }
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        CollectionViewSource.GetDefaultView(Candidates.ItemsSource).Filter = o => AssignmentPlanner.Matches(((Choice)o).Target, Search.Text, (TargetMatch)MatchMode.SelectedIndex);
        Update();
    }
    private void SelectFiltered(object sender, RoutedEventArgs e) { foreach (var c in choices.Where(c => AssignmentPlanner.Matches(c.Target, Search.Text, (TargetMatch)MatchMode.SelectedIndex))) c.Selected = true; }
    private void ClearSelection(object sender, RoutedEventArgs e) { foreach (var c in choices) c.Selected = false; }
    private void Update()
    {
        foreach (var c in choices) c.Node = null;
        Request = null; Apply.IsEnabled = false;
        try {
            if (!int.TryParse(Limit.Text, out int count)) throw new InvalidOperationException("請輸入每條代理的目標數（1–10）。");
            var items = AssignmentPlanner.Distribute(choices.Where(c => c.Selected).Select(c => c.Target).ToArray(), proxies.Select(p => p.Id).ToArray(), count)
                .Select(i => i with { IncludeChildren = i.Target.Kind == "Process" && Children.IsChecked == true }).ToArray();
            foreach (var item in items) choices.First(c => c.Target.Key == item.Target.Key).Node = new(proxies.First(p => p.Id == item.ProxyId), probes.FirstOrDefault(p => p.ProxyId == item.ProxyId));
            Request = new(items); Apply.IsEnabled = true;
            Summary.Text = $"已選 {items.Length} 個窗口 · 使用 {items.Select(i => i.ProxyId).Distinct().Count()} 個節點";
        } catch (InvalidOperationException ex) { Summary.Text = choices.Any(c => c.Selected) ? ex.Message : "選取要分配的窗口"; }
    }
    private void Confirm(object sender, RoutedEventArgs e) { Update(); if (Request is not null) DialogResult = true; }
    public sealed class Choice(TargetInstance target) : INotifyPropertyChanged
    {
        public TargetInstance Target { get; } = target;
        public string Identity => EmulatorCatalog.IsEmulator(Target.Kind) ? $"{EmulatorCatalog.Name(Target.Kind)} #{Target.Index}" : System.IO.Path.GetFileName(Target.Path) + " · " + string.Join(", ", Target.Processes.Select(p => p.Pid));
        private bool selected; private ProxyNodeDisplay? node;
        public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public ProxyNodeDisplay? Node { get => node; set { node = value; PropertyChanged?.Invoke(this, new(nameof(Node))); PropertyChanged?.Invoke(this, new(nameof(Proxy))); } }
        public string Proxy => Node?.Label ?? "—";
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

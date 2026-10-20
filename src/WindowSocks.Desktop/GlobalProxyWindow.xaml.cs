using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class GlobalProxyWindow : Window
{
    private readonly ObservableCollection<RemoteProgram> remotePrograms = new();
    public GlobalProxySettings? Settings { get; private set; }
    public GlobalProxyWindow(ProxyProfile[] profiles, GlobalProxySettings settings, ProbeResult[]? probes = null)
    {
        InitializeComponent(); Proxy.ItemsSource = profiles.Select(p => new ProxyNodeDisplay(p, probes?.FirstOrDefault(result => result.ProxyId == p.Id))).ToArray();
        Proxy.SelectedValue = profiles.Any(p => p.Id == settings.ProxyId) ? settings.ProxyId : profiles.FirstOrDefault()?.Id;
        BypassLan.IsChecked = settings.BypassLan;
        BypassRemoteControl.IsChecked = settings.BypassRemoteControl;
        foreach (var path in settings.RemoteControlExecutables) remotePrograms.Add(new(path));
        RemotePrograms.ItemsSource = remotePrograms;
    }
    private void AddRemoteProgram(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "遠端控制程式|*.exe", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        try {
            var paths = RemoteControlBypass.NormalizeCustom(remotePrograms.Select(p => p.Path).Concat(dialog.FileNames));
            remotePrograms.Clear(); foreach (var path in paths) remotePrograms.Add(new(path)); Error.Text = "";
        } catch (InvalidOperationException ex) { Error.Text = ex.Message; }
    }
    private void RemoveRemoteProgram(object sender, RoutedEventArgs e) { if (RemotePrograms.SelectedItem is RemoteProgram program) remotePrograms.Remove(program); }
    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (Proxy.SelectedItem is not ProxyNodeDisplay p) { Error.Text = "請先選擇代理節點。"; return; }
        Settings = new() { Enabled = true, ProxyId = p.Id, BypassLan = BypassLan.IsChecked == true, BypassRemoteControl = BypassRemoteControl.IsChecked == true,
            RemoteControlExecutables = RemoteControlBypass.NormalizeCustom(remotePrograms.Select(program => program.Path)) };
        DialogResult = true;
    }
    public sealed record RemoteProgram(string Path) { public string Name => System.IO.Path.GetFileName(Path); }
}

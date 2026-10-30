using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static async Task RunSandboxUi()
    {
        var fixture = new Fixture();
        ProxyProfile[] nodes = [new() { Name = "線路 A", Host = "192.0.2.1", Port = 1080 }, new() { Name = "線路 B", Host = "192.0.2.2", Port = 1080 }];
        var a = SandboxInstances.Create("遊戲 A", @"C:\SyntheticGame\Launcher.exe", nodes[0].Id) with { SimulateAdmin = true };
        var b = SandboxInstances.Create("遊戲 B", a.SourceLauncherPath, nodes[1].Id);
        fixture.State = fixture.State with { SupportsSandboxInstances = true, SupportsSandboxExperience = true, SupportsSandboxSafety = true, Proxies = nodes, SandboxInstances = [b, a],
            SandboxStatuses = [new(a.Id, "未啟動", "", []), new(b.Id, "啟動失敗 · 已封鎖", "合成錯誤", [])] };
        var backend = new FakeBackend(fixture);
        var watch = Stopwatch.StartNew();
        var window = new SandboxWindow(backend, fixture.State, a.SourceLauncherPath) { ShowInTaskbar = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
        try {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); watch.Stop();
            Check((Guid)((ComboBox)window.FindName("LineA")).SelectedValue == a.ProxyId, "Reverse profile order changed A's fixed line.");
            Check((Guid)((ComboBox)window.FindName("LineB")).SelectedValue == b.ProxyId, "Reverse profile order changed B's fixed line.");
            Check(((CheckBox)window.FindName("SimulateAdmin")).IsChecked == true, "Compatibility setting was not restored.");
            Check(((DataGrid)window.FindName("Instances")).Items.Count == 2, "Instances missing from sandbox list.");
            await Until(() => ((CheckBox)window.FindName("YongCompatibility")).IsChecked == true);
            SaveImage(window, "sandbox-normal.png"); window.Width = 800; window.Height = 630;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); SaveImage(window, "sandbox-minimum.png");
            var work = (Task)typeof(SandboxWindow).GetMethod("Work", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [new Func<CancellationToken, Task>(ct => Task.Delay(Timeout.Infinite, ct))])!;
            Check(!((Button)window.FindName("StartBothButton")).IsEnabled, "Duplicate start remained enabled during work.");
            Check(!((ComboBox)window.FindName("LineA")).IsEnabled, "Line selection changed during pending preparation.");
            var response = Stopwatch.StartNew(); await Dispatcher.Yield(DispatcherPriority.Background); response.Stop();
            Check(response.ElapsedMilliseconds < 300, "Pending work blocked the UI dispatcher.");
            ((Button)window.FindName("CancelWork")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await work;
            Check(((Button)window.FindName("StartBothButton")).IsEnabled, "Cancel did not restore controls.");
            Check(fixture.Saves == 0 && fixture.VmwareWrites == 0, "Opening/cancelling wrote unrelated rules.");
            ((CheckBox)window.FindName("YongCompatibility")).IsChecked = false;
            await Task.Delay(300);
            Check(((CheckBox)window.FindName("YongCompatibility")).IsChecked == false, "Background detection overrode the user's choice.");
            var launch = new SandboxLaunchWindow(backend, [a, b], 10) { ShowInTaskbar = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
            launch.Show(); await Until(() => backend.SandboxStarts.Count == 1);
            Check(backend.SandboxStarts.Single() == a.Id, "Batch launch ignored fixed A/B ordering.");
            ((Button)launch.FindName("CancelLaunch")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !backend.SandboxLaunchBusy); launch.Close();
            Check(backend.SandboxStarts.Count == 1, "Cancelled countdown launched B.");
            var loading = new TaskCompletionSource<TargetInstance[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var assign = new AssignmentWindow(nodes, [], true, loadCandidates: _ => loading.Task) { ShowInTaskbar = false, Left = -5000, Top = -5000 };
            assign.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(assign.IsVisible && !loading.Task.IsCompleted, "Assignment editor waited for process enumeration.");
            assign.Close(); loading.SetResult([]);
            fixture.State = fixture.State with { SupportsSandboxSafety = false };
            bool safetyRejected = false; try { await backend.SandboxSnapshot(); } catch (InvalidOperationException) { safetyRejected = true; }
            Check(safetyRejected, "P0-1: service without first-packet sandbox identity protection was accepted.");
            fixture.State = fixture.State with { SupportsSandboxSafety = true };
            fixture.State = fixture.State with { SupportsSandboxInstances = false };
            bool rejected = false; try { await backend.SandboxSnapshot(); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("0.1.27"); }
            Check(rejected, "Old service was accepted for sandbox launch.");
            Console.WriteLine($"Sandbox UI checks passed; open {watch.ElapsedMilliseconds} ms; dispatcher {response.ElapsedMilliseconds} ms.");
        } finally { window.Close(); }
        fixture.State = fixture.State with { SupportsSandboxInstances = true };
        var main = new MainWindow(backend) { ShowInTaskbar = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
        ((System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(main)!).Visible = false;
        try {
            main.Show(); await Until(() => typeof(MainWindow).GetField("snapshot", Private)!.GetValue(main) is not null);
            typeof(MainWindow).GetField("contextRuleKey", Private)!.SetValue(main, b.TargetKey);
            Invoke(main, "StartRuleClick"); var launch = await Editor<SandboxLaunchWindow>();
            await Until(() => backend.SandboxStarts.Count == 2);
            Check(backend.SandboxStarts.Last() == b.Id, "Right-click launch used a different rule's instance.");
            await Until(() => launch.Finished);
            typeof(MainWindow).GetField("contextRuleKey", Private)!.SetValue(main, a.TargetKey);
            Invoke(main, "StartRuleClick"); await Until(() => backend.SandboxStarts.Count == 3);
            Check(!launch.IsVisible && backend.SandboxStarts.Last() == a.Id, "Completed progress window prevented the next rule launch.");
            launch = await Editor<SandboxLaunchWindow>();
            launch.Close();
            Check(fixture.Saves == 0, "Launch changed automatic rules.");
        } finally {
            fixture.Discovery.TrySetResult([]);
            typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close();
        }
    }
}

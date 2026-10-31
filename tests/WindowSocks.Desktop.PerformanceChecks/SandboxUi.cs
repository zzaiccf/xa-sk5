using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static async Task RunSandboxUi()
    {
        await CheckSandboxHandoff();
        var fixture = new Fixture();
        ProxyProfile[] nodes = Enumerable.Range(1, 10).Select(i => new ProxyProfile { Name = "線路 " + i, Host = "192.0.2." + i, Port = 1080 }).ToArray();
        var a = SandboxInstances.Create("遊戲 A", @"C:\SyntheticGame\Launcher.exe", nodes[0].Id) with { SimulateAdmin = true };
        var b = SandboxInstances.Create("遊戲 B", a.SourceLauncherPath, nodes[1].Id);
        var c = SandboxInstances.Create("遊戲 C", a.SourceLauncherPath, nodes[2].Id);
        fixture.State = fixture.State with { SupportsSandboxInstances = true, SupportsSandboxExperience = true, SupportsSandboxSafety = true, SupportsSandboxLocalNetwork = true, Proxies = nodes, SandboxInstances = [c, b, a],
            SandboxStatuses = [new(a.Id, "未啟動", "", []), new(b.Id, "啟動失敗 · 已封鎖", "合成錯誤", [])] };
        var backend = new FakeBackend(fixture);
        var watch = Stopwatch.StartNew();
        var window = new SandboxWindow(backend, fixture.State, a.SourceLauncherPath) { ShowInTaskbar = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
        try {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); watch.Stop();
            Check(window.FindName("InstallSandboxButton") is Button, "A new PC needs an in-app Sandboxie installation action.");
            Check(window.FindName("LineAssignments") is ItemsControl assignments && assignments.Items.Count == 3, "Three saved sandboxes must expose three editable fixed-line rows.");
            var rows = (ItemsControl)window.FindName("LineAssignments");
            SandboxWindow.LineAssignment[] Choices() => rows.Items.Cast<SandboxWindow.LineAssignment>().ToArray();
            Check(Choices().Select(r => r.ProxyId).SequenceEqual(new[] { a.ProxyId, b.ProxyId, c.ProxyId }), "Reverse profile order changed fixed lines.");
            Check(Choices().All(r => !r.UseLocalNetwork), "Existing proxied instances silently became local.");
            Choices()[0].UseLocalNetwork = true;
            fixture.State = fixture.State with { Proxies = nodes.Select(p => p with { Name = p.Name + " updated" }).ToArray() };
            await (Task)typeof(SandboxWindow).GetMethod("RefreshState", Private)!.Invoke(window, [])!;
            Check(Choices()[0].UseLocalNetwork && !Choices()[0].CanSelectProxy && Choices()[0].Choice.ProxyId == Guid.Empty && Choices()[0].InstanceId == a.Id, "Refresh lost explicit local choice or instance identity.");
            Choices()[0].UseLocalNetwork = false;
            Check(Choices()[0].CanSelectProxy && Choices()[0].ProxyId == a.ProxyId, "Switching back to proxy did not restore the draft line.");
            Check(((CheckBox)window.FindName("SimulateAdmin")).IsChecked == true, "Compatibility setting was not restored.");
            Check(((DataGrid)window.FindName("Instances")).Items.Count == 3, "Instances missing from sandbox list.");
            Check(!((Button)window.FindName("RemoveDraftButton")).IsEnabled, "Saved instances could be silently removed.");
            for (int i = 3; i < 10; i++) ((Button)window.FindName("AddInstanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rows.Items.Count == 10 && Choices().Select(r => r.ProxyId).Distinct().Count() == 10, "Adding instances did not allocate ten distinct draft lines.");
            Check(!((Button)window.FindName("AddInstanceButton")).IsEnabled, "An eleventh instance remained available.");
            ((Button)window.FindName("AddInstanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rows.Items.Count == 10, "Disabled add allowed an eleventh instance.");
            for (int i = 10; i > 3; i--) ((Button)window.FindName("RemoveDraftButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Choices()[2].ProxyId = nodes[9].Id;
            await (Task)typeof(SandboxWindow).GetMethod("RefreshState", Private)!.Invoke(window, [])!;
            Check(Choices()[2].ProxyId == nodes[9].Id, "Refreshing silently discarded C's unsaved line selection.");
            Choices()[2].ProxyId = c.ProxyId;
            await Until(() => ((CheckBox)window.FindName("YongCompatibility")).IsChecked == true);
            SaveImage(window, "sandbox-normal.png"); window.Width = 800; window.Height = 630;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); SaveImage(window, "sandbox-minimum.png");
            var work = (Task)typeof(SandboxWindow).GetMethod("Work", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [new Func<CancellationToken, Task>(ct => Task.Delay(Timeout.Infinite, ct))])!;
            Check(!((Button)window.FindName("StartAllButton")).IsEnabled, "Duplicate start remained enabled during work.");
            Check(!rows.IsEnabled && !((Button)window.FindName("AddInstanceButton")).IsEnabled, "Line/count selection remained enabled during preparation.");
            var response = Stopwatch.StartNew(); await Dispatcher.Yield(DispatcherPriority.Background); response.Stop();
            Check(response.ElapsedMilliseconds < 300, "Pending work blocked the UI dispatcher.");
            ((Button)window.FindName("CancelWork")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await work;
            Check(((Button)window.FindName("StartAllButton")).IsEnabled, "Cancel did not restore controls.");
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
            fixture.State = fixture.State with { SupportsSandboxInstances = true };
            var grid = (DataGrid)window.FindName("Instances");
            grid.SelectedItem = grid.Items.Cast<SandboxWindow.InstanceRow>().Single(r => r.Profile.Id == c.Id);
            ((Button)window.FindName("StartSelectedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => backend.SandboxStarts.Count == 2 && ((Button)window.FindName("StartSelectedButton")).IsEnabled);
            Check(backend.SandboxStarts.Last() == c.Id, "Selected-instance launch did not target C.");
            backend.EnvironmentStatus = new(false, true, "合成：未安裝");
            await (Task)typeof(SandboxWindow).GetMethod("CheckEnvironmentState", Private)!.Invoke(window, [])!;
            Check(((Button)window.FindName("InstallSandboxButton")).IsEnabled, "Missing installation did not enable one-click setup.");
            ((Button)window.FindName("InstallSandboxButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => backend.Installs == 1 && ((Button)window.FindName("PrepareButton")).IsEnabled);
            Check(!((Button)window.FindName("InstallSandboxButton")).IsEnabled && ((TextBlock)window.FindName("EnvironmentText")).Text.Contains("完成"), "Installation did not recheck environment/disable duplicate install.");
            backend.EnvironmentStatus = new(false, false, "設定待初始化") { NeedsInitialization = true };
            await (Task)typeof(SandboxWindow).GetMethod("CheckEnvironmentState", Private)!.Invoke(window, [])!;
            Check(((Button)window.FindName("PrepareButton")).IsEnabled && !((Button)window.FindName("InstallSandboxButton")).IsEnabled, "Pending initialization created a dead end or requested reinstall.");
            int previous = backend.SandboxStarts.Count;
            ((TextBox)window.FindName("LaunchInterval")).Text = "1";
            ((Button)window.FindName("StartAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => backend.SandboxStarts.Count == previous + 3 && ((Button)window.FindName("StartAllButton")).IsEnabled);
            Check(backend.SandboxStarts.Skip(previous).SequenceEqual(new[] { a.Id, b.Id, c.Id }), "Batch launch omitted or reordered a saved instance.");
            // The real importer and resume planner feed all three rows into preparation; only install/copy/IPC are faked.
            string sourceFolder = Path.Combine(Output, "SyntheticGame"); Directory.CreateDirectory(sourceFolder);
            string sourcePath = Path.Combine(sourceFolder, "Launcher.exe"); File.WriteAllText(sourcePath, "synthetic non-executable input");
            ((TextBox)window.FindName("SourcePath")).Text = sourcePath;
            await Task.Delay(300);
            ((Button)window.FindName("AddInstanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rows.Items.Count == 3, "New game count did not expand to three.");
            Guid[] requestedLines = Choices().Select(r => r.ProxyId).ToArray();
            backend.EnvironmentStatus = new(false, true, "合成：未安裝");
            ((Button)window.FindName("PrepareButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => backend.PreparedProfiles is { Length: 3 } && ((Button)window.FindName("PrepareButton")).IsEnabled);
            Check(backend.Installs == 2 && backend.PreparedProfiles!.Select(p => p.ProxyId).SequenceEqual(requestedLines), "Prepare did not automatically install and retain every selected line.");
            Check(((DataGrid)window.FindName("Instances")).Items.Count == 3, "Prepared third instance did not appear in the launch list.");
            Guid savedA = Choices()[0].InstanceId!.Value;
            Choices()[0].UseLocalNetwork = true;
            ((Button)window.FindName("SaveNetworkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => backend.SavedNetworks is { Length: 1 } && ((Button)window.FindName("SaveNetworkButton")).IsEnabled);
            Check(backend.SavedNetworks![0].Id == savedA && backend.SavedNetworks[0].UseLocalNetwork && backend.SavedNetworks[0].ProxyId == Guid.Empty, "Network-only save did not target just changed A.");
            var localRow = ((DataGrid)window.FindName("Instances")).Items.Cast<SandboxWindow.InstanceRow>().Single(r => r.Profile.Id == savedA);
            Check(localRow.Line.Contains("本地網路") && localRow.Line.Contains("略過全局代理"), "Sandbox list does not distinguish local mode.");
            SaveImage(window, "sandbox-local-network.png");
            Console.WriteLine($"Sandbox UI checks passed; open {watch.ElapsedMilliseconds} ms; dispatcher {response.ElapsedMilliseconds} ms.");
        } finally { window.Close(); }
        fixture.State = fixture.State with { SupportsSandboxInstances = true };
        int startsBeforeContext = backend.SandboxStarts.Count;
        var main = new MainWindow(backend) { ShowInTaskbar = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
        ((System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(main)!).Visible = false;
        try {
            main.Show(); await Until(() => typeof(MainWindow).GetField("snapshot", Private)!.GetValue(main) is not null);
            typeof(MainWindow).GetMethod("SelectWorkspace", Private)!.Invoke(main, [1]);
            var rules = (DataGrid)main.FindName("RuleGrid");
            Check(rules.SelectionMode == DataGridSelectionMode.Extended, "Rules must support Ctrl/Shift selection without expanding to every instance of a game.");
            ProxyRuleItem Row(string key) => rules.Items.Cast<ProxyRuleItem>().Single(r => r.Key == key);
            void Menu() {
                var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, [rules, true], null)!;
                typeof(MainWindow).GetMethod("RuleContextOpening", Private)!.Invoke(main, [rules, args]);
            }
            void Select(params string[] keys) { rules.UnselectAll(); foreach (string key in keys) rules.SelectedItems.Add(Row(key)); Menu(); }
            void RightClick(string key) {
                rules.ScrollIntoView(Row(key)); rules.UpdateLayout();
                var row = (DataGridRow)rules.ItemContainerGenerator.ContainerFromItem(Row(key));
                var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = row };
                // PreviewMouseRightButtonDown is re-raised as a direct event by
                // WPF's mouse pipeline; invoke the actual preview handler with
                // the realized row rather than pretending RaiseEvent bubbles it.
                typeof(MainWindow).GetMethod("RuleGridRightMouseDown", Private)!.Invoke(main, [rules, mouse]);
                Check(mouse.Handled, "Right-click was not handled for a realized rule row.");
                Menu();
            }
            var startMenu = (MenuItem)main.FindName("ContextStartSandbox"); var stopMenu = (MenuItem)main.FindName("ContextStopSandbox");
            var editMenu = (MenuItem)main.FindName("ContextEditRule"); var deleteMenu = (MenuItem)main.FindName("ContextDeleteRule");
            Select(a.TargetKey, b.TargetKey); RightClick(a.TargetKey);
            Check(rules.SelectedItems.Count == 2, "Right-click inside selection discarded another selected rule.");
            Check((string)startMenu.Header == "啟動選取的 2 個實例（間隔 10 秒）" && (string)stopMenu.Header == "停止選取的 2 個實例", "Multiple-selection menu has wrong scope.");
            Check(!editMenu.IsEnabled && !deleteMenu.IsEnabled && !((Button)main.FindName("EditSelectedRule")).IsEnabled, "Multi-selection still permits edit/remove.");
            typeof(MainWindow).GetMethod("RenderSnapshot", Private)!.Invoke(main, []);
            Check(rules.SelectedItems.Count == 2, "Refresh lost multi-selection.");
            RightClick(c.TargetKey); Check(rules.SelectedItems.Count == 1 && (rules.SelectedItem as ProxyRuleItem)?.Key == c.TargetKey, "Right-click outside selection did not replace selection.");
            Check((string)startMenu.Header == "啟動此實例" && (string)stopMenu.Header == "停止此實例" && editMenu.IsEnabled && deleteMenu.IsEnabled, "Single-selection menu mismatch.");
            Check(main.FindName("ContextStartSandboxGroup") is null && startMenu.ToolTip.ToString()!.Contains("啟動器"), "Obsolete same-game action or launcher explanation missing.");
            var extra = new ProxyRuleItem("synthetic-ordinary", "Ordinary", "Process", "", "", "", "", "");
            var actualRows = rules.Items.Cast<ProxyRuleItem>().ToArray(); rules.ItemsSource = actualRows.Append(extra).ToArray();
            Select(a.TargetKey, extra.Key); Check(startMenu.Visibility == Visibility.Collapsed && stopMenu.Visibility == Visibility.Collapsed, "Mixed selection exposed sandbox launch/stop.");
            rules.ItemsSource = actualRows;
            Select(b.TargetKey);
            Invoke(main, "StartRuleClick"); var launch = await Editor<SandboxLaunchWindow>();
            await Until(() => backend.SandboxStarts.Count == startsBeforeContext + 1);
            Check(backend.SandboxStarts.Last() == b.Id, "Right-click launch used a different rule's instance.");
            await Until(() => launch.Finished);
            Select(a.TargetKey);
            Invoke(main, "StartRuleClick"); await Until(() => backend.SandboxStarts.Count == startsBeforeContext + 2);
            Check(!launch.IsVisible && backend.SandboxStarts.Last() == a.Id, "Completed progress window prevented the next rule launch.");
            launch = await Editor<SandboxLaunchWindow>();
            Check(((TextBlock)launch.FindName("DescriptionText")).Text.Contains("啟動器"), "Launch window promises direct game entry.");
            launch.Close();
            Check(fixture.Saves == 0, "Launch changed automatic rules.");
            backend.StorageEntries = [new(a, System.IO.Path.GetDirectoryName(a.WorkingDirectory)!, 123, true, "")];
            Invoke(main, "DeleteRuleClick"); var storage = await Editor<SandboxStorageWindow>();
            await Until(() => ((DataGrid)storage.FindName("StorageRows")).SelectedItem is SandboxStorageEntry);
            Check(((SandboxStorageEntry)((DataGrid)storage.FindName("StorageRows")).SelectedItem).Profile?.Id == a.Id, "Right-click removal selected the wrong instance.");
            storage.Close(); Check(backend.RemovalCalls == 0 && fixture.Saves == 0, "Closing removal dialog deleted a rule.");
            backend.SandboxAlreadyRunning.Add(a.Id);
            Select(a.TargetKey, c.TargetKey); // Click order differs from displayed C,B,A order.
            var expected = rules.Items.Cast<ProxyRuleItem>().Where(rules.SelectedItems.Contains).Select(r => fixture.State.SandboxInstances.Single(p => p.TargetKey == r.Key).Id).ToArray();
            Invoke(main, "StartRuleClick"); launch = await Editor<SandboxLaunchWindow>();
            await Until(() => backend.SandboxStarts.Count == startsBeforeContext + 3);
            Check(backend.SandboxStarts.Last() == expected[0], "Selected launch used click/name order instead of grid order.");
            Invoke(main, "StartRuleClick"); await Task.Delay(100);
            Check(System.Windows.Application.Current.Windows.OfType<SandboxLaunchWindow>().Count(w => w.IsVisible) == 1 && backend.SandboxStarts.Count == startsBeforeContext + 3, "Double-click launched a duplicate batch.");
            var launchWait = Stopwatch.StartNew();
            while (!launch.Finished && launchWait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            Check(launch.Finished && backend.SandboxStarts.Skip(startsBeforeContext + 2).SequenceEqual(expected), "Selected batch order or count is wrong.");
            Check(((TextBlock)launch.FindName("LaunchResults")).Text.Contains("已在執行，略過") && ((TextBlock)launch.FindName("LaunchResults")).Text.Contains(SandboxLaunchErrors.LauncherOpened), "UI merged already-running and newly-opened outcomes.");
            launch.Close();
        } finally {
            fixture.Discovery.TrySetResult([]);
            typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close();
        }
    }
}

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static async Task RunSandboxStorage()
    {
        var fixture = new Fixture(); var backend = new FakeBackend(fixture);
        var a = SandboxInstances.Create("測試遊戲 A", @"C:\SyntheticGame\Launcher.exe", Guid.NewGuid());
        var b = SandboxInstances.Create("測試遊戲 B", a.SourceLauncherPath, Guid.NewGuid());
        backend.StorageEntries = [new(a, System.IO.Path.GetDirectoryName(a.WorkingDirectory)!, 8L * 1024 * 1024 * 1024, true, ""), new(b, System.IO.Path.GetDirectoryName(b.WorkingDirectory)!, 200, false, ""), new(null, @"C:\Synthetic\Unknown", null, false, "找不到歸屬紀錄，未允許刪除")];
        backend.StorageScanWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new SandboxStorageWindow(backend, a.Id) { ShowInTaskbar = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
        Button Button(string name) => (Button)window.FindName(name);
        void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var rows = (DataGrid)window.FindName("StorageRows");
        var permanent = (RadioButton)window.FindName("DeleteFiles");
        var confirm = (CheckBox)window.FindName("ConfirmPermanent");
        try {
            window.Show(); await Until(() => backend.StorageScanCalls > 0);
            var response = Stopwatch.StartNew(); await Dispatcher.Yield(DispatcherPriority.Background); response.Stop();
            Check(!backend.StorageScanWait.Task.IsCompleted && response.ElapsedMilliseconds < 300, "Slow storage scan blocked dispatcher.");
            Click("CancelStorageButton"); await Until(() => Button("RefreshStorageButton").IsEnabled);
            Check(backend.RemovalCalls == 0, "Cancelling scan changed data.");
            backend.StorageScanWait = null; Click("RefreshStorageButton"); await Until(() => rows.Items.Count == 3 && Button("RemoveStorageButton").IsEnabled);
            Check(((SandboxStorageEntry)rows.SelectedItem).Profile?.Id == a.Id, "Preferred instance was not selected.");
            Check(((RadioButton)window.FindName("KeepFiles")).IsChecked == true, "Removal must default to retaining data.");
            permanent.IsChecked = true; Check(!Button("RemoveStorageButton").IsEnabled, "Permanent deletion enabled without confirmation.");
            Click("RemoveStorageButton"); Check(backend.RemovalCalls == 0, "Synthetic click bypassed permanent confirmation.");
            confirm.IsChecked = true; rows.SelectedIndex = 1;
            Check(confirm.IsChecked == false && !permanent.IsChecked!.Value, "Changing selection retained permanent approval.");
            Click("RemoveStorageButton"); await Until(() => Button("RemoveStorageButton").IsEnabled && backend.RemovalCalls == 1);
            Check(backend.RemovedId == b.Id && !backend.DeletedFiles && rows.Items.Count == 3, "Keep mode deleted data or targeted another row.");
            rows.SelectedIndex = 0; permanent.IsChecked = true; confirm.IsChecked = true;
            SaveImage(window, "storage-normal.png"); window.Width = 800; window.Height = 650;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); SaveImage(window, "storage-minimum.png");
            backend.WaitForRemoval = true; Click("RemoveStorageButton");
            await Until(() => backend.RemovalCalls == 2);
            Check(!rows.IsEnabled && !Button("RemoveStorageButton").IsEnabled, "Concurrent deletion remained enabled.");
            Click("CancelStorageButton"); await Until(() => Button("RemoveStorageButton").IsEnabled);
            Check(((TextBlock)window.FindName("StorageProgress")).Text.Contains("取消"), "Cancellation falsely reported success.");
            backend.WaitForRemoval = false; backend.FailRemoval = true; Click("RemoveStorageButton");
            await Until(() => Button("RemoveStorageButton").IsEnabled && backend.RemovalCalls == 3);
            Check(((TextBlock)window.FindName("StorageProgress")).Text.Contains("檔案仍被占用"), "Partial failure was hidden.");
            backend.FailRemoval = false; Click("RemoveStorageButton");
            await Until(() => rows.Items.Count == 2 && Button("RefreshStorageButton").IsEnabled);
            Check(backend.RemovedId == a.Id && backend.DeletedFiles, "Confirmed removal targeted wrong instance.");
            rows.SelectedItem = rows.Items.Cast<SandboxStorageEntry>().Single(r => r.Profile is null);
            Check(!Button("RemoveStorageButton").IsEnabled, "Unverified residual folder allowed automatic deletion.");
            Console.WriteLine($"Sandbox storage UI PASS; scan/cancel/retention/confirmation/retry; dispatcher {response.ElapsedMilliseconds} ms; synthetic backend only.");
        } finally { window.Close(); }
    }
}

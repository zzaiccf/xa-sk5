using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class SandboxStorageWindow : Window
{
    private readonly Backend backend;
    private Guid? preferred;
    private bool busy, ready;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    public SandboxStorageWindow(Backend backend, Guid? selected = null)
    {
        this.backend = backend; preferred = selected; InitializeComponent(); ready = true;
        Loaded += async (_, _) => await Scan();
        Closed += (_, _) => { lifetime.Cancel(); operation?.Cancel(); };
    }
    private void Controls()
    {
        if (!ready) return;
        RefreshStorageButton.IsEnabled = StorageRows.IsEnabled = KeepFiles.IsEnabled = DeleteFiles.IsEnabled = !busy;
        ConfirmPermanent.IsEnabled = !busy && DeleteFiles.IsChecked == true;
        RemoveStorageButton.IsEnabled = !busy && StorageRows.SelectedItem is SandboxStorageEntry { CanRemove: true } && (DeleteFiles.IsChecked != true || ConfirmPermanent.IsChecked == true);
        RemoveStorageButton.Content = DeleteFiles.IsChecked == true ? "永久移除並清除資料" : "移除設定、保留資料";
        CancelStorageButton.IsEnabled = busy;
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); Controls();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) StorageProgress.Text = "已取消；尚未完成的清理紀錄保留，請重新掃描後重試。"; }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) StorageProgress.Text = "未完成：" + ex.Message + "\n請重新掃描；已保留的資料或清理紀錄不會隱藏。"; }
        finally { operation.Dispose(); operation = null; busy = false; if (!lifetime.IsCancellationRequested) Controls(); }
    }
    private async Task LoadRows(CancellationToken ct)
    {
        var rows = await backend.SandboxStorageSnapshot(ct);
        if (lifetime.IsCancellationRequested) return;
        StorageRows.ItemsSource = rows;
        StorageRows.SelectedItem = rows.FirstOrDefault(r => r.Profile?.Id == preferred) ?? rows.FirstOrDefault();
        StorageSummary.Text = $"{rows.Length} 項 · 已計算 {rows.Where(r => r.Bytes.HasValue).Sum(r => r.Bytes!.Value) / 1073741824d:F2} GB · 無法完整確認 {rows.Count(r => !r.CanRemove)} 項";
    }
    private Task Scan() => Run(async ct => { StorageProgress.Text = "正在背景掃描，請稍候…"; await LoadRows(ct); StorageProgress.Text = "掃描完成。僅會處理你選取的實例。"; });
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Scan();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        ConfirmPermanent.IsChecked = false; KeepFiles.IsChecked = true;
        if (!busy) StorageProgress.Text = "";
        if (StorageRows.SelectedItem is SandboxStorageEntry row) {
            preferred = row.Profile?.Id;
            SelectedName.Text = row.Name + " · " + row.Size;
            SelectedPath.Text = row.Path + (row.Profile is { } p ? "\n原始遊戲（不會刪除）：" + p.SourceLauncherPath : "\n" + row.Error);
        }
        Controls();
    }
    private void ModeChanged(object sender, RoutedEventArgs e) => Controls();
    private async void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (busy || StorageRows.SelectedItem is not SandboxStorageEntry { CanRemove: true, Profile: { } profile } || DeleteFiles.IsChecked == true && ConfirmPermanent.IsChecked != true) return;
        bool delete = DeleteFiles.IsChecked == true;
        await Run(async ct => {
            var progress = new Progress<string>(text => { if (!lifetime.IsCancellationRequested) StorageProgress.Text = text; });
            await backend.RemoveSandboxData(profile, delete, progress, ct);
            await LoadRows(ct);
            StorageProgress.Text = delete ? "已清除選取實例的設定及副本。" : "設定已移除，資料仍保留並列在清單中；選擇永久清除才能釋放空間。";
        });
    }
    private void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}

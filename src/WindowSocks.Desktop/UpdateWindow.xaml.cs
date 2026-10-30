using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class UpdateWindow : Window
{
    private readonly ReleaseClient client;
    private readonly string preferencesFile;
    private readonly Func<UpdateJob, string, CancellationToken, Task>? handoff;
    private CancellationTokenSource? cancellation;
    private AppRelease? candidate;
    private Uri? checkedSource;
    private string? localManifest;
    private bool working;
    private bool initialCheckStarted;
    public bool ReadyToExit { get; private set; }
    public UpdateWindow() : this(new ReleaseClient()) { }
    public UpdateWindow(ReleaseClient client, string? preferencesFile = null, Func<UpdateJob, string, CancellationToken, Task>? handoff = null)
    {
        this.client = client; this.handoff = handoff; this.preferencesFile = preferencesFile ?? UpdatePaths.Preferences; InitializeComponent(); CurrentVersionText.Text = AppReleasePackage.CurrentVersion;
        UpdateService.Visibility = UpdatePaths.ServiceInstalled ? Visibility.Visible : Visibility.Collapsed;
        UpdateService.IsChecked = UpdatePaths.ServiceInstalled && Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(UpdatePaths.Installed, StringComparison.OrdinalIgnoreCase);
        UpdateService.IsEnabled = false;
        try {
            string file = File.Exists(this.preferencesFile) ? this.preferencesFile : Path.Combine(AppContext.BaseDirectory, "update-source.json");
            if (File.Exists(file)) SourceText.Text = JsonSerializer.Deserialize<SourcePreference>(File.ReadAllText(file), Json.Options)?.ManifestUrl ?? "";
            else SourceText.Text = ReleaseClient.DefaultSource;
            if (SourceText.Text.Equals(ReleaseClient.LegacyDefaultSource, StringComparison.OrdinalIgnoreCase)) SourceText.Text = ReleaseClient.DefaultSource;
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { UpdateStatus.Text = "無法讀取更新來源，請在下方重新設定。"; }
        Closing += (_, e) => { if (working) { cancellation?.Cancel(); e.Cancel = true; UpdateStatus.Text = "正在取消準備，請稍候…"; } };
        Closed += (_, _) => { cancellation?.Dispose(); client.Dispose(); };
        Loaded += async (_, _) => {
            if (initialCheckStarted) return;
            initialCheckStarted = true;
            await CheckForUpdates();
        };
        UpdateImpact();
    }
    private void InstallerDownloadClick(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/zzaiccf/xa-sk5/releases/latest") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { UpdateStatus.Text = "無法開啟下載頁：" + ex.Message; }
    }
    private void ServiceSelectionChanged(object sender, RoutedEventArgs e) { if (ImpactText is not null) UpdateImpact(); }
    private void UpdateImpact() => ImpactText.Text = UpdateService.IsChecked == true
        ? "已安裝版本透過受保護更新器取得官方套件，停止受管遊戲後更新。需要 0.1.31 以上服務；可攜版、離線包及回復請使用受信任安裝程式處理服務元件。"
        : "以一般權限更新此可攜版。程式與背景元件需要分別更新；代理帳密、到期日及配置保留。";
    private void SourceChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        candidate = null; checkedSource = null; localManifest = null;
        if (UpgradeButton is not null) { UpgradeButton.IsEnabled = false; AvailableVersionText.Text = "尚未檢查"; ReleaseNotes.Visibility = Visibility.Collapsed; ReleaseDetails.Text = ""; }
    }
    private void SetWorking(bool value)
    {
        working = value; CheckButton.IsEnabled = !value; LocalCheck.IsEnabled = !value; SourceText.IsEnabled = !value; RecoverButton.IsEnabled = !value;
        CancelDownload.IsEnabled = true;
        UpgradeButton.IsEnabled = !value && candidate is not null; CancelDownload.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        UpdateService.IsEnabled = false;
        if (!value) DownloadProgress.Visibility = Visibility.Collapsed;
    }
    private async Task Operation(Func<CancellationToken, Task> action)
    {
        if (working) return; cancellation?.Dispose(); cancellation = new(); SetWorking(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { UpdateStatus.Text = cancellation.IsCancellationRequested ? "已取消下載／準備，原程式與服務未變更。" : "連線逾時，請檢查網路後重試。"; }
        catch (Exception ex) { candidate = null; AvailableVersionText.Text = "未取得"; UpdateStatus.Text = ex is System.Net.Http.HttpRequestException ? "無法連線發布來源，請確認網路與發布網址後重試。" : ex.Message; }
        finally { SetWorking(false); }
    }
    private async void CheckClick(object sender, RoutedEventArgs e) => await CheckForUpdates();
    private Task CheckForUpdates() => Operation(async ct => {
        candidate = null; localManifest = null; ReleaseNotes.Visibility = Visibility.Collapsed; ReleaseDetails.Text = ""; UpdateStatus.Text = "正在檢查最新發布版本…"; AvailableVersionText.Text = "檢查中";
        var source = ReleaseClient.SourceUri(SourceText.Text); var release = await client.CheckAsync(source, ct); checkedSource = source;
        Directory.CreateDirectory(Path.GetDirectoryName(preferencesFile)!); File.WriteAllText(preferencesFile, JsonSerializer.Serialize(new SourcePreference(source.AbsoluteUri), Json.Options)); DisplayRelease(release);
    });
    private void DisplayRelease(AppRelease release)
    {
        bool newer = AppReleasePackage.IsNewer(release.Version, AppReleasePackage.CurrentVersion); candidate = newer ? release : null;
        AvailableVersionText.Text = release.Version; ReleaseNotes.Text = release.Notes; ReleaseNotes.Visibility = Visibility.Visible;
        ReleaseDetails.Text = $"發布時間：{release.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm}　下載：{release.Portable.Size / 1024d / 1024:0.0} MB\nSHA-256：{release.Portable.Sha256}";
        UpdateStatus.Text = newer ? "有可用的新版本。確認更新內容後按「升級」。" : release.Version == AppReleasePackage.CurrentVersion ? "目前已是最新發布版本。" : "目前版本比此來源的發布版本新，無需更新。";
    }
    private async void LocalCheckClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "發布版本資訊|*.json", Title = "選擇與 ZIP 放在一起的 latest.json" };
        if (dialog.ShowDialog(this) != true) return;
        await Operation(async ct => {
            candidate = null; checkedSource = null; localManifest = null;
            if (new FileInfo(dialog.FileName).Length > 128 * 1024) throw new InvalidDataException("版本資訊超過大小限制。");
            var release = AppReleasePackage.Read(await File.ReadAllTextAsync(dialog.FileName, ct));
            _ = LocalArchive(dialog.FileName, release); localManifest = dialog.FileName; DisplayRelease(release);
        });
    }
    private static string LocalArchive(string manifest, AppRelease release)
    {
        if (Uri.TryCreate(release.Portable.Url, UriKind.Absolute, out _)) throw new InvalidDataException("離線版本資訊必須使用同目錄 ZIP 的相對檔名。");
        string file = AppReleasePackage.SafePath(Path.GetDirectoryName(manifest)!, release.Portable.Url); AppReleasePackage.RejectLinks(file); return file;
    }
    private async void UpgradeClick(object sender, RoutedEventArgs e)
    {
        if (candidate is not { } release || working) return;
        await Operation(async ct => {
            string work = NewWork(); DownloadProgress.Visibility = Visibility.Visible; DownloadProgress.IsIndeterminate = true;
            var download = new UpdateDownload(release, localManifest is null ? checkedSource!.AbsoluteUri : null, localManifest is null ? null : LocalArchive(localManifest, release));
            UpdatePreparation.Validate(download, release.Version);
            await LaunchHelper(work, "", release.Version, false, ct, download);
        });
        if (ReadyToExit) { DialogResult = true; Close(); }
    }
    private async void RecoverClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "將回復未完成更新前的程式備份。\n\n" + ImpactText.Text, "回復上次更新", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await Operation(async ct => await LaunchHelper(NewWork(), "", AppReleasePackage.CurrentVersion, true, ct));
        if (ReadyToExit) { DialogResult = true; Close(); }
    }
    private static string NewWork() { string path = Path.Combine(UpdatePaths.UserData, "Updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); AppReleasePackage.RejectLinks(path); return path; }
    private async Task LaunchHelper(string work, string payload, string version, bool recover, CancellationToken ct, UpdateDownload? download = null)
    {
        UpdateStatus.Text = "準備獨立更新程式，完成後會重新開啟介面…";
        string helper = Path.Combine(work, "helper");
        // Run the existing trusted updater outside the directory that will be replaced.
        using var parent = Process.GetCurrentProcess();
        var job = new UpdateJob(payload, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), version, Environment.ProcessPath!, parent.Id, parent.StartTime.ToUniversalTime().Ticks, UpdateService.IsChecked == true, recover) { Download = download };
        if(job.IncludeService)ProtectedUpdateRunner.ValidateJob(job);
        if (handoff is not null) { await handoff(job, work, ct); ReadyToExit = true; return; }
        UpdateRunnerTicket? ticket=null;
        string exe,jobFile;
        if(job.IncludeService){
            ticket=await PipeTransport.CallAsync<UpdateRunnerTicket>(Command.Create("prepareUpdateRunner",job),ct);
            _=ProtectedUpdateRunner.ReadTicket(ticket,ticket.Executable);
            work=ticket.Directory;exe=ticket.Executable;jobFile=ticket.JobFile;helper=Path.GetDirectoryName(exe)!;
        }else{
            await Task.Run(() => UpdateHelperPackage.Stage(AppContext.BaseDirectory, helper, ct), ct);
            exe = Path.Combine(helper, "WindowSocks.Updater.exe");
            jobFile = Path.Combine(work, "job.json"); await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(job, Json.Options), ct);
        }
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = helper }; start.ArgumentList.Add("--update"); start.ArgumentList.Add(jobFile);
        if(ticket is not null)start.ArgumentList.Add(ticket.JobHash);
        using var process = Process.Start(start) ?? throw new IOException("無法開啟獨立更新程式。");
        // Once launched, let the handoff complete. Cancellation must not leave a live orphan coordinator.
        CancelDownload.IsEnabled = false;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!File.Exists(Path.Combine(work, "ready"))||new FileInfo(Path.Combine(work,"ready")).Length==0) { if (process.HasExited || DateTime.UtcNow > deadline) throw new IOException("更新程式未就緒；介面維持開啟，請查看更新視窗。"); await Task.Delay(100); }
        ReadyToExit = true;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private sealed record SourcePreference(string ManifestUrl);
}

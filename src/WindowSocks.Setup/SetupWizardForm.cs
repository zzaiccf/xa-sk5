using System.Diagnostics;
using System.Text;

namespace WindowSocks.Setup;

internal sealed class SetupWizardForm : Form
{
    private enum Page { Welcome, Ready, Installing, Complete, Failed }
    private readonly Label title = new() { AutoSize = true, Font = new("Microsoft JhengHei UI", 17, FontStyle.Bold), ForeColor = Color.FromArgb(31, 43, 66), Dock = DockStyle.Fill };
    private readonly Label subtitle = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(107, 119, 142), Text = "安裝精靈", AutoSize = true };
    private readonly Label heading = new() { AutoSize = true, Font = new("Microsoft JhengHei UI", 12, FontStyle.Bold), Dock = DockStyle.Top, Padding = new(0, 0, 0, 14) };
    private readonly Label introduction = new() { Dock = DockStyle.Fill, Padding = new(0, 4, 0, 0) };
    private readonly Panel overview = new() { Dock = DockStyle.Fill, Padding = new(20), BackColor = Color.White };
    private readonly TableLayoutPanel activity = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(20), BackColor = Color.White, Visible = false };
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Margin = new(0, 0, 0, 15) };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new(0, 0, 0, 10) };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 250, 253), BorderStyle = BorderStyle.FixedSingle };
    private readonly Button next = new() { AutoSize = true, MinimumSize = new(132, 38), BackColor = Color.FromArgb(53, 99, 233), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new(8, 0, 0, 0) };
    private readonly Button back = new() { Text = "上一步", AutoSize = true, MinimumSize = new(92, 38), Margin = new(8, 0, 0, 0) };
    private readonly Button cancel = new() { Text = "取消", AutoSize = true, MinimumSize = new(92, 38), Margin = new(8, 0, 0, 0) };
    private readonly LinkLabel openLog = new() { Text = "開啟安裝紀錄", AutoSize = true, Dock = DockStyle.Left, LinkColor = Color.FromArgb(53, 99, 233), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Func<IProgress<SetupProgress>, Task> install;
    private readonly Action launch;
    private Page page;
    private string? logPath;
    private bool writingLogFailed;
    public int Result { get; private set; }

    internal SetupWizardForm(bool update = false, Func<IProgress<SetupProgress>, Task>? install = null, Action? launch = null)
    {
        this.install = install ?? (reporter => Program.RunSetupWorkerAsync(update, reporter));
        this.launch = launch ?? Program.LaunchInstalledDesktop;
        Text = "xa sk5 安裝精靈"; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new(740, 540); MinimumSize = new(700, 540); MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font; Font = new("Microsoft JhengHei UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        Name = "SetupWizard";
        next.Name = "NextButton"; back.Name = "BackButton"; cancel.Name = "CancelButton";
        status.Name = "InstallStatus"; progressBar.Name = "InstallProgress"; log.Name = "InstallLog";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.Absolute, 80)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 60));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Absolute, 58)); header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.RowStyles.Add(new(SizeType.Percent, 55)); header.RowStyles.Add(new(SizeType.Percent, 45));
        header.Controls.Add(new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new(0, 0, 14, 17) }, 0, 0);
        header.SetRowSpan(header.GetControlFromPosition(0, 0)!, 2);
        header.Controls.Add(title, 1, 0); header.Controls.Add(subtitle, 1, 1);
        var body = new Panel { Dock = DockStyle.Fill };
        overview.Controls.Add(introduction); overview.Controls.Add(heading); body.Controls.Add(overview);
        activity.RowStyles.Add(new(SizeType.Absolute, 38)); activity.RowStyles.Add(new(SizeType.Absolute, 58)); activity.RowStyles.Add(new(SizeType.Percent, 100));
        activity.Controls.Add(progressBar, 0, 0); activity.Controls.Add(status, 0, 1); activity.Controls.Add(log, 0, 2); body.Controls.Add(activity);
        var footer = new Panel { Dock = DockStyle.Fill, Padding = new(0, 18, 0, 0) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false };
        actions.Controls.Add(next); actions.Controls.Add(cancel); actions.Controls.Add(back); footer.Controls.Add(actions); footer.Controls.Add(openLog);
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(body, 0, 1); layout.Controls.Add(footer, 0, 2); Controls.Add(layout);
        AcceptButton = next; CancelButton = cancel;
        next.Click += async (_, _) => {
            if (page == Page.Welcome) ShowPage(Page.Ready);
            else if (page is Page.Ready or Page.Failed) await Install();
            else if (page == Page.Complete) {
                try { this.launch(); Close(); }
                catch (Exception ex) { status.Text = "無法啟動：" + ex.Message; AddLog(status.Text); }
            }
        };
        back.Click += (_, _) => ShowPage(page == Page.Ready ? Page.Welcome : Page.Ready);
        cancel.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (page == Page.Installing) e.Cancel = true; };
        openLog.LinkClicked += (_, _) => {
            if (logPath is null) return;
            try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { AddLog("無法開啟紀錄檔：" + ex.Message); }
        };
        try {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks", "Setup", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); logPath = Path.Combine(folder, "install.log");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { writingLogFailed = true; }
        openLog.Enabled = logPath is not null;
        AddLog($"已開啟 xa sk5 {Program.SetupVersion} 安裝精靈。");
        ShowPage(Page.Welcome);
    }

    private void ShowPage(Page value)
    {
        page = value; title.Text = value switch { Page.Installing => "正在安裝 xa sk5", Page.Complete => "安裝完成", Page.Failed => "安裝未完成", _ => "安裝 xa sk5" };
        subtitle.Text = value switch { Page.Welcome => $"版本 {Program.SetupVersion} · Windows x64", Page.Ready => "確認後即可開始安裝", Page.Installing => "請稍候，安裝進度與紀錄顯示如下", Page.Complete => "現在可以啟動程式", _ => "請查看下方紀錄後重試" };
        bool showActivity = value is Page.Installing or Page.Complete or Page.Failed;
        activity.Visible = showActivity; overview.Visible = !showActivity;
        if (showActivity) activity.BringToFront(); else overview.BringToFront();
        heading.Text = value == Page.Welcome ? "歡迎使用 xa sk5" : "準備安裝";
        introduction.Text = value == Page.Welcome
            ? "按「下一步」確認安裝位置，再開始安裝。\r\n\r\n安裝時會顯示進度與紀錄，完成後可直接啟動 xa sk5。"
            : $"安裝位置\r\n{Program.SetupInstallDirectory}\r\n\r\n將安裝主程式、背景代理服務及桌面／開始功能表捷徑。\r\n保留既有代理與開機啟動設定；首次安裝預設開機啟動。\r\n\r\n如已有代理服務執行中，安裝時會短暫重啟服務。";
        back.Visible = value is Page.Ready or Page.Failed;
        cancel.Text = value == Page.Complete ? "完成" : value == Page.Failed ? "關閉" : "取消";
        next.Text = value switch { Page.Welcome => "下一步", Page.Ready => "開始安裝", Page.Installing => "安裝中…", Page.Complete => "啟動 xa sk5", _ => "重試安裝" };
        next.Enabled = cancel.Enabled = value != Page.Installing;
        status.ForeColor = value == Page.Failed ? Color.FromArgb(178, 52, 52) : Color.FromArgb(31, 43, 66);
    }

    private async Task Install()
    {
        ShowPage(Page.Installing); Result = 1; progressBar.Value = 0;
        AddLog($"開始安裝 xa sk5 {Program.SetupVersion}");
        var reporter = new Progress<SetupProgress>(p => {
            progressBar.Value = Math.Max(progressBar.Value, Math.Clamp(p.Percent, 0, 100));
            status.Text = $"{progressBar.Value}% · {p.Message}"; AddLog(p.Message);
        });
        try {
            await install(reporter);
            // Drain posted progress before displaying the final state.
            await Task.Yield();
            progressBar.Value = 100; status.Text = "100% · 程式與背景服務已安裝完成。";
            AddLog("安裝完成，可以啟動 xa sk5。"); Result = 0; ShowPage(Page.Complete);
        }
        catch (Exception ex) {
            await Task.Yield();
            status.Text = ex.Message; AddLog(ex.Message); ShowPage(Page.Failed);
        }
    }

    private void AddLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        log.AppendText(line);
        if (logPath is null || writingLogFailed) return;
        try { File.AppendAllText(logPath, line, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            writingLogFailed = true; openLog.Enabled = false;
            log.AppendText("紀錄檔無法寫入，畫面上的安裝紀錄仍會保留。" + Environment.NewLine);
        }
    }
}

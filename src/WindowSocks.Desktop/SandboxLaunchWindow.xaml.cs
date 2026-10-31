using System;
using System.Linq;
using System.Threading;
using System.Windows;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public partial class SandboxLaunchWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private bool finished, started;
    public bool Finished => finished;
    public SandboxLaunchWindow(Backend backend, SandboxInstanceProfile[] profiles, int intervalSeconds)
    {
        InitializeComponent();
        DescriptionText.Text = (profiles.Length > 1 ? $"依序開啟 {profiles.Length} 個實例的遊戲啟動器，間隔 {intervalSeconds} 秒。已在執行的實例會略過。" : "核對固定線路與沙盒出口後開啟遊戲啟動器。已在執行時不會重複開啟。") + "請在啟動器按開始遊戲並登入。";
        LaunchResults.Text = string.Join("\n", profiles.Select(p => p.Name + " · 等候啟動"));
        Loaded += async (_, _) => {
            if (started) return; started = true;
            try {
                var progress = new Progress<string>(text => { if (IsVisible && !finished) LaunchProgress.Text = text; });
                var results = await backend.StartSandboxes(profiles.Select(p => p.Id).ToArray(), intervalSeconds, progress, lifetime.Token);
                if (!IsVisible) return;
                LaunchResults.Text = string.Join("\n\n", results.Select(r => profiles.Single(p => p.Id == r.Id).Name + "：" + r.Detail));
                LaunchProgress.Text = results.All(r => r.Success) ? "啟動工作完成。新開實例請在啟動器按開始遊戲並登入。" : "部分實例未完成，請依下方原因處理後重試。";
            } catch (OperationCanceledException) { if (IsVisible) LaunchProgress.Text = "已取消。先前已成功啟動的實例保持執行。"; }
            catch (Exception ex) { if (IsVisible) LaunchProgress.Text = ex.Message; }
            finally { finished = true; CancelLaunch.IsEnabled = false; lifetime.Dispose(); }
        };
        Closed += (_, _) => { if (!finished) lifetime.Cancel(); };
    }
    private void Cancel(object sender, RoutedEventArgs e) { if (!finished) { CancelLaunch.IsEnabled = false; LaunchProgress.Text = "正在取消後續啟動；已交接的實例繼續由服務核驗…"; lifetime.Cancel(); } }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}

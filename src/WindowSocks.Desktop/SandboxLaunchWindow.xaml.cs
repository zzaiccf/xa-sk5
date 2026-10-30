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
        DescriptionText.Text = profiles.Length > 1 ? $"依序啟動 {profiles.Length} 個實例，間隔 {intervalSeconds} 秒。已在執行的實例會略過。" : "核對固定線路與沙盒出口後啟動。已在執行時不會重複開啟。";
        LaunchResults.Text = string.Join("\n", profiles.Select(p => p.Name + " · 等候啟動"));
        Loaded += async (_, _) => {
            if (started) return; started = true;
            try {
                var progress = new Progress<string>(text => { if (IsVisible && !finished) LaunchProgress.Text = text; });
                var results = await backend.StartSandboxes(profiles.Select(p => p.Id).ToArray(), intervalSeconds, progress, lifetime.Token);
                if (!IsVisible) return;
                LaunchResults.Text = string.Join("\n\n", results.Select(r => profiles.Single(p => p.Id == r.Id).Name + "：" + r.Detail));
                LaunchProgress.Text = results.All(r => r.Success) ? "啟動工作完成，請在遊戲視窗登入。" : "部分實例未完成，請依下方原因處理後重試。";
            } catch (OperationCanceledException) { if (IsVisible) LaunchProgress.Text = "已取消。先前已成功啟動的實例保持執行。"; }
            catch (Exception ex) { if (IsVisible) LaunchProgress.Text = ex.Message; }
            finally { finished = true; CancelLaunch.IsEnabled = false; lifetime.Dispose(); }
        };
        Closed += (_, _) => { if (!finished) lifetime.Cancel(); };
    }
    private void Cancel(object sender, RoutedEventArgs e) { if (!finished) { CancelLaunch.IsEnabled = false; LaunchProgress.Text = "正在取消未完成的啟動…"; lifetime.Cancel(); } }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}

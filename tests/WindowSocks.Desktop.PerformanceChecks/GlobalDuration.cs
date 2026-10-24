using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static async Task RunGlobalDuration()
    {
        string output = Path.Combine(Root, "artifacts", "global-duration"); Directory.CreateDirectory(output);
        var fixture = new Fixture(); fixture.Discovery.TrySetResult([]); fixture.Adapters.TrySetResult([]);
        var profile = new ProxyProfile { Name = "測試線路", Host = "192.0.2.10", Port = 1080 };
        var since = DateTimeOffset.UtcNow.AddHours(-25).AddMinutes(-2);
        fixture.State = fixture.State with {
            Proxies = [profile], Probes = [new(profile.Id, true, true, true, true, "203.0.113.20", "203.0.113.20", 5, "synthetic", DateTimeOffset.UtcNow)],
            GlobalProxy = new() { Enabled = true, ProxyId = profile.Id },
            GlobalProxyRouting = true, GlobalProxyConnectedAt = since,
            Diagnostics = fixture.State.Diagnostics with { EngineRunning = true }
        };
        var backend = new FakeBackend(fixture);
        var main = new MainWindow(backend) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000 };
        Task Refresh() => (Task)typeof(MainWindow).GetMethod("Refresh", Private)!.Invoke(main, [false, true])!;
        try {
            main.Show(); await Until(() => backend.Snapshots > 0 && ((TextBlock)main.FindName("GlobalDuration")).Text.StartsWith("1 天 01:02:"));
            ((DispatcherTimer)typeof(MainWindow).GetField("timer", Private)!.GetValue(main)!).Stop();
            await Until(() => backend.Snapshots >= 2);
            var duration = (TextBlock)main.FindName("GlobalDuration");
            string first = duration.Text; int calls = backend.Snapshots;
            await Until(() => duration.Text != first);
            Check(backend.Snapshots == calls, "Second ticks queried the service instead of using its session timestamp.");
            main.Width = main.MinWidth; main.Height = main.MinHeight; main.UpdateLayout();
            SaveBulkImage(main, Path.Combine(output, "global-duration-minimum.png"));
            var policies = (FrameworkElement)main.FindName("GlobalPolicies");
            var bounds = duration.TransformToAncestor(policies).TransformBounds(new Rect(duration.RenderSize));
            Check(duration.ActualWidth > 0 && bounds.Left >= 0 && bounds.Right <= policies.ActualWidth + 1 && bounds.Bottom <= policies.ActualHeight + 1,
                "Duration was clipped at the minimum window size.");
            main.Width = 1280; main.Height = 820; main.UpdateLayout();
            SaveBulkImage(main, Path.Combine(output, "global-duration-normal.png"));
            fixture.State = fixture.State with { GlobalProxyRouting = false, GlobalProxyConnectedAt = null }; await Refresh();
            Check(duration.Text == "—", "Blocked global routing continued counting.");
            fixture.State = fixture.State with { GlobalProxyRouting = true, GlobalProxyConnectedAt = DateTimeOffset.UtcNow }; await Refresh();
            Check(duration.Text.StartsWith("00:00:"), "Restored global session did not reset its display.");
            backend.Disconnect();
            typeof(MainWindow).GetMethod("UpdateGlobalDuration", Private)!.Invoke(main, null);
            Check(duration.Text == "未確認", "Disconnected service kept counting cached time.");
            fixture.State = fixture.State with { GlobalProxyConnectedAt = null }; await Refresh();
            Check(duration.Text == "待服務提供", "An older service fabricated a local duration.");
            File.WriteAllText(Path.Combine(output, "summary.json"), System.Text.Json.JsonSerializer.Serialize(new {
                version = AppReleasePackage.CurrentVersion, passed = true, perSecondRpcCalls = backend.Snapshots - calls - 3,
                checks = new[] { "service timestamp with days", "one-second ticks without RPC", "minimum window layout", "interruption and restoration", "offline state", "legacy service" },
                scope = "Isolated WPF with synthetic backend. No live proxy or service changes."
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Global duration WPF verification passed: " + output);
        } finally { typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close(); }
    }
}

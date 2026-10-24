using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static async Task RunTrayBehavior()
    {
        string output = Path.Combine(Root, "artifacts", "tray-behavior"); Directory.CreateDirectory(output);
        var hints = new List<(string Title, string Text, int Duration)>();
        var fixture = new Fixture(); fixture.Discovery.TrySetResult([]); fixture.Adapters.TrySetResult([]);
        var backend = new FakeBackend(fixture); var service = new TraySession();
        var main = new MainWindow(backend, serviceSession: service,
            backgroundNotification: (title, text, duration) => hints.Add((title, text, duration))) {
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000
        };
        bool closed = false; main.Closed += (_, _) => closed = true;
        try {
            main.Show(); await Until(() => service.Starts == 1 && backend.Snapshots > 0);
            var tray = (System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(main)!;
            var open = tray.ContextMenuStrip!.Items.Cast<System.Windows.Forms.ToolStripItem>().Single(i => i.Text == "開啟 xa sk5");
            var exit = tray.ContextMenuStrip.Items.Cast<System.Windows.Forms.ToolStripItem>().Single(i => i.Text == "退出");

            main.WindowState = WindowState.Minimized;
            await Until(() => hints.Count == 1);
            Check(!closed && service.Stops == 0, "Minimize stopped proxy service or closed the application.");
            Check(hints[0].Duration == 3000 && hints[0].Text.Contains("工作列") && hints[0].Text.Contains("退出"), "Background hint omitted the restore/exit instructions or short duration.");
            main.Close(); main.Close();
            Check(!closed && service.Stops == 0 && hints.Count == 1, "Repeated taskbar Close stopped the proxy or spammed notifications.");
            open.PerformClick();
            Check(main.IsVisible && main.WindowState == WindowState.Normal && service.Starts == 1, "Tray restore created a new service session or failed to restore.");
            main.Close(); await Until(() => hints.Count == 2);
            Check(!closed && main.WindowState == WindowState.Minimized && service.Stops == 0, "X did not preserve the background proxy session.");

            var taskbar = typeof(MainWindow).Assembly.GetType("WindowSocks.Desktop.DesktopTaskbar")!;
            var task = (JumpTask)taskbar.GetMethod("ExitTask", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [@"C:\Program Files\Synthetic xa sk5\xa sk5.exe"])!;
            Check(task.Title == "退出" && task.Arguments == "--exit" && task.ApplicationPath.EndsWith("xa sk5.exe") && task.WorkingDirectory == @"C:\Program Files\Synthetic xa sk5", "Taskbar Exit does not route the spaced executable path through the single-instance exit command.");

            exit.PerformClick(); exit.PerformClick();
            await Until(() => service.Stops == 1);
            Check(!closed && !main.IsEnabled && main.WindowState == WindowState.Normal, "Exit did not wait visibly for the proxy service to stop.");
            main.Close(); Check(!closed && service.Stops == 1 && hints.Count == 2, "Close during shutdown interrupted the exit operation.");
            service.Stop.TrySetResult(); await Until(() => closed);
            Check((bool)typeof(MainWindow).GetField("exitCleaned", Private)!.GetValue(main)! && !tray.Visible, "Exit left the tray or lifecycle resources active.");

            await VerifyFailedTrayExit(hints);
            await VerifyExitDuringStartup();
            File.WriteAllText(Path.Combine(output, "summary.json"), System.Text.Json.JsonSerializer.Serialize(new {
                version = AppReleasePackage.CurrentVersion, passed = true, normalExitStops = service.Stops, hints = hints.Count,
                checks = new[] { "minimize and X preserve proxies", "short restore/exit hint with duplicate suppression", "tray restore", "taskbar exit command and path", "one service stop while waiting", "stop failure restores retryable UI", "exit waits for outstanding startup", "update handoff still closes directly" },
                scope = "Isolated WPF with fake backend/session. No live jump-list, startup, service or game changes."
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Tray/taskbar lifecycle verification passed: " + output);
        } finally {
            service.Stop.TrySetResult();
            if (!closed) { typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close(); }
        }
    }
    private static async Task VerifyFailedTrayExit(List<(string Title, string Text, int Duration)> hints)
    {
        var fixture = new Fixture(); fixture.Discovery.TrySetResult([]); fixture.Adapters.TrySetResult([]);
        var session = new TraySession { FailOnce = true }; var main = new MainWindow(new FakeBackend(fixture), serviceSession: session,
            backgroundNotification: (title, text, duration) => hints.Add((title, text, duration))) {
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000
        };
        bool closed = false; main.Closed += (_, _) => closed = true;
        try {
            main.Show(); await Until(() => session.Starts == 1);
            main.RequestExit(); await Until(() => session.Stops == 1 && main.IsEnabled);
            Check(!closed && main.IsVisible && ((TextBlock)main.FindName("StatusText")).Text.Contains("尚未完成關閉"), "A failed stop silently exited instead of restoring the UI.");
            main.Close(); Check(!closed && session.Stops == 1 && main.WindowState == WindowState.Minimized, "X after an exit failure retried shutdown unexpectedly.");
            main.RequestExit(); await Until(() => session.Stops == 2); session.Stop.TrySetResult(); await Until(() => closed);

            // The updater has already handed off its work; it uses the existing
            // quitting path and must not issue another service stop on close.
            var handoffSession = new TraySession(); var handoff = new MainWindow(new FakeBackend(fixture), serviceSession: handoffSession) { ShowInTaskbar = false };
            typeof(MainWindow).GetField("quitting", Private)!.SetValue(handoff, true); handoff.Close();
            Check(handoffSession.Stops == 0 && (bool)typeof(MainWindow).GetField("exitCleaned", Private)!.GetValue(handoff)!, "Update handoff was redirected to background minimization or a redundant stop.");
        } finally {
            session.Stop.TrySetResult(); if (!closed) { typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close(); }
        }
    }
    private sealed class TraySession : IProxyServiceSession
    {
        public int Starts, Stops; public bool FailOnce;
        public TaskCompletionSource? PendingStart;
        public CancellationToken StartToken;
        public readonly TaskCompletionSource Stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken ct) { Starts++; StartToken = ct; return PendingStart?.Task ?? Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Stops++; if (FailOnce && Stops == 1) throw new IOException("Synthetic service stop failure"); return Stop.Task; }
    }
    private static async Task VerifyExitDuringStartup()
    {
        var fixture = new Fixture(); fixture.Discovery.TrySetResult([]); fixture.Adapters.TrySetResult([]);
        var session = new TraySession { PendingStart = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var main = new MainWindow(new FakeBackend(fixture), serviceSession: session) {
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000
        };
        bool closed = false; main.Closed += (_, _) => closed = true;
        try {
            main.Show(); await Until(() => session.Starts == 1); main.RequestExit(); await Task.Delay(50);
            Check(session.StartToken.IsCancellationRequested && session.Stops == 0 && !closed, "Exit raced an outstanding service start.");
            session.PendingStart.TrySetResult(); await Until(() => session.Stops == 1); session.Stop.TrySetResult(); await Until(() => closed);
        } finally {
            session.PendingStart.TrySetResult(); session.Stop.TrySetResult();
            if (!closed) { typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close(); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static readonly string Root = Directory.GetCurrentDirectory();
    private static readonly string Output = Path.Combine(Root, "artifacts", "rule-editor-performance");
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread] private static int Main()
    {
        Directory.CreateDirectory(Output);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(Root, "src", "WindowSocks.Desktop", "App.xaml"));
        var resources = new XElement(source.Root!.Element(wpf + "Application.Resources")!.Element(wpf + "ResourceDictionary")!);
        resources.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString()) };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        int exit = 1;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () => {
            try { if (Environment.GetCommandLineArgs().Contains("--global-duration")) await RunGlobalDuration(); else if (Environment.GetCommandLineArgs().Contains("--tray-behavior")) await RunTrayBehavior(); else if (Environment.GetCommandLineArgs().Contains("--bulk-actions")) await RunBulkActions(); else await Run(); exit = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }));
        Dispatcher.Run(); return exit;
    }
    private static async Task Run()
    {
        var fixture = new Fixture(); var backend = new FakeBackend(fixture);
        var main = new MainWindow(backend) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000 };
        ((System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(main)!).Visible = false;
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? busy = null;
        var report = new Dictionary<string, object>();
        try {
            main.Show();
            await Until(() => typeof(MainWindow).GetField("snapshot", Private)!.GetValue(main) is not null && fixture.DiscoverEntered > 0);
            ((DispatcherTimer)typeof(MainWindow).GetField("timer", Private)!.GetValue(main)!).Stop();
            ((DispatcherTimer)typeof(MainWindow).GetField("durationTimer", Private)!.GetValue(main)!).Stop();
            Check(!fixture.Discovery.Task.IsCompleted, "Synthetic slow discovery completed unexpectedly.");
            Check(fixture.DiscoverOnWorker, "Discovery's synchronous work ran on the WPF dispatcher.");
            busy = (Task)typeof(MainWindow).GetMethod("Run", Private)!.Invoke(main, ["", new Func<Task>(() => blocker.Task), false])!;
            int snapshots = backend.Snapshots;
            var watch = Stopwatch.StartNew(); Invoke(main, "AutoProxyClick");
            var editor = await Editor<AutoProxyWindow>(); watch.Stop();
            report["openWhileBusyAndDiscoveryPendingMs"] = watch.Elapsed.TotalMilliseconds;
            Check(backend.Snapshots == snapshots, "Opening the editor queried the service instead of using rendered state.");
            Check(!busy.IsCompleted && !fixture.Discovery.Task.IsCompleted, "Editor waited for unrelated work.");
            Invoke(main, "AutoProxyClick");
            Check(System.Windows.Application.Current.Windows.OfType<AutoProxyWindow>().Count() == 1, "Repeated add opened duplicate editors.");
            foreach (int mode in new[] { 0, 1, 2, 3, 2 }) {
                ((ComboBox)editor.FindName("Mode")).SelectedIndex = mode;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(((ComboBox)editor.FindName("Mode")).SelectedIndex == mode, "Target type did not switch.");
            }
            SaveImage(editor, "add-rule-normal.png");
            editor.Width = 760; editor.Height = 650; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SaveImage(editor, "add-rule-minimum.png");
            editor.Close(); await Until(() => !(bool)typeof(MainWindow).GetField("ruleDialogOpen", Private)!.GetValue(main)!);
            Check(fixture.Saves == 0, "Cancelling the editor wrote a rule.");
            Invoke(main, "AutoProxyClick"); editor = await Editor<AutoProxyWindow>();
            ((ComboBox)editor.FindName("Mode")).SelectedIndex = 2;
            ((TextBox)editor.FindName("Pattern")).Text = @"C:\SyntheticGame\Login.exe";
            ((TextBox)editor.FindName("Limit")).Text = "1";
            ((Button)editor.FindName("SaveRuleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!editor.IsVisible, "Save did not dismiss the editor before waiting for the work queue.");
            blocker.TrySetResult(); await busy;
            await Until(() => fixture.Saves == 1 && !(bool)typeof(MainWindow).GetField("ruleDialogOpen", Private)!.GetValue(main)!);
            Check(fixture.State.AutoProxy is { PerProxy: 1, IncludeChildren: true, Match: TargetMatch.ExecutablePath }, "Saving changed process handoff settings.");
            Check(!fixture.Discovery.Task.IsCompleted && fixture.DiscoverEntered == 1, "Saving waited for or restarted discovery.");
            // Both add-menu handoff and the dedicated VMware entry must render before network discovery.
            blocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            busy = (Task)typeof(MainWindow).GetMethod("Run", Private)!.Invoke(main, ["", new Func<Task>(() => blocker.Task), false])!;
            Invoke(main, "AutoProxyClick"); editor = await Editor<AutoProxyWindow>();
            watch.Restart(); ((ComboBox)editor.FindName("Mode")).SelectedIndex = 4;
            var vmware = await Editor<VmwareWindow>(); watch.Stop(); report["vmwareFromAddMenuMs"] = watch.Elapsed.TotalMilliseconds;
            await Until(() => fixture.AdapterEntered > 0);
            Check(!fixture.Adapters.Task.IsCompleted, "Synthetic slow adapter lookup completed unexpectedly.");
            watch.Restart(); vmware.Close(); await Until(() => !vmware.IsVisible); watch.Stop();
            report["cancelDuringVmwareDiscoveryMs"] = watch.Elapsed.TotalMilliseconds;
            await Until(() => !(bool)typeof(MainWindow).GetField("vmwareDialogOpen", Private)!.GetValue(main)!);
            Invoke(main, "VmwareClick"); vmware = await Editor<VmwareWindow>();
            Check(!busy.IsCompleted, "Dedicated VMware entry waited for the main work queue.");
            Check(((DataGrid)vmware.FindName("Machines")).Items.Cast<VmwareWindow.Row>().Single().Name == "快取虛擬機", "Opening lost the cached VM display name.");
            fixture.Adapters.TrySetException(new IOException("Synthetic adapter lookup failure.")); fixture.Discovery.TrySetResult([]);
            await Until(() => ((Button)vmware.FindName("CaptureButton")).IsEnabled);
            Check(!((Button)vmware.FindName("Save")).IsEnabled, "A failed network lookup allowed saving an incomplete adapter selection.");
            typeof(VmwareWindow).GetMethod("RenderState", Private)!.Invoke(vmware, [fixture.State.Vmware! with { HasBackup = true }]);
            Check(((Button)vmware.FindName("Restore")).IsEnabled, "An adapter lookup failure hid the existing network recovery action.");
            vmware.Close(); blocker.TrySetResult(); await busy;
            Check(fixture.Saves == 1 && fixture.VmwareWrites == 0, "Opening/cancelling VMware changed configuration.");
            report["passed"] = true; report["version"] = AppReleasePackage.CurrentVersion;
            report["checks"] = new[] { "cached open while queue and discovery are blocked", "no service RPC to open", "duplicate open suppressed", "all target types switch", "cancel does not save", "save retains path/handoff/quota on 0.1.20", "save does not await discovery", "VMware opens before slow adapter scan", "read-only discovery can be cancelled", "cached VM name retained", "network lookup failure prevents incomplete saves but preserves recovery", "no live service, registry, game or network mutation" };
            await File.WriteAllTextAsync(Path.Combine(Output, "summary.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
        } finally {
            blocker.TrySetResult(); fixture.Discovery.TrySetResult([]); fixture.Adapters.TrySetResult([]);
            foreach (var child in System.Windows.Application.Current.Windows.OfType<Window>().Where(w => w != main).ToArray()) child.Close();
            typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close();
            await Task.Delay(100);
        }
    }
    private static void Invoke(MainWindow window, string method) => window.Dispatcher.BeginInvoke(new Action(() => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, [window, new RoutedEventArgs()])));
    private static async Task<T> Editor<T>() where T : Window {
        T? result = null;
        await Until(() => (result = System.Windows.Application.Current.Windows.OfType<T>().SingleOrDefault(w => w.IsLoaded && w.IsVisible)) is not null);
        return result!;
    }
    private static async Task Until(Func<bool> condition) {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("WPF operation did not finish while independent work was blocked."); await Task.Delay(10); }
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void SaveImage(Window window, string name) {
        var content = window.Content; window.Content = null;
        int width = (int)window.Width - 16, height = (int)window.Height - 39;
        var host = new ContentControl { Content = content, Width = width, Height = height, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, FontFamily = new FontFamily("Microsoft JhengHei UI"), FontSize = 13, Foreground = (Brush)System.Windows.Application.Current.FindResource("Ink") };
        var surface = new Border { Child = host, Background = (Brush)System.Windows.Application.Current.FindResource("Canvas") };
        surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
        var save = (Button)window.FindName("SaveRuleButton"); var point = save.TransformToAncestor(surface).Transform(new Point());
        Check(point.X >= 0 && point.Y >= 0 && point.X + save.ActualWidth <= width && point.Y + save.ActualHeight <= height, "Save button was clipped.");
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var file = File.Create(Path.Combine(Output, name))) png.Save(file);
        host.Content = null; window.Content = content;
    }
    private sealed class Fixture
    {
        public readonly TaskCompletionSource<VmwareDiscovered[]> Discovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<VmwareAdapter[]> Adapters = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DiscoverEntered, AdapterEntered, Saves, VmwareWrites;
        public bool DiscoverOnWorker;
        public Snapshot State = new([], [], [], [], [], new(true, false, false, false, "0.1.20 預覽版", "synthetic", [])) {
            AutoProxy = new() { Kind = "Process", Pattern = "Login.exe", IncludeChildren = true }, SupportsProcessHandoffs = true,
            SupportsVmware = true, SupportsVmwareQuickSetup = true, SupportsVmwareLocalNetwork = true,
            Vmware = new(new() { Machines = [new(@"C:\SyntheticVM\windows.vmx", "", Guid.Empty) { Name = "快取虛擬機" }] }, [], false, false, "合成狀態")
        };
        public async Task<Reply> Transport(Command command, CancellationToken ct) {
            switch (command.Action) {
                case "discoverVmware":
                    DiscoverOnWorker = Environment.CurrentManagedThreadId != System.Windows.Application.Current.Dispatcher.Thread.ManagedThreadId;
                    Interlocked.Increment(ref DiscoverEntered);
                    // Reproduce an external API doing synchronous setup before returning a task.
                    Thread.Sleep(150);
                    return Reply.Success(await Discovery.Task.WaitAsync(ct));
                case "vmwareAdapters": Interlocked.Increment(ref AdapterEntered); return Reply.Success(await Adapters.Task.WaitAsync(ct));
                case "snapshot": return Reply.Success(State);
                case "setAutoProxy": State = State with { AutoProxy = Json.Read<AutoProxyRule>(command.Data) }; Saves++; return Reply.Success(true);
                case "setupVmware": VmwareWrites++; return Reply.Success(true);
                default: return Reply.Success(true);
            }
        }
    }
    private sealed class FakeBackend : Backend
    {
        private readonly Fixture fixture;
        public int Snapshots;
        public void Disconnect() => Online = false;
        public FakeBackend(Fixture fixture) : base(Path.Combine(Output, "fixture-" + Guid.NewGuid().ToString("N")), fixture.Transport) {
            this.fixture = fixture; ConsolePath = @"C:\SyntheticMissingEmulator\ldconsole.exe"; MuMuConsolePath = "";
        }
        public override Task<Snapshot> Snapshot(TargetInstance[] targets) { Online = true; Snapshots++; return Task.FromResult(fixture.State); }
    }
}

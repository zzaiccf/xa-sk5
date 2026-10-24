using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static async Task RunBulkActions()
    {
        string output = Path.Combine(Root, "artifacts", "bulk-actions-ui"); Directory.CreateDirectory(output);
        var fixture = new BulkFixture();
        var backend = new BulkBackend(fixture, Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")));
        bool accept = false; int confirmations = 0; string confirmation = "";
        var main = new MainWindow(backend, confirmBulkAction: (message, _) => { confirmations++; confirmation = message; return accept; }) {
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000
        };
        ((System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("tray", Private)!.GetValue(main)!).Visible = false;
        bool Pending() => (bool)typeof(MainWindow).GetField("bulkActionPending", Private)!.GetValue(main)!;
        Task Refresh() => (Task)typeof(MainWindow).GetMethod("Refresh", Private)!.Invoke(main, [false, true])!;
        try {
            main.Show(); await Until(() => typeof(MainWindow).GetField("snapshot", Private)!.GetValue(main) is not null);
            ((DispatcherTimer)typeof(MainWindow).GetField("timer", Private)!.GetValue(main)!).Stop();
            ((DispatcherTimer)typeof(MainWindow).GetField("durationTimer", Private)!.GetValue(main)!).Stop();
            void Workspace(int index) => typeof(MainWindow).GetMethod("SelectWorkspace", Private)!.Invoke(main, [index]);
            Workspace(2);
            var clear = (Button)main.FindName("ClearAllProxyIps");
            var close = (MenuItem)main.FindName("CloseAllProxyProcesses");
            Check(clear.IsEnabled && !close.IsEnabled, "Empty process scopes must not enable process termination.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SaveBulkImage(main, Path.Combine(output, "proxy-toolbar-normal.png"));
            main.Width = main.MinWidth; main.Height = main.MinHeight; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var page = (Grid)main.FindName("ProxyPage"); var search = (TextBox)main.FindName("ProxySearch");
            Point clearAt = clear.TranslatePoint(new Point(), page), searchAt = search.TranslatePoint(new Point(), page);
            Check(clear.ActualWidth > 80 && clearAt.X + clear.ActualWidth <= searchAt.X + 1 && searchAt.X + search.ActualWidth <= page.ActualWidth + 1, "Minimum-width toolbar clips or overlaps the bulk action.");
            SaveBulkImage(main, Path.Combine(output, "proxy-toolbar-minimum.png"));
            search.Text = "203.0.113.1";
            Check(((DataGrid)main.FindName("ProxyGrid")).Items.Count == 1, "Synthetic proxy search did not filter to one row.");
            Invoke(main, "ClearAllProxyIpsClick"); await Until(() => confirmations == 1 && !Pending());
            Check(confirmation.Contains("全部 3 個") && fixture.Clears == 0 && fixture.State.Proxies.Length == 3, "Cancel deleted nodes or confirmation used only visible rows.");
            accept = true; Invoke(main, "ClearAllProxyIpsClick"); await Until(() => fixture.Clears == 1 && !Pending());
            Check(fixture.State.Proxies.Length == 0 && !clear.IsEnabled && fixture.Deletes == 0, "Clear-all did not use one atomic backend operation.");
            fixture.State = fixture.Initial with {
                GlobalProxy = new() { Enabled = true, ProxyId = fixture.Initial.Proxies[0].Id },
                Bindings = [new() { TargetKey = "fixture-game", Name = "Synthetic game", Kind = "Process", ProxyId = fixture.Initial.Proxies[1].Id, Processes = [new(777777, 100)], IncludeChildren = true }]
            };
            await Refresh(); Workspace(0);
            var global = fixture.State.GlobalProxy; Check(close.IsEnabled, "Individual process scopes did not enable the close option.");
            var menu = (MenuItem)((Menu)main.FindName("RuntimeMenu")).Items[0]; menu.IsSubmenuOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = (System.Windows.Controls.Primitives.Popup)menu.Template.FindName("PART_Popup", menu);
            if (popup?.Child is FrameworkElement content) SaveBulkElement(content, Path.Combine(output, "window-actions-menu.png"));
            menu.IsSubmenuOpen = false;
            accept = false; int before = confirmations; Invoke(main, "CloseAllProxyProcessesClick");
            await Until(() => confirmations == before + 1 && !Pending());
            Check(fixture.Closes == 0 && confirmation.Contains("全局代理") && confirmation.Contains("Synthetic game"), "Cancel closed a program or confirmation omitted its scope.");
            accept = true; Invoke(main, "CloseAllProxyProcessesClick"); await Until(() => fixture.Closes == 1 && !Pending());
            Check(fixture.State.GlobalProxy == global && fixture.State.Proxies.Length == 3, "Closing processes changed global routing or deleted proxy nodes.");
            Check(!close.IsEnabled, "A closed scope left the bulk close action enabled.");
            fixture.State = fixture.Initial with { SupportsBulkActions = false }; await Refresh();
            Check(!clear.IsEnabled && !close.IsEnabled, "An older service enabled unsupported bulk actions.");
            await VerifyOfflineClear(output);
            File.WriteAllText(Path.Combine(output, "summary.json"), System.Text.Json.JsonSerializer.Serialize(new {
                version = AppReleasePackage.CurrentVersion, passed = true, confirmations, atomicClearCalls = fixture.Clears,
                closeCalls = fixture.Closes, globalPreserved = fixture.State.GlobalProxy == fixture.Initial.GlobalProxy,
                minimumWidth = main.MinWidth, scope = "Isolated WPF and synthetic backend only. No user IPs or programs modified."
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Bulk action WPF verification passed: " + output);
        } finally {
            typeof(MainWindow).GetField("quitting", Private)!.SetValue(main, true); main.Close();
            await Task.Delay(100);
            string directory = Path.GetFullPath(backend.Directory);
            if (Path.GetDirectoryName(directory) != output) throw new InvalidOperationException("Unexpected fixture directory.");
            Directory.Delete(directory, true);
        }
    }
    private static void SaveBulkImage(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void SaveBulkElement(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static async Task VerifyOfflineClear(string output)
    {
        string directory = Path.GetFullPath(Path.Combine(output, "offline-" + Guid.NewGuid().ToString("N")));
        var store = new ConfigStore(directory, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var profile = new ProxyProfile { Host = "203.0.113.100", Port = 1080 };
        try {
            store.Save(new() { Proxies = [profile], Bindings = [new() { TargetKey = "paused", ProxyId = profile.Id }] });
            Func<Command, CancellationToken, Task<Reply>> noService = (_, _) => throw new InvalidOperationException("Offline clear must not contact the real service.");
            var blocked = new Backend(directory, noService); bool refused = false;
            try { await blocked.ClearProxyPool(); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("未刪除任何節點"); }
            Check(refused && store.Load().Proxies.Count == 1, "Offline clear ignored a saved assignment.");
            store.Save(new() { Proxies = [profile], LastProbes = [new(profile.Id, true, true, true, true, profile.Host, profile.Host, 1, "synthetic", DateTimeOffset.UtcNow)] });
            var unused = new Backend(directory, noService);
            Check((await unused.ClearProxyPool()).RemovedIds.SequenceEqual(new[] { profile.Id }), "Offline clear did not remove all saved profiles.");
            Check(store.Load().Proxies.Count == 0 && store.Load().LastProbes.Count == 0, "Offline clear left saved nodes or health records.");
        } finally {
            if (Path.GetDirectoryName(directory) != output) throw new InvalidOperationException("Unexpected offline fixture directory.");
            Directory.Delete(directory, true);
        }
    }
    private sealed class BulkFixture
    {
        public readonly Snapshot Initial;
        public Snapshot State;
        public int Clears, Deletes, Closes;
        private Guid token;
        public BulkFixture()
        {
            var profiles = Enumerable.Range(1, 3).Select(i => new ProxyProfile { Name = "Synthetic " + i, Host = "203.0.113." + i, Port = 1080 }).ToArray();
            Initial = State = new(profiles, [], [], profiles.Select(p => new ProbeResult(p.Id, true, true, true, true, p.Host, p.Host, 5, "synthetic", DateTimeOffset.UtcNow)).ToArray(), [],
                new(true, false, true, false, "0.1.22 test", "synthetic", [])) { SupportsBulkActions = true };
        }
        public Task<Reply> Transport(Command command, CancellationToken ct)
        {
            Reply reply = command.Action switch {
                "snapshot" => Reply.Success(State),
                "discoverVmware" => Reply.Success(Array.Empty<VmwareDiscovered>()),
                "syncTargets" => Reply.Success(true),
                "clearProxyPool" => Clear(),
                "prepareCloseProxyProcesses" => Prepare(),
                "closeProxyProcesses" => Close(Json.Read<CloseProxyProcessesRequest>(command.Data)),
                "probe" or "probeTcp" => Reply.Success(State.Probes.Single(p => p.ProxyId == Json.Read<ProxyRequest>(command.Data).ProxyId)),
                "deleteProxy" => Deleted(),
                _ => throw new InvalidOperationException("Unexpected UI transport action: " + command.Action)
            };
            return Task.FromResult(reply);
        }
        private Reply Clear() { Clears++; Guid[] ids = State.Proxies.Select(p => p.Id).ToArray(); State = State with { Proxies = [], Probes = [] }; return Reply.Success(new ClearProxyPoolResult(ids)); }
        private Reply Deleted() { Deletes++; return Reply.Success(true); }
        private Reply Prepare() { token = Guid.NewGuid(); return Reply.Success(new CloseProxyProcessesPlan(token, State.Bindings.Select(b => new ProxyProcessScope(b.TargetKey, b.Name, b.Processes)).ToArray(), 1, DateTimeOffset.UtcNow.AddMinutes(2))); }
        private Reply Close(CloseProxyProcessesRequest request)
        {
            Check(request.Token == token, "UI sent a different close confirmation token."); Closes++;
            var identities = State.Bindings.SelectMany(b => b.Processes).ToArray();
            State = State with { Bindings = State.Bindings.Select(b => b with { Processes = [], Descendants = [] }).ToArray() };
            return Reply.Success(new CloseProxyProcessesResult(identities.Select(p => new ProcessCloseOutcome(p, "closed", "synthetic")).ToArray()));
        }
    }
    private sealed class BulkBackend : Backend
    {
        private readonly BulkFixture fixture;
        public string Directory { get; }
        public BulkBackend(BulkFixture fixture, string directory) : base(directory, fixture.Transport) { this.fixture = fixture; Directory = directory; ConsolePath = @"C:\SyntheticMissingEmulator\ldconsole.exe"; MuMuConsolePath = ""; }
        public override Task<Snapshot> Snapshot(TargetInstance[] targets) { Online = true; return Task.FromResult(fixture.State); }
    }
}

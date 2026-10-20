using System.Runtime.InteropServices;
using WindowSocks.Core;

namespace WindowSocks.Service;

// Independent of proxy probes and the pipe gate: a slow network test must not hide a launcher exit.
internal sealed class GameGroupObserver : IDisposable
{
    private readonly object sync = new();
    private readonly Func<int, bool> owned;
    private readonly Timer timer;
    private AutoProxyRule rule;
    private GameGroupScan scan;
    private string error = "";
    private readonly Dictionary<ProcessIdentity, nint> handles = [];
    public GameGroupObserver(AutoProxyRule rule, IEnumerable<GameLaunchGroup> saved, Func<int, bool> owned)
    {
        this.rule = rule; this.owned = owned; scan = new(saved.ToArray(), []);
        timer = new(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }
    public void SetRule(AutoProxyRule value) { lock (sync) rule = value; }
    public (GameGroupScan Scan, string Error) Read() { lock (sync) return (scan, error); }
    private void Sample()
    {
        if (!Monitor.TryEnter(sync)) return;
        try {
            var paths = scan.Groups.SelectMany(g => new[] { g.LauncherPath, g.ClientPath }).Concat(rule.Enabled && rule.Kind == "GameGroup" ? new[] { rule.Pattern, rule.ClientPath } : []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) { foreach (var h in handles.Values) CloseHandle(h); handles.Clear(); scan = new([], []); error = ""; return; }
            var parents = ProcessCatalog.Parents();
            var observed = paths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(name => ProcessCatalog.List(name)).Where(t => paths.Contains(t.Path, StringComparer.OrdinalIgnoreCase))
                .SelectMany(t => t.Processes.Where(p => owned(p.Pid)).Select(p => new GameProcessObservation(p, t.Path, parents.GetValueOrDefault(p.Pid))))
                .DistinctBy(p => p.Identity).ToArray();
            foreach (var p in observed) if (!handles.ContainsKey(p.Identity)) {
                nint handle = OpenProcess(0x1000, false, p.Identity.Pid);
                if (handle != 0) {
                    if (GetProcessTimes(handle, out long birth, out _, out _, out _) && birth == p.Identity.CreatedFileTime) handles.Add(p.Identity, handle);
                    else CloseHandle(handle);
                }
            }
            var exits = new Dictionary<ProcessIdentity, long>();
            foreach (var (p, handle) in handles) if (GetProcessTimes(handle, out _, out long end, out _, out _) && end > 0) exits[p] = end;
            scan = GameGroupTracker.Update(scan.Groups, rule, observed, exits, DateTime.UtcNow.ToFileTimeUtc()); error = "";
            // Release the handle the moment the exit is recorded: an open handle keeps a dead process object alive,
            // so the PID keeps showing as a running game image and can block the next launcher round.
            foreach (var p in exits.Keys.Where(handles.ContainsKey).ToArray()) { CloseHandle(handles[p]); handles.Remove(p); }
            var retained = scan.Groups.SelectMany(g => g.Members.Select(m => m.Identity)).Concat(observed.Select(p => p.Identity)).ToHashSet();
            foreach (var p in handles.Keys.Where(p => !retained.Contains(p)).ToArray()) { CloseHandle(handles[p]); handles.Remove(p); }
        } catch (Exception ex) when (ex is not OutOfMemoryException) { error = "遊戲組探索失敗：" + ex.GetType().Name; }
        finally { Monitor.Exit(sync); }
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    public void Dispose()
    {
        using var done = new ManualResetEvent(false); timer.Dispose(done); done.WaitOne();
        lock (sync) { foreach (var h in handles.Values) CloseHandle(h); handles.Clear(); }
    }
}

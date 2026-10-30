using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowSocks.Core;

// Load the optional, administrator-installed runtime only from its protected installation directory.
// No Sandboxie DLL or configuration is shipped with xa sk5.
public sealed class SandboxieRuntime : IDisposable
{
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus");
    public static bool Installed => File.Exists(Path.Combine(InstallDirectory, "SbieDll.dll")) && File.Exists(Path.Combine(InstallDirectory, "Start.exe"));
    private readonly nint library;
    private readonly EnumProcesses enumerate;
    private readonly QueryProcess query;
    private readonly KillBox terminate;
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate byte KillBox(uint session, string box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int EnumProcesses(string box, byte allSessions, uint session, [Out] uint[] pids, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryProcess(nint pid, StringBuilder box, StringBuilder image, StringBuilder sid, out uint session);
    public SandboxieRuntime()
    {
        if (!Installed) throw new InvalidOperationException("請先安裝官方 Sandboxie Plus 1.18.8 x64。");
        for (var folder = new DirectoryInfo(InstallDirectory); folder is not null; folder = folder.Parent)
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Sandboxie 安裝目錄不可使用重新解析點。");
        library = NativeLibrary.Load(Path.Combine(InstallDirectory, "SbieDll.dll"));
        try {
            enumerate = Marshal.GetDelegateForFunctionPointer<EnumProcesses>(NativeLibrary.GetExport(library, "SbieApi_EnumProcessEx"));
            query = Marshal.GetDelegateForFunctionPointer<QueryProcess>(NativeLibrary.GetExport(library, "SbieApi_QueryProcess"));
            terminate = Marshal.GetDelegateForFunctionPointer<KillBox>(NativeLibrary.GetExport(library, "SbieDll_KillAll"));
        } catch { NativeLibrary.Free(library); throw; }
    }
    public SandboxProcess? Query(int pid)
    {
        var box = new StringBuilder(34); var image = new StringBuilder(96); var sid = new StringBuilder(96);
        var identity = LdPlayer.Identity(pid);
        if (identity is null || query(pid, box, image, sid, out uint session) != 0 || LdPlayer.Identity(pid) != identity) return null;
        return new(identity.Value, box.ToString(), sid.ToString(), session);
    }
    public SandboxProcess[] Processes(string box)
    {
        var pids = new uint[512];
        uint count = (uint)pids.Length;
        // With a non-null count pointer the API uses it as IN capacity / OUT count, and PID data starts at index zero.
        if (enumerate(box, 1, uint.MaxValue, pids, ref count) != 0) throw new InvalidOperationException("無法確認沙盒程序，保持封鎖。");
        if (count >= pids.Length) throw new InvalidOperationException("沙盒程序過多，無法完整驗證。");
        return CompleteSnapshot(pids.Take((int)count).Select(p => Query((int)p)));
    }
    public static SandboxProcess[] CompleteSnapshot(IEnumerable<SandboxProcess?> members)
    {
        var snapshot = members.ToArray();
        if (snapshot.Any(p => p is null)) throw new IOException("沙盒成員查詢不完整，保留上次核驗身分。");
        return snapshot.Select(p => p!).ToArray();
    }
    public void ValidateSettings(SandboxInstanceProfile profile) => ValidatePersistedSettings(profile);
    public static void ValidatePersistedSettings(SandboxInstanceProfile profile, string? settingsFile = null)
    {
        SandboxInstances.Validate(profile);
        string ini = settingsFile ?? new[] { Path.Combine(InstallDirectory,"Sandboxie.ini"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Sandboxie.ini") }.FirstOrDefault(File.Exists)
            ?? throw new IOException("找不到沙盒設定，拒絕啟動。");
        using var stream = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if(stream.Length>1024*1024)throw new IOException("沙盒設定過大，拒絕啟動。");
        using var reader=new StreamReader(stream, System.Text.Encoding.UTF8, true);
        var lines=reader.ReadToEnd().Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);
        if (!lines.Any(l=>l.Trim().Equals("["+profile.BoxName+"]",StringComparison.OrdinalIgnoreCase))) throw new IOException("BoxName 不符，拒絕啟動。");
        var persisted=SandboxIniSettings.Read(lines,profile.BoxName);var expected=SandboxCompatibility.Settings(profile);
        foreach(string key in new[]{"Enabled","FileRootPath","OpenPipePath"}) {
            if (!persisted.GetValueOrDefault(key,[]).SequenceEqual(expected[key],StringComparer.OrdinalIgnoreCase)) throw new IOException(key+" 不符，拒絕啟動。");
        }
    }
    public void TerminateOwned(SandboxInstanceProfile profile, string ownerSid)
    {
        SandboxInstances.Validate(profile);
        var members = Processes(profile.BoxName);
        if (members.Any(p => p.OwnerSid != ownerSid || p.BoxName != profile.BoxName)) throw new IOException("沙盒擁有者不符，未結束程序。");
        // Sandboxie's service checks the caller's session. Run its official command as
        // the verified owner when this broker is hosted by the LocalSystem service.
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        foreach (uint session in members.Select(p => p.SessionId).Distinct()) {
            if (current.IsSystem) SandboxSessionStop.Run(session, ownerSid, profile.BoxName);
            else if (terminate(session, profile.BoxName) == 0 && Processes(profile.BoxName).Any(p => p.SessionId == session))
                throw new IOException("無法停止受管沙盒：" + profile.Name);
        }
        for (int attempt = 0; attempt < 300 && Processes(profile.BoxName).Length > 0; attempt++) Thread.Sleep(100);
        if (Processes(profile.BoxName).Length > 0) throw new IOException("沙盒仍有程序未結束，請重試。");
    }
    public void Dispose() => NativeLibrary.Free(library);
    public static async Task<string> Command(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        if (executable is not ("Start.exe" or "SbieIni.exe")) throw new InvalidOperationException("不支援的沙盒命令。");
        var start = new ProcessStartInfo(Path.Combine(InstallDirectory, executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("無法執行 Sandboxie。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); } catch { try { process.Kill(); } catch { } throw; }
        string text = await output, detail = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Sandboxie 操作失敗（{process.ExitCode}）：{detail}");
        return text;
    }
    public static async Task Configure(SandboxInstanceProfile profile, CancellationToken ct, IProgress<string>? progress = null)
    {
        SandboxInstances.Validate(profile);
        string ini = new[] { Path.Combine(InstallDirectory, "Sandboxie.ini"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sandboxie.ini") }.FirstOrDefault(File.Exists)
            ?? throw new IOException("找不到 Sandboxie 設定，無法備份。");
        string section = "", heading = "[" + profile.BoxName + "]";
        bool capture = false;
        foreach (string line in await File.ReadAllLinesAsync(ini, ct)) {
            if (line.TrimStart().StartsWith('[')) capture = line.Trim().Equals(heading, StringComparison.OrdinalIgnoreCase);
            if (capture) section += line + Environment.NewLine;
        }
        string backupDirectory = Path.Combine(Path.GetDirectoryName(profile.WorkingDirectory)!, "ConfigBackups");
        Directory.CreateDirectory(backupDirectory);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".ini"), section, ct);
        progress?.Report(profile.Name + " · 正在套用並核對隔離設定…");
        var settings = SandboxCompatibility.Settings(profile);
        await SandboxSettingsTransaction.Apply(settings,
            async (key, token) => SandboxIniSettings.Read(await File.ReadAllLinesAsync(ini, token), profile.BoxName).GetValueOrDefault(key, []),
            async (key, values, token) => {
                await Command("SbieIni.exe", new[] { "set", profile.BoxName, key }.Concat(values.Take(1)), token);
                foreach (string value in values.Skip(1)) await Command("SbieIni.exe", ["append", profile.BoxName, key, value], token);
            }, async token => {
                await Command("Start.exe", ["/reload"], token);
                var persisted = SandboxIniSettings.Read(await File.ReadAllLinesAsync(ini, token), profile.BoxName);
                foreach (string key in settings.Keys) {
                    var direct = persisted.GetValueOrDefault(key, []);
                    if (direct.Length == 0) continue;
                    var active = (await Command("SbieIni.exe", ["query", profile.BoxName, key], token)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).ToArray();
                    if (!active.Take(direct.Length).SequenceEqual(direct, StringComparer.OrdinalIgnoreCase)) throw new IOException("沙盒設定尚未載入：" + key);
                }
            }, ct);
    }
}

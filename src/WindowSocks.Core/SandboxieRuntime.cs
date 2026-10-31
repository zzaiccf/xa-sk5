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
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryConfig(string? box, string setting, uint index, StringBuilder value, uint bytes);
    public string ConfigurationPath()
    {
        // QueryConfAsIs is a header macro, not an exported DLL function.
        var queryConfig = Marshal.GetDelegateForFunctionPointer<QueryConfig>(NativeLibrary.GetExport(library, "SbieApi_QueryConf"));
        var value = new StringBuilder(2048);
        int result = queryConfig(null, "IniLocation", 0x20000000, value, 4096); // CONF_GET_NO_EXPAND, api_flags.h v1.18.8
        if (result != 0) throw new IOException($"無法查詢 Sandboxie 設定位置（0x{result:X8}），請確認 SbieSvc 與驅動可用。");
        return SandboxConfiguration.ResolveLocation(value.ToString(), InstallDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }
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
    public void ValidateSettings(SandboxInstanceProfile profile) => ValidatePersistedSettings(profile, ConfigurationPath());
    public static void ValidatePersistedSettings(SandboxInstanceProfile profile, string? settingsFile = null)
    {
        SandboxInstances.Validate(profile);
        string ini = settingsFile ?? SandboxConfiguration.ResolvePath();
        var lines = SandboxIniFile.Read(ini);
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
    public static Task<string> Command(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        string[] args = arguments.ToArray();
        return executable == "SbieIni.exe" || executable == "Start.exe" && args.Contains("/reload")
            ? SandboxConfiguration.Run(() => CommandCore(executable, args, ct), ct) : CommandCore(executable, args, ct);
    }
    private static async Task<string> CommandCore(string executable, string[] args, CancellationToken ct)
    {
        if (executable is not ("Start.exe" or "SbieIni.exe")) throw new InvalidOperationException("不支援的沙盒命令。");
        var start = new ProcessStartInfo(Path.Combine(InstallDirectory, executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        var started = DateTimeOffset.UtcNow;
        Process startedProcess;
        try { startedProcess = Process.Start(start) ?? throw new IOException("無法執行 Sandboxie。"); }
        catch (Exception ex) { SandboxCommandTimeline.Record(started, executable, args, null, ex.Message); throw; }
        using var process = startedProcess;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(); } catch { } SandboxCommandTimeline.Record(started, executable, args, null, "cancelled or timed out"); throw; }
        string text = await output, detail = await error;
        SandboxCommandTimeline.Record(started, executable, args, process.ExitCode, detail);
        // stdout of a query is configuration data, never an error-code source.
        // SbieIni only exposes wrong-password failures reliably in stdout.
        if (executable == "SbieIni.exe" && process.ExitCode != 0 && text.Contains("Password Invalid", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Sandboxie 設定受到密碼保護，請先由官方管理器處理後重試。");
        string errors = detail;
        if (errors.Contains("C0000043", StringComparison.OrdinalIgnoreCase)) throw new SandboxConfigurationPendingException("Sandboxie 設定分享衝突（C0000043）。");
        if (errors.Contains("C0000022", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Sandboxie 拒絕設定存取（C0000022）；請確認官方管理器的權限／設定密碼。");
        if (errors.Contains("C000003A", StringComparison.OrdinalIgnoreCase) || errors.Contains("C0000033", StringComparison.OrdinalIgnoreCase)) throw new DirectoryNotFoundException("Sandboxie 設定路徑無效；請由官方管理器確認。");
        if (process.ExitCode != 0) throw new InvalidOperationException($"Sandboxie 操作失敗（{process.ExitCode}）：{detail}");
        return text;
    }
    public static Task Configure(SandboxInstanceProfile profile, CancellationToken ct, IProgress<string>? progress = null) => Configure(profile, ct, progress, SandboxConfiguration.ResolvePath, Command);
    internal static Task Configure(SandboxInstanceProfile profile, CancellationToken ct, IProgress<string>? progress,
        Func<string> resolve, Func<string, IEnumerable<string>, CancellationToken, Task<string>> command)
        => SandboxConfiguration.Run(() => ConfigureCore(profile, ct, progress, resolve, command), ct);
    private static async Task ConfigureCore(SandboxInstanceProfile profile, CancellationToken ct, IProgress<string>? progress,
        Func<string> resolve, Func<string, IEnumerable<string>, CancellationToken, Task<string>> command)
    {
        SandboxInstances.Validate(profile);
        string path = SandboxConfiguration.ResolvePath(resolve); // A missing helper is not a missing ini.
        async Task<string[]> Read(CancellationToken token) => await SandboxConfiguration.Retry(async () => {
            try { return await SandboxIniFile.ReadAsync(path, token); }
            catch (FileNotFoundException) { return []; } // Not permission or missing parent directory.
        }, token);
        var original = await Read(ct);
        string section = "", heading = "[" + profile.BoxName + "]";
        bool capture = false;
        foreach (string line in original) {
            if (line.TrimStart().StartsWith('[')) capture = line.Trim().Equals(heading, StringComparison.OrdinalIgnoreCase);
            if (capture) section += line + Environment.NewLine;
        }
        string backupDirectory = Path.Combine(Path.GetDirectoryName(profile.WorkingDirectory)!, "ConfigBackups");
        Directory.CreateDirectory(backupDirectory);
        if (original.Length == 0) section = "; 無既有設定可備份；後續由官方 SbieIni 初始化。" + Environment.NewLine;
        using var timeline = SandboxCommandTimeline.Begin(backupDirectory);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".ini"), section, ct);
        progress?.Report(profile.Name + " · 正在套用並核對隔離設定…");
        var settings = SandboxCompatibility.Settings(profile);
        async Task VerifyResident(CancellationToken token) {
            var persisted = SandboxIniSettings.Read(await Read(token), profile.BoxName);
            foreach (string key in settings.Keys) {
                var direct = persisted.GetValueOrDefault(key, []);
                if (direct.Length == 0) continue;
                var active = (await command("SbieIni.exe", ["query", profile.BoxName, key], token)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).ToArray();
                if (!active.Take(direct.Length).SequenceEqual(direct, StringComparer.OrdinalIgnoreCase)) throw new SandboxConfigurationPendingException("沙盒設定尚未載入：" + key);
            }
        }
        await SandboxSettingsTransaction.Apply(settings,
            async (key, token) => SandboxIniSettings.Read(await Read(token), profile.BoxName).GetValueOrDefault(key, []),
            async (key, values, token) => {
                await SandboxConfiguration.Retry(async () => {
                    await command("SbieIni.exe", new[] { "set", profile.BoxName, key }.Concat(values.Take(1)), token);
                    foreach (string value in values.Skip(1)) await command("SbieIni.exe", ["append", profile.BoxName, key, value], token);
                    // SbieIni may report exit 0 even when its service write failed.
                    if (!SandboxIniSettings.Read(await Read(token), profile.BoxName).GetValueOrDefault(key, []).SequenceEqual(values, StringComparer.OrdinalIgnoreCase))
                        throw new SandboxConfigurationPendingException("沙盒設定尚未穩定：" + key + "；官方工具未回報詳細原因時，請一併檢查設定權限與路徑。");
                }, token);
            }, async token => {
                await SandboxConfiguration.Retry(async () => {
                    await command("Start.exe", ["/reload"], token);
                    await VerifyResident(token);
                }, token);
            }, ct);
        // Also verify unchanged profiles: disk equality alone does not establish
        // that the resident driver loaded the same values.
        bool refresh = false;
        await SandboxConfiguration.Retry(async () => {
            if (refresh) await command("Start.exe", ["/reload"], ct);
            refresh = true;
            var disk = SandboxIniSettings.Read(await Read(ct), profile.BoxName);
            foreach (var (key, expected) in settings)
                if (!disk.GetValueOrDefault(key, []).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
                    throw new SandboxConfigurationPendingException("設定在完成前已變更：" + key);
            await VerifyResident(ct);
        }, ct);
    }
}

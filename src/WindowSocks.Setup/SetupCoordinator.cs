using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowSocks.Core;

namespace WindowSocks.Setup;

internal sealed record SetupProgress(int Percent, string Message, bool? Success = null);

internal static partial class Program
{
    internal static string SetupInstallDirectory => InstallDirectory;
    internal static string SetupVersion => ProductVersion;

    // The wizard keeps the original user's token. Only this worker requests elevation.
    internal static async Task RunSetupWorkerAsync(bool update, IProgress<SetupProgress> progress)
    {
        string pipeName = "xa-sk5-setup-" + Guid.NewGuid().ToString("N");
        string owner = WindowsIdentity.GetCurrent().User!.Value;
        await using var pipe = PipeTransport.CreateServer(owner, pipeName);
        using var connectTimeout = new CancellationTokenSource();
        var connected = pipe.WaitForConnectionAsync(connectTimeout.Token);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--setup-worker"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--owner-sid"); start.ArgumentList.Add(owner);
        if (update) start.ArgumentList.Add("--update");
        progress.Report(new(0, "等候 Windows 管理員授權…"));
        try {
            using var worker = await Task.Run(() => Process.Start(start)) ?? throw new IOException("無法啟動安裝工作。");
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var exited = worker.WaitForExitAsync();
            if (await Task.WhenAny(connected, exited) == exited && !connected.IsCompleted)
                throw new IOException($"安裝工作未能啟動（代碼 {worker.ExitCode}），程式尚未安裝。");
            await connected;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != worker.Id)
                throw new IOException("安裝進度連線身分不符，已停止接收。");
            SetupProgress result;
            do {
                result = await PipeTransport.ReadAsync<SetupProgress>(pipe, CancellationToken.None);
                progress.Report(result);
            } while (result.Success is null);
            await exited;
            if (result.Success != true || worker.ExitCode != 0) throw new IOException(result.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            throw new IOException("已取消 Windows 管理員授權，尚未進行安裝。可按「重試安裝」再試一次。", ex);
        }
        catch (EndOfStreamException ex) {
            throw new IOException("安裝工作意外結束，請查看安裝紀錄後重試。", ex);
        }
        catch (OperationCanceledException ex) when (connectTimeout.IsCancellationRequested) {
            throw new IOException("安裝工作未能在 60 秒內建立進度連線，請關閉其他安裝視窗後重試。", ex);
        }
        finally {
            connectTimeout.Cancel();
            try { await connected; } catch (OperationCanceledException) { }
        }
    }

    private static int RunSetupWorker(string[] args)
    {
        if (args.Length is not (4 or 5) || args[0] != "--setup-worker" || args[2] != "--owner-sid" ||
            args.Length == 5 && args[4] != "--update" || !args[1].StartsWith("xa-sk5-setup-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1]["xa-sk5-setup-".Length..], "N", out _))
            throw new ArgumentException("安裝工作參數無效。");
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(30000); // A visible coordinator must be listening before anything is installed.
        bool logReady = false;
        void Report(SetupProgress progress)
        {
            if (logReady) try { File.AppendAllText(Path.Combine(DataDirectory, "setup-install.log"), $"{DateTimeOffset.Now:O} [{progress.Percent}%] {progress.Message}{Environment.NewLine}", Encoding.UTF8); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { PipeTransport.WriteAsync(pipe, progress, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { } // A lost UI must not interrupt a transaction or its rollback.
        }
        try {
            if (args.Contains("--update") && !File.Exists(Path.Combine(InstallDirectory, "WindowSocks.Service.exe")))
                throw new IOException("找不到可更新的安裝，請重新開啟安裝程式進行一般安裝。");
            Directory.CreateDirectory(DataDirectory); Protect(DataDirectory, true); logReady = true;
            Report(new(1, $"開始安裝 xa sk5 {ProductVersion}"));
            InstallEmbeddedPayload((percent, message) => Report(new(percent, message)));
            Report(new(100, "安裝完成，程式與背景服務已就緒。", true)); return 0;
        }
        catch (Exception ex) {
            Report(new(0, "安裝未完成：" + ex.Message, false)); return 1;
        }
    }

    internal static void LaunchInstalledDesktop()
    {
        string executable = Path.Combine(InstallDirectory, "xa sk5.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到主程式，請重新執行安裝。", executable);
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = InstallDirectory })
            ?? throw new IOException("無法啟動 xa sk5。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

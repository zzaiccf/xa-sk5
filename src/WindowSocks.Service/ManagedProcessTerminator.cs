using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowSocks.Core;

namespace WindowSocks.Service;

internal static class ManagedProcessTerminator
{
    private const uint Query = 0x1000, Terminate = 0x0001, Synchronize = 0x100000;
    private static readonly HashSet<string> infrastructure = new(StringComparer.OrdinalIgnoreCase) {
        "xa sk5.exe", "WindowSocks.exe", "WindowSocks.Service.exe", "WindowSocks.Tools.exe",
        "WindowSocks.Maintenance.exe", "WindowSocks.Updater.exe", "WindowSocks.Setup.exe"
    };
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    public static bool ProtectedImage(string path) => infrastructure.Contains(Path.GetFileName(path));
    public static async Task<ProcessCloseOutcome[]> CloseAsync(ProcessIdentity[] identities, string ownerSid)
    {
        var outcomes = new List<ProcessCloseOutcome>();
        var opened = new List<(ProcessIdentity Identity, SafeProcessHandle Handle)>();
        try {
            // Pin all handles before terminating parents. Reusing a PID cannot redirect a kill.
            foreach (var identity in identities.Distinct().OrderBy(p => p.CreatedFileTime)) {
                var handle = OpenProcess(Query | Terminate | Synchronize, false, identity.Pid);
                bool retained = false;
                try {
                    if (handle.IsInvalid) {
                        int error = Marshal.GetLastWin32Error();
                        outcomes.Add(new(identity, error == 87 ? "exited" : "failed", error == 87 ? "程序已結束。" : $"無法開啟程序（Windows {error}）。")); continue;
                    }
                    if (!GetProcessTimes(handle, out long birth, out long exit, out _, out _)) { outcomes.Add(new(identity, "failed", "無法確認程序建立時間。")); continue; }
                    if (birth != identity.CreatedFileTime) { outcomes.Add(new(identity, "skipped", "PID 已由其他程序使用，沒有關閉。")); continue; }
                    if (exit != 0) { outcomes.Add(new(identity, "exited", "程序已結束。")); continue; }
                    if (!OpenProcessToken(handle, 8, out var token)) { outcomes.Add(new(identity, "skipped", "無法確認程序擁有者。")); continue; }
                    using (token) using (var owner = new WindowsIdentity(token.DangerousGetHandle())) {
                        if (owner.User?.Value != ownerSid) { outcomes.Add(new(identity, "skipped", "程序不屬於安裝使用者。")); continue; }
                    }
                    var path = new StringBuilder(32768); uint length = 32768;
                    if (!QueryFullProcessImageNameW(handle, 0, path, ref length)) { outcomes.Add(new(identity, "skipped", "無法確認程序路徑。")); continue; }
                    if (identity.Pid == Environment.ProcessId || ProtectedImage(path.ToString())) { outcomes.Add(new(identity, "skipped", "保留代理主程式及背景元件。")); continue; }
                    opened.Add((identity, handle)); retained = true;
                } finally { if (!retained) handle.Dispose(); }
            }
            var waiting = new List<(ProcessIdentity Identity, SafeProcessHandle Handle)>();
            foreach (var process in opened) {
                if (WaitForSingleObject(process.Handle, 0) == 0) outcomes.Add(new(process.Identity, "exited", "程序已結束。"));
                else if (TerminateProcess(process.Handle, 1)) waiting.Add(process);
                else outcomes.Add(new(process.Identity, "failed", $"無法結束程序（Windows {Marshal.GetLastWin32Error()}）。"));
            }
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (waiting.Any(p => WaitForSingleObject(p.Handle, 0) != 0) && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
            foreach (var process in waiting) outcomes.Add(WaitForSingleObject(process.Handle, 0) == 0
                ? new(process.Identity, "closed", "已結束程序。") : new(process.Identity, "failed", "已送出結束要求，但程序尚未退出。"));
            return outcomes.ToArray();
        } finally { foreach (var process in opened) process.Handle.Dispose(); }
    }
}

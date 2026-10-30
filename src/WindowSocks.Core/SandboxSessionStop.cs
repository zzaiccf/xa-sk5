using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowSocks.Core;

// A narrow service-only operation: the verified owner's session runs the protected
// Sandboxie Start.exe with a fixed terminate command. No arbitrary executable or args.
internal static class SandboxSessionStop
{
    public static void Run(uint session, string ownerSid, string box)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(box, "^Xa_[0-9a-f]{24}$")) throw new IOException("沙盒名稱無效。");
        if (!WTSQueryUserToken(session, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "無法取得沙盒擁有者工作階段。");
        using (token) {
            using var owner = new WindowsIdentity(token.DangerousGetHandle());
            if (owner.User?.Value != ownerSid) throw new IOException("工作階段擁有者不符，未結束沙盒。");
            string executable = Path.Combine(SandboxieRuntime.InstallDirectory, "Start.exe");
            var command = new StringBuilder($"\"{executable}\" /box:{box} /terminate");
            if (!CreateEnvironmentBlock(out nint environment, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default", Flags = 1, ShowWindow = 0 };
                if (!CreateProcessAsUserW(token, executable, command, 0, 0, false, 0x08000400, environment, SandboxieRuntime.InstallDirectory, ref startup, out var process))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "無法在沙盒擁有者工作階段執行停止命令。");
                using var processHandle = new SafeWaitHandle(process.Process, true);
                using var threadHandle = new SafeWaitHandle(process.Thread, true);
                uint wait = WaitForSingleObject(processHandle, 3000);
                if (wait != 0) {
                    TerminateProcess(processHandle, 1);
                    throw new IOException("Sandboxie 停止命令逾時，維持代理保護。");
                }
                if (!GetExitCodeProcess(processHandle, out uint code) || code != 0) throw new IOException($"Sandboxie 停止命令失敗（{code}）。");
            } finally { DestroyEnvironmentBlock(environment); }
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder command, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);
}

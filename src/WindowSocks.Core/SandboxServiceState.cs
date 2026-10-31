using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowSocks.Core;

internal static class SandboxServiceState
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out Status status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    public static bool IsRunning()
    {
        nint manager = OpenSCManager(null, null, 1); if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            nint service = OpenService(manager, "SbieSvc", 4); if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error()); return status.State == 4; }
            finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
}

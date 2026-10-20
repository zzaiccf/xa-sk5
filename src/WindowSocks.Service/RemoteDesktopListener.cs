using System.Runtime.InteropServices;
using Microsoft.Win32;
using WindowSocks.Core;

namespace WindowSocks.Service;

internal sealed class RemoteDesktopListener
{
    internal readonly record struct Listener(uint Pid, ulong Created, uint Port);
    private Listener cached;
    private DateTimeOffset refreshed;
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    public Listener Read()
    {
        if (DateTimeOffset.UtcNow - refreshed < TimeSpan.FromSeconds(5)) return cached;
        refreshed = DateTimeOffset.UtcNow; cached = default;
        nint manager = OpenSCManagerW(null, null, 1); if (manager == 0) return cached;
        try {
            nint service = OpenServiceW(manager, "TermService", 4); if (service == 0) return cached;
            try {
                if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _) || status.State != 4 || status.Pid == 0 || status.Pid > int.MaxValue) return cached;
                if (LdPlayer.Identity((int)status.Pid) is not { } identity) return cached;
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
                if (key?.GetValue("PortNumber") is int port && port is > 0 and <= 65535) cached = new(status.Pid, (ulong)identity.CreatedFileTime, (uint)port);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
        return cached;
    }
}

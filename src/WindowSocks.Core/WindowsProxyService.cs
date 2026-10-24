using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WindowSocks.Core;

// Only control this product's registered service. Never grant configuration or binary replacement rights.
public static class WindowsProxyService
{
    public const int OwnerControl = 0x04 | 0x10 | 0x20; // query status, start, stop
    public static bool Installed => new WindowsStartupService().Read() is not null;
    private static nint Open(uint rights, bool missingAllowed = false)
    {
        if (!Installed) { if (missingAllowed) return 0; throw new IOException("尚未安裝背景代理元件。"); }
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            nint service = OpenService(manager, "WindowSocks", rights);
            if (service == 0) { int error = Marshal.GetLastWin32Error(); if (missingAllowed && error == 1060) return 0; throw new Win32Exception(error); }
            return service;
        } finally { CloseServiceHandle(manager); }
    }
    public static uint State()
    {
        nint service = Open(4, true); if (service == 0) return 1;
        try {
            if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return status.State;
        } finally { CloseServiceHandle(service); }
    }
    public static bool Running() => State() != 1;
    public static async Task StartAsync(CancellationToken ct = default)
    {
        if (!Installed) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try {
            while (State() == 3) await Task.Delay(150, deadline.Token);
            if (State() == 4) return;
            nint service = Open(0x10 | 4);
            try { if (!StartService(service, 0, 0) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { CloseServiceHandle(service); }
            while (State() != 4) await Task.Delay(150, deadline.Token);
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { throw new IOException("無法啟動代理元件，請先安裝／修復新版元件。", ex); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("代理元件啟動逾時，請重試。因未確認就緒，目前未啟用新的代理。 "); }
    }
    public static void RequestStop()
    {
        nint service = Open(0x20 | 4, true); if (service == 0) return;
        try { if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        finally { CloseServiceHandle(service); }
    }
    public static byte[] OwnerDescriptor(byte[] descriptor, string ownerSid)
    {
        var security = new RawSecurityDescriptor(descriptor, 0); var sid = new SecurityIdentifier(ownerSid);
        var acl = security.DiscretionaryAcl ?? throw new IOException("背景服務權限描述無效。");
        if (acl.Cast<GenericAce>().OfType<CommonAce>().Any(a => a.AceQualifier == AceQualifier.AccessAllowed && a.AceFlags == AceFlags.None && a.SecurityIdentifier == sid && (a.AccessMask & OwnerControl) == OwnerControl)) return descriptor;
        int at = 0;
        while (at < acl.Count && acl[at] is CommonAce { AceQualifier: AceQualifier.AccessDenied }) at++;
        acl.InsertAce(at, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, OwnerControl, sid, false, null));
        byte[] result = new byte[security.BinaryLength]; security.GetBinaryForm(result, 0); return result;
    }
    public static void GrantOwnerControl(string ownerSid)
    {
        nint service = Open(0x20000 | 0x40000);
        try {
            QueryServiceObjectSecurity(service, 4, null, 0, out uint size);
            if (size is 0 or > 65536) throw new IOException("無法讀取背景服務權限。");
            byte[] before = new byte[size];
            if (!QueryServiceObjectSecurity(service, 4, before, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] after = OwnerDescriptor(before, ownerSid);
            if (!SetServiceObjectSecurity(service, 4, after)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { CloseServiceHandle(service); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint rights);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartService(nint service, int count, nint arguments);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(nint service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceObjectSecurity(nint service, uint information, byte[]? descriptor, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceObjectSecurity(nint service, uint information, byte[] descriptor);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
}

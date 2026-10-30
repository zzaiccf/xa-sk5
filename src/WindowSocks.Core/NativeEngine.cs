using System.Runtime.InteropServices;

namespace WindowSocks.Core;

public static class NativeEngine
{
    private const string Dll = "WindowSocksNative.dll";
    private static readonly Lazy<bool> sandboxGuardSupport = new(() => {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Dll), out var library)) return false;
        try { return NativeLibrary.TryGetExport(library, "Core_SupportsSandboxGuards", out _); } finally { NativeLibrary.Free(library); }
    });
    public static bool SupportsSandboxGuards => Present && sandboxGuardSupport.Value;
    private static readonly Lazy<bool> p0RoutingSafety = new(() => {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Dll), out var library)) return false;
        try { return NativeLibrary.TryGetExport(library, "Core_SupportsP0RoutingSafety", out _); } finally { NativeLibrary.Free(library); }
    });
    public static bool SupportsP0RoutingSafety => Present && p0RoutingSafety.Value;
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_CheckSandboxIdentity();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_SandboxIdentityState();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern void Core_SandboxIdentityDetail(System.Text.StringBuilder buffer, int capacity);
    [StructLayout(LayoutKind.Sequential)] public struct Rule
    { public uint Pid, Proxy; public ulong Created; public uint Blocked, Reserved; }
    [StructLayout(LayoutKind.Sequential)] public struct Traffic
    { public ulong Sent, Received, Dropped; }
    [StructLayout(LayoutKind.Sequential)] public struct GatewayRule
    { public uint Source, Pid, Proxy, Blocked; public ulong Created; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_ReplaceGatewayRules([In] GatewayRule[] rules, uint count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_StartGateway(uint network, uint mask, uint host, uint interfaceIndex);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void Core_StopGateway();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_GatewayRunning();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_GatewayRouteForTest(uint source, out uint proxy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_GatewayTranslationForTest([In, Out] byte[] packet, uint length, uint guest, uint destination, uint host, uint interfaceIndex, int reply, out uint outbound);
    private static readonly Lazy<bool> vmwareSupport = new(() => {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Dll), out var library)) return false;
        try { return NativeLibrary.TryGetExport(library, nameof(Core_StartGateway), out _); } finally { NativeLibrary.Free(library); }
    });
    public static bool SupportsVmwareGateway => Present && vmwareSupport.Value;
    private static readonly Lazy<bool> vmwareLocalSupport = new(() => {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Dll), out var library)) return false;
        try { return NativeLibrary.TryGetExport(library, "Core_GatewayLocalSupported", out _); } finally { NativeLibrary.Free(library); }
    });
    public static bool SupportsVmwareLocalNetwork => SupportsVmwareGateway && vmwareLocalSupport.Value;
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern nuint Core_GatewayOpenLocalForTest(uint destination, ushort port, int udp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint Core_GatewayDestinationForTest(uint host, uint destination, ushort port, int local);
    [StructLayout(LayoutKind.Sequential)] public struct FlowEvent
    {
        public ulong Sequence, Flow, At, Created, Sent, Received;
        public uint Pid, Proxy, Source, Destination, SourcePort, DestinationPort, Protocol, Stage, Error, Reserved;
    }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_SetProxy(uint id, [MarshalAs(UnmanagedType.LPUTF8Str)] string host, ushort port, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, [MarshalAs(UnmanagedType.LPUTF8Str)] string password);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_ReplaceRules([In] Rule[] rules, uint count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int Core_ReplacePolicy([In] Rule[] rules, uint count, string guards);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int Core_ReplacePolicyEx([In] Rule[] rules, uint count, string guards, uint globalProxy, uint globalState, uint bypassLan);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int Core_ReplacePolicyEx2([In] Rule[] rules, uint count, string guards, uint globalProxy, uint globalState, uint bypassLan,
        uint bypassRemoteControl, string remoteImages, uint rdpPid, ulong rdpCreated, uint rdpPort);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint Core_GetDescendants(uint pid, ulong birth, [Out] Rule[] output, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_ProcessHandoffMode(uint pid, ulong birth);
    private static readonly Lazy<bool> handoffSupport = new(() => {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Dll), out var library)) return false;
        try { return NativeLibrary.TryGetExport(library, nameof(Core_ProcessHandoffMode), out _); } finally { NativeLibrary.Free(library); }
    });
    public static bool SupportsProcessHandoffs => Present && handoffSupport.Value;
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_GlobalBypassForTest([In] byte[] packet, uint length);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int Core_SetOwner(string sid);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_RouteForTest(uint pid, ulong birth, int ipv6, out uint proxy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_RoutePacketForTest(uint pid, ulong birth, int ipv6, ushort localPort, out uint proxy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_LocalCommunicationForTest([In] byte[] packet, uint length);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void Core_ClearGuards();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern void Core_AddGuard(string path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern void Core_SetGuards(string paths);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int Core_Start(string path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void Core_Stop();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_ReplaceCapture();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_IsRunning();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint Core_LastError();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void Core_GetTraffic(uint pid, out Traffic traffic);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void Core_GetProcessStats(uint pid, ulong birth, out Traffic traffic, out uint tcp, out uint udp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint Core_GetFlowEvents([Out] FlowEvent[] events, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint Core_RelayForTest(nuint accepted, nuint upstream, uint pid, ulong birth);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int Core_ReadRelayMetrics([Out] ulong[] values, uint capacity);
    public static RelayPerformance? ReadRelayPerformance()
    {
        if (!Present) return null;
        try {
            var values = new ulong[5];
            return Core_ReadRelayMetrics(values, (uint)values.Length) == values.Length ? new(values[0], values[1], values[2], values[3], values[4]) : null;
        } catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException) { return null; }
    }
    public static FlowEvent[] FlowEvents()
    {
        if (!Present) return [];
        var rows = new FlowEvent[1024]; uint count = Core_GetFlowEvents(rows, (uint)rows.Length); return rows.Take(checked((int)count)).ToArray();
    }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_Connect(nuint socket, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, ushort port, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, [MarshalAs(UnmanagedType.LPUTF8Str)] string password);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int Core_Associate(nuint socket, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, out uint ip, out ushort port);
    public static bool Present => File.Exists(Path.Combine(AppContext.BaseDirectory, Dll));
    private static readonly Lazy<bool> remoteControlSupport = new(() => {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Dll), out var library)) return false;
        try { return NativeLibrary.TryGetExport(library, nameof(Core_ReplacePolicyEx2), out _); }
        finally { NativeLibrary.Free(library); }
    });
    public static bool SupportsRemoteControlBypass => Present && remoteControlSupport.Value;
    public static bool DriverPresent => File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert64.sys"));
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowSocks.Core;

public sealed record ProxyProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public DateOnly? ExpiresOn { get; set; }
    public string Endpoint => $"{Host}:{Port}";
    public override string ToString() => $"{Name} ({Endpoint})";
    public ProxyProfile Redacted() => this with { Password = "" };
}
public readonly record struct ProcessIdentity(int Pid, long CreatedFileTime);
public sealed record TargetInstance(string Key, string Name, string Kind, string Path, int Index,
    bool Running, ProcessIdentity[] Processes, string Detail = "")
{
    public string EmulatorProcessPath { get; init; } = "";
    public string EmulatorCommandHash { get; init; } = "";
}
public sealed record InstanceBinding
{
    public string TargetKey { get; set; } = "";
    public Guid ProxyId { get; set; }
    public bool Enabled { get; set; }
    public bool IncludeChildren { get; set; }
    public ProcessIdentity[] Descendants { get; set; } = [];
    public bool SharedProxy { get; set; }
    public bool AutoManaged { get; set; }
    public bool AutoPaused { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public int Index { get; set; } = -1;
    public ProcessIdentity[] Processes { get; set; } = [];
    public string EmulatorProcessPath { get; set; } = "";
    public string EmulatorCommandHash { get; set; } = "";
}
public sealed record ProbeResult(Guid ProxyId, bool Reachable, bool Authenticated, bool Tcp,
    bool Udp, string? TcpExit, string? UdpExit, long LatencyMs, string Message, DateTimeOffset At)
{
    public IpLocation? Location { get; init; }
    public string LocationMessage { get; init; } = "尚未查詢";
    public long? ConnectLatencyMs { get; init; }
    public long? UdpLatencyMs { get; init; }
    public long? TotalDurationMs { get; init; }
    public Guid CheckId { get; init; }
    public bool UdpPending { get; init; }
    public bool UdpChecked { get; init; } = true;
    public string StatusLabel => UdpPending ? "TCP 通過 · UDP 背景檢測中" : Ready ? "TCP／UDP 可用" : Tcp ? "僅 TCP 可用" : !Reachable ? "無法連線" : !Authenticated ? "驗證未通過" : "出口檢測失敗";
    public string LatencyLabel => ConnectLatencyMs is { } connected ? $"{connected} ms" : "—";
    public bool Ready => Tcp && Udp && !UdpPending;
    public string TcpLabel => Tcp ? "通過" : "未通過";
    public string UdpLabel => UdpPending ? "背景檢測中" : !UdpChecked ? "未檢測" : Udp ? "實際收發通過" : "未通過";
    public string ExitLabel => TcpExit is null ? "未驗證" : TcpExit;
}
public sealed record RuntimeStatus(string TargetKey, string State, string Detail, long Sent, long Received)
{
    public uint TcpConnections { get; init; }
    public uint UdpAssociations { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
}
public sealed record Diagnostics(bool ServiceOnline, bool DriverPresent, bool CorePresent, bool EngineRunning,
    string Version, string Detail, string[] Logs);
public sealed record Snapshot(ProxyProfile[] Proxies, InstanceBinding[] Bindings, TargetInstance[] Targets,
    ProbeResult[] Probes, RuntimeStatus[] Statuses, Diagnostics Diagnostics)
{
    public bool SupportsProxyExpiration { get; init; }
    public bool SupportsFixedEmulators { get; init; }
    public bool SupportsSequentialEmulators { get; init; }
    public bool SupportsRemoteControlBypass { get; init; }
    public bool SupportsLowFrequencyProbes { get; init; }
    public bool SupportsTcpFirstProbes { get; init; }
    public bool SupportsProxyEditing { get; init; }
    public bool SupportsVmware { get; init; }
    public bool SupportsVmwareQuickSetup { get; init; }
    public bool SupportsVmwareLocalNetwork { get; init; }
    public bool SupportsRuleManagement { get; init; }
    public bool SupportsProcessHandoffs { get; init; }
    public bool SupportsBulkActions { get; init; }
    public VmwareState? Vmware { get; init; }
    public bool HasPendingEmulatorConfiguration { get; init; }
    public AutoProxyRule AutoProxy { get; init; } = new();
    public string AutoProxyStatus { get; init; } = "尚未啟用自動分配";
    public NetworkFlowEvent[] FlowEvents { get; init; } = [];
    public GlobalProxySettings GlobalProxy { get; init; } = new();
    public string GlobalProxyStatus { get; init; } = "全局代理未啟用";
    // Null for older services; Enabled alone does not confirm that traffic can be routed.
    public bool? GlobalProxyRouting { get; init; }
}
public sealed record SavedConfig
{
    public int SchemaVersion { get; init; } = 1;
    public List<ProxyProfile> Proxies { get; set; } = [];
    public List<InstanceBinding> Bindings { get; set; } = [];
    public List<ProbeResult> LastProbes { get; set; } = [];
    // Dates imported while an older service is running, keyed by its stable proxy IDs.
    public Dictionary<Guid, DateOnly> ServiceProxyExpirations { get; set; } = [];
    public AutoProxyRule AutoProxy { get; set; } = new();
    public AutoProxyRule? PendingEmulatorRule { get; set; }
    public GlobalProxySettings GlobalProxy { get; set; } = new();
    public List<string> AutoExcludedTargets { get; set; } = [];
    public List<ProcessIdentity> AutoExcludedDescendants { get; set; } = [];
    public List<GameLaunchGroup> GameGroups { get; set; } = [];
    public string LdConsole { get; set; } = @"C:\LDPlayer\LDPlayer9\ldconsole.exe";
    public string MuMuConsole { get; set; } = "";
    public VmwareSettings Vmware { get; set; } = new();
    public VmwareRuntime[] VmwareSessions { get; set; } = [];
}
public sealed record Command(string Action, JsonElement Data)
{
    public static Command Create<T>(string action, T data) => new(action, JsonSerializer.SerializeToElement(data, Json.Options));
}
public sealed record Reply(bool Ok, string Error, JsonElement Data)
{
    public static Reply Success<T>(T value) => new(true, "", JsonSerializer.SerializeToElement(value, Json.Options));
    public static Reply Failure(string error) => new(false, error, JsonSerializer.SerializeToElement<object?>(null));
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };
    public static T Read<T>(JsonElement element) => element.Deserialize<T>(Options) ?? throw new InvalidDataException("資料不可為空。");
}
public sealed record BindRequest(string TargetKey, Guid ProxyId);
public sealed record TargetRequest(string TargetKey);
public sealed record ProxyRequest(Guid ProxyId);
public sealed record ImportRequest(string Text);
public sealed record ProcessRequest(int Pid, Guid ProxyId, bool IncludeChildren = false);
public sealed record AssignmentItem(TargetInstance Target, Guid ProxyId, bool SharedProxy = true, bool IncludeChildren = false);
public sealed record BatchBindRequest(AssignmentItem[] Items);
public sealed record GlobalProxySettings
{
    public bool Enabled { get; init; }
    public Guid ProxyId { get; init; }
    public bool BypassLan { get; init; } = true;
    public bool BypassRemoteControl { get; init; } = true;
    public string[] RemoteControlExecutables { get; init; } = [];
    // Pinned by the service before interception starts, preventing recursive DNS when bootstrapping SOCKS.
    public string ResolvedHost { get; init; } = "";
}
public sealed record AutoProxyRule
{
    public bool Configured { get; init; }
    public bool Enabled { get; init; }
    public string Kind { get; init; } = "LDPlayer";
    public TargetMatch Match { get; init; } = TargetMatch.ExecutableName;
    public string Pattern { get; init; } = "";
    public string ClientPath { get; init; } = "";
    public bool IncludeChildren { get; init; }
    public int PerProxy { get; init; } = 1;
    public string ConsolePath { get; init; } = @"C:\LDPlayer\LDPlayer9\ldconsole.exe";
    public string EmulatorKind { get; init; } = "LDPlayer";
    public EmulatorAssignment[] EmulatorAssignments { get; init; } = [];
}
public sealed record EmulatorAssignment(string Kind, string ConsolePath, int Index, string Name, Guid ProxyId)
{
    public string TargetKey => EmulatorCatalog.Key(Kind, ConsolePath, Index);
}
public sealed record IpLocation(string Ip, string Country, string Region, string City, string Isp, string Source)
{
    public string Label => string.Join(" · ", new[] { Country, Region, City }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

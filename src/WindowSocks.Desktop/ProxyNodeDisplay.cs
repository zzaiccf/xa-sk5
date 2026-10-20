using System;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public sealed record ProxyNodeDisplay(ProxyProfile Profile, ProbeResult? Probe = null)
{
    public Guid Id => Profile.Id;
    public string Name => Profile.Name;
    public string ExitIp => string.IsNullOrWhiteSpace(Probe?.TcpExit) ? "未驗證" : Probe.TcpExit;
    public string Account => string.IsNullOrEmpty(Profile.Username) ? "免帳號" : Profile.Username;
    public string AccountPort => $"{Account}／{Profile.Port}";
    public string EntryIp => Profile.Host;
    public string Label => $"實際 IP：{ExitIp} · 連接帳號／連接端口：{AccountPort} · 連入 IP：{EntryIp}";
    public override string ToString() => Label;
}

using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public interface IProxyServiceSession
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
public sealed class DesktopProxySession(Backend backend) : IProxyServiceSession
{
    public Task StartAsync(CancellationToken ct) => WindowsProxyService.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => ServiceExit.StopAsync(backend.RequestServiceStop, WindowsProxyService.Running, ct);
}
internal sealed class PreviewProxySession : IProxyServiceSession
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

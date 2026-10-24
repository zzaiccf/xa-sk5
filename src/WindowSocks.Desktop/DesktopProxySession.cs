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
    public Task StopAsync(CancellationToken ct) => ServiceExit.StopAsync(async token => {
        try { await backend.RequestServiceStop(token); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException) {
            try { WindowsProxyService.RequestStop(); }
            catch (Win32Exception error) { throw new IOException("無法停止代理元件，請先安裝／修復新版元件後重試關閉。", error); }
        }
    }, WindowsProxyService.Running, ct);
}
internal sealed class PreviewProxySession : IProxyServiceSession
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

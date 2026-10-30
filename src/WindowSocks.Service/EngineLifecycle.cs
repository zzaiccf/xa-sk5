using WindowSocks.Core;

namespace WindowSocks.Service;

public interface IEngineLifecycle
{
    bool Present { get; }
    bool DriverPresent { get; }
    bool Compatible { get; }
    bool Healthy { get; }
    uint Error { get; }
    bool Start(string path);
    void Stop();
    bool ReplaceCapture(string path);
}

internal sealed class NativeEngineLifecycle : IEngineLifecycle
{
    public bool Present => NativeEngine.Present;
    public bool DriverPresent => NativeEngine.DriverPresent;
    public bool Compatible => NativeEngine.SupportsP0RoutingSafety;
    public bool Healthy => NativeEngine.Core_IsRunning() != 0;
    public uint Error => NativeEngine.Core_LastError();
    public bool Start(string path) => NativeEngine.Core_Start(path) != 0;
    public void Stop() => NativeEngine.Core_Stop();
    public bool ReplaceCapture(string path) => NativeEngine.Core_ReplaceCapture() != 0;
}

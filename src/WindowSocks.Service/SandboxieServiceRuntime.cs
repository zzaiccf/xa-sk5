using System.Diagnostics;
using System.Text.Json;
using WindowSocks.Core;

namespace WindowSocks.Service;

// Sandboxie reload restarts SCM services which have SbieDll loaded. Keep its official
// APIs in a private child with inherited stdio, so configuration reload cannot stop routing.
internal sealed class SandboxieServiceRuntime : IDisposable
{
    private readonly object gate = new();
    private Process? broker;
    private System.Collections.Concurrent.BlockingCollection<string>? responses;
    private T Call<T>(SandboxBrokerRequest request)
    {
        lock (gate) return CallLocked<T>(request);
    }
    private T CallLocked<T>(SandboxBrokerRequest request)
    {
        try {
            if (broker is null || broker.HasExited) {
                broker?.Dispose();
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WindowSocks.Tools.exe")) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
                };
                start.ArgumentList.Add("sandbox-broker");
                broker = Process.Start(start) ?? throw new IOException("無法啟動沙盒身分查詢程序。");
                var queue = new System.Collections.Concurrent.BlockingCollection<string>(); responses = queue;
                // A late callback from a replaced helper must never feed the next helper's replies.
                broker.OutputDataReceived += (_, e) => { if (e.Data is not null) queue.Add(e.Data); };
                broker.BeginOutputReadLine();
            }
            broker.StandardInput.WriteLine(JsonSerializer.Serialize(request, Json.Options)); broker.StandardInput.Flush();
            if (!responses!.TryTake(out string? line, TimeSpan.FromSeconds(5))) throw new IOException("沙盒身分查詢逾時，維持封鎖。");
            if (line.Length > PipeTransport.Limit) throw new IOException("沙盒身分回覆過大。");
            var response = JsonSerializer.Deserialize<Reply>(line, Json.Options) ?? throw new IOException("沙盒身分回覆無效。");
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            return response.Data.ValueKind == JsonValueKind.Null ? default! : Json.Read<T>(response.Data);
        } catch { Dispose(); throw; }
    }
    public SandboxProcess[] Processes(string box) => Call<SandboxProcess[]>(new("list", box));
    public SandboxProcess? Query(int pid) => Call<SandboxProcess?>(new("query", Pid: pid));
    public void TerminateOwned(SandboxInstanceProfile profile, string ownerSid) => Call<bool>(new("terminate", Profile: profile, OwnerSid: ownerSid));
    public void Dispose()
    {
        lock (gate) DisposeLocked();
    }
    private void DisposeLocked()
    {
        if (broker is null) return;
        try { if (!broker.HasExited) broker.Kill(); } catch (InvalidOperationException) { }
        finally { broker.Dispose(); broker = null; }
    }
}

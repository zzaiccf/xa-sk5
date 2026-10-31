using System.Diagnostics;
using System.Text.Json;
using WindowSocks.Core;

namespace WindowSocks.Service;

// Sandboxie reload restarts SCM services which have SbieDll loaded. Keep its official
// APIs in a private child with inherited stdio, so configuration reload cannot stop routing.
internal sealed class SandboxieServiceRuntime : ISandboxRuntime
{
    private readonly object gate = new();
    private Process? broker;
    private int transportFailures;
    private DateTimeOffset retryAfter;
    private readonly Func<SandboxBrokerRequest,string>? exchange;
    private readonly Action? closed;
    private readonly Func<DateTimeOffset> clock;
    internal SandboxieServiceRuntime(Func<SandboxBrokerRequest,string>? exchange=null,Action? closed=null,Func<DateTimeOffset>? clock=null)
    {this.exchange=exchange;this.closed=closed;this.clock=clock??(()=>DateTimeOffset.UtcNow);}
    private sealed class BrokerRejected(string message) : InvalidOperationException(message);
    private System.Collections.Concurrent.BlockingCollection<string>? responses;
    private T Call<T>(SandboxBrokerRequest request)
    {
        lock (gate) return CallLocked<T>(request);
    }
    private T CallLocked<T>(SandboxBrokerRequest request)
    {
        if(clock()<retryAfter)throw new IOException($"沙盒查詢暫停重試至 {retryAfter:O}（request {request.RequestId}）。");
        try {
            string line;
            if(exchange is not null)line=exchange(request);
            else{
            bool coldStart = broker is null || broker.HasExited;
            if (coldStart) {
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
            broker!.StandardInput.WriteLine(JsonSerializer.Serialize(request, Json.Options)); broker.StandardInput.Flush();
            if (!responses!.TryTake(out line!, ResponseTimeout(request.Action, coldStart))) throw new IOException($"沙盒 {request.Action} 逾時（request {request.RequestId}），維持封鎖。");
            }
            if (line.Length > PipeTransport.Limit) throw new IOException("沙盒身分回覆過大。");
            var envelope = JsonSerializer.Deserialize<SandboxBrokerReply>(line, Json.Options) ?? throw new IOException("沙盒身分回覆無效。");
            if(envelope.RequestId!=request.RequestId)throw new IOException("沙盒回覆識別不符："+request.RequestId);
            transportFailures=0;retryAfter=default;
            var response=envelope.Reply;
            if (!response.Ok) throw new BrokerRejected($"{response.Error}（request {request.RequestId}）");
            return response.Data.ValueKind == JsonValueKind.Null ? default! : Json.Read<T>(response.Data);
        } catch(BrokerRejected){throw;}
        catch { transportFailures=Math.Min(transportFailures+1,7);retryAfter=clock().AddMilliseconds(Math.Min(30000,500*Math.Pow(2,transportFailures-1)));DisposeLocked();throw; }
    }
    public SandboxProcess[] Processes(string box) => Call<SandboxProcess[]>(new("list", box));
    public SandboxProcess? Query(int pid) => Call<SandboxProcess?>(new("query", Pid: pid));
    public void TerminateOwned(SandboxInstanceProfile profile, string ownerSid) => Call<bool>(new("terminate", Profile: profile, OwnerSid: ownerSid));
    internal static TimeSpan ResponseTimeout(string action, bool coldStart) => TimeSpan.FromSeconds(
        action == "terminate" ? 65 : coldStart ? 20 : action == "validate" ? 10 : 5);
    public void ValidateSettings(SandboxInstanceProfile profile)
    {
        if (!Call<bool>(new("validate", Profile: profile))) throw new IOException("沙盒設定未通過核驗，維持封鎖。");
    }
    public void VerifyCompatibility()
    {
        if (!NativeEngine.Present) throw new IOException("缺少沙盒身分核心。");
        if (NativeEngine.Core_CheckSandboxIdentity() != 1) {
            var detail=new System.Text.StringBuilder(256);NativeEngine.Core_SandboxIdentityDetail(detail,detail.Capacity);
            throw new IOException(detail+"；目前支援 Plus 1.18.8／驅動 5.73.8 ABI 0x57230。");
        }
    }
    public bool IdentityAvailable => NativeEngine.Present && NativeEngine.Core_SandboxIdentityState() == 1;
    public void Dispose()
    {
        lock (gate) DisposeLocked();
    }
    private void DisposeLocked()
    {
        closed?.Invoke();
        if (broker is null) return;
        try { if (!broker.HasExited) broker.Kill(); } catch (InvalidOperationException) { }
        finally { broker.Dispose(); broker = null; }
    }
}

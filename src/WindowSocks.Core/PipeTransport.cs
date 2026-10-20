using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace WindowSocks.Core;

public static class PipeTransport
{
    public const string Name = "WindowSocks.v1";
    public const int Limit = 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        if (data.Length > Limit) throw new IOException("訊息超過大小限制。");
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(data, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct); int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > Limit) throw new IOException("無效訊息長度。");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data, Json.Options) ?? throw new IOException("無效訊息。");
    }
    public static async Task<T> CallAsync<T>(Command command, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        await using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, timeout.Token);
        await WriteAsync(pipe, command, timeout.Token); var reply = await ReadAsync<Reply>(pipe, timeout.Token);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error);
        return Json.Read<T>(reply.Data);
    }
    public static NamedPipeServerStream CreateServer(string ownerSid, string name = Name)
    {
        var security = new PipeSecurity();
        foreach (var sid in new[] { ownerSid, "S-1-5-18", "S-1-5-32-544" }.Distinct()) security.AddAccessRule(new(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier("S-1-5-2"), PipeAccessRights.FullControl, AccessControlType.Deny)); // network logons
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
    }
}

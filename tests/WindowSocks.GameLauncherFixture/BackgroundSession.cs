using System.Diagnostics;
using System.Text.Json;

// File-controlled ordinary sockets let the harness test a child AFTER its launcher has exited.
// This fixture never reads proxy credentials or calls the routing engine directly.
internal static class BackgroundSession
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static async Task WriteAsync(string file, object value)
    {
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, file, true);
    }
    public static async Task LaunchAsync(string directory, bool holdLauncher = false)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pid = Environment.ProcessId }, Json));
        if (await Console.In.ReadLineAsync() != "spawn") return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--client"); start.ArgumentList.Add(directory);
        using var child = Process.Start(start) ?? throw new IOException("Test child failed to start.");
        var identity = new { clientPid = child.Id, createdFileTime = child.StartTime.ToUniversalTime().ToFileTimeUtc() };
        // Persist identity before the launcher exits, including if the harness loses its output pipe.
        await WriteAsync(Path.Combine(directory, "child.json"), identity);
        Console.WriteLine(JsonSerializer.Serialize(identity, Json));
        if (holdLauncher) await Console.In.ReadLineAsync();
    }
    public static async Task ClientAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        string commandFile = Path.Combine(directory, "request.json");
        var lifetime = Stopwatch.StartNew(); var completed = new HashSet<Guid>();
        while (lifetime.Elapsed < TimeSpan.FromMinutes(15) && !File.Exists(Path.Combine(directory, "stop"))) {
            if (File.Exists(commandFile)) {
                SessionCommand? command;
                try { command = JsonSerializer.Deserialize<SessionCommand>(await File.ReadAllTextAsync(commandFile), Json); }
                catch (Exception ex) when (ex is IOException or JsonException) { await Task.Delay(100); continue; }
                if (command is not null && command.Id != Guid.Empty && completed.Add(command.Id)) {
                    object result;
                    try {
                        result = command.Action switch {
                            "probe" => await TransparentTrafficProbe.RunAsync(),
                            "tcp" => new { pid = Environment.ProcessId, tcpExit = await TransparentTrafficProbe.TcpExitAsync(6), error = (string?)null },
                            "identity" => new { pid = Environment.ProcessId, createdFileTime = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc() },
                            "passiveTcp" => await PassiveTcpAsync(command.Address, command.Port),
                            _ => throw new InvalidOperationException("Unknown diagnostic action.")
                        };
                    } catch (Exception ex) when (ex is not OutOfMemoryException) {
                        result = new { pid = Environment.ProcessId, tcpExit = (string?)null, error = ex.GetType().Name };
                    }
                    await WriteAsync(Path.Combine(directory, "result-" + command.Id.ToString("N") + ".json"), new { command.Id, command.Action, result, at = DateTimeOffset.Now });
                }
            }
            await Task.Delay(100);
        }
    }
    private static async Task<object> PassiveTcpAsync(string? address, int port)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || port is < 1 or > 65535)
            throw new InvalidOperationException("A diagnostic IPv4 endpoint is required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        var watch = Stopwatch.StartNew(); long? connectedMs = null, firstByteMs = null; int receivedBytes = 0; bool eof = false; string? error = null;
        try {
            await client.ConnectAsync(ip, port, timeout.Token); connectedMs = watch.ElapsedMilliseconds;
            // Read only the initial server banner. Never send game commands or record its content.
            byte[] buffer = new byte[1024];
            receivedBytes = await client.GetStream().ReadAsync(buffer, timeout.Token);
            eof = receivedBytes == 0; if (!eof) firstByteMs = watch.ElapsedMilliseconds;
        } catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { error = ex.GetType().Name; }
        return new { pid = Environment.ProcessId, address, port, connectedMs, firstByteMs, receivedBytes, eof, error, elapsedMs = watch.ElapsedMilliseconds };
    }
    private sealed record SessionCommand(Guid Id, string Action, string? Address = null, int Port = 0);
}

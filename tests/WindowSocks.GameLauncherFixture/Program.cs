using System.Diagnostics;
using System.Text.Json;

// Live diagnostic only: a distinct launcher image creates a real ordinary TCP client, then exits.
Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pid = Environment.ProcessId }));
if (await Console.In.ReadLineAsync() != "spawn" || args.Length != 1) return;
var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe")) { UseShellExecute = false, CreateNoWindow = true };
foreach (string argument in new[] { "--silent", "--show-error", "--fail", "--connect-timeout", "25", "--max-time", "35", "--output", Path.GetFullPath(args[0]), "https://api.ipify.org" }) start.ArgumentList.Add(argument);
using var client = Process.Start(start) ?? throw new IOException("Test client failed to start.");
Console.WriteLine(JsonSerializer.Serialize(new { clientPid = client.Id, createdFileTime = client.StartTime.ToUniversalTime().ToFileTimeUtc() }));

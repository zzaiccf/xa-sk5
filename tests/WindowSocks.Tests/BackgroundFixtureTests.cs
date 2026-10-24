using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace WindowSocks.Tests;

public sealed class BackgroundFixtureTests
{
    [Fact] public async Task FileControlledChildAcceptsStandardGuidCommandsAfterItsLauncherExits()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string fixture = Path.Combine(output.Parent!.Parent!.Parent!.Parent!.FullName,
            "WindowSocks.GameLauncherFixture", "bin", output.Parent.Name, output.Name, "WindowSocks.GameLauncherFixture.exe");
        string directory = Path.Combine(Path.GetTempPath(), "WindowSocksFixtureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); Process? child = null;
        var start = new ProcessStartInfo(fixture) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--session"); start.ArgumentList.Add(directory);
        using var launcher = Process.Start(start)!;
        try {
            using var ready = JsonDocument.Parse((await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            Assert.True(ready.RootElement.GetProperty("ready").GetBoolean());
            await launcher.StandardInput.WriteLineAsync("spawn"); await launcher.StandardInput.FlushAsync();
            using var identity = JsonDocument.Parse((await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            int pid = identity.RootElement.GetProperty("clientPid").GetInt32(); child = Process.GetProcessById(pid);
            long birth = identity.RootElement.GetProperty("createdFileTime").GetInt64();
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, launcher.ExitCode);
            Guid command = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new { id = command.ToString("D"), action = "identity" }));
            string reply = Path.Combine(directory, "result-" + command.ToString("N") + ".json");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(reply)) await Task.Delay(20, deadline.Token);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(reply));
            Assert.Equal(command, result.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(pid, result.RootElement.GetProperty("result").GetProperty("pid").GetInt32());
            Assert.Equal(birth, result.RootElement.GetProperty("result").GetProperty("createdFileTime").GetInt64());
            // A repeated command must not execute twice, and the child shuts down through a local marker.
            DateTime written = File.GetLastWriteTimeUtc(reply); await Task.Delay(250);
            Assert.Equal(written, File.GetLastWriteTimeUtc(reply));
            await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, child.ExitCode);
        } finally {
            await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "");
            if (child is null && File.Exists(Path.Combine(directory, "child.json"))) {
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child.json")));
                int pid = saved.RootElement.GetProperty("clientPid").GetInt32();
                try { child = Process.GetProcessById(pid); if (child.StartTime.ToUniversalTime().ToFileTimeUtc() != saved.RootElement.GetProperty("createdFileTime").GetInt64()) { child.Dispose(); child = null; } }
                catch (ArgumentException) { }
            }
            foreach (var process in new[] { child, launcher }.OfType<Process>()) { if (!process.HasExited) process.Kill(); process.WaitForExit(); }
            child?.Dispose(); Directory.Delete(directory, true);
        }
    }
}

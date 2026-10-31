using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowSocks.Core;

public static class SandboxCommandTimeline
{
    private static readonly AsyncLocal<string?> destination = new();
    public static IDisposable Begin(string directory)
    {
        string? previous = destination.Value;
        destination.Value = Path.Combine(directory, "commands-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".jsonl");
        return new Scope(() => destination.Value = previous);
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    public static void Record(DateTimeOffset started, string executable, string[] arguments, int? exitCode, string stderr)
    {
        if (destination.Value is not { } file) return;
        File.AppendAllText(file, JsonSerializer.Serialize(new { started, ended = DateTimeOffset.UtcNow, executable,
            operation = executable == "SbieIni.exe" ? arguments.FirstOrDefault() : string.Join(" ", arguments.Where(a => a is "/reload" or "/terminate")),
            key = executable == "SbieIni.exe" && arguments.Length > 2 ? arguments[2] : null,
            exitCode, stderr = Summary(stderr, arguments) }) + Environment.NewLine);
    }
    internal static string Summary(string text, string[] arguments)
    {
        foreach (string value in arguments.Skip(1).Where(a => a.Length > 0).OrderByDescending(a => a.Length)) text = text.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)(password|passwd|token|secret)\s*[=:]\s*\S+", "$1=[redacted]");
        return text[..Math.Min(text.Length, 500)].Replace('\r', ' ').Replace('\n', ' ');
    }
}

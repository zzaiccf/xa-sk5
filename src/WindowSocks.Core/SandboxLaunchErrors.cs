using System.Text.RegularExpressions;

namespace WindowSocks.Core;

public sealed record SandboxLaunchDiagnostic(Guid Id, DateTimeOffset At, string Detail, string ErrorFile);
public static class SandboxLaunchErrors
{
    public const string LauncherOpened = "已開啟啟動器，請在啟動器按開始遊戲並登入";
    public static string Clean(string? message, int limit = 16000)
    {
        string text = message ?? "";
        text = Regex.Replace(text, @"(?i)(password|passwd|token|secret)\s*[=:]\s*\S+", "$1=[redacted]");
        text = Regex.Replace(text, @"(?i)(https?|socks5)://[^/\s@]+@", "$1://[redacted]@");
        return text[..Math.Min(limit, text.Length)];
    }
    public static SandboxLaunchReport Report(Guid id, Exception ex, string file) => new(id, false, Clean(ex.Message, 300)) { ErrorDetail = Clean(ex.ToString()), ErrorFile = file };
}

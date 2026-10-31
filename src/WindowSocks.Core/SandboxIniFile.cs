namespace WindowSocks.Core;

// Shared file boundary; no Sandboxie runtime is loaded to read a supplied path.
public static class SandboxIniFile
{
    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    public static string[] Read(string path)
    {
        using var stream = Open(path);
        if (stream.Length > 1024 * 1024) throw new IOException("Sandboxie 設定過大。");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
        return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }
    public static Task<string[]> ReadAsync(string path, CancellationToken ct) => SandboxConfiguration.Retry(async () => {
        var first = Read(path); // Each handle is closed before yielding or calling a tool.
        await Task.Delay(25, ct).ConfigureAwait(false);
        var second = Read(path);
        if (!first.SequenceEqual(second, StringComparer.Ordinal)) throw new SandboxConfigurationPendingException("Sandboxie 設定仍在寫入，稍後重試。");
        return second;
    }, ct);
}

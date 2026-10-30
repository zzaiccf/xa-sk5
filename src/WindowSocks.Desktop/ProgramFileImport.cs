using System;
using System.IO;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public static class ProgramFileImport
{
    public static bool CanImport(string[]? paths) => paths is { Length: 1 } &&
        Path.GetExtension(paths[0]).ToLowerInvariant() is ".exe" or ".bin" or ".lnk";

    public static string Resolve(string[] paths)
    {
        if (paths.Length != 1) throw new InvalidOperationException("請一次拖入一個遊戲捷徑或執行檔。");
        if (!CanImport(paths)) throw new InvalidOperationException("請拖入 .exe／.bin 遊戲本體或 .lnk 桌面捷徑；網址捷徑請改選遊戲本體。");
        string path = ProgramRuleInput.Normalize(paths[0]);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException("找不到拖入的檔案，請確認檔案位置。");
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) {
            try { path = Environment.ExpandEnvironmentVariables(WindowsShortcuts.Target(path)); }
            catch (Exception ex) { throw new InvalidOperationException("無法讀取此捷徑，請改拖入遊戲本體。", ex); }
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("此捷徑沒有可用的執行檔位置，請改拖入遊戲本體。");
        }
        // Resolve only the matching path. Never launch the shortcut or execute its arguments.
        var rule = ProgramRuleInput.Create(path, false, "", 1);
        if (rule.Match != TargetMatch.ExecutablePath || !File.Exists(rule.Pattern))
            throw new InvalidOperationException("捷徑指向的遊戲不存在，請改選目前的遊戲執行檔。");
        return Path.GetFullPath(rule.Pattern);
    }
}

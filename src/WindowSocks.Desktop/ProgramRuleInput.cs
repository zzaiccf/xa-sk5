using System;
using System.IO;
using WindowSocks.Core;

namespace WindowSocks.Desktop;

public static class ProgramRuleInput
{
    public static string Normalize(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1].Trim() : trimmed;
    }
    public static TargetMatch Match(string pattern) => pattern.IndexOfAny(['\\', '/', ':']) >= 0
        ? TargetMatch.ExecutablePath : TargetMatch.ExecutableName;

    public static AutoProxyRule Create(string target, bool specifyClient, string client, int perProxy)
    {
        string pattern = Normalize(target), clientPath = specifyClient ? Normalize(client) : "";
        if (specifyClient && (!Path.IsPathFullyQualified(pattern) || !Path.IsPathFullyQualified(clientPath)))
            throw new InvalidOperationException("指定搭配遊戲時，請選擇登入器與遊戲的完整執行檔路徑；只填檔名時請取消進階指定。");
        var rule = new AutoProxyRule { Enabled = true, Kind = specifyClient ? "GameGroup" : "Process",
            Match = specifyClient ? TargetMatch.ExecutablePath : Match(pattern), Pattern = pattern,
            ClientPath = clientPath, IncludeChildren = !specifyClient, PerProxy = perProxy };
        AutoProxyPlanner.Validate(rule);
        return rule;
    }
}

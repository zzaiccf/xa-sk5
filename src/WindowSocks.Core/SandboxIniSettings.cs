namespace WindowSocks.Core;

// Persistent section values only. SbieIni query also returns GlobalSettings fallbacks,
// which must never be copied into a sandbox during rollback.
public static class SandboxIniSettings
{
    public static Dictionary<string, string[]> Read(IEnumerable<string> lines, string section)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (string raw in lines) {
            string line = raw.Trim();
            if (line.StartsWith('[')) { inside = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inside || line.StartsWith(';') || line.StartsWith('#')) continue;
            int separator = line.IndexOf('='); if (separator < 1) continue;
            string key = line[..separator].Trim();
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(line[(separator + 1)..].Trim());
        }
        return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}

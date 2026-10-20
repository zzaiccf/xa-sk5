using System.Globalization;
using System.Net;
using System.Text;

namespace WindowSocks.Core;

public static class ProxyImport
{
    public static List<ProxyProfile> Parse(string text)
    {
        var result = new List<ProxyProfile>();
        var rows = CsvRows(text.TrimStart('\uFEFF'));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 1 && (string.IsNullOrWhiteSpace(row[0]) || row[0].TrimStart().StartsWith('#'))) continue;
            if (i == 0 && row[0].Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                ProxyProfile p;
                if (row.Count > 1)
                {
                    if (row.Count is < 2 or > 6) throw new FormatException();
                    p = new() { Host = row[0].Trim(), Port = int.Parse(row[1], CultureInfo.InvariantCulture),
                        Username = row.ElementAtOrDefault(2) ?? "", Password = row.ElementAtOrDefault(3) ?? "",
                        Name = row.ElementAtOrDefault(4) ?? "", ExpiresOn = ParseDate(row.ElementAtOrDefault(5)) };
                }
                else if (row[0].Trim().StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(row[0].Trim());
                    if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new FormatException();
                    var auth = uri.UserInfo.Split(':', 2);
                    p = new() { Host = uri.IdnHost, Port = uri.Port,
                        Username = Uri.UnescapeDataString(auth[0]), Password = auth.Length == 2 ? Uri.UnescapeDataString(auth[1]) : "" };
                }
                else if (row[0].Contains('/') && !row[0].Contains("://"))
                {
                    var parts = row[0].Trim().Split('/');
                    if (parts.Length is not (4 or 5)) throw new FormatException();
                    p = new() { Host = parts[0], Port = int.Parse(parts[1], CultureInfo.InvariantCulture), Username = parts[2], Password = parts[3], ExpiresOn = ParseDate(parts.ElementAtOrDefault(4)) };
                }
                else
                {
                    var parts = row[0].Trim().Split(':', 4);
                    if (parts.Length is not (2 or 4)) throw new FormatException();
                    p = new() { Host = parts[0], Port = int.Parse(parts[1], CultureInfo.InvariantCulture),
                        Username = parts.Length == 4 ? parts[2] : "", Password = parts.Length == 4 ? parts[3] : "" };
                }
                Validate(p);
                if (p.Name.Length == 0) p.Name = p.Endpoint;
                var existing = result.FirstOrDefault(x => SameProxy(x, p));
                if (existing is null) result.Add(p);
                else if (p.ExpiresOn.HasValue) existing.ExpiresOn = p.ExpiresOn;
            }
            catch (Exception ex) when (ex is FormatException or UriFormatException or ArgumentException or OverflowException)
            { throw new FormatException($"第 {i + 1} 筆代理格式不正確。請使用 host:port、host:port:user:password、host/port/user/password[/yyyy-MM-dd]、SOCKS5 URI 或 CSV；到期時間須為 yyyy-MM-dd 或留空。原始內容已隱藏。"); }
        }
        if (result.Count == 0) throw new FormatException("沒有可匯入的代理。");
        return result;
    }

    public static void Validate(ProxyProfile p)
    {
        if (p.Port is < 1 or > 65535 || p.Host.Length > 253 || string.IsNullOrWhiteSpace(p.Host) || p.Host.Any(char.IsWhiteSpace) || p.Host.Contains('\0')) throw new FormatException();
        if (Uri.CheckHostName(p.Host) == UriHostNameType.Unknown || IPAddress.TryParse(p.Host, out var ip) && ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new FormatException("第一版代理端點須為 IPv4 或主機名稱。");
        if (Encoding.UTF8.GetByteCount(p.Username) > 255 || Encoding.UTF8.GetByteCount(p.Password) > 255 || p.Username.Contains('\0') || p.Password.Contains('\0') || p.Username.Length == 0 && p.Password.Length != 0) throw new FormatException();
    }

    public static bool SameProxy(ProxyProfile a, ProxyProfile b) => SameEndpointAccount(a, b) && a.Password == b.Password;
    public static bool SameEndpointAccount(ProxyProfile a, ProxyProfile b) => a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port && a.Username == b.Username;
    public static Guid[] ApplyTo(List<ProxyProfile> profiles, IReadOnlyCollection<ProxyProfile> imported)
    {
        var additions = imported.Where(p => !profiles.Any(x => SameProxy(x, p))).ToArray();
        if (profiles.Count + additions.Length > 2000) throw new InvalidOperationException("代理清單上限為 2000 筆。");
        profiles.AddRange(additions);
        foreach (var p in imported.Where(p => p.ExpiresOn.HasValue))
            profiles.First(x => SameProxy(x, p)).ExpiresOn = p.ExpiresOn;
        return profiles.Where(p => imported.Any(i => SameProxy(p, i))).Select(p => p.Id).ToArray();
    }
    public static string Export(IEnumerable<ProxyProfile> proxies) => Csv(proxies, false);
    public static string ExportForServiceTransfer(IEnumerable<ProxyProfile> proxies, bool includeExpiration = true) => Csv(proxies, true, includeExpiration);
    private static string Csv(IEnumerable<ProxyProfile> proxies, bool includePasswords, bool includeExpiration = true) => "host,port,username,password,name" + (includeExpiration ? ",expiresOn" : "") + "\r\n" +
        string.Join("\r\n", proxies.Select(p => string.Join(',', new[] { p.Host, p.Port.ToString(CultureInfo.InvariantCulture), p.Username, includePasswords ? p.Password : "", p.Name, p.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "" }.Take(includeExpiration ? 6 : 5).Select(Quote))));
    private static DateOnly? ParseDate(string? value) => string.IsNullOrWhiteSpace(value) ? null : DateOnly.ParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static List<List<string>> CsvRows(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && c == ',') { row.Add(value.ToString()); value.Clear(); }
            else if (!quoted && c is '\r' or '\n') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(value.ToString()); rows.Add(row); row = []; value.Clear(); }
            else value.Append(c);
        }
        if (quoted) throw new FormatException("CSV 引號未關閉。");
        if (value.Length > 0 || row.Count > 0) { row.Add(value.ToString()); rows.Add(row); }
        return rows;
    }
}

using System.Net;
using System.Text.RegularExpressions;

namespace WindowSocks.Core;

public static class VmwareDhcp
{
    public static string[] AvailableAddresses(string text, string host, IReadOnlyList<string> macs)
    {
        var guests = new List<(string Ip, string Mac)>();
        uint subnet = Number(host) & 0xffffff00;
        foreach (string mac in macs) {
            bool found = false;
            foreach (int suffix in Enumerable.Range(20, 235).Concat(Enumerable.Range(2, 18))) {
                string ip = Address(subnet + (uint)suffix);
                try { _ = Configure(text, host, guests.Append((ip, mac)).ToArray()); }
                catch (InvalidDataException) { continue; }
                guests.Add((ip, mac)); found = true; break;
            }
            if (!found) throw new InvalidOperationException("找不到可用的客體保留位址，請檢查 VMware DHCP 網路。");
        }
        return guests.Select(g => g.Ip).ToArray();
    }
    public static uint Number(string address) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(address).GetAddressBytes());
    public static string Address(uint address) => new IPAddress(new[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address }).ToString();
    public static string Configure(string text, string host, IReadOnlyCollection<(string Ip, string Mac)> guests)
    {
        if (text.Length > 2 * 1024 * 1024 || guests.Count is < 1 or > 10) throw new InvalidDataException("VMware DHCP 設定無效。");
        uint gateway = Number(host), subnet = gateway & 0xffffff00;
        if (!(gateway >> 24 == 10 || gateway >> 20 == 0xac1 || gateway >> 16 == 0xc0a8)) throw new InvalidDataException("專用 VMnet 須使用 IPv4 私有網段。");
        string clean = Regex.Replace(text, @"#[^\r\n]*", match => new string(' ', match.Length));
        var blocks = Regex.Matches(clean, @"\bsubnet\s+(\d+\.\d+\.\d+\.\d+)\s+netmask\s+(\d+\.\d+\.\d+\.\d+)\s*\{")
            .Where(m => Number(m.Groups[1].Value) == subnet && m.Groups[2].Value == "255.255.255.0").ToArray();
        if (blocks.Length != 1) throw new InvalidDataException("找不到唯一的 Host-only DHCP /24 網段，請先在 VMware 虛擬網路編輯器完成設定。");
        int begin = blocks[0].Index + blocks[0].Length, end = begin, depth = 1;
        for (; end < clean.Length && depth > 0; end++) { if (clean[end] == '{') depth++; else if (clean[end] == '}') depth--; }
        if (depth != 0) throw new InvalidDataException("DHCP 網段括號不完整。");
        string body = text[begin..(end - 1)];
        if (Regex.IsMatch(clean, @"\bhost\s+xa_sk5_", RegexOptions.IgnoreCase)) throw new InvalidDataException("已有同名 xa sk5 保留租約，請先回復原網路。");
        if (body.Contains("# xa-sk5 begin", StringComparison.Ordinal)) throw new InvalidDataException("此網段已有 xa sk5 設定，請先回復原網路後再重新準備。");
        var ranges = Regex.Matches(clean[begin..(end - 1)], @"\brange\s+(\d+\.\d+\.\d+\.\d+)\s+(\d+\.\d+\.\d+\.\d+)\s*;").Select(m => (First: Number(m.Groups[1].Value), Last: Number(m.Groups[2].Value))).ToArray();
        var ips = new HashSet<uint>(); var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guest in guests) {
            uint ip = Number(guest.Ip);
            if ((ip & 0xffffff00) != subnet || (ip & 255) is 0 or 255 || ip == gateway || !ips.Add(ip) || !macs.Add(guest.Mac) || !Regex.IsMatch(guest.Mac, @"^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$", RegexOptions.IgnoreCase)) throw new InvalidDataException("客體 IP／MAC 重複或不在專用網段。");
            if (ranges.Any(r => ip >= r.First && ip <= r.Last) || Regex.IsMatch(clean, @"\bfixed-address\s+" + Regex.Escape(guest.Ip) + @"\s*;", RegexOptions.IgnoreCase) || Regex.IsMatch(clean, @"\bhardware\s+ethernet\s+" + Regex.Escape(guest.Mac) + @"\s*;", RegexOptions.IgnoreCase))
                throw new InvalidDataException("客體 IP／MAC 與既有 DHCP 範圍或保留租約衝突；請使用未占用的保留位址。");
        }
        body = Regex.Replace(body, @"(?im)^\s*option\s+(routers|domain-name-servers)\s+[^;]+;", "");
        string additions = "\r\n# xa-sk5 begin\r\noption routers " + host + ";\r\noption domain-name-servers " + host + ";\r\n";
        int index = 0;
        foreach (var guest in guests) additions += $"host xa_sk5_{++index} {{ hardware ethernet {guest.Mac}; fixed-address {guest.Ip}; }}\r\n";
        return text[..begin] + body.TrimEnd() + additions + "# xa-sk5 end\r\n" + text[(end - 1)..];
    }
}

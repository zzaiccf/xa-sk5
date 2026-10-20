using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowSocks.Core;

public sealed record InstallFile(string Path, string Sha256);

public static class ComponentPackage
{
    public const string DriverUrl = "https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip";
    public static readonly string[] DriverFiles = ["WinDivert.dll", "WinDivert64.sys"];
    public const int ArchiveLimit = 4 * 1024 * 1024;

    public static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(p => p == ".."))
            throw new InvalidDataException("安裝套件包含不合法路徑。");
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("套件路徑超出安裝範圍。");
        return result;
    }

    public static InstallFile[] VerifyManifest(string root)
    {
        var file = Path.Combine(root, "install-manifest.json");
        if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("缺少有效的安裝檔案清單，請重新解壓縮完整可攜版。");
        var entries = JsonSerializer.Deserialize<InstallFile[]>(File.ReadAllText(file), Json.Options) ?? throw new InvalidDataException("無效安裝清單。");
        if (entries.Length is < 1 or > 4000 || entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length) throw new InvalidDataException("安裝清單內容重複或超出限制。");
        foreach (var entry in entries) {
            string path = ContainedPath(root, entry.Path);
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安裝檔案損毀或遭修改：" + entry.Path);
        }
        foreach (var required in new[] { "WindowSocks.exe", "WindowSocks.Service.exe", "WindowSocksNative.dll", "WindowSocks.Maintenance.exe" })
            if (!entries.Any(e => e.Path.Equals(required, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("安裝清單缺少 " + required);
        return entries;
    }

    public static bool AllowedDownloadUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public static async Task DownloadDriverAsync(string destination, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowSocks/0.1.1");
        var uri = new Uri(DriverUrl);
        for (int redirect = 0; redirect < 6; redirect++) {
            if (!AllowedDownloadUri(uri)) throw new IOException("下載重新導向到非允許的官方發布主機，已停止。");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { uri = new Uri(uri, location); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > ArchiveLimit) throw new InvalidDataException("驅動下載超過大小限制。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            byte[] buffer = new byte[65536]; int total = 0;
            while (true) { int read = await input.ReadAsync(buffer, ct); if (read == 0) break; total += read; if (total > ArchiveLimit) throw new InvalidDataException("驅動下載超過大小限制。"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            return;
        }
        throw new IOException("官方下載重新導向次數過多。");
    }

    public static void ExtractDriver(Stream stream, string destination)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries) _ = ContainedPath(destination, entry.FullName);
        foreach (var name in DriverFiles) {
            var candidates = archive.Entries.Where(e => e.FullName.Equals("WinDivert-2.2.2-A/x64/" + name, StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("WinDivert-2.2.2/x64/" + name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1 || candidates[0].Length is < 64 or > 16 * 1024 * 1024) throw new InvalidDataException("官方套件缺少唯一的 x64 " + name);
        }
        Directory.CreateDirectory(destination);
        foreach (var name in DriverFiles) {
            var entry = archive.Entries.Single(e => e.FullName.EndsWith("/x64/" + name, StringComparison.OrdinalIgnoreCase));
            string target = Path.Combine(destination, name);
            entry.ExtractToFile(target, overwrite: false); RequireX64(target);
        }
    }

    public static void RequireX64(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("元件不是有效的 Windows 執行檔。");
        stream.Position = 0x3c; int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("元件 PE 標頭不完整。");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("驅動元件不是 x64 版本。");
    }

    public static string DriverError(uint code) => code switch {
        2 or 3 => "找不到驅動檔案，檔案可能尚未下載或已遭隔離。",
        5 => "權限不足；管理員授權未取得，或系統政策拒絕載入。",
        225 or 226 => "Windows 安全性將檔案判定為不允許內容並阻擋／移除。管理員授權不會解除此攔截。",
        577 => "Windows 拒絕驅動數位簽章；請確認官方配對檔案及系統的驅動政策。",
        1275 => "Windows 驅動封鎖政策拒絕此驅動；管理員授權不會解除此限制。",
        1223 => "你已取消 Windows 管理員授權，尚未開始安裝。",
        193 => "驅動 DLL 的架構不正確或檔案損毀。",
        _ => "驅動無法載入，Windows 錯誤 " + code + "。"
    };
}

public static class DriverTrust
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileInfoNative { public uint Size; public nint Path, Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData {
        public uint Size; public nint Policy, Sip; public uint Ui, Revocation, Choice; public nint File; public uint StateAction; public nint State, Url; public uint Flags, Context;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    public static void VerifySignature(string path)
    {
        var info = new FileInfoNative { Size = (uint)Marshal.SizeOf<FileInfoNative>(), Path = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path)) };
        nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>()); Marshal.StructureToPtr(info, memory, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Revocation = 1, Choice = 1, File = memory, StateAction = 1, Flags = 0x80 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try { int result = WinVerifyTrust(-1, ref action, ref data); if (result != 0) throw new InvalidDataException($"驅動數位簽章驗證未通過（0x{result:X8}），沒有執行或載入下載的檔案。"); }
        finally { data.StateAction = 2; _ = WinVerifyTrust(-1, ref action, ref data); Marshal.FreeHGlobal(memory); Marshal.FreeCoTaskMem(info.Path); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)] private delegate nint Open([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool Close(nint handle);
    public static void ProbeInstalled(string dll)
    {
        nint library = LoadLibraryEx(Path.GetFullPath(dll), 0, 0x100 | 0x800);
        if (library == 0) { uint error = (uint)Marshal.GetLastPInvokeError(); throw new IOException(ComponentPackage.DriverError(error)); }
        try {
            nint openPtr = GetProcAddress(library, "WinDivertOpen"), closePtr = GetProcAddress(library, "WinDivertClose");
            if (openPtr == 0 || closePtr == 0) throw new InvalidDataException("DLL 不是相容的 WinDivert 元件。");
            var open = Marshal.GetDelegateForFunctionPointer<Open>(openPtr); var close = Marshal.GetDelegateForFunctionPointer<Close>(closePtr);
            // Installs on demand, but the false filter never captures a user's packet.
            nint handle = open("false", 0, 0, 0x1 | 0x4);
            if (handle == -1) { uint error = (uint)Marshal.GetLastPInvokeError(); throw new IOException(ComponentPackage.DriverError(error)); }
            close(handle);
        } finally { FreeLibrary(library); }
    }
}

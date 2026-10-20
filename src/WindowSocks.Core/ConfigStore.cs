using System.Security.Cryptography;
using System.Text.Json;

namespace WindowSocks.Core;

public sealed class ConfigStore(string directory, DataProtectionScope scope = DataProtectionScope.LocalMachine)
{
    private readonly string file = Path.Combine(directory, "settings.dpapi");
    public byte[] Backup(SavedConfig config) { Save(config); return File.ReadAllBytes(file); }
    public static byte[] WithProxyExpirations(byte[] backup, IReadOnlyDictionary<Guid, DateOnly> dates, DataProtectionScope scope = DataProtectionScope.LocalMachine)
    {
        if (dates.Count == 0) return backup;
        var plain = ProtectedData.Unprotect(backup, null, scope);
        byte[]? updated = null;
        try {
            var config = JsonSerializer.Deserialize<SavedConfig>(plain, Json.Options) ?? throw new InvalidDataException("設定無效。");
            config.Proxies = ProxyExpirationMetadata.Apply(config.Proxies, dates).ToList();
            updated = JsonSerializer.SerializeToUtf8Bytes(config, Json.Options);
            return ProtectedData.Protect(updated, null, scope);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (updated is not null) CryptographicOperations.ZeroMemory(updated); }
    }
    public SavedConfig Load()
    {
        if (!File.Exists(file)) return new();
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, scope);
        try { var result = JsonSerializer.Deserialize<SavedConfig>(plain, Json.Options) ?? throw new InvalidDataException("設定無效。"); if (result.SchemaVersion != 1) throw new InvalidDataException("不支援的設定版本。"); return result; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(SavedConfig config)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(config, Json.Options);
        try
        {
            var cipher = ProtectedData.Protect(plain, null, scope); var temp = file + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fs.Write(cipher); fs.Flush(true); }
            if (File.Exists(file)) File.Replace(temp, file, file + ".bak"); else File.Move(temp, file);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

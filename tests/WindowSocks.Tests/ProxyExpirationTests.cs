using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ProxyExpirationTests
{
    [Fact] public void ImportsVendorDateWithoutChangingCredentials()
    {
        var profile = Assert.Single(ProxyImport.Parse("127.0.0.1/21803/test-account/TEST-SECRET/2027-01-11"));
        Assert.Equal(new DateOnly(2027, 1, 11), profile.ExpiresOn);
        Assert.Equal("TEST-SECRET", profile.Password);
        Assert.Equal("test-account", profile.Username);
        Assert.Equal(21803, profile.Port);
        Assert.Equal(profile.ExpiresOn, profile.Redacted().ExpiresOn);
        Assert.Empty(profile.Redacted().Password);
    }

    [Theory]
    [InlineData("127.0.0.1/1080/user/password")]
    [InlineData("127.0.0.1/1080/user/password/")]
    [InlineData("127.0.0.1/1080/user/password/   ")]
    [InlineData("127.0.0.1:1080")]
    [InlineData("127.0.0.1:1080:user:password")]
    [InlineData("socks5://user:password@127.0.0.1:1080")]
    [InlineData("host,port,username,password,name\n127.0.0.1,1080,user,password,node")]
    [InlineData("host,port,username,password,name,expiresOn\n127.0.0.1,1080,user,password,node,")]
    public void MissingExpirationStaysEmpty(string text) => Assert.Null(Assert.Single(ProxyImport.Parse(text)).ExpiresOn);

    [Theory]
    [InlineData("2027-02-29")]
    [InlineData("2027-13-01")]
    [InlineData("2027-01-32")]
    [InlineData("not-a-date")]
    public void InvalidDatesReportSafeErrors(string date)
    {
        var error = Assert.Throws<FormatException>(() => ProxyImport.Parse($"127.0.0.1/1080/SECRET-ACCOUNT/TEST-SECRET/{date}"));
        Assert.Contains("yyyy-MM-dd", error.Message);
        Assert.DoesNotContain("TEST-SECRET", error.Message);
        Assert.DoesNotContain("SECRET-ACCOUNT", error.Message);
    }

    [Fact] public void RenewalUpdatesExistingIdAndUndatedReimportPreservesKnownDate()
    {
        var profiles = ProxyImport.Parse("proxy.example/1080/user/password/2026-12-31");
        var id = profiles[0].Id;
        Assert.Equal(new[] { id }, ProxyImport.ApplyTo(profiles, ProxyImport.Parse("PROXY.EXAMPLE/1080/user/password/2027-01-11")));
        ProxyImport.ApplyTo(profiles, ProxyImport.Parse("proxy.example:1080:user:password"));
        Assert.Equal(id, Assert.Single(profiles).Id);
        Assert.Equal(new DateOnly(2027, 1, 11), profiles[0].ExpiresOn);
        var batch = ProxyImport.Parse("proxy.example/1080/user/password\nproxy.example/1080/user/password/2027-01-11");
        Assert.Equal(new DateOnly(2027, 1, 11), Assert.Single(batch).ExpiresOn);
    }

    [Fact] public void CsvAndServiceTransferPreserveDatesNamesAndCredentialEscaping()
    {
        var profile = new ProxyProfile { Host = "proxy.example", Port = 1080, Username = "a,b", Password = "SECRET:/@,word", Name = "name,\"one\"", ExpiresOn = new(2027, 1, 11) };
        var exported = ProxyImport.Export([profile]);
        Assert.DoesNotContain(profile.Password, exported);
        var redacted = Assert.Single(ProxyImport.Parse(exported));
        Assert.Empty(redacted.Password); Assert.Equal(profile.Name, redacted.Name); Assert.Equal(profile.ExpiresOn, redacted.ExpiresOn);
        var transfer = Assert.Single(ProxyImport.Parse(ProxyImport.ExportForServiceTransfer([profile])));
        Assert.Equal(profile.Password, transfer.Password); Assert.Equal(profile.ExpiresOn, transfer.ExpiresOn);
        var legacy = Assert.Single(ProxyImport.Parse(ProxyImport.ExportForServiceTransfer([profile], includeExpiration: false)));
        Assert.Equal(profile.Password, legacy.Password); Assert.Equal(profile.Name, legacy.Name); Assert.Null(legacy.ExpiresOn);
    }

    [Fact] public void EncryptedSettingsAndLegacyBackupsRetainDates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        try {
            var store = new ConfigStore(directory, DataProtectionScope.CurrentUser);
            var profile = new ProxyProfile { Host = "127.0.0.1", Port = 1080, Password = "TEST-SECRET", Username = "user" };
            var config = new SavedConfig { Proxies = [profile], ServiceProxyExpirations = { [profile.Id] = new(2027, 1, 11) } };
            var backup = ConfigStore.WithProxyExpirations(store.Backup(config), config.ServiceProxyExpirations, DataProtectionScope.CurrentUser);
            Assert.DoesNotContain("TEST-SECRET", Encoding.UTF8.GetString(backup));
            File.WriteAllBytes(Path.Combine(directory, "settings.dpapi"), backup);
            var restored = store.Load();
            Assert.Equal(new DateOnly(2027, 1, 11), Assert.Single(restored.Proxies).ExpiresOn);
            Assert.Equal(new DateOnly(2027, 1, 11), restored.ServiceProxyExpirations[profile.Id]);
            var old = JsonSerializer.Deserialize<SavedConfig>("{\"schemaVersion\":1,\"proxies\":[{\"host\":\"127.0.0.1\",\"port\":1080}]}", Json.Options)!;
            Assert.Null(Assert.Single(old.Proxies).ExpiresOn); Assert.Empty(old.ServiceProxyExpirations);
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact] public void LegacyMetadataTracksNewCredentialIdAndUsesServiceDateWhenAvailable()
    {
        var old = new ProxyProfile { Host = "proxy.example", Port = 1080, Username = "user" };
        var added = old with { Id = Guid.NewGuid() };
        var imported = old with { ExpiresOn = new(2027, 1, 11), Password = "different" };
        var dates = new Dictionary<Guid, DateOnly>();
        ProxyExpirationMetadata.ValidateLegacyImport([old], [imported]);
        ProxyExpirationMetadata.Remember([old], [old, added], [imported], dates);
        Assert.False(dates.ContainsKey(old.Id)); Assert.Equal(imported.ExpiresOn, dates[added.Id]);
        var visible = ProxyExpirationMetadata.Apply([old, added], dates);
        Assert.Null(visible[0].ExpiresOn); Assert.Equal(imported.ExpiresOn, visible[1].ExpiresOn);
        var serviceDate = new DateOnly(2028, 2, 29);
        Assert.Equal(serviceDate, ProxyExpirationMetadata.Apply([added with { ExpiresOn = serviceDate }], dates)[0].ExpiresOn);
        Assert.Throws<InvalidOperationException>(() => ProxyExpirationMetadata.ValidateLegacyImport([old, added], [imported]));
    }

    [Fact] public async Task ServicePersistsRenewalsAcrossRestartAndExportsDates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
        Guid id;
        try {
            using (var service = new Controller(directory)) {
                Assert.True((await service.Dispatch(Command.Create("import", new ImportRequest("127.0.0.1/1080/user/TEST-SECRET/2026-12-31")), default)).Ok);
                var initial = Json.Read<Snapshot>((await service.Dispatch(Command.Create("snapshot", new { }), default)).Data);
                id = Assert.Single(initial.Proxies).Id; Assert.True(initial.SupportsProxyExpiration);
                Assert.True((await service.Dispatch(Command.Create("import", new ImportRequest("127.0.0.1/1080/user/TEST-SECRET/2027-01-11")), default)).Ok);
                var exported = Json.Read<string>((await service.Dispatch(Command.Create("export", new { }), default)).Data);
                Assert.DoesNotContain("TEST-SECRET", exported);
                Assert.Equal(new DateOnly(2027, 1, 11), Assert.Single(ProxyImport.Parse(exported)).ExpiresOn);
            }
            using var reopened = new Controller(directory);
            var state = Json.Read<Snapshot>((await reopened.Dispatch(Command.Create("snapshot", new { }), default)).Data);
            var profile = Assert.Single(state.Proxies);
            Assert.Equal(id, profile.Id); Assert.Empty(profile.Password); Assert.Equal(new DateOnly(2027, 1, 11), profile.ExpiresOn);
        } finally { Directory.Delete(directory, true); }
    }
}

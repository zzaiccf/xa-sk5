using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ComponentInstallLogTests
{
    [Fact]
    public void FailedAttemptOnlyShowsItsOwnLogAndSettingsCanReopenLastLog()
    {
        var path = Path.GetTempFileName();
        try {
            var started = DateTime.UtcNow;
            File.WriteAllText(path, "previous attempt");
            File.SetLastWriteTimeUtc(path, started.AddMinutes(-1));
            Assert.DoesNotContain("previous attempt", ComponentPackage.ReadInstallLog(path, started));
            Assert.Equal("previous attempt", ComponentPackage.ReadInstallLog(path, null));
            File.WriteAllText(path, "本次錯誤");
            File.SetLastWriteTimeUtc(path, started.AddSeconds(1));
            Assert.Equal("本次錯誤", ComponentPackage.ReadInstallLog(path, started));
        } finally { File.Delete(path); }
        Assert.Contains("尚無", ComponentPackage.ReadInstallLog(path, null));
    }
}

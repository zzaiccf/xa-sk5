using WindowSocks.Core;
using Xunit;

namespace WindowSocks.Tests;

public sealed class SandboxMultiInstanceTests : IDisposable
{
    private readonly string drafts = Path.Combine(Path.GetTempPath(), "WindowSocksTests", Guid.NewGuid().ToString("N"));
    private const string Source = @"C:\SyntheticGame\Launcher.exe";
    [Theory] [InlineData(1)] [InlineData(3)] [InlineData(10)]
    public void RequestedCountHasIndependentStableIdentityAndFixedLines(int count)
    {
        var lines = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var first = SandboxPreparation.Resume(Source, lines, [], drafts);
        var retry = SandboxPreparation.Resume(Source, lines, [], drafts);
        Assert.Equal(count, first.Length); Assert.Equal(first, retry);
        Assert.Equal(count, first.Select(p => p.BoxName).Distinct().Count());
        Assert.Equal(count, first.Select(p => p.WorkingDirectory).Distinct().Count());
        Assert.Equal(lines, first.Select(p => p.ProxyId));
        Assert.Equal(first, SandboxPreparation.Resume(Source, lines, first.Reverse().ToArray(), drafts));
        var compatibility = first.Select(p => SandboxCompatibility.Settings(p with { IsolateDeviceIdentity = true })).ToArray();
        Assert.Equal(count, compatibility.Select(c => c["DiskSerialNumber"][0]).Distinct().Count());
        Assert.Equal(count * 32, compatibility.SelectMany(c => c["NetworkAdapterMAC"]).Distinct().Count());
        Assert.Equal(count, compatibility.Select(c => c["BorderColor"][0]).Distinct().Count());
    }
    [Fact] public void ExpandPairThenResumePartialSaveKeepsAllExistingIdentities()
    {
        var lines = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var pair = SandboxPreparation.Resume(Source, lines[..2], [], drafts);
        var three = SandboxPreparation.Resume(Source, lines[..3], pair.Reverse().ToArray(), drafts);
        Assert.Equal(pair, three[..2]);
        var retry = SandboxPreparation.Resume(Source, lines[..3], [three[0]], drafts);
        Assert.Equal(three, retry);
        var four = SandboxPreparation.Resume(Source, lines, three, drafts);
        Assert.Equal(three, four[..3]);
    }
    [Fact] public void SelectedLineChangesStayWithDisplayedInstanceAndDoNotModifyOtherGame()
    {
        var lines = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var profiles = SandboxPreparation.Resume(Source, lines, [], drafts);
        var replacement = Guid.NewGuid(); lines[2] = replacement;
        var other = SandboxInstances.Create("Other A", @"C:\OtherGame\Launcher.exe", Guid.NewGuid());
        var changed = SandboxPreparation.Resume(Source.ToUpperInvariant(), lines, [other, .. profiles.Reverse()], drafts);
        Assert.Equal(profiles[..2], changed[..2]);
        Assert.Equal(profiles[2] with { ProxyId = replacement }, changed[2]);
        Assert.DoesNotContain(changed, p => p.Id == other.Id);
    }
    [Fact] public void InvalidCountDuplicateOrMissingLinesAndImplicitDeletionAreRejected()
    {
        var lines = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Throws<InvalidOperationException>(() => SandboxPreparation.Resume(Source, [], [], drafts));
        Assert.Throws<InvalidOperationException>(() => SandboxPreparation.Resume(Source, lines, [], drafts));
        Assert.Throws<InvalidOperationException>(() => SandboxPreparation.Resume(Source, [lines[0], lines[0]], [], drafts));
        Assert.Throws<InvalidOperationException>(() => SandboxPreparation.Resume(Source, [Guid.Empty], [], drafts));
        Assert.False(Directory.Exists(drafts));
        var saved = SandboxPreparation.Resume(Source, lines[..3], [], drafts);
        Assert.Throws<InvalidOperationException>(() => SandboxPreparation.Resume(Source, lines[..2], saved, drafts));
        Assert.Equal(saved, SandboxPreparation.Resume(Source, lines[..3], saved, drafts));
        Assert.Throws<InvalidOperationException>(() => SandboxPreparation.ValidateBatch([saved[0], saved[0]]));
        Assert.Throws<InvalidOperationException>(() => SandboxPreparation.ValidateBatch([saved[0], saved[1] with { ProxyId = saved[0].ProxyId }]));
    }
    public void Dispose() { if (Directory.Exists(drafts)) Directory.Delete(drafts, true); }
}

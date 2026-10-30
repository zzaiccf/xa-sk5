using WindowSocks.Core;
using WindowSocks.Desktop;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ProgramRuleInputTests
{
    [Theory]
    [InlineData(" Login.exe ", "Login.exe", TargetMatch.ExecutableName)]
    [InlineData("Client.BIN", "Client.BIN", TargetMatch.ExecutableName)]
    [InlineData("\"C:\\Game Files\\登入器.exe\"", "C:\\Game Files\\登入器.exe", TargetMatch.ExecutablePath)]
    [InlineData("C:/Game/Client.bin", "C:/Game/Client.bin", TargetMatch.ExecutablePath)]
    [InlineData("\\\\server\\games\\Login.exe", "\\\\server\\games\\Login.exe", TargetMatch.ExecutablePath)]
    public void OneInputSelectsExactNameOrPathWithoutLosingHandoff(string input, string pattern, TargetMatch match)
    {
        var rule = ProgramRuleInput.Create(input, false, @"C:\OldGame\Unused.bin", 2);
        Assert.Equal("Process", rule.Kind); Assert.Equal(match, rule.Match); Assert.Equal(pattern, rule.Pattern);
        Assert.True(rule.Enabled); Assert.True(rule.IncludeChildren); Assert.Equal(2, rule.PerProxy); Assert.Empty(rule.ClientPath);
        var target = new TargetInstance("test", "test", "Process", match == TargetMatch.ExecutableName ? @"C:\AnotherFolder\" + pattern : pattern, 0, true, [new(1, 1)]);
        Assert.True(AutoProxyPlanner.Matches(target, rule));
        Assert.False(AutoProxyPlanner.Matches(target with { Path = @"C:\Different\Unrelated.exe" }, rule));
    }
    [Theory]
    [InlineData("")]
    [InlineData("games\\Login.exe")]
    [InlineData("C:Login.exe")]
    [InlineData("*Login.exe")]
    [InlineData("Login.exe\nOther.exe")]
    [InlineData("game.txt")]
    public void InvalidOrRelativePatternsAreNotSilentlyBroadenedToFileNames(string input)
        => Assert.Throws<InvalidOperationException>(() => ProgramRuleInput.Create(input, false, "", 1));

    [Fact] public void OptionalPairPreservesExplicitGameGroupSemanticsAndCapacity()
    {
        var rule = ProgramRuleInput.Create("\"C:\\Game Files\\Login.exe\"", true, "\"C:\\Game Files\\Client.bin\"", 3);
        Assert.Equal("GameGroup", rule.Kind); Assert.Equal(TargetMatch.ExecutablePath, rule.Match);
        Assert.Equal(@"C:\Game Files\Login.exe", rule.Pattern); Assert.Equal(@"C:\Game Files\Client.bin", rule.ClientPath);
        Assert.False(rule.IncludeChildren); Assert.Equal(3, rule.PerProxy);
        var row = Assert.Single(ProxyRules.List(new([], [], [], [], [], new(false, false, false, false, "", "", [])) { AutoProxy = rule }));
        Assert.Equal("遊戲／程式", row.Type); Assert.Contains(rule.ClientPath, row.Name);
    }
    [Theory]
    [InlineData("Login.exe", "C:\\Game\\Client.bin")]
    [InlineData("C:\\Game\\Login.exe", "")]
    [InlineData("C:\\Game\\Login.exe", "Client.bin")]
    [InlineData("C:\\Game\\Login.exe", "C:\\Game\\LOGIN.exe")]
    public void ExplicitPairRequiresTwoDifferentFullExecutablePaths(string launcher, string client)
        => Assert.Throws<InvalidOperationException>(() => ProgramRuleInput.Create(launcher, true, client, 1));
}

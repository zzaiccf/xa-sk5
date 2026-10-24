using WindowSocks.Desktop;
using Xunit;

namespace WindowSocks.Tests;

public sealed class DesktopLaunchTests
{
    [Theory]
    [InlineData(true, DesktopLaunchIntent.Start)]
    [InlineData(false, DesktopLaunchIntent.Activate)]
    public void OrdinaryLaunchStartsOrRestoresTheExistingWindow(bool owns, DesktopLaunchIntent expected)
        => Assert.Equal(expected, DesktopLaunch.Resolve([], owns));

    [Theory]
    [InlineData(false, DesktopLaunchIntent.ExitExisting)]
    [InlineData(true, DesktopLaunchIntent.NothingToExit)]
    public void TaskbarExitDoesNotStartANewProxySession(bool owns, DesktopLaunchIntent expected)
        => Assert.Equal(expected, DesktopLaunch.Resolve([DesktopLaunch.ExitArgument], owns));

    [Fact] public void TaskbarExitIsCaseInsensitiveAndWinsOverAutostart()
        => Assert.Equal(DesktopLaunchIntent.ExitExisting, DesktopLaunch.Resolve(["--autostart", "--EXIT"], false));

    [Fact] public void UpdateResultStillRestoresTheWindow()
        => Assert.Equal(DesktopLaunchIntent.Activate, DesktopLaunch.Resolve(["--update-result", "result.json"], false));
}

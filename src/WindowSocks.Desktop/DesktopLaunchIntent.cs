using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowSocks.Desktop;

public enum DesktopLaunchIntent { Start, Activate, ExitExisting, NothingToExit }

public static class DesktopLaunch
{
    public const string ExitArgument = "--exit";
    public static DesktopLaunchIntent Resolve(IEnumerable<string> arguments, bool ownsInstance)
    {
        bool exit = arguments.Contains(ExitArgument, StringComparer.OrdinalIgnoreCase);
        return exit ? ownsInstance ? DesktopLaunchIntent.NothingToExit : DesktopLaunchIntent.ExitExisting
            : ownsInstance ? DesktopLaunchIntent.Start : DesktopLaunchIntent.Activate;
    }
}

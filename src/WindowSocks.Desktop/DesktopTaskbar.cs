using System.IO;
using System.Windows;
using System.Windows.Shell;

namespace WindowSocks.Desktop;

internal static class DesktopTaskbar
{
    internal static JumpTask ExitTask(string executable) => new() {
        Title = "退出", Description = "停止代理與背景服務，並退出 xa sk5",
        ApplicationPath = executable, Arguments = DesktopLaunch.ExitArgument,
        WorkingDirectory = Path.GetDirectoryName(executable)!, IconResourcePath = executable, IconResourceIndex = 0
    };
    internal static void RegisterExit(string executable)
    {
        var list = JumpList.GetJumpList(Application.Current) ?? new JumpList();
        for (int i = list.JumpItems.Count - 1; i >= 0; i--)
            if (list.JumpItems[i] is JumpTask task && task.Arguments == DesktopLaunch.ExitArgument) list.JumpItems.RemoveAt(i);
        list.JumpItems.Add(ExitTask(executable));
        JumpList.SetJumpList(Application.Current, list); list.Apply();
    }
}

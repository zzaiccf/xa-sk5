using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static void RunProgramFileDrops()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xa-ui-import-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        string launcher = Path.Combine(directory, "遊戲 Login.exe"), client = Path.Combine(directory, "遊戲 Client.bin"), linkPath = Path.Combine(directory, "桌面遊戲.lnk");
        File.WriteAllText(launcher, "inert test fixture"); File.WriteAllText(client, "inert test fixture");
        try {
            object? shell = null, link = null;
            try {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
                link = ((dynamic)shell!).CreateShortcut(linkPath); dynamic value = link;
                value.TargetPath = launcher; value.Arguments = "--not-executed"; value.Save();
            } finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
            var imported = ExerciseProgramEditor(new AutoProxyRule(), editor => {
                var target = (TextBox)editor.FindName("Pattern");
                var over = FileDragEvent(editor, [linkPath], DragDrop.PreviewDragOverEvent); editor.RaiseEvent(over);
                Check(over.Handled && over.Effects == DragDropEffects.Copy, "Shortcut did not advertise a copy drop.");
                DropProgramFiles(editor, editor, [linkPath]);
                Check(((ComboBox)editor.FindName("Mode")).SelectedIndex == 1 && target.Text == launcher, "Shortcut drop failed to switch to game rules and resolve its target.");
                DropProgramFiles(editor, target, [client]); Check(target.Text == client, "Direct game body drop did not replace the target path.");
                DropProgramFiles(editor, target, [launcher]);
                DropProgramFiles(editor, target, [launcher, client]);
                Check(target.Text == launcher && ((TextBlock)editor.FindName("Error")).Text.Length > 0, "Multiple files replaced the existing target.");
                DropProgramFiles(editor, target, [Path.Combine(directory, "missing.exe")]);
                Check(target.Text == launcher && ((TextBlock)editor.FindName("Error")).Text.Length > 0, "Missing file cleared the previous target.");
                ((Expander)editor.FindName("ProgramAdvanced")).IsExpanded = true;
                ((CheckBox)editor.FindName("SpecifyClient")).IsChecked = true;
                var clientBox = (TextBox)editor.FindName("ClientPath"); DropProgramFiles(editor, clientBox, [client]);
                Check(target.Text == launcher && clientBox.Text == client, "Client field drop overwrote the launcher.");
                SaveImage(editor, "unified-rule-imported.png"); SaveProgramEditor(editor);
            });
            Check(imported.Kind == "GameGroup" && imported.Pattern == launcher && imported.ClientPath == client && imported.Match == TargetMatch.ExecutablePath,
                "Imported files were not saved as exact executable paths.");
        } finally { Directory.Delete(directory, true); }
    }
    private static DragEventArgs FileDragEvent(DependencyObject target, string[] paths, RoutedEvent routedEvent)
    {
        var data = new DataObject(DataFormats.FileDrop, paths);
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { data, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point() }, null)!;
        args.RoutedEvent = routedEvent; return args;
    }
    private static void DropProgramFiles(AutoProxyWindow editor, UIElement target, string[] paths)
    {
        target.RaiseEvent(FileDragEvent(target, paths, DragDrop.PreviewDropEvent));
        var save = (Button)editor.FindName("SaveRuleButton"); var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (save.IsEnabled || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(save.IsEnabled, "Import did not finish or release the save button.");
    }
}

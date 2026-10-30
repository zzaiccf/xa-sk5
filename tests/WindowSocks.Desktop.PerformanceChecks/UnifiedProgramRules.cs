using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static void RunUnifiedProgramRules()
    {
        var name = new AutoProxyRule { Kind = "Process", Match = TargetMatch.ExecutableName, Pattern = "Login.exe", IncludeChildren = true, PerProxy = 2, Enabled = true };
        var path = name with { Match = TargetMatch.ExecutablePath, Pattern = @"C:\Synthetic Game\Login.exe" };
        var pair = path with { Kind = "GameGroup", ClientPath = @"C:\Synthetic Game\Client.bin", IncludeChildren = false, PerProxy = 3 };
        foreach (var original in new[] { name, path, pair }) {
            var saved = ExerciseProgramEditor(original, editor => {
                var mode = (ComboBox)editor.FindName("Mode");
                Check(mode.Items.Count == 3 && mode.SelectedIndex == 1, "Old process/name/pair rules did not map to one target type.");
                Check(((CheckBox)editor.FindName("SpecifyClient")).IsChecked == (original.Kind == "GameGroup"), "Legacy explicit client option was lost.");
                Check(((Expander)editor.FindName("ProgramAdvanced")).IsExpanded == (original.Kind == "GameGroup"), "Legacy pair fields were hidden on edit.");
                mode.SelectedIndex = 0; mode.SelectedIndex = 1;
                if (original == name) SaveImage(editor, "unified-rule-simple.png");
                if (original.Kind == "GameGroup") {
                    SaveImage(editor, "unified-rule-pair-normal.png");
                    editor.Width = 760; editor.Height = 650; SaveImage(editor, "unified-rule-pair-minimum.png");
                }
                SaveProgramEditor(editor);
            });
            Check(saved.Kind == original.Kind && saved.Match == original.Match && saved.Pattern == original.Pattern && saved.ClientPath == original.ClientPath
                && saved.IncludeChildren == original.IncludeChildren && saved.PerProxy == original.PerProxy, "Editing changed an existing rule's matching or routing semantics.");
        }
        var changed = ExerciseProgramEditor(pair, editor => {
            ((CheckBox)editor.FindName("SpecifyClient")).IsChecked = false;
            ((TextBox)editor.FindName("Pattern")).Text = "NewLogin.exe";
            Check(((TextBlock)editor.FindName("MatchHint")).Text.Contains("依檔名"), "Filename hint was not updated.");
            SaveImage(editor, "unified-rule-switched.png");
            ((TextBox)editor.FindName("Pattern")).Text = "\"C:\\Synthetic Game\\NewLogin.exe\"";
            Check(((TextBlock)editor.FindName("MatchHint")).Text.Contains("依完整路徑"), "Path hint was not updated.");
            SaveProgramEditor(editor);
        });
        Check(changed is { Kind: "Process", IncludeChildren: true, Match: TargetMatch.ExecutablePath, ClientPath: "" }
            && changed.Pattern == @"C:\Synthetic Game\NewLogin.exe", "Disabling the optional pair leaked its old client or failed path detection.");
        ExerciseProgramEditor(name, editor => {
            ((Expander)editor.FindName("ProgramAdvanced")).IsExpanded = true;
            ((CheckBox)editor.FindName("SpecifyClient")).IsChecked = true;
            SaveProgramEditor(editor);
            Check(editor.IsVisible && editor.Rule is null && ((TextBlock)editor.FindName("Error")).Text.Contains("完整"), "Incomplete pair was accepted or dismissed the dialog.");
            ((TextBox)editor.FindName("Pattern")).Text = path.Pattern;
            ((TextBox)editor.FindName("ClientPath")).Text = pair.ClientPath;
            SaveProgramEditor(editor);
        });
        RunProgramFileDrops();
        File.WriteAllText(Path.Combine(Output, "unified-program-rules.json"), System.Text.Json.JsonSerializer.Serialize(new {
            version = AppReleasePackage.CurrentVersion, passed = true,
            checks = new[] { "one shared target entry", "three legacy rule round trips", "filename and path auto-detection", "explicit pair validation and correction", "disabled pair clears stale client", "mode switching preserves input", "normal and minimum window layouts", "Explorer shortcut and executable drop routing", "advanced client drop preserves launcher", "invalid and multiple drops preserve previous input" },
            scope = "Isolated modal WPF editors using synthetic paths. No service or network configuration changes."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Unified program rule UI checks passed: " + Output);
    }
    private static void SaveProgramEditor(AutoProxyWindow editor) => ((Button)editor.FindName("SaveRuleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static AutoProxyRule ExerciseProgramEditor(AutoProxyRule original, Action<AutoProxyWindow> action, ProxyProfile[]? profiles = null, InstanceBinding[]? bindings = null)
    {
        var editor = new AutoProxyWindow(original, @"C:\SyntheticMissingEmulator\ldconsole.exe", profiles?.Length ?? 2, profiles, bindings: bindings) {
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000
        };
        Exception? failure = null;
        editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(new Action(() => {
            try { action(editor); Check(!editor.IsVisible, "Editor did not accept the final valid input: " + ((TextBlock)editor.FindName("Error")).Text); }
            catch (Exception ex) { failure = ex; editor.Close(); }
        }));
        try {
            bool? accepted = editor.ShowDialog();
            if (failure is not null) throw failure;
            Check(accepted == true && editor.Rule is not null, "Valid rule was not saved by the modal editor.");
            return editor.Rule!;
        } finally { if (editor.IsVisible) editor.Close(); }
    }
}

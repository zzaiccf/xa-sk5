using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowSocks.Core;
using WindowSocks.Desktop;

internal static partial class Program
{
    private static async Task RunLineCapacities()
    {
        var pool = Enumerable.Range(1, 3).Select(n => new ProxyProfile { Name = "測試線路 " + n, Host = "192.0.2." + n, Port = 1080 }).ToArray();
        var rule = new AutoProxyRule { Kind = "Process", Pattern = "Login.exe", IncludeChildren = true, Enabled = true, ConsolePath = @"C:\LegacyEmulator\ldconsole.exe" };
        var saved = ExerciseProgramEditor(rule, editor => {
            var grid = (DataGrid)editor.FindName("CapacityGrid"); var rows = grid.Items.Cast<AutoProxyWindow.CapacityRow>().ToArray();
            Check(rows.Length == 3 && rows.All(r => r.CountText == "1"), "New lines did not default to one automatic slot.");
            Check(((FrameworkElement)editor.FindName("FixedLimitPanel")).Visibility == Visibility.Collapsed, "Automatic allocation still requires a per-window setup.");
            var mode = (ComboBox)editor.FindName("Mode"); mode.SelectedIndex = 0;
            Check(((ComboBox)editor.FindName("AllocationMode")).SelectedIndex == 0 && ((FrameworkElement)editor.FindName("FixedMappingPanel")).Visibility == Visibility.Collapsed,
                "Emulator allocation did not default to automatic assignment.");
            mode.SelectedIndex = 1; editor.UpdateLayout(); grid.ScrollIntoView(rows[0]); grid.UpdateLayout();
            var input = CapacityVisuals<TextBox>(grid).Single(t => ReferenceEquals(t.DataContext, rows[0]));
            input.Text = "4"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Check(rows[0].CountText == "4", "Editing a row did not update its line capacity.");
            rows[1].Selected = true; rows[2].Selected = true;
            ((TextBox)editor.FindName("BatchCapacity")).Text = "2";
            ((Button)editor.FindName("ApplyCapacityButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rows[0].CountText == "4" && rows[1].CountText == "2" && rows[2].CountText == "2", "Batch update changed an unchecked line.");
            ((TextBox)editor.FindName("BatchCapacity")).Text = "11";
            ((Button)editor.FindName("ApplyCapacityButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rows[1].CountText == "2" && ((TextBlock)editor.FindName("Error")).Text.Length > 0, "Invalid batch partially changed rows.");
            rows[2].CountText = "bad"; SaveProgramEditor(editor);
            Check(editor.IsVisible && editor.Rule is null, "An invalid row was saved.");
            rows[2].CountText = "0";
            ((TextBlock)editor.FindName("Error")).Text = "";
            ((TextBox)editor.FindName("BatchCapacity")).Text = "2";
            Check(grid.Columns[1].ActualWidth > 400, "Line label did not use the available table width.");
            SaveImage(editor, "line-capacities-normal.png");
            editor.Width = 760; editor.Height = 650; SaveImage(editor, "line-capacities-minimum.png");
            SaveProgramEditor(editor);
        }, pool);
        Check(saved.ProxyCapacities[pool[0].Id] == 4 && saved.ProxyCapacities[pool[1].Id] == 2 && saved.ProxyCapacities[pool[2].Id] == 0, "Saved capacities did not match the edited lines.");
        Check(saved.ConsolePath == rule.ConsolePath, "Changing capacities altered legacy rule scope metadata.");
        var roundtrip = ExerciseProgramEditor(saved, editor => {
            var rows = ((DataGrid)editor.FindName("CapacityGrid")).Items.Cast<AutoProxyWindow.CapacityRow>().ToArray();
            Check(rows.Select(r => r.CountText).SequenceEqual(new[] { "4", "2", "0" }), "Reopening lost per-line quantities.");
            SaveProgramEditor(editor);
        }, pool);
        Check(roundtrip.ProxyCapacities.Count == 3, "Round trip changed custom line counts.");
        var fixture = new Fixture(); fixture.State = fixture.State with { Proxies = pool };
        var backend = new FakeBackend(fixture); await backend.Snapshot([]);
        bool rejected = false;
        try { await backend.ConfigureAutomatic(saved); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("0.1.26"); }
        Check(rejected && fixture.Saves == 0, "An older service silently accepted unsupported line capacities.");
        fixture.State = fixture.State with { SupportsProxyCapacities = true };
        Check(await backend.ConfigureAutomatic(saved) && fixture.Saves == 1 && fixture.State.AutoProxy.ProxyCapacities[pool[2].Id] == 0, "Supported service did not receive custom capacities.");
        File.WriteAllText(Path.Combine(Output, "line-capacities.json"), System.Text.Json.JsonSerializer.Serialize(new {
            version = AppReleasePackage.CurrentVersion, passed = true,
            checks = new[] { "default automatic assignment", "single row edit through WPF binding", "selected-only batch edit", "invalid batch and row validation", "zero stops additions", "reopen preserves counts", "legacy capability rejection", "supported backend receives capacities" },
            scope = "Synthetic modal editors and transport; no live service changes."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Line capacity UI/backend checks passed: " + Output);
    }
    private static IEnumerable<T> CapacityVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match;
            foreach (var nested in CapacityVisuals<T>(child)) yield return nested;
        }
    }
}

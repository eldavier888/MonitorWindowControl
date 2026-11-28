using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MonitorWindowControl;

/// <summary>
/// "Split into areas" (Monitors page): one monitor, or a big ultrawide, divided into 2 to 4 areas that work like
/// separate monitors. The dialog has the ways to split it, a drawing of the areas, and their names. The General page
/// suggests it when only one monitor is connected.
/// </summary>
public partial class MainWindow
{
    /// <summary>Shows or hides the split cards: the suggestion on General, and "Maximize fills the area" on Monitors.</summary>
    private void UpdateSplitCards(bool launchOnly)
    {
        bool split = _engine.Monitors.Any(m => m.IsArea);
        var physical = _engine.PhysicalMonitors;
        SplitSuggestCard.Visibility = physical.Count == 1 && !split && !physical[0].IsDuplicated ? Visibility.Visible : Visibility.Collapsed;
        MaximizeFillsCard.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        MaximizeFillsToggle.IsChecked = _engine.Settings.MaximizeFillsArea;
        MaximizeFillsToggle.IsEnabled = !launchOnly;
        MaximizeFillsHint.Text = launchOnly
            ? "Needs something running in the background to notice a window being maximized."
            : "When you maximize a window on a split monitor, it fills its area instead of the whole monitor (it isn't left maximized). " +
              "Turn off to let maximized windows cover the whole monitor.";
    }

    private void MaximizeFillsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = MaximizeFillsToggle.IsChecked == true;
        _engine.SetMaximizeFillsArea(on);
        Announcer.Announce(this, on ? "Maximized windows fill their area" : "Maximized windows fill the whole monitor");
    }

    private async void SplitMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonitorInfo m) await ShowSplitDialogAsync(m);
    }

    /// <summary>General page, one monitor: go to the Monitors page and open its "Split into areas".</summary>
    private async void SplitSuggest_Click(object sender, RoutedEventArgs e)
    {
        var page = NavList.Items.OfType<Wpf.Ui.Controls.ListViewItem>().FirstOrDefault(i => (i.Tag as string) == "Monitors");
        if (page != null) NavList.SelectedItem = page;
        var m = _engine.Monitors.FirstOrDefault();
        if (m != null) await ShowSplitDialogAsync(m);
    }

    /// <summary>The dialog: how to split the monitor, a live drawing of the areas, and their names.</summary>
    private async System.Threading.Tasks.Task ShowSplitDialogAsync(MonitorInfo m)
    {
        var physical = _engine.PhysicalOf(m);
        if (physical.IsDuplicated) return;
        var saved = _engine.SplitFor(m);
        string current = saved?.Preset ?? SplitPresets.None;

        TextBlock Text(string text, double top = 0, bool secondary = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 6) };
            if (secondary)
            {
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                t.FontSize = 12 * ThemeSupport.TextScale;
            }
            return t;
        }

        var presetBox = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(presetBox, "How to split it");
        foreach (var p in SplitPresets.All)
            presetBox.Items.Add(new ComboBoxItem { Content = SplitPresets.Describe(p), Tag = p });
        presetBox.SelectedIndex = Math.Max(0, Array.IndexOf(SplitPresets.All, current));

        // The drawing: the monitor's shape, with one box per area.
        var preview = new Grid();
        var previewFrame = new Border
        {
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(4),
            Height = 140, Child = preview, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        };
        var b = physical.Bounds;
        previewFrame.Width = Math.Clamp(140.0 * Math.Max(1, b.Width) / Math.Max(1, b.Height), 160, 440);
        previewFrame.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
        previewFrame.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");

        var namesPanel = new StackPanel();
        var nameBoxes = new List<Wpf.Ui.Controls.TextBox>();
        string? shownPreset = null;

        string Preset() => (presetBox.SelectedItem as ComboBoxItem)?.Tag as string ?? SplitPresets.None;

        void DrawPreview()
        {
            string preset = Preset();
            preview.Children.Clear();
            preview.RowDefinitions.Clear();
            preview.ColumnDefinitions.Clear();
            var ratios = SplitPresets.DefaultRatios(preset) ?? new[] { 1.0 };
            bool stacked = SplitPresets.IsStacked(preset);
            var parts = new List<string>();
            for (int i = 0; i < ratios.Length; i++)
            {
                if (stacked) preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratios[i], GridUnitType.Star) });
                else preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratios[i], GridUnitType.Star) });
                string name = ratios.Length == 1 ? physical.FriendlyName
                    : i < nameBoxes.Count && !string.IsNullOrWhiteSpace(nameBoxes[i].Text) ? nameBoxes[i].Text.Trim()
                    : SplitPresets.DefaultNames(preset)[i];
                var label = new TextBlock
                {
                    Text = ratios.Length == 1 ? name : $"{i + 1} · {name}", TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4),
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, ratios.Length == 1 ? "TextFillColorPrimaryBrush" : "TextOnAccentFillColorPrimaryBrush");
                var cell = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(2), Child = label };
                cell.SetResourceReference(Border.BackgroundProperty, ratios.Length == 1 ? "ControlFillColorDefaultBrush" : "AccentFillColorDefaultBrush");
                if (stacked) Grid.SetRow(cell, i); else Grid.SetColumn(cell, i);
                preview.Children.Add(cell);
                parts.Add(ratios.Length == 1 ? name : $"{name}, {Math.Round(ratios[i] * 100)} %");
            }
            AutomationProperties.SetName(previewFrame, ratios.Length == 1
                ? $"Preview: {physical.FriendlyName} is not split"
                : $"Preview, {(stacked ? "top to bottom" : "left to right")}: " + string.Join("; ", parts));
        }

        void FillNames()
        {
            string preset = Preset();
            if (preset == shownPreset) return;
            shownPreset = preset;
            namesPanel.Children.Clear();
            nameBoxes.Clear();
            int n = SplitPresets.AreaCount(preset);
            if (n < 2) { DrawPreview(); return; }
            var defaults = SplitPresets.DefaultNames(preset);
            var keep = saved != null && saved.Preset == preset ? SplitPresets.Names(saved) : defaults;
            namesPanel.Children.Add(Text("Area names", 14));
            for (int i = 0; i < n; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var label = new TextBlock { Text = $"Area {i + 1}", Width = 70, VerticalAlignment = VerticalAlignment.Center };
                var box = new Wpf.Ui.Controls.TextBox { Text = keep[i], PlaceholderText = defaults[i], MinWidth = 240, MaxLength = 40 };
                AutomationProperties.SetName(box, $"Name of area {i + 1}");
                AutomationProperties.SetLabeledBy(box, label);
                box.TextChanged += (_, _) => DrawPreview();
                row.Children.Add(label);
                row.Children.Add(box);
                namesPanel.Children.Add(row);
                nameBoxes.Add(box);
            }
            DrawPreview();
        }

        presetBox.SelectionChanged += (_, _) => FillNames();

        var content = new StackPanel { MinWidth = 380 };
        content.Children.Add(Text("How to split it"));
        content.Children.Add(presetBox);
        content.Children.Add(Text("Preview", 14));
        content.Children.Add(previewFrame);
        content.Children.Add(namesPanel);
        content.Children.Add(Text("Each area works like a separate monitor here: it has its own number and name, apps open in their area " +
                                  "sized to fill it, and they're learned there. " + SplitPresets.HonestNote, 14, secondary: true));
        FillNames();
        DrawPreview();

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Split into areas: {physical.FriendlyName}", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
        };
        box.Loaded += (_, _) => presetBox.Focus();
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        string chosen = Preset();
        _engine.SetMonitorSplit(physical, chosen, nameBoxes.Select(x => x.Text).ToList());
        Announcer.Announce(this, chosen == SplitPresets.None
            ? $"{physical.FriendlyName} is one monitor again"
            : $"{physical.FriendlyName} is split into {SplitPresets.Describe(chosen).ToLowerInvariant()}");
    }
}

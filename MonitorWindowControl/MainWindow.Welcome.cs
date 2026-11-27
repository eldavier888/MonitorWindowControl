using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace MonitorWindowControl;

/// <summary>
/// The first-run welcome, shown inside the window the first time the app starts (no settings yet): your monitors,
/// apps that belong on a specific monitor, and how it runs. Every step can be skipped; Next saves that step's
/// choices with the same engine calls as the pages, so nothing here works differently from the rest of the app.
/// </summary>
public partial class MainWindow
{
    private const string LearnLabel = "Learn it by itself";
    private int _welcomeStep;
    private readonly List<WelcomeMonitorRow> _welcomeMonitors = new();
    private readonly List<WelcomeAppRow> _welcomeApps = new();
    private readonly List<(MonitorInfo Monitor, CheckBox Box)> _welcomeOnlyChosen = new();

    private sealed record WelcomeMonitorRow(string Device, Wpf.Ui.Controls.TextBox NameBox, ComboBox NumberBox);
    private sealed record WelcomeAppRow(string Key, string Name, ComboBox Choice, object Initial);

    /// <summary>Shows the welcome over the pages, on its first step.</summary>
    public void ShowWelcome()
    {
        WelcomeOverlay.Visibility = Visibility.Visible;
        MainArea.IsEnabled = false; // (hidden behind it: keep Tab inside the welcome)
        ShowWelcomeStep(1);
    }

    private void ShowWelcomeStep(int step)
    {
        _welcomeStep = step;
        WelcomeStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeStepText.Text = $"Welcome · step {step} of 3";
        switch (step)
        {
            case 1:
                WelcomeTitle.Text = "These are your monitors";
                WelcomeIntro.Text = "Give them your own numbers and names if you like, for example \"3 · TV\". You'll see them everywhere in " +
                                    "Monitor Window Control. Windows keeps its own display numbers.";
                FillWelcomeMonitors();
                break;
            case 2:
                WelcomeTitle.Text = "Anything that belongs on a specific monitor?";
                WelcomeIntro.Text = "You don't have to choose anything: each app is learned after you use it for a few seconds on a monitor. " +
                                    "Choose a monitor only for apps that should always open there. Their open windows move there right away.";
                FillWelcomeApps();
                break;
            default:
                WelcomeTitle.Text = "How should it run?";
                WelcomeIntro.Text = "Choose what keeps running while you work. All three use about 0% of the processor.";
                FillWelcomeRunMode();
                break;
        }
        WelcomeBackButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeNextButton.Content = step == 3 ? "Finish" : "Next";
        AutomationProperties.SetName(WelcomeNextButton, step == 3 ? "Finish" : "Next");
        AutomationProperties.SetName(WelcomeOverlay, $"Welcome, step {step} of 3: {WelcomeTitle.Text}");
        WelcomeScroller.ScrollToTop();
        Dispatcher.BeginInvoke(() =>
        {
            var panel = step == 1 ? WelcomeStep1 : step == 2 ? WelcomeStep2 : WelcomeStep3;
            if (!panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) WelcomeNextButton.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A card with an icon, a title and description, and a control on the right (like the pages' cards).</summary>
    private CardControl WelcomeCard(SymbolRegular icon, string title, string description, UIElement control)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        header.Children.Add(new System.Windows.Controls.TextBlock { Text = title, Style = (Style)FindResource("CardTitle") });
        if (description.Length > 0)
            header.Children.Add(new System.Windows.Controls.TextBlock { Text = description, Style = (Style)FindResource("Secondary") });
        return new CardControl { Icon = new SymbolIcon { Symbol = icon }, Header = header, Content = control };
    }

    // ------------------------------------------------------------------ Step 1: your monitors

    private void FillWelcomeMonitors()
    {
        var monitors = _engine.Monitors.ToList();
        WelcomeLayout.SetMonitors(monitors);
        WelcomeMonitorRows.Children.Clear();
        _welcomeMonitors.Clear();
        // No numbers yet: suggest 1, 2, 3… from left to right (top to bottom when stacked).
        bool suggest = monitors.Count > 1 && monitors.All(m => m.Number == null);
        var order = monitors.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).Select(m => m.DeviceName).ToList();
        foreach (var m in monitors)
        {
            var numbers = new List<object> { "No number" };
            numbers.AddRange(Enumerable.Range(1, 9).Cast<object>());
            object number = m.Number is int n ? n : suggest ? order.IndexOf(m.DeviceName) + 1 : "No number";
            var numberBox = new ComboBox { ItemsSource = numbers, SelectedItem = number, Width = 130, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(numberBox, $"Number for {m.FriendlyName}");
            numberBox.PreviewMouseWheel += WelcomeCombo_PreviewMouseWheel;
            var nameBox = new Wpf.Ui.Controls.TextBox { Text = m.Nickname ?? "", PlaceholderText = m.FriendlyName, Width = 220 };
            AutomationProperties.SetName(nameBox, $"Name for {m.FriendlyName}");
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(numberBox);
            controls.Children.Add(nameBox);
            string title = m.FriendlyName + (m.IsPrimary ? " (main)" : "");
            WelcomeMonitorRows.Children.Add(WelcomeCard(SymbolRegular.Desktop24, title,
                "Leave the name empty to use the model name.", controls));
            _welcomeMonitors.Add(new WelcomeMonitorRow(m.DeviceName, nameBox, numberBox));
        }
    }

    /// <summary>Saves the numbers and names that changed. False (and says why) when two monitors got the same number.</summary>
    private bool ApplyWelcomeMonitors()
    {
        var chosen = _welcomeMonitors.Select(r => (r, Number: r.NumberBox.SelectedItem as int?)).ToList();
        var twice = chosen.Where(c => c.Number != null).GroupBy(c => c.Number).FirstOrDefault(g => g.Count() > 1);
        if (twice != null)
        {
            _ = new Wpf.Ui.Controls.MessageBox
            {
                Title = $"Two monitors have number {twice.Key}",
                Content = "Give each monitor its own number, or choose No number.", CloseButtonText = "OK",
            }.ShowDialogAsync();
            return false;
        }
        // Clear numbers that move first, so giving a number to one monitor never takes it from another one here.
        foreach (var (r, number) in chosen.OrderBy(c => c.Number == null ? 0 : 1))
        {
            var m = _engine.Monitors.FirstOrDefault(x => x.DeviceName == r.Device);
            if (m == null) continue; // (disconnected meanwhile)
            string name = r.NameBox.Text?.Trim() ?? "";
            if (name == (m.Nickname ?? "") && number == m.Number) continue;
            _engine.RenameMonitor(m, name, number);
        }
        return true;
    }

    // ------------------------------------------------------------------ Step 2: apps that belong on a monitor

    private void FillWelcomeApps()
    {
        var monitors = _engine.Monitors.ToList();
        WelcomeAppRows.Children.Clear();
        _welcomeApps.Clear();
        foreach (var app in _engine.GetOpenApps().OrderBy(a => a.App.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var rule = _engine.Settings.Find(app.App.Key);
            var items = new List<object> { LearnLabel };
            items.AddRange(monitors);
            items.Add(NeverMoveLabel);
            object initial = rule == null || !(rule.Pinned || rule.NeverMove) ? LearnLabel
                : rule.NeverMove ? NeverMoveLabel
                : (object?)(rule.FollowLastUsed || rule.FollowMouse ? null : _engine.Resolve(rule)) ?? LearnLabel;
            var combo = new ComboBox { ItemsSource = items, SelectedItem = initial, MinWidth = 220 };
            var (name, _) = Names(app.App.Key, app.App.DisplayName);
            AutomationProperties.SetName(combo, $"Where {name} opens");
            combo.PreviewMouseWheel += WelcomeCombo_PreviewMouseWheel;
            string where = app.Monitor != null ? $"Open now on {app.Monitor.Name}" : "Open now";
            WelcomeAppRows.Children.Add(WelcomeCard(SymbolRegular.WindowApps24, name, where, combo));
            _welcomeApps.Add(new WelcomeAppRow(app.App.Key, name, combo, initial));
        }
        WelcomeNoApps.Visibility = _welcomeApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // A TV or other monitor kept for a few chosen apps.
        WelcomeOnlyChosenRows.Children.Clear();
        _welcomeOnlyChosen.Clear();
        var choices = monitors.Where(m => m.ShowMonitorChoices).ToList();
        WelcomeOnlyChosenSection.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var m in choices)
        {
            var box = new CheckBox { Content = "Only apps I choose", IsChecked = m.OnlyChosenApps };
            AutomationProperties.SetName(box, $"Only apps I choose can open on {m.Name}");
            WelcomeOnlyChosenRows.Children.Add(WelcomeCard(SymbolRegular.LockClosed24, m.Label,
                "For a TV, for example: only the apps you set to open there do, and nothing else is learned there.", box));
            _welcomeOnlyChosen.Add((m, box));
        }
    }

    /// <summary>Saves the apps whose choice changed (their open windows move, as on the Apps page) and the kept monitors.</summary>
    private void ApplyWelcomeApps()
    {
        foreach (var (m, box) in _welcomeOnlyChosen)
        {
            var now = _engine.Monitors.FirstOrDefault(x => x.DeviceName == m.DeviceName);
            if (now != null && now.OnlyChosenApps != (box.IsChecked == true)) _engine.SetMonitorOnlyChosenApps(now, box.IsChecked == true);
        }
        foreach (var row in _welcomeApps)
        {
            var choice = row.Choice.SelectedItem;
            if (Equals(choice, row.Initial)) continue;
            switch (choice)
            {
                case MonitorInfo m when _engine.Monitors.FirstOrDefault(x => x.DeviceName == m.DeviceName) is MonitorInfo now:
                    _engine.Pin(row.Key, now, row.Name); break;
                case string s when s == NeverMoveLabel: _engine.Pin(row.Key, null, row.Name); break;
                case string s when s == LearnLabel: _engine.Forget(row.Key); break; // (it had a monitor set before)
            }
        }
        Activate(); // moving windows can bring one to the front
    }

    // ------------------------------------------------------------------ Step 3: how it runs

    private void FillWelcomeRunMode()
    {
        var s = _engine.Settings;
        WelcomeModeTray.IsChecked = s.RunMode == RunModes.Tray;
        WelcomeModeHelper.IsChecked = s.RunMode == RunModes.Helper;
        WelcomeModeLaunchOnly.IsChecked = s.RunMode == RunModes.LaunchOnly;
        WelcomeStartupToggle.IsChecked = s.StartWithWindows == true;
        WelcomeModeHelper.IsEnabled = HelperManager.IsAvailable || s.RunMode == RunModes.Helper;
        if (_engine.Policy.RunMode != null) // set by your organization
            foreach (var r in new[] { WelcomeModeTray, WelcomeModeHelper, WelcomeModeLaunchOnly }) r.IsEnabled = r.IsChecked == true;
        foreach (var r in new[] { WelcomeModeTray, WelcomeModeHelper, WelcomeModeLaunchOnly })
        {
            r.Checked -= WelcomeMode_Checked;
            r.Checked += WelcomeMode_Checked;
        }
        WelcomeStartupToggle.IsEnabled = s.RunMode != RunModes.LaunchOnly;
    }

    private void WelcomeMode_Checked(object sender, RoutedEventArgs e) =>
        WelcomeStartupToggle.IsEnabled = WelcomeModeLaunchOnly.IsChecked != true;

    private void ApplyWelcomeRunMode()
    {
        string mode = WelcomeModeHelper.IsChecked == true ? RunModes.Helper
            : WelcomeModeLaunchOnly.IsChecked == true ? RunModes.LaunchOnly : RunModes.Tray;
        if (mode != _engine.Settings.RunMode) _app.ChangeRunMode(mode);
        bool startup = mode != RunModes.LaunchOnly && WelcomeStartupToggle.IsChecked == true;
        if (mode != RunModes.LaunchOnly && startup != (_engine.Settings.StartWithWindows == true)) _app.ChangeStartWithWindows(startup);
    }

    // ------------------------------------------------------------------ Buttons

    private void WelcomeNext_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (_welcomeStep)
            {
                case 1: if (!ApplyWelcomeMonitors()) return; break;
                case 2: ApplyWelcomeApps(); break;
                default: ApplyWelcomeRunMode(); break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Unexpected error (welcome): " + ex);
            _ = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Couldn't save that", Content = "Something went wrong. The details are in the activity log.", CloseButtonText = "OK",
            }.ShowDialogAsync();
            return;
        }
        NextWelcomeStep();
    }

    private void WelcomeSkip_Click(object sender, RoutedEventArgs e) => NextWelcomeStep();

    private void WelcomeBack_Click(object sender, RoutedEventArgs e)
    {
        if (_welcomeStep > 1) ShowWelcomeStep(_welcomeStep - 1);
    }

    private void NextWelcomeStep()
    {
        if (_welcomeStep < 3) { ShowWelcomeStep(_welcomeStep + 1); return; }
        // Done: show the General page.
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        MainArea.IsEnabled = true;
        NavList.SelectedIndex = 0;
        _panelState = "";
        Refresh();
        Dispatcher.BeginInvoke(() => NavList.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A closed drop-down in the welcome never changes its value when you scroll over it.</summary>
    private void WelcomeCombo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen) return;
        e.Handled = true;
        WelcomeScroller.ScrollToVerticalOffset(WelcomeScroller.VerticalOffset - e.Delta);
    }
}

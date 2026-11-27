using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MonitorWindowControl;

/// <summary>true → Collapsed, false → Visible (hides "Make main" on the monitor that already is).</summary>
public sealed class HideWhenTrueConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>One row: a saved app (learned / set by you), or an app that's open right now but not saved yet.</summary>
public sealed record AppRow(string Key, string Name, string Detail, string OpensOn, string Source,
                            bool IsSaved, string? CurrentDevice);

public partial class MainWindow : FluentWindow
{
    private const string NeverMoveLabel = "Never move this app";
    private const string LastUsedLabel = "Where I used it last…";
    private readonly Engine _engine;
    private readonly App _app;
    private readonly System.Windows.Threading.DispatcherTimer _openAppsTimer;
    private bool _updating;
    private string? _panelKey;   // which row the "Open it on" box was last filled for
    private string _panelState = "";

    public MainWindow(Engine engine, App app)
    {
        _engine = engine;
        _app = app;
        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        _engine.Changed += Refresh;
        // While this window is open, keep the "open now" apps current (apps get opened and closed),
        // and pick up what the helper learned meanwhile.
        _openAppsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _openAppsTimer.Tick += (_, _) =>
        {
            if (!_engine.ReloadIfChanged()) RefreshAppList(background: true); // a reload refreshes everything itself
            BackgroundStatusText.Text = _app.BackgroundStatus();
        };
        _openAppsTimer.Start();
        Refresh();
    }

    private string _monitorSig = "";

    /// <summary>Stop listening to the engine (called on exit).</summary>
    public void Detach()
    {
        _engine.Changed -= Refresh;
        _openAppsTimer.Stop();
    }

    // Closing the window really closes it (frees its memory); the app keeps running in the tray.
    protected override void OnClosed(EventArgs e)
    {
        Detach();
        base.OnClosed(e);
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            var s = _engine.Settings;
            EnabledToggle.IsChecked = s.Enabled;
            LearnToggle.IsChecked = s.LearnAutomatically;
            StartupToggle.IsChecked = s.StartWithWindows == true;
            UnknownCombo.SelectedIndex = s.MoveUnknownToMain ? 0 : 1;

            RunModeCombo.SelectedItem = RunModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.RunMode);
            bool launchOnly = s.RunMode == RunModes.LaunchOnly;
            RunModeHint.Text = s.RunMode switch
            {
                RunModes.Helper => "A tiny helper (a few MB, no tray icon) places every new window and learns where you use apps. " +
                                   "This settings window only runs while it's open.",
                RunModes.LaunchOnly => "Nothing runs in the background. Create a launch shortcut for an app below (Start menu → Monitor Window Control) " +
                                       "and open it from there: it opens on its monitor, then nothing keeps running. Windows opened another way aren't moved.",
                _ => "Monitor Window Control stays in the tray, places every new window and learns where you use apps.",
            };
            BackgroundStatusText.Text = _app.BackgroundStatus();
            StartupToggle.IsEnabled = !launchOnly;
            StartupHint.Text = s.RunMode switch
            {
                RunModes.Helper => "Starts the tiny helper when you sign in (no window, no tray icon).",
                RunModes.LaunchOnly => "Not needed: nothing has to run in the background.",
                _ => "Runs quietly in the tray when you sign in.",
            };
            LearnToggle.IsEnabled = !launchOnly;
            LearnHint.Text = launchOnly
                ? "Needs something running in the background to notice where you use apps; set apps' monitors below instead."
                : "When you use an app on a monitor for a few seconds, that becomes its home. Only the window you're using counts.";
            LastActionText.Text = _engine.LastAction;
            LastActionText.Visibility = string.IsNullOrEmpty(_engine.LastAction) ? Visibility.Collapsed : Visibility.Visible;

            // Rebuild the monitor cards only when monitors (or their names) actually changed.
            string sig = string.Join("|", _engine.Monitors.Select(m => $"{m.DeviceName}={m.Label}"));
            if (sig != _monitorSig) { _monitorSig = sig; MonitorsList.ItemsSource = _engine.Monitors.ToList(); }
        }
        finally { _updating = false; }
        RefreshAppList(background: false);
    }

    /// <summary>
    /// Rebuilds the apps list (saved apps + apps open right now). The periodic background refresh never runs
    /// while you're choosing something, and never moves the page or your selection.
    /// </summary>
    private void RefreshAppList(bool background)
    {
        if (background && (TargetCombo.IsDropDownOpen || UnknownCombo.IsDropDownOpen
                           || System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed))
            return;
        _updating = true;
        double scroll = PageScroller.VerticalOffset;
        try
        {
            var s = _engine.Settings;
            // Saved apps first, then everything else that's open right now.
            var open = _engine.GetOpenApps();
            var saved = s.Apps
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => ToRow(r, open.FirstOrDefault(o => string.Equals(o.App.Key, r.Key, StringComparison.OrdinalIgnoreCase))));
            var openOnly = open
                .Where(o => s.Find(o.App.Key) == null)
                .OrderBy(o => o.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(ToRow);
            var rows = saved.Concat(openOnly).ToList();

            // Only rebuild the list when something changed, so a selection or an open dropdown isn't disturbed.
            if (AppsList.ItemsSource is not List<AppRow> current || !current.SequenceEqual(rows))
            {
                string? selectedKey = (AppsList.SelectedItem as AppRow)?.Key;
                AppsList.ItemsSource = rows;
                AppsList.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey);
            }
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updating = false; }
        UpdateSelectionPanel();
        if (Math.Abs(PageScroller.VerticalOffset - scroll) > 0.5) PageScroller.ScrollToVerticalOffset(scroll);
    }

    private static (string name, string detail) Names(string key, string displayName)
    {
        string name = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
        return (name, string.Equals(name, key, StringComparison.OrdinalIgnoreCase) ? "" : key);
    }

    private AppRow ToRow(AppRule r, Engine.OpenApp? open)
    {
        string opensOn;
        if (r.NeverMove) opensOn = "Never moved";
        else if (r.FollowLastUsed)
        {
            var m = _engine.ResolveTarget(r);
            int n = r.AllowedMonitors.Count == 0 ? _engine.Monitors.Count : r.AllowedMonitors.Count;
            opensOn = $"Last used: {m?.Name ?? "not yet"} (of {n})";
        }
        else
        {
            var m = _engine.Resolve(r);
            opensOn = m != null ? m.Label : $"{r.MonitorName ?? r.MonitorDevice} (not connected)";
        }
        var (name, detail) = Names(r.Key, r.DisplayName);
        return new AppRow(r.Key, name, detail, opensOn, r.Pinned || r.NeverMove ? "set by you" : "learned",
                          true, open?.Monitor?.DeviceName);
    }

    private AppRow ToRow(Engine.OpenApp o)
    {
        var (name, detail) = Names(o.App.Key, o.App.DisplayName);
        string where = o.Monitor != null ? $"Not set (now on {o.Monitor.Name})" : "Not set";
        return new AppRow(o.App.Key, name, detail, where, "open now", false, o.Monitor?.DeviceName);
    }

    private void UpdateSelectionPanel()
    {
        var row = AppsList.SelectedItem as AppRow;
        var rule = row == null ? null : _engine.Settings.Find(row.Key);
        SelectionPanel.IsEnabled = row != null;
        ForgetButton.IsEnabled = rule != null;
        UpdateShortcutRow(row, rule);

        // Don't reset what the user is picking unless the selection or that app's setting changed.
        string state = row == null ? "" :
            $"{row.Key}|{rule?.NeverMove}|{rule?.FollowLastUsed}|{rule?.MonitorDevice}|" +
            string.Join(",", rule?.AllowedMonitors.Select(a => a.Device) ?? Enumerable.Empty<string>()) + "|" +
            string.Join(",", _engine.Monitors.Select(m => m.DeviceName));
        if (row?.Key == _panelKey && state == _panelState) return;
        _panelKey = row?.Key; _panelState = state;

        var items = new List<object>(_engine.Monitors) { LastUsedLabel, NeverMoveLabel };
        TargetCombo.ItemsSource = items;

        // Checkboxes for "Where I used it last": ticked = monitors it may open on (all, if none chosen yet).
        AllowedList.ItemsSource = _engine.Monitors.Select(m => new CheckBox
        {
            Content = m.Label, Tag = m, Margin = new Thickness(0, 0, 16, 4),
            IsChecked = rule == null || !rule.FollowLastUsed || _engine.IsAllowed(rule, m),
        }).ToList();

        if (row == null) { TargetCombo.SelectedIndex = -1; }
        else if (rule != null)
            TargetCombo.SelectedItem = rule.NeverMove ? NeverMoveLabel
                : rule.FollowLastUsed ? LastUsedLabel
                : (object?)_engine.Resolve(rule) ?? _engine.MainMonitor;
        else // not saved yet: start from the monitor it's on now
            TargetCombo.SelectedItem = _engine.Monitors.FirstOrDefault(m => m.DeviceName == row.CurrentDevice) ?? _engine.MainMonitor;
        UpdateAllowedPanel();
    }

    private void UpdateAllowedPanel()
    {
        AllowedPanel.Visibility = TargetCombo.SelectedItem as string == LastUsedLabel ? Visibility.Visible : Visibility.Collapsed;
        LastUsedLaunchOnlyNote.Visibility = _engine.Settings.RunMode == RunModes.LaunchOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The "launch shortcut" line for the selected app.</summary>
    private void UpdateShortcutRow(AppRow? row, AppRule? rule)
    {
        bool launchOnly = _engine.Settings.RunMode == RunModes.LaunchOnly;
        if (row == null) { ShortcutStatus.Text = ""; ShortcutButton.IsEnabled = false; return; }
        if (rule == null)
        {
            ShortcutStatus.Text = "Save where it opens first; then you can make a launch shortcut for it.";
            ShortcutButton.Content = "Create launch shortcut";
            ShortcutButton.IsEnabled = false;
            return;
        }
        bool exists = ShortcutManager.Exists(rule);
        ShortcutButton.IsEnabled = true;
        ShortcutButton.Content = exists ? "Remove shortcut" : "Create launch shortcut";
        ShortcutStatus.Text = exists
            ? $"Shortcut: Start menu → Monitor Window Control → {rule.ShortcutFile}. Opening it from there puts it on its monitor."
            : launchOnly
                ? "Make a shortcut so this app opens on its monitor without anything running in the background."
                : "Optional: a shortcut that places the app even when nothing runs in the background.";
    }

    /// <summary>The Start menu shortcut exists even if only the desktop copy failed.</summary>
    private static bool ShortcutManagerStartMenuMade(string error) => error.StartsWith("The Start menu shortcut was created", StringComparison.Ordinal);

    private async void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row || _engine.Settings.Find(row.Key) is not AppRule rule) return;
        if (ShortcutManager.Exists(rule))
        {
            ShortcutManager.Delete(rule);
            _engine.SetShortcutFile(rule.Key, null);
            _panelState = ""; UpdateSelectionPanel();
            return;
        }
        if (rule.LaunchTarget == null) _engine.UpdateLaunchTarget(rule.Key);
        rule = _engine.Settings.Find(row.Key) ?? rule;

        var desktop = new CheckBox { Content = "Also put it on the desktop", IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
        var content = new StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"This adds \"{ShortcutManager.FileName(rule, _engine.Settings.Apps)}\" to Start menu → Monitor Window Control. Opening the app from it " +
                   "puts its window on its monitor; a tiny helper does that and exits after a few seconds. " +
                   "Tip: right-click it there and choose \"Pin to taskbar\". Your existing shortcuts aren't changed.",
        });
        content.Children.Add(desktop);
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Launch shortcut for {ShortcutManager.ShortcutName(rule)}", Content = content,
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        string? error = ShortcutManager.Create(rule, _engine.Settings.Apps, desktop.IsChecked == true, out string file);
        if (error == null || ShortcutManagerStartMenuMade(error)) _engine.SetShortcutFile(rule.Key, file);
        if (error != null)
            await new Wpf.Ui.Controls.MessageBox { Title = "Shortcut", Content = error, CloseButtonText = "OK" }.ShowDialogAsync();
        _panelState = ""; UpdateSelectionPanel();
    }

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAllowedPanel();

    /// <summary>The list grows with its content; let the mouse wheel scroll the whole page instead.</summary>
    private void AppsList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset - e.Delta);
    }

    /// <summary>The page only scrolls when you scroll it, never by itself when focus or content changes.</summary>
    private void Page_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    /// <summary>Clicking a row shouldn't make the page jump.</summary>
    private void AppsList_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) UpdateSelectionPanel();
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e) => _engine.SetEnabled(EnabledToggle.IsChecked == true);

    private void LearnToggle_Click(object sender, RoutedEventArgs e) => _engine.SetLearn(LearnToggle.IsChecked == true);

    private void StartupToggle_Click(object sender, RoutedEventArgs e) =>
        _app.ChangeStartWithWindows(StartupToggle.IsChecked == true);

    private void RunModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || RunModeCombo.SelectedItem is not ComboBoxItem item) return;
        string mode = (string)item.Tag;
        if (mode == _engine.Settings.RunMode) return;
        _app.ChangeRunMode(mode);
        _panelState = "";
        Refresh();
    }

    private void UnknownCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || UnknownCombo.SelectedIndex < 0) return;
        _engine.SetMoveUnknownToMain(UnknownCombo.SelectedIndex == 0);
    }

    private void ChangeMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row) return;
        switch (TargetCombo.SelectedItem)
        {
            case MonitorInfo m: _engine.Pin(row.Key, m, row.Name); break;
            case string s when s == NeverMoveLabel: _engine.Pin(row.Key, null, row.Name); break;
            case string s when s == LastUsedLabel:
                var allowed = (AllowedList.ItemsSource as IEnumerable<CheckBox> ?? Enumerable.Empty<CheckBox>())
                    .Where(c => c.IsChecked == true).Select(c => (MonitorInfo)c.Tag).ToList();
                if (allowed.Count == 0) allowed = _engine.Monitors.ToList(); // nothing ticked = any monitor
                var now = _engine.Monitors.FirstOrDefault(m => m.DeviceName == row.CurrentDevice);
                _engine.SetFollowLastUsed(row.Key, allowed, now, row.Name);
                break;
        }
        Activate(); // moving a maximized window can bring it to the front; stay on top for the next choice
    }

    private async void MakeMain_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorInfo m) return;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Make {m.Name} your main monitor?",
            Content = "This changes the main display in Windows itself: the taskbar clock, the Start menu and many new windows go there, " +
                      "and apps Monitor Window Control hasn't learned yet open there. You can change it back here or in Windows Settings → Display.",
            PrimaryButtonText = "Make main",
            CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        string? error = _engine.SetMainMonitor(m);
        if (error != null)
            await new Wpf.Ui.Controls.MessageBox { Title = "Couldn't change the main monitor", Content = error, CloseButtonText = "OK" }.ShowDialogAsync();
    }

    private async void RenameMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorInfo m) return;
        System.Windows.Controls.TextBlock Label(string text, double top = 0) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 6),
        };

        var nameBox = new Wpf.Ui.Controls.TextBox { Text = m.Nickname ?? "", PlaceholderText = m.FriendlyName, MinWidth = 320 };
        var numbers = new List<object> { "No number" };
        numbers.AddRange(Enumerable.Range(1, 9).Cast<object>());
        var numberBox = new ComboBox { ItemsSource = numbers, SelectedItem = m.Number is int n ? n : "No number", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };

        var content = new StackPanel();
        content.Children.Add(Label("Name"));
        content.Children.Add(nameBox);
        content.Children.Add(Label("Number", 14));
        content.Children.Add(numberBox);
        var note = Label($"Leave the name empty to use the model name ({m.FriendlyName}). Your number decides the order and shows in front of the name, " +
                         "e.g. \"2 · TV\". If another monitor already has that number, it moves to this one. These only apply inside Monitor Window Control: " +
                         "Windows keeps its own display numbers, which apps can't change.", 14);
        note.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
        note.FontSize = 12;
        content.Children.Add(note);

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Name & number: {m.FriendlyName}", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
        };
        box.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        if (await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
            _engine.RenameMonitor(m, nameBox.Text, numberBox.SelectedItem as int?);
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row) return;
        if (_engine.Settings.Find(row.Key) is AppRule rule) ShortcutManager.Delete(rule); // its launch shortcut goes too
        _engine.Forget(row.Key);
    }

    private async void ForgetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.Settings.Apps.Count == 0) return;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Forget all apps?",
            Content = $"Monitor Window Control will forget where all {_engine.Settings.Apps.Count} apps open, including the ones you set yourself. It will start learning again from scratch.",
            PrimaryButtonText = "Forget all",
            CloseButtonText = "Cancel",
        };
        var result = await box.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        foreach (var rule in _engine.Settings.Apps.ToList()) ShortcutManager.Delete(rule);
        _engine.ForgetAll();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(Paths.LogFile)) Log.Write("Log opened.");
            Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Could not open log: " + ex.Message);
        }
    }
}

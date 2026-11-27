using System;
using System.Collections.Generic;
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
public sealed record AppRow(string Key, string Name, string Detail, string OpensOn, string Source, string? CurrentDevice, string? Hint = null)
{
    /// <summary>Show the tip icon (an app that keeps moving itself back).</summary>
    public bool HasHint => Hint != null;

    /// <summary>What Narrator and other screen readers say for the row.</summary>
    public override string ToString() => $"{Name}, opens on {OpensOn}, {Source}" + (Hint == null ? "" : $". {Hint}");
}

/// <summary>
/// One choice in an "If it's off, open on" list. <see cref="KeepSaved"/> marks the saved monitor when it isn't
/// connected now (choosing it keeps that choice as it is).
/// </summary>
public sealed record FallbackItem(string Label, string? Mode, MonitorInfo? Monitor, bool KeepSaved = false)
{
    public override string ToString() => Label;
}

public partial class MainWindow : FluentWindow
{
    private const string NeverMoveLabel = "Never move this app";
    private const string LastUsedLabel = "Where I used it last…";
    private const string MouseLabel = "The monitor I'm using (mouse pointer)…";
    private const string StubbornTip = "It moved itself back twice after it was moved: try a longer wait (select it, then Advanced).";
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
            try
            {
                if (!_engine.ReloadIfChanged()) RefreshAppList(background: true); // a reload refreshes everything itself
                BackgroundStatusText.Text = _app.BackgroundStatus();
                UpdatePauseInfo(); // (in helper mode, a game starting or ending is only seen here)
                UpdateNavStatus();
            }
            catch (Exception ex) { Log.Write("Unexpected error (window refresh): " + ex); }
        };
        _openAppsTimer.Start();
        // In tray mode the engine sees app windows open and close: update the list right away (debounced).
        _appsChangedDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _appsChangedDebounce.Tick += (_, _) =>
        {
            _appsChangedDebounce.Stop();
            try { RefreshAppList(background: true); } catch (Exception ex) { Log.Write("Unexpected error (app list): " + ex); }
        };
        _engine.AppWindowsChanged += OnAppWindowsChanged;
        SizeChanged += (_, _) => ApplyLayout();
        PageScroller.SizeChanged += (_, _) => FitAppsList();
        // A closed drop-down must never change its value when you scroll the page over it (WPF does that by default
        // for a focused ComboBox — scrolling past "How it runs" could switch the mode).
        foreach (var combo in new[] { RunModeCombo, UnknownCombo, RemoteCombo, FallbackCombo, ReturnCombo, TargetCombo, AppFallbackCombo,
                                      PlaceCombo, WaitCombo, CorrectCombo })
            combo.PreviewMouseWheel += Combo_PreviewMouseWheel;
        PreviewKeyDown += (_, _) => _lastKeyTick = Environment.TickCount;
        Loaded += (_, _) => PageScroller.ScrollToTop();
        VersionText.Text = $"Version {AppVersion} · free and open source (MIT licence)";
        SettingsFolderText.Text = Paths.Dir;
        NavList.SelectedIndex = 0;
        Refresh();
    }

    private bool? _wide;
    private int _lastKeyTick = int.MinValue / 2;

    private bool? _compactNav;

    /// <summary>
    /// Adapts to the window like Windows Settings: a narrow window shows the navigation as icons only; a wide one
    /// (16:9, 21:9, 32:9 maximized…) puts the selected app's options beside the list instead of below it, so nothing
    /// is stretched across empty space.
    /// </summary>
    private void ApplyLayout()
    {
        bool compact = ActualWidth < 760;
        if (_compactNav != compact)
        {
            _compactNav = compact;
            NavColumn.Width = new GridLength(compact ? 64 : 280);
            NavIdentity.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var item in NavList.Items.OfType<Wpf.Ui.Controls.ListViewItem>())
                if (item.Content is StackPanel sp && sp.Children.Count > 1) sp.Children[1].Visibility = NavIdentity.Visibility;
            // Small window: keep the drop-downs compact so the descriptions beside them stay readable.
            foreach (var combo in new[] { RunModeCombo, UnknownCombo, RemoteCombo, FallbackCombo, ReturnCombo })
            {
                combo.MinWidth = compact ? 150 : 250;
                combo.MaxWidth = compact ? 210 : double.PositiveInfinity;
            }
        }
        bool wide = PageScroller.ActualWidth - 56 >= 740;
        if (_wide != wide)
        {
            _wide = wide;
            DetailColumn.Width = wide ? new GridLength(320) : new GridLength(0);
            Grid.SetRow(DetailPane, wide ? 0 : 1);
            Grid.SetColumn(DetailPane, wide ? 1 : 0);
            DetailPane.Margin = wide ? new Thickness(12, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        }
        FitAppsList();
    }

    /// <summary>
    /// The app list scrolls by itself and fits the window, so the selected app's options (beside it when wide, below
    /// it when narrow) are always in view without scrolling past every app.
    /// </summary>
    private void FitAppsList()
    {
        double room = PageScroller.ViewportHeight - 270;          // title, tip, search box, column headers
        if (_wide != true) room -= 300;                            // the options card below the list
        AppsList.MaxHeight = Math.Max(220, room);
    }

    /// <summary>A closed drop-down never changes its value when you scroll the page over it: the page scrolls instead.</summary>
    private void Combo_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen) return;
        e.Handled = true;
        PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset - e.Delta);
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string page = (NavList.SelectedItem as Wpf.Ui.Controls.ListViewItem)?.Tag as string ?? "General";
        PageGeneral.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageMonitors.Visibility = page == "Monitors" ? Visibility.Visible : Visibility.Collapsed;
        PageApps.Visibility = page == "Apps" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Could not open {uri}: {ex.Message}"); }
    }

    private void OpenDisplaySettings_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:display");
    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenUri("https://github.com/eldavier888/MonitorWindowControl");
    private void OpenPrivacy_Click(object sender, RoutedEventArgs e) => OpenUri("https://github.com/eldavier888/MonitorWindowControl/blob/main/PRIVACY.md");
    private void OpenFolder_Click(object sender, RoutedEventArgs e) { System.IO.Directory.CreateDirectory(Paths.Dir); OpenUri(Paths.Dir); }

    public static string AppVersion
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _appsChangedDebounce;

    private void OnAppWindowsChanged() { _appsChangedDebounce.Stop(); _appsChangedDebounce.Start(); }

    private void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        _engine.ReloadIfChanged();
        RefreshAppList(background: false);
    }

    private string _monitorSig = "";

    /// <summary>Stop listening to the engine (called on exit).</summary>
    public void Detach()
    {
        _engine.Changed -= Refresh;
        _engine.AppWindowsChanged -= OnAppWindowsChanged;
        _openAppsTimer.Stop();
        _appsChangedDebounce.Stop();
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
            UnknownCombo.SelectedIndex = s.UnknownFollowMouse ? 2 : s.MoveUnknownToMain ? 0 : 1;
            RemoteCombo.SelectedIndex = s.PauseDuringRemoteDesktop ? 0 : 1;
            GameToggle.IsChecked = s.PauseForGames;
            UpdatePauseInfo();

            RunModeCombo.SelectedItem = RunModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.RunMode);
            bool launchOnly = s.RunMode == RunModes.LaunchOnly;
            RunModeHint.Text = s.RunMode switch
            {
                RunModes.Helper => "A tiny helper (a few MB, no tray icon) places every new window and learns where you use apps. " +
                                   "This settings window only runs while it's open.",
                RunModes.LaunchOnly => "Nothing runs in the background. Create a launch shortcut for an app on the Apps page (Start menu → Monitor Window Control) " +
                                       "and open it from there: it opens on its monitor, then nothing keeps running. Windows opened another way aren't moved.",
                _ => "Monitor Window Control stays in the tray, places every new window and learns where you use apps.",
            };
            BackgroundStatusText.Text = _app.BackgroundStatus();
            StartupToggle.IsEnabled = !launchOnly;
            ExitCard.Visibility = s.RunMode == RunModes.Tray ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavStatus();
            StartupHint.Text = s.RunMode switch
            {
                RunModes.Helper => "Starts the tiny helper when you sign in (no window, no tray icon).",
                RunModes.LaunchOnly => "Not needed: nothing has to run in the background.",
                _ => "Runs quietly in the tray when you sign in.",
            };
            SignInToggle.IsChecked = s.PlaceAppsOpenedAtSignIn;
            SignInToggle.IsEnabled = !launchOnly && s.StartWithWindows == true;
            SignInHint.Text = launchOnly
                ? "Needs something running in the background, started with Windows."
                : s.StartWithWindows != true
                    ? "Works with Start with Windows: turn that on first."
                    : "Apps that start with Windows, like a chat or music app, often open before Monitor Window Control does. Right after you sign in, " +
                      "their windows are moved to their monitors too. Windows you've already started using are left alone.";
            DialogsToggle.IsChecked = s.MoveDialogsWithApp;
            DialogsToggle.IsEnabled = !launchOnly;
            DialogsHint.Text = launchOnly
                ? "Needs something running in the background to notice dialogs opening."
                : "Save, Print and other small windows that open on another monitor than their app move over the app's window. They're never resized.";
            LearnToggle.IsEnabled = !launchOnly;
            LearnHint.Text = launchOnly
                ? "Needs something running in the background to notice where you use apps; set apps' monitors on the Apps page instead."
                : "When you use an app on a monitor for a few seconds, that becomes its home. Only the window you're using counts.";
            LastActionText.Text = _engine.LastAction;
            RecentPanel.Visibility = string.IsNullOrEmpty(_engine.LastAction) ? Visibility.Collapsed : Visibility.Visible;

            // When monitors change (these need something running in the background, except where apps open).
            ReturnCombo.SelectedIndex = s.MoveBackWhenMonitorReturns ? 0 : 1;
            ReturnCombo.IsEnabled = !launchOnly;
            ReturnHint.Text = launchOnly
                ? "Needs something running in the background to notice a monitor coming back."
                : "Windows that opened on another monitor while it was off or disconnected, and windows Windows moved off it, go back to it. Windows you've moved since stay where they are.";
            SleepToggle.IsChecked = s.RestoreAfterSleep;
            SleepToggle.IsEnabled = !launchOnly;
            SleepHint.Text = launchOnly
                ? "Needs something running in the background to notice the PC going to sleep."
                : "When the PC wakes up, windows that Windows moved or squeezed go back where they were before it went to sleep. Windows you move after waking up are left alone.";
            FillDefaultFallback();

            // Rebuild the monitor cards only when monitors (or their names) actually changed.
            string sig = string.Join("|", _engine.Monitors.Select(m => $"{m.DeviceName}={m.Label}/{m.Details}/{m.ShowTemporaryOption}{m.IsTemporary}" +
                                                                       $"/{m.ShowMonitorChoices}{m.SwitchedOffNote}{m.OpenHereIndex}"));
            if (sig != _monitorSig) { _monitorSig = sig; MonitorsList.ItemsSource = _engine.Monitors.ToList(); }
            UpdateKeepMainCard(launchOnly);
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
        if (background && (TargetCombo.IsDropDownOpen || UnknownCombo.IsDropDownOpen || AppFallbackCombo.IsDropDownOpen
                           || PlaceCombo.IsDropDownOpen || WaitCombo.IsDropDownOpen || CorrectCombo.IsDropDownOpen
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
            var all = saved.Concat(openOnly).ToList();
            string q = SearchBox.Text?.Trim() ?? "";
            var rows = q.Length == 0 ? all : all.Where(r =>
                r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                r.Detail.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                r.OpensOn.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();

            // Only rebuild the list when something changed, so a selection or an open dropdown isn't disturbed.
            if (AppsList.ItemsSource is not List<AppRow> current || !current.SequenceEqual(rows))
            {
                string? selectedKey = (AppsList.SelectedItem as AppRow)?.Key;
                AppsList.ItemsSource = rows;
                AppsList.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey);
            }
            EmptyText.Text = all.Count == 0
                ? "No apps yet. Open an app and it shows up here, or just use your apps: after a few seconds on a monitor, an app is learned."
                : $"No apps match \"{q}\".";
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ForgetAllButton.IsEnabled = s.Apps.Count > 0;
        }
        finally { _updating = false; }
        UpdateSelectionPanel();
        if (Math.Abs(PageScroller.VerticalOffset - scroll) > 0.5) PageScroller.ScrollToVerticalOffset(scroll);
    }

    private static (string name, string detail) Names(string key, string displayName)
    {
        string name = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
        if (key.StartsWith(WindowInfo.WebAppKeyPrefix, StringComparison.Ordinal)) return (name, "Web app"); // Netflix, Crave… inside Edge
        return (name, string.Equals(name, key, StringComparison.OrdinalIgnoreCase) ? "" : key);
    }

    private AppRow ToRow(AppRule r, Engine.OpenApp? open)
    {
        string opensOn;
        if (r.NeverMove) opensOn = "Never moved";
        else if (r.FollowMouse)
        {
            int n = r.AllowedMonitors.Count;
            opensOn = "The monitor you're using";
            if (n > 0 && n < _engine.Monitors.Count) opensOn += $", {n} allowed";
        }
        else if (r.FollowLastUsed)
        {
            var m = _engine.ResolveTarget(r);
            int n = r.AllowedMonitors.Count == 0 ? _engine.Monitors.Count : r.AllowedMonitors.Count;
            opensOn = m != null ? $"Where last used (now {m.Name})" : "Where last used";
            if (n > 1 && n < _engine.Monitors.Count) opensOn += $", {n} allowed";
        }
        else
        {
            var m = _engine.Resolve(r);
            opensOn = m == null ? $"{r.MonitorName ?? r.MonitorDevice} (not connected)"
                : m.IsSwitchedOff ? $"{m.Label} (switched off)" : m.Label;
        }
        var (name, detail) = Names(r.Key, r.DisplayName);
        if (!r.NeverMove && r.Place == PlaceModes.Maximized) opensOn += ", maximized";
        else if (!r.NeverMove && r.Place == PlaceModes.Last) opensOn += ", where you had it";
        return new AppRow(r.Key, name, detail, opensOn, r.Pinned || r.NeverMove ? "Set by you" : "Learned",
                          open?.Monitor?.DeviceName, r.MovedItselfBack && !r.NeverMove ? StubbornTip : null);
    }

    private AppRow ToRow(Engine.OpenApp o)
    {
        var (name, detail) = Names(o.App.Key, o.App.DisplayName);
        string where = o.Monitor != null ? $"Not set (on {o.Monitor.Name} now)" : "Not set";
        return new AppRow(o.App.Key, name, detail, where, "Open now", o.Monitor?.DeviceName);
    }

    private void UpdateSelectionPanel()
    {
        var row = AppsList.SelectedItem as AppRow;
        var rule = row == null ? null : _engine.Settings.Find(row.Key);
        SelectionPanel.IsEnabled = row != null;
        ForgetButton.IsEnabled = rule != null;
        SelectedAppName.Text = row?.Name ?? "No app selected";
        SelectedAppDetail.Text = row == null ? "Select an app in the list." :
            string.Join(" · ", new[] { row.Detail, row.Source }.Where(x => !string.IsNullOrEmpty(x)));
        UpdateShortcutRow(row, rule);

        // Don't reset what the user is picking unless the selection or that app's setting changed.
        var s = _engine.Settings;
        string state = row == null ? "" :
            $"{row.Key}|{rule?.NeverMove}|{rule?.FollowLastUsed}|{rule?.FollowMouse}|{rule?.MonitorDevice}|" +
            string.Join(",", rule?.AllowedMonitors.Select(a => a.Device) ?? Enumerable.Empty<string>()) + "|" +
            $"{rule?.Fallback}|{rule?.FallbackMonitor?.Device}|{s.Fallback}|{s.FallbackMonitor?.Device}|" +
            $"{rule?.Place}|{rule?.Positions.Count}|{rule?.WaitMs}|{rule?.CorrectForMs}|{rule?.MovedItselfBack}|" +
            string.Join(",", _engine.Monitors.Select(m => m.DeviceName + "=" + m.Label));
        if (row?.Key == _panelKey && state == _panelState) return;
        _panelKey = row?.Key; _panelState = state;

        var items = new List<object>(_engine.Monitors) { LastUsedLabel, MouseLabel, NeverMoveLabel };
        TargetCombo.ItemsSource = items;

        // "Where I used it last" and "The monitor I'm using": ticked = monitors it may open on, in its order (then the
        // others). All are ticked when it isn't set yet, except monitors kept for chosen apps.
        bool hasList = rule is { FollowLastUsed: true } || rule is { FollowMouse: true };
        var ordered = hasList ? _engine.AllowedInOrder(rule!) : new List<MonitorInfo>();
        _allowedItems = ordered.Concat(_engine.Monitors.Where(m => !ordered.Any(o => o.DeviceName == m.DeviceName)))
            .Select(m => new AllowedItem(m, hasList ? _engine.IsAllowed(rule!, m) : !m.OnlyChosenApps))
            .ToList();
        BuildAllowedRows();

        // "Place it" and Advanced.
        PlaceCombo.SelectedIndex = rule?.Place switch { PlaceModes.Last => 1, PlaceModes.Maximized => 2, _ => 0 };
        WaitCombo.SelectedIndex = rule?.WaitMs switch { 1000 => 1, 3000 => 2, _ => 0 };
        CorrectCombo.SelectedIndex = rule?.CorrectForMs == 5000 ? 1 : 0;
        bool tip = rule is { MovedItselfBack: true, NeverMove: false };
        AdvancedTip.Visibility = tip ? Visibility.Visible : Visibility.Collapsed;
        AdvancedExpander.IsExpanded = tip || rule?.WaitMs != null || rule?.CorrectForMs != null;
        UpdatePlaceNote(rule);

        // "If it's off, open on".
        var fallbacks = FallbackItems(forApp: true, rule?.Fallback, rule?.FallbackMonitor, out var chosen);
        AppFallbackCombo.ItemsSource = fallbacks;
        AppFallbackCombo.SelectedItem = chosen;

        if (row == null) { TargetCombo.SelectedIndex = -1; }
        else if (rule != null)
            TargetCombo.SelectedItem = rule.NeverMove ? NeverMoveLabel
                : rule.FollowLastUsed ? LastUsedLabel
                : rule.FollowMouse ? MouseLabel
                : (object?)_engine.Resolve(rule) ?? _engine.MainMonitor;
        else // not saved yet: start from the monitor it's on now
            TargetCombo.SelectedItem = _engine.Monitors.FirstOrDefault(m => m.DeviceName == row.CurrentDevice) ?? _engine.MainMonitor;
        UpdateAllowedPanel();
    }

    private void UpdateAllowedPanel()
    {
        string? choice = TargetCombo.SelectedItem as string;
        AllowedPanel.Visibility = choice is LastUsedLabel or MouseLabel ? Visibility.Visible : Visibility.Collapsed;
        AllowedIntro.Text = choice == MouseLabel
            ? "It opens on the monitor with the mouse pointer (or, if you use only the keyboard, the monitor of the window you're using), " +
              "but only on the monitors ticked here. Otherwise it opens on the first ticked monitor in this order:"
            : "It opens on whichever of these monitors you used it on last. If that one is off, it opens on the next one in this order:";
        LastUsedLaunchOnlyNote.Visibility = choice == LastUsedLabel && _engine.Settings.RunMode == RunModes.LaunchOnly
            ? Visibility.Visible : Visibility.Collapsed;
        var moves = TargetCombo.SelectedItem == null || choice == NeverMoveLabel ? Visibility.Collapsed : Visibility.Visible;
        AppFallbackPanel.Visibility = moves;
        PlacePanel.Visibility = moves;
        AdvancedExpander.Visibility = moves;
    }

    /// <summary>The line under "Place it" for "Where I last had it": on how many monitors it's known, with Reset.</summary>
    private void UpdatePlaceNote(AppRule? rule)
    {
        bool last = PlaceCombo.SelectedIndex == 1;
        PlaceLastPanel.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        int known = rule?.Positions.Count ?? 0;
        bool launchOnly = _engine.Settings.RunMode == RunModes.LaunchOnly;
        PlaceLastNote.Text = launchOnly && known == 0
            ? "Needs something running in the background to notice where you have it. Until then it opens in the center."
            : known == 0 ? "Remembered for each monitor as you use it. Until then it opens in the center."
            : known == 1 ? "Remembered on 1 monitor. On others it opens in the center until you've used it there."
            : $"Remembered on {known} monitors.";
        ResetPlaceButton.Visibility = known > 0 && rule?.Place == PlaceModes.Last ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaceLastPanel == null || ResetPlaceButton == null) return; // (still loading the window)
        var row = AppsList.SelectedItem as AppRow;
        UpdatePlaceNote(row == null ? null : _engine.Settings.Find(row.Key));
    }

    private async void ResetPlace_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row || _engine.Settings.Find(row.Key) is not AppRule rule) return;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Forget where {row.Name} was?",
            Content = "Where its window was on each monitor is forgotten. It opens in the center until you've used it on a monitor again.",
            PrimaryButtonText = "Forget", CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        _engine.ResetPositions(rule.Key);
    }

    /// <summary>One monitor in the "Where I used it last" list: ticked or not, in the order shown.</summary>
    private sealed class AllowedItem
    {
        public AllowedItem(MonitorInfo monitor, bool isChecked) { Monitor = monitor; Checked = isChecked; }
        public MonitorInfo Monitor { get; }
        public bool Checked { get; set; }
    }

    private List<AllowedItem> _allowedItems = new();

    /// <summary>
    /// The "Where I used it last" list: a checkbox per monitor with Move up and Move down buttons, so the order says
    /// which monitor comes next when the last one is off.
    /// </summary>
    private void BuildAllowedRows(int focusIndex = -1, bool focusUp = false)
    {
        var rows = new List<UIElement>();
        Wpf.Ui.Controls.Button? focus = null;
        for (int i = 0; i < _allowedItems.Count; i++)
        {
            var item = _allowedItems[i];
            int index = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new CheckBox { Content = item.Monitor.Label, IsChecked = item.Checked, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, item.Monitor.Label);
            box.Click += (_, _) => item.Checked = box.IsChecked == true;
            row.Children.Add(box);
            var up = MoveButton(SymbolRegular.ArrowUp24, $"Move {item.Monitor.Name} up", index > 0, () => MoveAllowed(index, -1));
            var down = MoveButton(SymbolRegular.ArrowDown24, $"Move {item.Monitor.Name} down", index < _allowedItems.Count - 1, () => MoveAllowed(index, +1));
            Grid.SetColumn(up, 1); Grid.SetColumn(down, 2);
            down.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(up); row.Children.Add(down);
            rows.Add(row);
            if (i == focusIndex) focus = focusUp ? (up.IsEnabled ? up : down) : (down.IsEnabled ? down : up);
        }
        AllowedList.ItemsSource = rows;
        if (focus != null)
            Dispatcher.BeginInvoke(() => focus.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static Wpf.Ui.Controls.Button MoveButton(SymbolRegular symbol, string name, bool enabled, Action move)
    {
        var b = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = symbol }, IsEnabled = enabled, ToolTip = name, Padding = new Thickness(8, 4, 8, 4),
        };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => move();
        return b;
    }

    /// <summary>Moves a monitor up or down in the "Where I used it last" order (keyboard focus stays on its button).</summary>
    private void MoveAllowed(int index, int by)
    {
        int to = index + by;
        if (to < 0 || to >= _allowedItems.Count) return;
        (_allowedItems[index], _allowedItems[to]) = (_allowedItems[to], _allowedItems[index]);
        BuildAllowedRows(to, focusUp: by < 0);
    }

    /// <summary>
    /// The choices for "If it's off, open on" (one app) or "When an app's monitor is off" (all apps), and which one is
    /// selected now. A saved monitor that isn't connected is still listed, so the choice isn't lost.
    /// </summary>
    private List<FallbackItem> FallbackItems(bool forApp, string? mode, MonitorRef? saved, out FallbackItem selected)
    {
        var s = _engine.Settings;
        var list = new List<FallbackItem>();
        FallbackItem? same = null;
        if (forApp)
        {
            same = new FallbackItem($"Same as other apps ({_engine.FallbackText(s.Fallback, s.FallbackMonitor)})", null, null);
            list.Add(same);
        }
        var main = new FallbackItem("Main monitor", forApp ? FallbackModes.Main : null, null);
        list.Add(main);
        var savedMonitor = _engine.FindMonitor(saved);
        FallbackItem? monitorItem = null;
        foreach (var m in _engine.Monitors.Where(m => !m.IsPrimary))
        {
            var item = new FallbackItem(m.Label, FallbackModes.Monitor, m);
            list.Add(item);
            if (mode == FallbackModes.Monitor && savedMonitor?.DeviceName == m.DeviceName) monitorItem = item;
        }
        if (mode == FallbackModes.Monitor && monitorItem == null && saved != null)
        {
            // Saved, but not connected now (or it's the main monitor at the moment): keep it listed.
            monitorItem = savedMonitor != null
                ? new FallbackItem(savedMonitor.Label, FallbackModes.Monitor, savedMonitor)
                : new FallbackItem($"{saved.Name ?? saved.Device} (not connected)", FallbackModes.Monitor, null, KeepSaved: true);
            list.Add(monitorItem);
        }
        var leave = new FallbackItem("Leave where Windows puts it", FallbackModes.Leave, null);
        list.Add(leave);
        selected = mode switch
        {
            FallbackModes.Leave => leave,
            FallbackModes.Monitor when monitorItem != null => monitorItem,
            FallbackModes.Main => main,
            _ => same ?? main,
        };
        return list;
    }

    private string _defaultFallbackSig = "";

    /// <summary>Fills "When an app's monitor is off" (only when the monitors or the choice changed, so an open list stays open).</summary>
    private void FillDefaultFallback()
    {
        var s = _engine.Settings;
        string sig = $"{s.Fallback}|{s.FallbackMonitor?.Device}|{s.FallbackMonitor?.Name}|" +
                     string.Join(",", _engine.Monitors.Select(m => m.DeviceName + "=" + m.Label));
        if (sig == _defaultFallbackSig) return;
        _defaultFallbackSig = sig;
        FallbackCombo.ItemsSource = FallbackItems(forApp: false, s.Fallback, s.FallbackMonitor, out var selected);
        FallbackCombo.SelectedItem = selected;
    }

    /// <summary>"Keep my main monitor" on the Monitors page.</summary>
    private void UpdateKeepMainCard(bool launchOnly)
    {
        var s = _engine.Settings;
        KeepMainCard.Visibility = _engine.Monitors.Count > 1 || s.KeepMainMonitor != null ? Visibility.Visible : Visibility.Collapsed;
        KeepMainToggle.IsChecked = s.KeepMainMonitor != null;
        KeepMainToggle.IsEnabled = !launchOnly;
        string kept = s.KeepMainMonitor == null ? ""
            : _engine.FindMonitor(s.KeepMainMonitor)?.Name ?? $"{s.KeepMainMonitor.Name ?? s.KeepMainMonitor.Device} (not connected)";
        KeepMainHint.Text = launchOnly
            ? "Needs something running in the background to notice Windows changing the main monitor."
            : s.KeepMainMonitor == null
                ? $"If Windows makes another monitor the main one (after a reconnect, an update or a TV waking up), change it back to {_engine.MainMonitor?.Name ?? "this one"}."
                : _engine.KeepMainStopped
                    ? $"Windows kept making another monitor main, so {kept} is no longer changed back. Turn this off and on to try again."
                    : $"{kept} stays the main monitor: if Windows makes another monitor main, it's changed back. To keep another one, press Make main on it.";
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
            var ask = new Wpf.Ui.Controls.MessageBox
            {
                Title = $"Remove the launch shortcut for {ShortcutManager.ShortcutName(rule)}?",
                Content = "The shortcut in Start menu → Monitor Window Control (and on the desktop, if you made one) is deleted. " +
                          "The app itself and where it opens aren't changed. If you pinned the shortcut to the taskbar, unpin it too.",
                PrimaryButtonText = "Remove", CloseButtonText = "Cancel",
            };
            if (await ask.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
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

    /// <summary>
    /// The page doesn't jump when you click something or when the list refreshes itself, but it still scrolls to
    /// whatever you move to with the keyboard (Tab, arrow keys).
    /// </summary>
    private void Page_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Only follow focus that you moved with the keyboard just now; opening the window, clicks and refreshes leave
        // the page where it is.
        if (_updating || unchecked(Environment.TickCount - _lastKeyTick) > 1000) e.Handled = true;
    }

    private void AppsList_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) => Page_RequestBringIntoView(sender, e);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAppList(background: false);

    private void AppsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete && ForgetButton.IsEnabled) { e.Handled = true; Forget_Click(sender, e); }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.ExitApp();

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
        _engine.SetUnknownApps(UnknownCombo.SelectedIndex == 0, followMouse: UnknownCombo.SelectedIndex == 2);
    }

    private void DialogsToggle_Click(object sender, RoutedEventArgs e) => _engine.SetMoveDialogsWithApp(DialogsToggle.IsChecked == true);

    private void GameToggle_Click(object sender, RoutedEventArgs e) => _engine.SetPauseForGames(GameToggle.IsChecked == true);

    private void SignInToggle_Click(object sender, RoutedEventArgs e) => _engine.SetPlaceAppsOpenedAtSignIn(SignInToggle.IsChecked == true);

    private void RemoteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || RemoteCombo.SelectedIndex < 0) return;
        _engine.SetPauseDuringRemoteDesktop(RemoteCombo.SelectedIndex == 0);
    }

    /// <summary>The status line under the app's name in the navigation pane.</summary>
    private void UpdateNavStatus()
    {
        var s = _engine.Settings;
        string text = !s.Enabled ? "Paused" : _engine.IsPausedForRemoteDesktop ? "Paused during Remote Desktop"
            : s.RunMode != RunModes.LaunchOnly && _engine.IsPausedForGame ? "Paused while a game is running" : s.RunMode switch
            {
                RunModes.Helper => "Running as a background helper",
                RunModes.LaunchOnly => "Launch shortcuts only",
                _ => "Running in the tray",
            };
        if (NavStatus.Text != text) NavStatus.Text = text;
    }

    /// <summary>The bar at the top of General: says when something outside the app pauses it.</summary>
    private void UpdatePauseInfo()
    {
        string title = "", message = "";
        if (Engine.IsRemoteSession)
        {
            title = _engine.IsPausedForRemoteDesktop ? "Paused: Remote Desktop session" : "Remote Desktop session";
            message = _engine.IsPausedForRemoteDesktop
                ? "Windows aren't moved and nothing is learned until you're back on this PC's own monitors."
                : "Windows are still moved, as you chose, but nothing is learned until you're back on this PC's own monitors.";
        }
        else if (_engine.IsPausedForGame && _engine.Settings.Enabled && _engine.Settings.RunMode != RunModes.LaunchOnly)
        {
            title = "Paused: a game or presentation is running";
            message = "Windows aren't moved and nothing is learned until it ends. Windows that open meanwhile are placed then.";
        }
        else if (_engine.HasDuplicatedMonitors)
        {
            title = "Learning paused: monitors are duplicated";
            message = "Some monitors show the same picture, so apps aren't learned until they're extended again (Windows key + P).";
        }
        else if (_engine.Monitors.Where(m => m.IsSwitchedOff).Select(m => m.Name).ToList() is { Count: > 0 } off)
        {
            title = off.Count == 1 ? $"{off[0]} is treated as switched off" : $"{string.Join(", ", off)} are treated as switched off";
            message = "New windows open on another monitor until you change it on the Monitors page" +
                      (_engine.Settings.RunMode == RunModes.Tray ? " or under Monitors in the tray icon's menu." : ".");
        }
        if (PauseInfoBar.Title == title && PauseInfoBar.Message == message && PauseInfoBar.IsOpen == title.Length > 0) return;
        PauseInfoBar.Title = title;
        PauseInfoBar.Message = message;
        PauseInfoBar.IsOpen = title.Length > 0;
        System.Windows.Automation.AutomationProperties.SetName(PauseInfoBar, title.Length > 0 ? $"{title}. {message}" : "Status");
    }

    private void AppsCanOpenHere_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not MonitorInfo m) return;
        _engine.SetMonitorTemporary(m, toggle.IsChecked != true);
    }

    private void ChangeMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row) return;
        // Read every choice first: saving the first one refreshes this panel.
        var fallback = AppFallbackCombo.SelectedItem as FallbackItem;
        var allowed = _allowedItems.Where(a => a.Checked).Select(a => a.Monitor).ToList(); // in the order shown
        string? place = PlaceCombo.SelectedIndex switch { 1 => PlaceModes.Last, 2 => PlaceModes.Maximized, _ => null };
        int? wait = WaitCombo.SelectedIndex switch { 1 => 1000, 2 => 3000, _ => null };
        int? correct = CorrectCombo.SelectedIndex == 1 ? 5000 : null;
        object? choice = TargetCombo.SelectedItem;
        bool never = choice as string == NeverMoveLabel;
        // Nothing ticked = any monitor, except monitors kept for chosen apps (ticking one is choosing it).
        if (allowed.Count == 0) allowed = _allowedItems.Select(a => a.Monitor).Where(m => !m.OnlyChosenApps).ToList();
        if (allowed.Count == 0) allowed = _allowedItems.Select(a => a.Monitor).ToList();
        // An app saved already gets "Place it" first, so its open windows are placed that way when they move.
        bool saved = _engine.Settings.Find(row.Key) != null;
        if (saved && !never) _engine.SetAppPlacement(row.Key, place, wait, correct);
        switch (choice)
        {
            case MonitorInfo m: _engine.Pin(row.Key, m, row.Name); break;
            case string s when s == NeverMoveLabel: _engine.Pin(row.Key, null, row.Name); break;
            case string s when s == LastUsedLabel:
                var now = _engine.Monitors.FirstOrDefault(m => m.DeviceName == row.CurrentDevice);
                _engine.SetFollowLastUsed(row.Key, allowed, now, row.Name);
                break;
            case string s when s == MouseLabel:
                _engine.SetFollowMouse(row.Key, allowed, row.Name);
                break;
            default: return;
        }
        if (!never && fallback is { KeepSaved: false })
            _engine.SetAppFallback(row.Key, fallback.Mode, fallback.Monitor);
        if (!saved && !never) _engine.SetAppPlacement(row.Key, place, wait, correct);
        Activate(); // moving a maximized window can bring it to the front; stay on top for the next choice
    }

    private void FallbackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || FallbackCombo.SelectedItem is not FallbackItem item || item.KeepSaved) return;
        var s = _engine.Settings;
        bool same = item.Mode == FallbackModes.Monitor
            ? s.Fallback == FallbackModes.Monitor && _engine.FindMonitor(s.FallbackMonitor)?.DeviceName == item.Monitor?.DeviceName
            : (s.Fallback ?? FallbackModes.Main) == (item.Mode ?? FallbackModes.Main);
        if (!same) _engine.SetDefaultFallback(item.Mode, item.Monitor);
    }

    private void ReturnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ReturnCombo.SelectedIndex < 0) return;
        _engine.SetMoveBackWhenMonitorReturns(ReturnCombo.SelectedIndex == 0);
    }

    private void SleepToggle_Click(object sender, RoutedEventArgs e) => _engine.SetRestoreAfterSleep(SleepToggle.IsChecked == true);

    private void KeepMainToggle_Click(object sender, RoutedEventArgs e) => _engine.SetKeepMainMonitor(KeepMainToggle.IsChecked == true);

    /// <summary>"Treat as switched off": No / Until I turn it back on / For 1 hour.</summary>
    private void SwitchedOff_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not MonitorInfo m) return;
        int choice = combo.SelectedIndex;
        if (choice < 0 || choice == m.SwitchedOffIndex) return; // (also when the card is first shown)
        _engine.SetMonitorSwitchedOff(m, choice > 0, choice == 2 ? Engine.OffForAnHour : null);
    }

    /// <summary>"What can open here": Any app / Only apps I choose.</summary>
    private void OpenHere_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not MonitorInfo m) return;
        int choice = combo.SelectedIndex;
        if (choice < 0 || choice == m.OpenHereIndex) return;
        _engine.SetMonitorOnlyChosenApps(m, choice == 1);
    }

    private async void MakeMain_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorInfo m) return;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Make {m.Name} your main monitor?",
            Content = "This changes the main monitor in Windows itself: the taskbar clock, the Start menu and many new windows go there, " +
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
                         "for example \"2 · TV\". If another monitor already has that number, it moves to this one. These only apply inside Monitor Window Control: " +
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

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row || _engine.Settings.Find(row.Key) is not AppRule rule) return;
        bool hasShortcut = ShortcutManager.Exists(rule);
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Forget {row.Name}?",
            Content = (rule.Pinned || rule.NeverMove ? "The monitor you chose for it is removed" : "Where it opens is forgotten") +
                      (hasShortcut ? ", and its launch shortcut is deleted." : ".") +
                      (_engine.Settings.LearnAutomatically && _engine.Settings.RunMode != RunModes.LaunchOnly
                          ? " It's learned again the next time you use it." : ""),
            PrimaryButtonText = "Forget", CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        ShortcutManager.Delete(rule); // its launch shortcut goes too
        _engine.Forget(row.Key);
        AppsList.Focus();
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
            _ = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Couldn't open the activity log",
                Content = $"It's the file {Paths.LogFile}. ({ex.Message})", CloseButtonText = "OK",
            }.ShowDialogAsync();
        }
    }
}

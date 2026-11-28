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

/// <summary>
/// One row: a saved app (learned / set by you), an app that's open right now but not saved yet, or (indented under its
/// app, <see cref="IsChild"/>) one of an app's rules for some of its windows. <see cref="Chip"/> says where the app opens
/// with another set of monitors.
/// </summary>
public sealed record AppRow(string Key, string Name, string Detail, string OpensOn, string Source, string? CurrentDevice, string? Hint = null,
    bool IsChild = false, string? Chip = null)
{
    /// <summary>Show the tip icon (an app that keeps moving itself back).</summary>
    public bool HasHint => Hint != null;

    /// <summary>Show the "With 2 monitors: …" chip.</summary>
    public bool HasChip => Chip != null;

    /// <summary>A rule for some windows sits indented under its app.</summary>
    public Thickness Indent => IsChild ? new Thickness(20, 0, 8, 0) : new Thickness(0, 0, 8, 0);

    /// <summary>What Narrator and other screen readers say for the row.</summary>
    public override string ToString() => $"{Name}, opens on {OpensOn}, {Source}" + (Hint == null ? "" : $". {Hint}") + (Chip == null ? "" : $". {Chip}");
}

/// <summary>
/// One line in Recent activity. <see cref="FromHelper"/>: it happened in the background helper (which undoes it).
/// </summary>
public sealed record ActivityRow(ActivityEntry Entry, bool FromHelper)
{
    public string Text => Entry.Text;

    /// <summary>"12:04 · Learned: you used it there" (and "Undone" once it's undone).</summary>
    public string Detail => string.Join(" · ", new[] { $"{Entry.Time:t}", Entry.Reason, Entry.Undone ? "Undone" : "" }.Where(s => s.Length > 0));

    public bool CanUndo => Entry.CanUndo && !Entry.Undone;
    public string UndoName => $"Undo: {Entry.Text}";

    public SymbolRegular Icon => Entry.Undone ? SymbolRegular.ArrowUndo24 : Entry.Kind switch
    {
        ActivityKinds.Moved => SymbolRegular.ArrowMove24,
        ActivityKinds.Learned => SymbolRegular.Lightbulb24,
        ActivityKinds.SetByYou => SymbolRegular.PersonEdit24,
        ActivityKinds.Skipped => SymbolRegular.ShieldError24,
        ActivityKinds.Back => SymbolRegular.ArrowHookUpLeft24,
        _ => SymbolRegular.Info24,
    };

    /// <summary>What Narrator and other screen readers say for the line.</summary>
    public override string ToString() => $"{Text}. {Detail}";
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
    private readonly PlacerLink _link;
    private readonly System.Windows.Threading.DispatcherTimer _openAppsTimer;
    // What the background helper said last (helper mode): its Recent activity and the shortcuts another app uses.
    private List<ActivityEntry> _helperRecent = new();
    private HashSet<string> _helperProblems = new();
    private int _tidyCount = -1; // "Move open windows to their monitors" preview (-1 = not known)
    private bool _statusBusy;
    private bool _updating;
    private string? _panelKey;   // which row the "Open it on" box was last filled for
    private string _panelState = "";

    public MainWindow(Engine engine, App app)
    {
        _engine = engine;
        _app = app;
        _link = new PlacerLink(engine);
        InitializeComponent();
        ThemeSupport.Follow(this); // light/dark, contrast themes, transparency effects and text size, live
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
                UpdateHelperBar();
                UpdatePauseInfo(); // (in helper mode, a game starting or ending is only seen here)
                UpdateNavStatus();
                if (PageGeneral.Visibility == Visibility.Visible) _ = RefreshStatusAsync();
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
        Deactivated += (_, _) => IdentifyOverlay.HideOutline();
        MonitorLayout.TileClicked += (m, _) => ShowMonitorCard(m);
        TargetLayout.TileClicked += (m, _) =>
        {
            if (TargetCombo.Items.OfType<MonitorInfo>().FirstOrDefault(x => x.DeviceName == m.DeviceName) is MonitorInfo item) TargetCombo.SelectedItem = item;
        };
        WelcomeLayout.TileClicked += (m, _) => _welcomeMonitors.FirstOrDefault(r => r.Device == m.DeviceName)?.NumberBox.Focus();
        AllowedLayout.TileClicked += (m, on) =>
        {
            var item = _allowedItems.FirstOrDefault(a => a.Monitor.DeviceName == m.DeviceName);
            if (item != null) { item.Checked = on; BuildAllowedRows(); }
        };
        VersionText.Text = $"Version {AppVersion} · free and open source (MIT licence)";
        SettingsFolderText.Text = Paths.Dir;
        NavList.SelectedIndex = 0;
        Refresh();
        UpdateHelperBar();
        ShowCrashIfAny();
        FillBackups();
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
        // Measure after layout: fill from where the list starts down to the bottom of the window.
        Dispatcher.BeginInvoke(() =>
        {
            if (PageApps.Visibility != Visibility.Visible || !AppsList.IsVisible) return;
            double top = AppsList.TranslatePoint(new Point(0, 0), PageScroller).Y + PageScroller.VerticalOffset;
            double room = PageScroller.ViewportHeight - top - 16;
            if (_wide != true) room -= SelectionPanel.ActualHeight + 24;  // the options card below the list
            double h = Math.Max(220, room);
            if (Math.Abs(AppsList.MaxHeight - h) > 1) AppsList.MaxHeight = h;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
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
        if (page == "Apps") FitAppsList();
        PageAbout.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
        IdentifyOverlay.HideOutline();
        if (page == "General" && _engine != null) _ = RefreshStatusAsync();
        if (page == "About" && _engine != null) FillBackups();
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
        IdentifyOverlay.CloseAll();
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
            var policy = _engine.Policy;
            EnabledToggle.IsEnabled = policy.Enabled == null;
            UnknownCombo.IsEnabled = policy.UnknownApps == null;
            RunModeCombo.IsEnabled = policy.RunMode == null;
            UpdatePolicyBars();
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
            LearnToggle.IsEnabled = !launchOnly && policy.LearnAutomatically == null;
            LearnHint.Text = launchOnly
                ? "Needs something running in the background to notice where you use apps; set apps' monitors on the Apps page instead."
                : "When you use an app on a monitor for a few seconds, that becomes its home. Only the window you're using counts.";
            UpdateRecent();
            UpdateTidyCard();
            UpdateHotKeysSection(launchOnly);
            UpdateNotices();

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
            foreach (var m in _engine.Monitors) m.CanMakeMain = policy.MakeMainAllowed;
            string sig = policy.MakeMainAllowed + string.Join("|", _engine.Monitors.Select(m => $"{m.DeviceName}={m.Label}/{m.Details}/{m.ShowTemporaryOption}{m.IsTemporary}" +
                                                                       $"/{m.ShowMonitorChoices}{m.SwitchedOffNote}{m.OpenHereIndex}/{m.ShowSplitOption}{m.SplitNote}"));
            if (sig != _monitorSig) { _monitorSig = sig; MonitorsList.ItemsSource = _engine.Monitors.ToList(); }
            MonitorLayout.SetMonitors(_engine.Monitors);
            UpdateKeepMainCard(launchOnly);
            UpdateSplitCards(launchOnly);
            UpdateDisconnectedList();
            UpdateSetupsSection();
            UpdateGroups();
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
                .SelectMany(r => new[] { ToRow(r, open.FirstOrDefault(o => string.Equals(o.App.Key, r.Key, StringComparison.OrdinalIgnoreCase))) }
                                 .Concat(WindowRuleRows(r)));
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
            var now = _engine.EffectiveRule(r.Key) ?? r; // (its own monitor with the monitors connected now, if it has one)
            var m = _engine.Resolve(now);
            opensOn = m == null ? $"{now.MonitorName ?? now.MonitorDevice} (not connected)"
                : m.IsSwitchedOff ? $"{m.Label} (switched off)" : m.Label;
        }
        var (name, detail) = Names(r.Key, r.DisplayName);
        if (!r.NeverMove && r.Place == PlaceModes.Maximized) opensOn += ", maximized";
        else if (!r.NeverMove && r.Place == PlaceModes.Last) opensOn += ", where you had it";
        string source = _engine.Policy.IsAppLocked(r.Key) ? "Set by your organization" : r.Pinned || r.NeverMove ? "Set by you" : "Learned";
        return new AppRow(r.Key, name, detail, opensOn, source,
                          open?.Monitor?.DeviceName, r.MovedItselfBack && !r.NeverMove ? StubbornTip : null, Chip: SetupChip(r));
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
        bool locked = row != null && _engine.Policy.IsAppLocked(row.Key); // where it opens is set by your organization
        ForgetButton.IsEnabled = rule != null && !locked;
        TargetCombo.IsEnabled = !locked;
        TargetLayout.IsEnabled = !locked;
        AllowedPanel.IsEnabled = !locked;
        AppPolicyNote.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        UpdateSetupNote(rule);
        UpdateWindowRules(row, rule, locked);
        SelectedAppName.Text = row == null ? "No app selected" : row.IsChild ? rule?.DisplayName ?? row.Detail : row.Name;
        SelectedAppDetail.Text = row == null ? "Select an app in the list." :
            string.Join(" · ", new[] { row.Detail, row.Source }.Where(x => !string.IsNullOrEmpty(x)));
        UpdateShortcutRow(row, rule);

        // Don't reset what the user is picking unless the selection or that app's setting changed.
        var s = _engine.Settings;
        string state = row == null ? "" :
            $"{row.Key}|{rule?.NeverMove}|{rule?.FollowLastUsed}|{rule?.FollowMouse}|{rule?.MonitorDevice}|" +
            string.Join(",", rule?.AllowedMonitors.Select(a => a.Device) ?? Enumerable.Empty<string>()) + "|" +
            $"{rule?.Fallback}|{rule?.FallbackMonitor?.Device}|{s.Fallback}|{s.FallbackMonitor?.Device}|" +
            $"{rule?.Place}|{rule?.Positions.Count}|{rule?.WaitMs}|{rule?.CorrectForMs}|{rule?.MovedItselfBack}|{_engine.CurrentSetupId}|" +
            $"{_engine.EffectiveRule(row.Key)?.MonitorDevice}|{rule?.WindowRules.Count}|" +
            string.Join(",", _engine.Monitors.Select(m => m.DeviceName + "=" + m.Label));
        if (row?.Key == _panelKey && state == _panelState) return;
        _panelKey = row?.Key; _panelState = state;

        var items = new List<object>(_engine.Monitors) { LastUsedLabel, MouseLabel, NeverMoveLabel };
        TargetCombo.ItemsSource = items;
        TargetLayout.SetMonitors(_engine.Monitors);
        AllowedLayout.SetMonitors(_engine.Monitors);

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
                : (object?)_engine.Resolve(_engine.EffectiveRule(rule.Key) ?? rule) ?? _engine.MainMonitor;
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
        // "Move its windows there now": for an app that's open, when a monitor is chosen.
        MoveNowButton.Visibility = AppsList.SelectedItem is AppRow { CurrentDevice: not null } && TargetCombo.SelectedItem is MonitorInfo
            ? Visibility.Visible : Visibility.Collapsed;
        var moves = TargetCombo.SelectedItem == null || choice == NeverMoveLabel ? Visibility.Collapsed : Visibility.Visible;
        AppFallbackPanel.Visibility = moves;
        PlacePanel.Visibility = moves;
        AdvancedExpander.Visibility = moves;
        // The monitor picture: one tile chosen for a monitor, ticked tiles for "Where I used it last" and "The monitor I'm using".
        bool list = choice is LastUsedLabel or MouseLabel;
        bool several = _engine.Monitors.Count > 1;
        TargetLayout.Visibility = several && !list ? Visibility.Visible : Visibility.Collapsed;
        AllowedLayout.Visibility = several && list ? Visibility.Visible : Visibility.Collapsed;
        TargetLayout.Select(new[] { TargetCombo.SelectedItem as MonitorInfo });
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
            box.Click += (_, _) =>
            {
                item.Checked = box.IsChecked == true;
                AllowedLayout.Select(_allowedItems.Where(a => a.Checked).Select(a => (MonitorInfo?)a.Monitor));
            };
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
        AllowedLayout.Select(_allowedItems.Where(a => a.Checked).Select(a => (MonitorInfo?)a.Monitor));
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
        KeepMainCard.Visibility = _engine.PhysicalMonitors.Count > 1 || s.KeepMainMonitor != null ? Visibility.Visible : Visibility.Collapsed;
        KeepMainToggle.IsChecked = s.KeepMainMonitor != null;
        KeepMainToggle.IsEnabled = !launchOnly && _engine.Policy.MakeMainAllowed;
        string kept = s.KeepMainMonitor == null ? ""
            : _engine.FindMonitor(s.KeepMainMonitor)?.Name ?? $"{s.KeepMainMonitor.Name ?? s.KeepMainMonitor.Device} (not connected)";
        KeepMainHint.Text = launchOnly
            ? "Needs something running in the background to notice Windows changing the main monitor."
            : !_engine.Policy.MakeMainAllowed
                ? "Your organization doesn't allow changing the main monitor."
            : s.KeepMainMonitor == null
                ? $"If Windows makes another monitor the main one (after a reconnect, an update or a TV waking up), change it back to {_engine.MainMonitor?.Name ?? "this one"}."
                : _engine.KeepMainStopped
                    ? $"Windows kept making another monitor main, so {kept} is no longer changed back. Turn this off and on to try again."
                    : $"{kept} stays the main monitor: if Windows makes another monitor main, it's changed back. To keep another one, press Make main on it.";
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshAppList(background: false);
        // Say how many apps the search found (Narrator reads it without leaving the search box).
        int n = (AppsList.ItemsSource as List<AppRow>)?.Count ?? 0;
        Announcer.Announce(this, n == 0 ? "No apps found" : n == 1 ? "1 app" : $"{n} apps");
    }

    private void AppsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete && ForgetButton.IsEnabled) { e.Handled = true; Forget_Click(sender, e); }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.ExitApp();

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) UpdateSelectionPanel();
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = EnabledToggle.IsChecked == true;
        _engine.SetEnabled(on);
        Announcer.Announce(this, on ? "Moving new windows again" : "Paused: new windows aren't moved");
    }

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
        if (title.Length > 0) Announcer.Announce(this, title);
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
        Announcer.Announce(this, choice is MonitorInfo target ? $"Saved: {row.Name} opens on {target.Name}"
                                 : never ? $"Saved: {row.Name} is never moved" : $"Saved: {row.Name}");
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

    /// <summary>Identify (Monitors page and welcome): each monitor shows its number and name on it for 3 seconds.</summary>
    private void Identify_Click(object sender, RoutedEventArgs e) => IdentifyOverlay.ShowLabels(_engine.Monitors);

    /// <summary>A tile in the monitor picture was selected: go to that monitor's card.</summary>
    private void ShowMonitorCard(MonitorInfo m)
    {
        if (MonitorsList.ItemContainerGenerator.ContainerFromItem(
                (MonitorsList.ItemsSource as IEnumerable<MonitorInfo>)?.FirstOrDefault(x => x.DeviceName == m.DeviceName)) is not FrameworkElement card) return;
        _lastKeyTick = Environment.TickCount; // (let the page scroll to it)
        card.BringIntoView();
    }

    /// <summary>Pointing at a monitor's card outlines that monitor, so you can see which one it is.</summary>
    private void MonitorCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonitorInfo m && _engine.Monitors.Count > 1) IdentifyOverlay.ShowOutline(m);
    }

    private void MonitorCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => IdentifyOverlay.HideOutline();

    /// <summary>Moving the keyboard focus onto a monitor's card outlines that monitor too (and marks it in the picture).</summary>
    private void MonitorCard_FocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorInfo m) return;
        if (e.NewValue is true)
        {
            MonitorLayout.Select(new MonitorInfo?[] { m });
            if (_engine.Monitors.Count > 1) IdentifyOverlay.ShowOutline(m);
        }
        else IdentifyOverlay.HideOutline();
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
        note.FontSize = 12 * ThemeSupport.TextScale;
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
        bool hasShortcut = ShortcutManager.Exists(rule) || rule.MoreShortcuts.Count > 0;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Forget {row.Name}?",
            Content = (rule.Pinned || rule.NeverMove ? "The monitor you chose for it is removed" : "Where it opens is forgotten") +
                      (hasShortcut ? ", and its launch shortcuts are deleted." : ".") +
                      (rule.WindowRules.Count > 0 ? " Its rules for some of its windows are removed too." : "") +
                      (_engine.Settings.LearnAutomatically && _engine.Settings.RunMode != RunModes.LaunchOnly
                          ? " It's learned again the next time you use it." : ""),
            PrimaryButtonText = "Forget", CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        ShortcutManager.Delete(rule); // its launch shortcut goes too
        _engine.Forget(row.Key);
        Announcer.Announce(this, $"Forgot {row.Name}");
        AppsList.Focus();
    }

    private async void ForgetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.Settings.Apps.Count == 0) return;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Forget all apps?",
            Content = $"Monitor Window Control will forget where all {_engine.Settings.Apps.Count} apps open, including the ones you set yourself. It will start learning again from scratch. A copy of your settings is kept first (About → Restore a backup).",
            PrimaryButtonText = "Forget all",
            CloseButtonText = "Cancel",
        };
        var result = await box.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        foreach (var rule in _engine.Settings.Apps.ToList()) ShortcutManager.Delete(rule);
        _engine.ForgetAll();
        Announcer.Announce(this, "Forgot all apps");
    }

    // ------------------------------------------------------------------ Recent activity, tidy up, keyboard shortcuts

    /// <summary>
    /// Asks whoever places windows (the helper in helper mode, else this app) for Recent activity, the keyboard shortcuts
    /// another app uses, and how many windows "Move open windows to their monitors" would move. Only while General shows.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshStatusAsync()
    {
        if (_statusBusy) return;
        _statusBusy = true;
        try
        {
            bool viaHelper = _link.ViaHelper;
            var reply = await _link.RunAsync(new HelperRequest { Command = HelperRequest.Status, IncludeMoved = TidyIncludeMoved.IsChecked == true });
            _tidyCount = reply.Error == null ? reply.Count : -1;
            if (viaHelper && reply.Error == null)
            {
                _helperRecent = reply.Recent;
                _helperProblems = reply.HotKeyProblems.ToHashSet(StringComparer.Ordinal);
            }
            else if (!viaHelper) { _helperRecent = new(); _helperProblems = new(); }
            UpdateRecent();
            UpdateTidyCard();
            UpdateHotKeyRows();
        }
        catch (Exception ex) { Log.Write("Unexpected error (status): " + ex.Message); }
        finally { _statusBusy = false; }
    }

    private string _recentSig = "";

    /// <summary>Recent activity: this app's events and the helper's, newest first (the last 10).</summary>
    private void UpdateRecent()
    {
        var rows = _engine.Recent.Select(e => new ActivityRow(e, false))
            .Concat(_helperRecent.Select(e => new ActivityRow(e, true)))
            .OrderByDescending(r => r.Entry.Time).Take(10).ToList();
        string sig = string.Join("|", rows.Select(r => $"{r.FromHelper}{r.Entry.Id}{r.Entry.Undone}{r.Entry.CanUndo}"));
        RecentPanel.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (sig == _recentSig) return;
        _recentSig = sig;
        RecentList.ItemsSource = rows;
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityRow row) return;
        if (sender is UIElement b) b.IsEnabled = false;
        string? error, stopKey, stopName;
        if (row.FromHelper)
        {
            if (!_link.ViaHelper) { error = "The background helper isn't running any more, so this can't be undone."; stopKey = stopName = null; }
            else
            {
                var reply = await _link.RunAsync(new HelperRequest { Command = HelperRequest.Undo, ActivityId = row.Entry.Id });
                (error, stopKey, stopName) = (reply.Error, reply.StopMovingKey, reply.StopMovingName);
                if (reply.Error == null || reply.Recent.Count > 0) _helperRecent = reply.Recent;
            }
        }
        else
        {
            var outcome = _engine.Undo(row.Entry.Id);
            (error, stopKey, stopName) = (outcome.Error, outcome.StopMovingKey, outcome.StopMovingName);
        }
        _recentSig = "";
        UpdateRecent();
        if (error != null)
            await new Wpf.Ui.Controls.MessageBox { Title = "Couldn't undo that", Content = error, CloseButtonText = "OK" }.ShowDialogAsync();
        else if (stopKey != null) AskStopMoving(stopKey, stopName ?? stopKey);
        _ = RefreshStatusAsync();
    }

    /// <summary>You undid moving this app's new windows twice: offer to stop moving it ("Never move").</summary>
    public async void AskStopMoving(string key, string name)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Stop moving {name}?",
            Content = $"You've moved {name} back twice. Monitor Window Control can leave its windows where Windows opens them from now on " +
                      "(Never move this app). You can change this on the Apps page at any time.",
            PrimaryButtonText = "Never move it", CloseButtonText = "Keep moving it",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        _engine.Pin(key, null, name);
    }

    /// <summary>The "Move open windows to their monitors" card: its description and the preview count on the button.</summary>
    private void UpdateTidyCard()
    {
        bool launchOnly = _engine.Settings.RunMode == RunModes.LaunchOnly;
        TidyIncludeMoved.Visibility = launchOnly ? Visibility.Collapsed : Visibility.Visible;
        TidyHint.Text = launchOnly
            ? "Puts each open window on its app's monitor, once. Apps set to Never move, minimized and fullscreen windows stay where they are. " +
              "With nothing running in the background, it can't tell which windows you moved yourself."
            : "Puts each open window on its app's monitor, once. Apps set to Never move, minimized and fullscreen windows, and windows you moved " +
              "yourself stay where they are. Windows that end up outside every monitor after monitors change are moved back by themselves.";
        string text = _tidyCount < 0 ? "Move windows" : _tidyCount == 0 ? "Nothing to move" : _tidyCount == 1 ? "Move 1 window" : $"Move {_tidyCount} windows";
        if (TidyButton.Content as string != text) TidyButton.Content = text;
        TidyButton.IsEnabled = _tidyCount != 0;
        System.Windows.Automation.AutomationProperties.SetName(TidyButton, _tidyCount == 0
            ? "Nothing to move: open windows are on their monitors" : text + " to their monitors");
    }

    private void TidyIncludeMoved_Click(object sender, RoutedEventArgs e) => _ = RefreshStatusAsync();

    private async void Tidy_Click(object sender, RoutedEventArgs e)
    {
        TidyButton.IsEnabled = false;
        var reply = await _link.RunAsync(new HelperRequest { Command = HelperRequest.Tidy, IncludeMoved = TidyIncludeMoved.IsChecked == true });
        if (_link.ViaHelper && reply.Error == null) _helperRecent = reply.Recent;
        Activate(); // moving windows can bring one to the front
        if (reply.Error != null)
            await new Wpf.Ui.Controls.MessageBox { Title = "Couldn't move the windows", Content = reply.Error, CloseButtonText = "OK" }.ShowDialogAsync();
        await RefreshStatusAsync();
    }

    /// <summary>Monitors page → Move its windows: a menu of the other monitors; the windows on this one move there once.</summary>
    private void MoveMonitorWindows_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.DataContext is not MonitorInfo from) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Move them to", IsEnabled = false });
        foreach (var to in _engine.Monitors.Where(m => m.DeviceName != from.DeviceName))
        {
            var item = new System.Windows.Controls.MenuItem { Header = to.Label };
            System.Windows.Automation.AutomationProperties.SetName(item, $"Move the windows on {from.Name} to {to.Name}");
            item.Click += async (_, _) => await GatherAsync(from, to);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Moves the windows on one monitor to another, once (no app's monitor changes), and says if nothing moved.</summary>
    private async System.Threading.Tasks.Task GatherAsync(MonitorInfo from, MonitorInfo to)
    {
        var reply = await _link.RunAsync(new HelperRequest { Command = HelperRequest.Gather, FromDevice = from.DeviceName, ToDevice = to.DeviceName });
        if (_link.ViaHelper && reply.Error == null) _helperRecent = reply.Recent;
        Activate();
        string? message = reply.Error ?? (reply.Count == 0
            ? $"There were no windows to move on {from.Name}. Minimized and fullscreen windows, apps set to Never move and apps that run as administrator stay where they are."
            : null);
        if (message != null)
            await new Wpf.Ui.Controls.MessageBox { Title = "No windows moved", Content = message, CloseButtonText = "OK" }.ShowDialogAsync();
        _recentSig = "";
        UpdateRecent();
    }

    /// <summary>Apps page → Move its windows there now: the app's open windows move to the chosen monitor once, without saving.</summary>
    private async void MoveNow_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row || TargetCombo.SelectedItem is not MonitorInfo to) return;
        var reply = await _link.RunAsync(new HelperRequest
        {
            Command = HelperRequest.MoveApp, AppKey = row.Key, AppName = row.Name, ToDevice = to.DeviceName,
        });
        if (_link.ViaHelper && reply.Error == null) _helperRecent = reply.Recent;
        Activate();
        string? message = reply.Error ?? (reply.Count == 0
            ? $"{row.Name}'s windows weren't moved. They may already be on {to.Name}, or be minimized, fullscreen or run as administrator."
            : null);
        if (message != null)
            await new Wpf.Ui.Controls.MessageBox { Title = "No windows moved", Content = message, CloseButtonText = "OK" }.ShowDialogAsync();
        RefreshAppList(background: false);
    }

    /// <summary>Tray menu → More choices…: shows the Apps page with this app selected.</summary>
    public void ShowApp(string key, string name)
    {
        NavList.SelectedIndex = 2;
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
        RefreshAppList(background: false);
        var row = (AppsList.ItemsSource as List<AppRow>)?.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (row == null) { Log.Write($"Couldn't find {name} in the list of apps."); return; }
        AppsList.SelectedItem = row;
        AppsList.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() => TargetCombo.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The Notifications card: its choices, which only work with the tray icon.</summary>
    private void UpdateNotices()
    {
        var s = _engine.Settings;
        bool tray = s.RunMode == RunModes.Tray;
        NoticesSection.Visibility = s.RunMode == RunModes.LaunchOnly ? Visibility.Collapsed : Visibility.Visible;
        NoticesHint.Text = tray
            ? "Show a notification from the tray icon when something happens in the background. All are off until you turn them on."
            : "Notifications come from the tray icon, so they need How it runs set to In the tray. Everything is listed under Recent activity below.";
        foreach (var (box, on) in new[] { (NoticeLearnedBox, s.Notices.Learned), (NoticeElsewhereBox, s.Notices.OpenedElsewhere),
                                          (NoticeBackBox, s.Notices.MonitorBack), (NoticeAdminBox, s.Notices.AdminApp) })
        {
            box.IsChecked = on;
            box.IsEnabled = tray;
        }
    }

    private void Notice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.Tag is string which) _engine.SetNotice(which, box.IsChecked == true);
    }

    /// <summary>The Keyboard shortcuts section (hidden when nothing runs in the background).</summary>
    private void UpdateHotKeysSection(bool launchOnly)
    {
        var hk = _engine.Settings.HotKeys;
        HotKeysSection.Visibility = launchOnly ? Visibility.Collapsed : Visibility.Visible;
        HotKeysToggle.IsChecked = hk.Enabled;
        bool oneMonitor = _engine.Monitors.Count < 2;
        HotKeysToggle.IsEnabled = !oneMonitor || hk.Enabled;
        HotKeysHint.Text = oneMonitor
            ? "Needs two or more monitors."
            : "Move the window you're using to another monitor with the keyboard: Windows key + Ctrl + Alt + a monitor's number, " +
              "or the Left or Right arrow key. Add Shift to also make it the app's monitor from now on.";
        HotKeysExpander.IsEnabled = hk.Enabled && !oneMonitor;
        UpdateHotKeyRows();
    }

    private string _hotKeySig = "";

    /// <summary>The words for a shortcut's action, for the "Change the keys" list.</summary>
    private string HotKeyTitle(string action)
    {
        int n = HotKeyActions.MonitorNumber(action);
        return n > 0 ? $"Send to monitor {n}"
            : action == HotKeyActions.Next ? "Send to the next monitor"
            : action == HotKeyActions.Previous ? "Send to the previous monitor"
            : "Move open windows to their monitors";
    }

    /// <summary>The shortcuts another app already uses (the helper's in helper mode).</summary>
    private IReadOnlyCollection<string> HotKeyProblems => _link.ViaHelper ? _helperProblems : _engine.HotKeyProblems;

    /// <summary>
    /// "Change the keys": a row per shortcut (one per connected monitor, next, previous, and tidy up) with its keys, a note
    /// when another app uses them, and a Change button. Rebuilt only when something in it changed.
    /// </summary>
    private void UpdateHotKeyRows()
    {
        var hk = _engine.Settings.HotKeys;
        var actions = HotKeyActions.All.Where(a => HotKeyActions.MonitorNumber(a) <= _engine.Monitors.Count).ToList();
        var problems = HotKeyProblems;
        string sig = string.Join("|", actions.Select(a =>
        {
            int n = HotKeyActions.MonitorNumber(a);
            return $"{a}={HotKeyActions.KeysFor(hk, a)}{problems.Contains(a)}{problems.Contains(a + "+Shift")}" +
                   (n > 0 ? _engine.MonitorForShortcut(n)?.Label : "");
        })) + hk.Enabled;
        if (sig == _hotKeySig) return;
        _hotKeySig = sig;

        var focusedAction = (System.Windows.Input.Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
        HotKeyRows.Children.Clear();
        foreach (var action in actions)
        {
            var (mods, key) = HotKeyActions.KeysFor(hk, action);
            int n = HotKeyActions.MonitorNumber(action);
            string title = HotKeyTitle(action);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            string heading = n > 0 && _engine.MonitorForShortcut(n) is MonitorInfo m ? $"{title}: {m.Label}" : title;
            text.Children.Add(new System.Windows.Controls.TextBlock { Text = heading, Style = (Style)FindResource("CardTitle") });
            string keys = HotKeyActions.Text(mods, key);
            string detail = key == 0 ? "No shortcut" : HotKeyActions.HasSaveVersion(action) ? $"{keys} (with Shift: also save)" : keys;
            text.Children.Add(new System.Windows.Controls.TextBlock { Text = detail, Style = (Style)FindResource("Secondary") });
            string? problem = problems.Contains(action) ? "In use by another app: choose other keys."
                : problems.Contains(action + "+Shift") ? "With Shift, these keys are in use by another app." : null;
            if (problem != null)
                text.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = problem, Style = (Style)FindResource("Secondary"),
                    Foreground = TryFindResource("SystemFillColorCriticalBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Firebrick,
                });
            row.Children.Add(text);
            var change = new Wpf.Ui.Controls.Button { Content = "Change", VerticalAlignment = VerticalAlignment.Center, Tag = action };
            System.Windows.Automation.AutomationProperties.SetName(change,
                $"Change the keys for {heading}. Now: {detail}" + (problem != null ? $". {problem}" : ""));
            change.Click += (_, _) => ChangeHotKey(action, heading);
            Grid.SetColumn(change, 1);
            row.Children.Add(change);
            HotKeyRows.Children.Add(row);
            if (action == focusedAction) Dispatcher.BeginInvoke(() => change.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void HotKeysToggle_Click(object sender, RoutedEventArgs e) => _engine.SetHotKeysEnabled(HotKeysToggle.IsChecked == true);

    /// <summary>
    /// Asks for new keys for one shortcut: Ctrl, Alt and the Windows key as check boxes and the key from a list (Shift is
    /// left out: it's the "also save" version). Keys another of these shortcuts uses aren't accepted.
    /// </summary>
    private async void ChangeHotKey(string action, string heading)
    {
        var (mods, key) = HotKeyActions.KeysFor(_engine.Settings.HotKeys, action);
        var win = new CheckBox { Content = "Windows key", IsChecked = (mods & HotKeyActions.Win) != 0, Margin = new Thickness(0, 0, 16, 0) };
        var ctrl = new CheckBox { Content = "Ctrl", IsChecked = (mods & HotKeyActions.Ctrl) != 0, Margin = new Thickness(0, 0, 16, 0) };
        var alt = new CheckBox { Content = "Alt", IsChecked = (mods & HotKeyActions.Alt) != 0 };
        var keyItems = new List<string> { "No shortcut" };
        keyItems.AddRange(HotKeyActions.Choices.Select(k => HotKeyActions.KeyName(k)!));
        int index = key == 0 ? 0 : Array.IndexOf(HotKeyActions.Choices, key) + 1;
        var keyBox = new ComboBox { ItemsSource = keyItems, SelectedIndex = Math.Max(0, index), Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(keyBox, "Key");

        var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        modifiers.Children.Add(win); modifiers.Children.Add(ctrl); modifiers.Children.Add(alt);
        var content = new StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = "Hold", Margin = new Thickness(0, 0, 0, 6) });
        content.Children.Add(modifiers);
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = "and press", Margin = new Thickness(0, 0, 0, 6) });
        content.Children.Add(keyBox);
        if (HotKeyActions.HasSaveVersion(action))
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "The same keys with Shift also make that monitor the app's monitor.", TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0), Style = (Style)FindResource("Secondary"),
            });

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Keys for {heading}", Content = content,
            PrimaryButtonText = "Save", SecondaryButtonText = "Use the default", CloseButtonText = "Cancel",
        };
        box.Loaded += (_, _) => keyBox.Focus();
        var result = await box.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.None) return;
        int newMods = 0, newKey = 0;
        if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary) (newMods, newKey) = HotKeyActions.DefaultKeys(action);
        else
        {
            newKey = keyBox.SelectedIndex <= 0 ? 0 : HotKeyActions.Choices[keyBox.SelectedIndex - 1];
            if (win.IsChecked == true) newMods |= HotKeyActions.Win;
            if (ctrl.IsChecked == true) newMods |= HotKeyActions.Ctrl;
            if (alt.IsChecked == true) newMods |= HotKeyActions.Alt;
        }
        string? problem = !HotKeyActions.IsValid(newMods, newKey)
            ? "A shortcut needs Ctrl, Alt or the Windows key, so it doesn't get in the way when you type."
            : _engine.HotKeyUsedBy(action, newMods, newKey) is string other
                ? $"{HotKeyActions.Text(newMods, newKey)} is already the shortcut for \"{HotKeyTitle(other)}\". Change that one first, or choose other keys."
                : null;
        if (problem != null)
        {
            await new Wpf.Ui.Controls.MessageBox { Title = "Those keys can't be used", Content = problem, CloseButtonText = "OK" }.ShowDialogAsync();
            return;
        }
        _engine.SetHotKey(action, newMods, newKey);
        _hotKeySig = "";
        UpdateHotKeyRows();
        if (_link.ViaHelper) _ = RefreshStatusAsync(); // (the helper picks the new keys up within a few seconds)
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

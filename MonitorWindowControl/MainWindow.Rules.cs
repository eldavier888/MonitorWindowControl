using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace MonitorWindowControl;

/// <summary>One of the selected app's rules for some of its windows (its position in the list, and the rule in words).</summary>
public sealed record WindowRuleItem(int Index, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One of the selected app's launch shortcuts (a file name in Start menu → Monitor Window Control).</summary>
public sealed record ShortcutItem(string File, bool IsUsual)
{
    public override string ToString() => File;
}

/// <summary>A set of monitors in "Your setups".</summary>
public sealed record SetupRow(string Id, string Title, string Detail, bool CanForget)
{
    public override string ToString() => $"{Title}. {Detail}";
}

/// <summary>An app group on the Apps page.</summary>
public sealed record GroupRow(AppGroup Group, string Name, string Detail, bool CanOpen, string ShortcutText, string ShortcutName)
{
    public override string ToString() => $"{Name}. {Detail}";
}

/// <summary>
/// The window's parts for rules for some windows, monitor setups, launch shortcuts for a chosen monitor, app groups
/// (Apps page), and "Disconnect from Windows" (Monitors page).
/// </summary>
public partial class MainWindow
{
    private const int KeepDisplaySeconds = 15;
    private string _disconnectedSig = "", _setupsSig = "", _groupsSig = "";

    /// <summary>A small text for a dialog (secondary when <paramref name="secondary"/>).</summary>
    private System.Windows.Controls.TextBlock DialogText(string text, double top = 0, bool secondary = false)
    {
        var block = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 6) };
        if (secondary) { block.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"); block.FontSize = 12 * ThemeSupport.TextScale; }
        return block;
    }

    private static Task ShowErrorAsync(string title, string message) =>
        new MessageBox { Title = title, Content = message, CloseButtonText = "OK" }.ShowDialogAsync();

    private static Task<MessageBoxResult> AskAsync(string title, string message, string yes) =>
        new MessageBox { Title = title, Content = message, PrimaryButtonText = yes, CloseButtonText = "Cancel" }.ShowDialogAsync();

    // ------------------------------------------------------------------ rules for some of an app's windows

    /// <summary>The app's rules for some of its windows, as rows indented under it in the list.</summary>
    private IEnumerable<AppRow> WindowRuleRows(AppRule r)
    {
        var (name, _) = Names(r.Key, r.DisplayName);
        foreach (var w in r.WindowRules)
        {
            string match = Engine.DescribeMatch(w);
            yield return new AppRow(r.Key, "Windows whose " + match, name, _engine.DescribeTarget(w), "Rule for some windows", null, IsChild: true);
        }
    }

    /// <summary>"Rules for some of its windows" in the selected app's options.</summary>
    private void UpdateWindowRules(AppRow? row, AppRule? rule, bool locked)
    {
        var items = rule?.WindowRules.Select((w, i) => new WindowRuleItem(i, $"Windows whose {Engine.DescribeMatch(w)}: {_engine.DescribeTarget(w)}")).ToList()
                    ?? new List<WindowRuleItem>();
        if (WindowRulesList.ItemsSource is not List<WindowRuleItem> old || !old.SequenceEqual(items)) WindowRulesList.ItemsSource = items;
        WindowRulesList.IsEnabled = !locked;
        AddWindowRuleButton.IsEnabled = rule != null && !locked && _engine.Monitors.Count > 0;
        WindowRulesHint.Text = row == null ? ""
            : rule == null ? "Save where it opens first; then you can add rules for some of its windows."
            : items.Count == 0 ? "Some of its windows can open somewhere else: for example windows whose title contains \"Meeting\", or one browser profile."
            : "These come before where the app opens, from the top. Learning never changes them.";
    }

    private async void AddWindowRule_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row || _engine.Settings.Find(row.Key) is not AppRule rule) return;
        var open = _engine.OpenWindowsOf(rule.Key);
        var profiles = open.Where(o => o.Profile != null).GroupBy(o => o.Profile!, StringComparer.OrdinalIgnoreCase)
                           .Select(g => new FallbackItem($"{WindowInfo.ProfileLabel(g.Key)} ({g.First().Title})", g.Key, null)).ToList();
        bool browser = profiles.Count > 0 || IsBrowserApp(rule.Key);

        var kinds = new List<FallbackItem>
        {
            new("Window title contains", WindowMatches.TitleContains, null),
            new("Window title starts with", WindowMatches.TitleStartsWith, null),
            new("Window title is", WindowMatches.TitleIs, null),
        };
        if (browser) kinds.Add(new("Browser profile is", WindowMatches.Profile, null));
        var kindBox = new ComboBox { ItemsSource = kinds, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(kindBox, "Which windows");
        var textBox = new ComboBox
        {
            IsEditable = true, ItemsSource = open.Select(o => o.Title).Distinct().ToList(), HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        AutomationProperties.SetName(textBox, "Text in the window title");
        var profileBox = new ComboBox { ItemsSource = profiles, SelectedIndex = profiles.Count > 0 ? 0 : -1, Margin = new Thickness(0, 6, 0, 0),
                                        HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(profileBox, "Browser profile");
        var profileNote = DialogText("Open a window of each profile first: profiles are recognized from their open windows.", 4, secondary: true);
        profileNote.Visibility = Visibility.Collapsed;
        var targets = new List<object>(_engine.Monitors) { "Never move these windows" };
        var targetBox = new ComboBox { ItemsSource = targets, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(targetBox, "Open these windows on");
        kindBox.SelectionChanged += (_, _) =>
        {
            bool profile = (kindBox.SelectedItem as FallbackItem)?.Mode == WindowMatches.Profile;
            textBox.Visibility = profile ? Visibility.Collapsed : Visibility.Visible;
            profileBox.Visibility = profile && profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            profileNote.Visibility = profile && profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        var content = new StackPanel { MinWidth = 380 };
        content.Children.Add(DialogText("Windows of this app whose"));
        content.Children.Add(kindBox);
        content.Children.Add(textBox);
        content.Children.Add(profileBox);
        content.Children.Add(profileNote);
        content.Children.Add(DialogText("Open on", 14));
        content.Children.Add(targetBox);
        content.Children.Add(DialogText("Capital letters don't matter. A title often changes right after a window opens, so it's checked again for a moment. " +
                                        "Other windows of the app open where the app does.", 14, secondary: true));
        var box = new MessageBox
        {
            Title = $"Add a rule for some windows of {rule.DisplayName}", Content = content,
            PrimaryButtonText = "Add", CloseButtonText = "Cancel",
        };
        box.Loaded += (_, _) => textBox.Focus();
        if (await box.ShowDialogAsync() != MessageBoxResult.Primary) return;

        string kind = (kindBox.SelectedItem as FallbackItem)?.Mode ?? WindowMatches.TitleContains;
        string text = kind == WindowMatches.Profile ? (profileBox.SelectedItem as FallbackItem)?.Mode ?? "" : (textBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            await ShowErrorAsync("The rule wasn't added", kind == WindowMatches.Profile
                ? "Choose a browser profile. Open a window of that profile first if it isn't listed."
                : "Type the text the window title should contain, start with or be.");
            return;
        }
        var newRule = new WindowRule { Match = kind, Text = text };
        if (targetBox.SelectedItem is MonitorInfo m) newRule.Monitor = new MonitorRef { Device = m.DeviceName, Name = m.FriendlyName, Hardware = MonitorHardware.Clean(m.Hardware) };
        else newRule.NeverMove = true;
        var rules = rule.WindowRules.ToList();
        rules.Add(newRule);
        if (!_engine.SetWindowRules(rule.Key, rules)) await ShowErrorAsync("The rule wasn't added", "This app isn't saved any more, or its monitor is set by your organization.");
        _panelState = ""; UpdateSelectionPanel();
    }

    /// <summary>Is this one of the browsers whose windows can be told apart by profile?</summary>
    private static bool IsBrowserApp(string key) =>
        key is "msedge.exe" or "chrome.exe" or "brave.exe" or "vivaldi.exe" or "opera.exe";

    private async void RemoveWindowRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WindowRuleItem item
            || AppsList.SelectedItem is not AppRow row || _engine.Settings.Find(row.Key) is not AppRule rule) return;
        if (await AskAsync("Remove this rule?", $"{item.Text}. These windows then open where {rule.DisplayName} does.", "Remove") != MessageBoxResult.Primary) return;
        var rules = rule.WindowRules.ToList();
        if (item.Index < 0 || item.Index >= rules.Count) return;
        rules.RemoveAt(item.Index);
        _engine.SetWindowRules(rule.Key, rules);
        _panelState = ""; UpdateSelectionPanel();
    }

    // ------------------------------------------------------------------ monitor setups

    /// <summary>"With 2 monitors: 2 · Side" for an app that opens somewhere else with another set of monitors.</summary>
    private string? SetupChip(AppRule r)
    {
        if (_engine.Settings.Setups.Count < 2) return null;
        var homes = _engine.SetupHomesOf(r.Key);
        if (homes.Count == 0) return null;
        var pick = homes.FirstOrDefault(h => h.Setup.Id == _engine.CurrentSetupId);
        if (pick.Setup == null) pick = homes[0];
        var (setup, home) = pick;
        string where = _engine.FindMonitor(home.Monitor)?.Name ?? home.Monitor.Name ?? home.Monitor.Device;
        return $"With {Engine.SetupName(setup)}: {where}" + (homes.Count > 1 ? $" (and {homes.Count - 1} more)" : "");
    }

    /// <summary>Under "Open it on": says when a choice now is for the monitors connected now only.</summary>
    private void UpdateSetupNote(AppRule? rule)
    {
        bool show = _engine.InOtherSetup && rule is { NeverMove: false, FollowLastUsed: false, FollowMouse: false };
        SetupNote.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var setup = _engine.Setups.FirstOrDefault(s => s.Id == _engine.CurrentSetupId);
        string name = setup == null ? "another set of monitors" : Engine.SetupName(setup);
        string usual = _engine.Resolve(rule!)?.Name ?? rule!.MonitorName ?? "its usual monitor";
        SetupNote.Text = $"You're using {name}, not your usual monitors. A monitor you choose and save now is used only with these monitors; " +
                         $"with your usual monitors, it still opens on {usual}.";
    }

    /// <summary>"Your setups" on the Monitors page, shown once a second set of monitors has been seen.</summary>
    private void UpdateSetupsSection()
    {
        var s = _engine.Settings;
        var rows = s.Setups.Count < 2 ? new List<SetupRow>() : s.Setups
            .OrderByDescending(x => x.Id == s.UsualSetup).ThenByDescending(x => x.LastSeenUtc)
            .Select(x =>
            {
                bool usual = x.Id == s.UsualSetup, now = x.Id == _engine.CurrentSetupId;
                string title = Engine.SetupName(x) + (usual ? " (usual)" : "");
                string detail = string.Join(", ", x.Monitors) + (now ? " · Connected now" : "");
                if (!usual) detail += x.Homes.Count switch
                {
                    0 => ". Apps open where they usually do.",
                    1 => ". 1 app opens somewhere else.",
                    int n => $". {n} apps open somewhere else.",
                };
                return new SetupRow(x.Id, title, detail, !usual);
            }).ToList();
        string sig = string.Join("|", rows);
        SetupsSection.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (sig == _setupsSig) return;
        _setupsSig = sig;
        SetupsList.ItemsSource = rows;
    }

    private async void RenameSetup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SetupRow row || _engine.Setups.FirstOrDefault(x => x.Id == row.Id) is not MonitorSetup setup) return;
        var nameBox = new Wpf.Ui.Controls.TextBox { Text = setup.Name ?? "", PlaceholderText = Engine.SetupName(new MonitorSetup { Monitors = setup.Monitors }), MinWidth = 320 };
        AutomationProperties.SetName(nameBox, "Name");
        var content = new StackPanel();
        content.Children.Add(DialogText("Name"));
        content.Children.Add(nameBox);
        content.Children.Add(DialogText($"Its monitors: {string.Join(", ", setup.Monitors)}. Leave the name empty to call it by how many monitors it has.", 12, secondary: true));
        var box = new MessageBox { Title = "Rename this setup", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        box.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        if (await box.ShowDialogAsync() == MessageBoxResult.Primary) _engine.RenameSetup(setup.Id, nameBox.Text);
    }

    private async void ForgetSetup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SetupRow row || _engine.Setups.FirstOrDefault(x => x.Id == row.Id) is not MonitorSetup setup) return;
        string apps = setup.Homes.Count == 0 ? "" : setup.Homes.Count == 1 ? " The app that opens somewhere else with it then opens where it usually does."
            : $" The {setup.Homes.Count} apps that open somewhere else with it then open where they usually do.";
        if (await AskAsync($"Forget {Engine.SetupName(setup)}?", "It's removed from Your setups." + apps + " It's added again the next time you use these monitors.",
                           "Forget") != MessageBoxResult.Primary) return;
        _engine.ForgetSetup(setup.Id);
    }

    // ------------------------------------------------------------------ launch shortcuts for a chosen monitor

    /// <summary>The launch shortcuts of the selected app, and the button that makes another one.</summary>
    private void UpdateShortcutRow(AppRow? row, AppRule? rule)
    {
        bool launchOnly = _engine.Settings.RunMode == RunModes.LaunchOnly;
        var items = new List<ShortcutItem>();
        if (rule != null)
        {
            if (ShortcutManager.Exists(rule)) items.Add(new ShortcutItem(rule.ShortcutFile!, true));
            items.AddRange(rule.MoreShortcuts.Where(ShortcutManager.FileExists).Select(f => new ShortcutItem(f, false)));
        }
        if (ShortcutsList.ItemsSource is not List<ShortcutItem> old || !old.SequenceEqual(items)) ShortcutsList.ItemsSource = items;
        ShortcutButton.IsEnabled = rule != null;
        if (row == null) { ShortcutStatus.Text = ""; return; }
        ShortcutStatus.Text = rule == null ? "Save where it opens first; then you can make a launch shortcut for it."
            : items.Count > 0 ? "Its shortcuts in Start menu → Monitor Window Control (opening it from one puts it on its monitor):"
            : launchOnly ? "Make a shortcut so this app opens on its monitor without anything running in the background."
            : "Optional: a shortcut that places the app even when nothing runs in the background, or opens it on another monitor.";
    }

    /// <summary>The Start menu shortcut exists even if only the desktop copy failed.</summary>
    private static bool ShortcutManagerStartMenuMade(string error) => error.StartsWith("The Start menu shortcut was created", StringComparison.Ordinal);

    /// <summary>"Create launch shortcut…": which monitor it opens the app on, and (programs only) a web address or file to open.</summary>
    private async void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row || _engine.Settings.Find(row.Key) is null) return;
        if (_engine.Settings.Find(row.Key)!.LaunchTarget == null) _engine.UpdateLaunchTarget(row.Key);
        var rule = _engine.Settings.Find(row.Key)!;
        string appName = ShortcutManager.ShortcutName(rule);
        bool canOpen = ShortcutManager.CanOpenWith(rule);

        var onItems = new List<object> { "Its usual monitor" };
        onItems.AddRange(_engine.Monitors);
        var onBox = new ComboBox { ItemsSource = onItems, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(onBox, "Open on");
        var openBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = canOpen ? "A web address or file, for example https://www.example.com" : "Not available for Store apps",
            IsEnabled = canOpen, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(openBox, "Open with (optional): a web address or file");
        string DefaultName() => onBox.SelectedItem is MonitorInfo m ? $"{appName} on {m.Name}" : ShortcutManager.FileName(rule, _engine.Settings.Apps);
        string suggested = DefaultName();
        var nameBox = new Wpf.Ui.Controls.TextBox { Text = suggested, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(nameBox, "Shortcut name");
        onBox.SelectionChanged += (_, _) =>
        {
            if (nameBox.Text == suggested) nameBox.Text = suggested = DefaultName(); // (unless you typed your own)
        };
        var desktop = new CheckBox { Content = "Also put it on the desktop", IsChecked = false, Margin = new Thickness(0, 12, 0, 0) };

        var content = new StackPanel { MinWidth = 380 };
        content.Children.Add(DialogText("Open on"));
        content.Children.Add(onBox);
        content.Children.Add(DialogText("Open with (optional)", 12));
        content.Children.Add(openBox);
        content.Children.Add(DialogText("Name", 12));
        content.Children.Add(nameBox);
        content.Children.Add(desktop);
        content.Children.Add(DialogText("It's added to Start menu → Monitor Window Control (right-click it there to pin it to the taskbar). " +
                                        "A monitor chosen here is used only when the app is opened from this shortcut; where it usually opens doesn't change. " +
                                        "Your existing shortcuts aren't changed.", 12, secondary: true));
        var box = new MessageBox { Title = $"Launch shortcut for {appName}", Content = content, PrimaryButtonText = "Create", CloseButtonText = "Cancel" };
        if (await box.ShowDialogAsync() != MessageBoxResult.Primary) return;

        var on = onBox.SelectedItem as MonitorInfo;
        string? open = string.IsNullOrWhiteSpace(openBox.Text) ? null : openBox.Text.Trim();
        if (open != null && LaunchOpen.Clean(open) == null)
        {
            await ShowErrorAsync("The shortcut wasn't created", "\"Open with\" must be a web address that starts with https:// or http://, or the full path of a file or folder (for example C:\\Videos\\Holiday.mp4).");
            return;
        }
        rule = _engine.Settings.Find(row.Key) ?? rule;
        bool usual = on == null && open == null && !ShortcutManager.Exists(rule)
                     && string.Equals(nameBox.Text.Trim(), ShortcutManager.FileName(rule, _engine.Settings.Apps), StringComparison.Ordinal);
        string? error;
        string file;
        if (usual)
        {
            error = ShortcutManager.Create(rule, _engine.Settings.Apps, desktop.IsChecked == true, out file);
            if (error == null || ShortcutManagerStartMenuMade(error)) _engine.SetShortcutFile(rule.Key, file);
        }
        else
        {
            var monitorRef = on == null ? null : new MonitorRef { Device = on.DeviceName, Name = on.FriendlyName, Hardware = MonitorHardware.Clean(on.Hardware) };
            string name = ShortcutManager.UniqueFileName(nameBox.Text.Trim().Length == 0 ? DefaultName() : nameBox.Text.Trim(), _engine.Settings);
            error = ShortcutManager.Create(rule, _engine.Settings.Apps, desktop.IsChecked == true, out file, monitorRef, open, name);
            if (error == null || ShortcutManagerStartMenuMade(error)) _engine.AddMoreShortcut(rule.Key, file);
        }
        if (error != null) await ShowErrorAsync("Shortcut", error);
        _panelState = ""; UpdateSelectionPanel();
    }

    private async void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutItem item
            || AppsList.SelectedItem is not AppRow row || _engine.Settings.Find(row.Key) is not AppRule rule) return;
        if (await AskAsync($"Remove the shortcut {item.File}?",
                "The shortcut in Start menu → Monitor Window Control (and on the desktop, if you made one) is deleted. " +
                "The app itself and where it opens aren't changed. If you pinned the shortcut to the taskbar, unpin it too.", "Remove") != MessageBoxResult.Primary) return;
        ShortcutManager.DeleteFile(item.File);
        if (item.IsUsual) _engine.SetShortcutFile(rule.Key, null);
        else _engine.RemoveMoreShortcut(rule.Key, item.File);
        _panelState = ""; UpdateSelectionPanel();
    }

    // ------------------------------------------------------------------ app groups

    /// <summary>The "App groups" section of the Apps page.</summary>
    private void UpdateGroups()
    {
        var s = _engine.Settings;
        var rows = s.Groups.Select(g =>
        {
            var parts = g.Apps.Select(a =>
            {
                var rule = s.Find(a.Key);
                string name = rule?.DisplayName ?? a.Key;
                if (rule == null) return name + " (forgotten)";
                if (rule.LaunchTarget == null) return name + " (can't be started yet)";
                return a.Monitor == null ? name : $"{name} on {_engine.FindMonitor(a.Monitor)?.Name ?? a.Monitor.Name ?? a.Monitor.Device}";
            }).ToList();
            bool shortcut = g.ShortcutFile != null && ShortcutManager.FileExists(g.ShortcutFile);
            string detail = parts.Count == 0 ? "No apps" : string.Join(", ", parts);
            if (shortcut) detail += $" · Start menu → Monitor Window Control → {g.ShortcutFile}";
            bool canOpen = g.Apps.Any(a => s.Find(a.Key)?.LaunchTarget != null);
            return new GroupRow(g, g.Name, detail, canOpen, shortcut ? "Remove shortcut" : "Create shortcut",
                                shortcut ? $"Remove the launch shortcut of {g.Name}" : $"Create a launch shortcut for {g.Name}");
        }).ToList();
        string sig = string.Join("|", rows.Select(r => $"{r.Group.Id}{r}{r.CanOpen}{r.ShortcutText}"));
        if (sig == _groupsSig) return;
        _groupsSig = sig;
        GroupsList.ItemsSource = rows;
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e) => await EditGroupAsync(null);

    private async void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow row) await EditGroupAsync(row.Group);
    }

    /// <summary>"New group…" and "Edit": a name, and a list of saved apps to tick, each with the monitor it opens on.</summary>
    private async Task EditGroupAsync(AppGroup? group)
    {
        var s = _engine.Settings;
        var apps = s.Apps.Where(a => a.LaunchTarget != null || group?.Apps.Any(g => string.Equals(g.Key, a.Key, StringComparison.OrdinalIgnoreCase)) == true)
                         .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (apps.Count == 0)
        {
            await ShowErrorAsync("No apps to add yet", "A group opens saved apps that Monitor Window Control knows how to start. Open an app once and save where it opens on the Apps page first.");
            return;
        }
        var nameBox = new Wpf.Ui.Controls.TextBox { Text = group?.Name ?? "", PlaceholderText = "For example Start Streaming", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(nameBox, "Group name");
        var rows = new List<(AppRule Rule, CheckBox Check, ComboBox On)>();
        var list = new StackPanel();
        foreach (var a in apps)
        {
            var member = group?.Apps.FirstOrDefault(g => string.Equals(g.Key, a.Key, StringComparison.OrdinalIgnoreCase));
            var check = new CheckBox { Content = a.DisplayName, IsChecked = member != null, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            var onItems = new List<object> { "Its usual monitor" };
            onItems.AddRange(_engine.Monitors);
            var on = new ComboBox { ItemsSource = onItems, Width = 190, Margin = new Thickness(8, 0, 0, 0) };
            on.SelectedItem = member?.Monitor is MonitorRef r && _engine.FindMonitor(r) is MonitorInfo m ? m : onItems[0];
            AutomationProperties.SetName(on, $"{a.DisplayName} opens on");
            on.PreviewMouseWheel += (sender, args) => { if (!on.IsDropDownOpen) args.Handled = true; };
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(on, Dock.Right);
            line.Children.Add(on);
            line.Children.Add(check);
            list.Children.Add(line);
            rows.Add((a, check, on));
        }
        var content = new StackPanel { MinWidth = 420 };
        content.Children.Add(DialogText("Name"));
        content.Children.Add(nameBox);
        content.Children.Add(DialogText("Apps (each opens on the monitor next to it)", 12));
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(DialogText("Apps that are open already are left as they are. Opening a group never closes apps.", 8, secondary: true));
        var box = new MessageBox
        {
            Title = group == null ? "New app group" : $"Edit {group.Name}", Content = content,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel",
        };
        box.Loaded += (_, _) => nameBox.Focus();
        if (await box.ShowDialogAsync() != MessageBoxResult.Primary) return;

        string name = nameBox.Text.Trim();
        var chosen = rows.Where(r => r.Check.IsChecked == true).ToList();
        if (name.Length == 0 || chosen.Count == 0)
        {
            await ShowErrorAsync("The group wasn't saved", name.Length == 0 ? "Give the group a name." : "Tick at least one app.");
            return;
        }
        var result = new AppGroup
        {
            Id = group?.Id ?? "", Name = name, ShortcutFile = group?.ShortcutFile,
            Apps = chosen.Select(r => new GroupApp
            {
                Key = r.Rule.Key,
                Monitor = r.On.SelectedItem is MonitorInfo m ? new MonitorRef { Device = m.DeviceName, Name = m.FriendlyName, Hardware = MonitorHardware.Clean(m.Hardware) } : null,
            }).ToList(),
        };
        _engine.SaveGroup(result);
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupRow row) return;
        bool shortcut = row.Group.ShortcutFile != null && ShortcutManager.FileExists(row.Group.ShortcutFile);
        if (await AskAsync($"Delete the group {row.Name}?", "The group is removed" + (shortcut ? ", and its launch shortcut is deleted." : ".") +
                           " The apps in it and where they open aren't changed.", "Delete") != MessageBoxResult.Primary) return;
        ShortcutManager.Delete(row.Group);
        _engine.DeleteGroup(row.Group.Id);
    }

    private async void OpenGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow row) await OpenGroupAsync(row.Group);
    }

    /// <summary>Opens an app group's apps, each on its monitor (also from the tray menu).</summary>
    public async Task OpenGroupAsync(AppGroup group)
    {
        if (HelperManager.OpenGroup(group) is string error) await ShowErrorAsync($"Couldn't open {group.Name}", error);
    }

    private async void GroupShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupRow row) return;
        var g = row.Group;
        if (g.ShortcutFile != null && ShortcutManager.FileExists(g.ShortcutFile))
        {
            if (await AskAsync($"Remove the launch shortcut of {g.Name}?",
                    "The shortcut in Start menu → Monitor Window Control (and on the desktop, if you made one) is deleted. The group isn't changed.",
                    "Remove") != MessageBoxResult.Primary) return;
            ShortcutManager.Delete(g);
            _engine.SetGroupShortcutFile(g.Id, null);
            return;
        }
        var desktop = new CheckBox { Content = "Also put it on the desktop", IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
        var content = new StackPanel();
        content.Children.Add(DialogText($"This adds \"{g.Name}\" to Start menu → Monitor Window Control. Opening it starts each app of the group on its monitor. " +
                                        "Tip: right-click it there and choose \"Pin to Start\" or \"Pin to taskbar\"."));
        content.Children.Add(desktop);
        if (await new MessageBox { Title = $"Launch shortcut for {g.Name}", Content = content, PrimaryButtonText = "Create", CloseButtonText = "Cancel" }
                .ShowDialogAsync() != MessageBoxResult.Primary) return;
        var forName = new AppGroup { Id = g.Id, Name = g.Name, Apps = g.Apps, ShortcutFile = null };
        string? error = ShortcutManager.CreateForGroup(forName, _engine.Settings, desktop.IsChecked == true, out string file);
        if (error == null || ShortcutManagerStartMenuMade(error)) _engine.SetGroupShortcutFile(g.Id, file);
        if (error != null) await ShowErrorAsync("Shortcut", error);
    }

    // ------------------------------------------------------------------ disconnect a monitor from Windows

    /// <summary>Monitors that are plugged in but disconnected from Windows, each with Connect.</summary>
    private void UpdateDisconnectedList()
    {
        var list = DisplayTopology.ListDisconnected();
        string sig = string.Join("|", list.Select(d => $"{d.Name}/{d.Adapter.LowPart}/{d.TargetId}"));
        if (sig == _disconnectedSig) return;
        _disconnectedSig = sig;
        DisconnectedList.ItemsSource = list;
    }

    private async void DisconnectMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonitorInfo m) await DisconnectMonitorAsync(m);
    }

    private async void ConnectMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DisplayTopology.Disconnected d) await ConnectMonitorAsync(d);
    }

    /// <summary>"Disconnect from Windows" (Monitors page and tray menu): asks first, then offers to keep it for 15 seconds.</summary>
    public async Task DisconnectMonitorAsync(MonitorInfo m)
    {
        if (DisplayTopology.WhyNotDisconnect(m, _engine.Monitors) is string why) { await ShowErrorAsync($"{m.Name} can't be disconnected", why); return; }
        if (await AskAsync($"Disconnect {m.Name} from Windows?",
                "Windows stops using this monitor, like \"Disconnect this display\" in Windows Settings → Display. It stays plugged in but goes dark, " +
                "and its windows move to your other monitors. They come back when you connect it again: here (it's listed under your monitors), " +
                $"or in Windows Settings. If something looks wrong, the change is undone after {KeepDisplaySeconds} seconds unless you keep it.",
                "Disconnect") != MessageBoxResult.Primary) return;
        if (DisplayTopology.Disconnect(m, _engine.Monitors, out var before) is string error)
        {
            await ShowErrorAsync($"Couldn't disconnect {m.Name}", error);
            return;
        }
        await KeepOrRevertAsync(before);
    }

    /// <summary>"Connect" for a monitor that's disconnected from Windows, then offers to keep it for 15 seconds.</summary>
    internal async Task ConnectMonitorAsync(DisplayTopology.Disconnected d)
    {
        if (DisplayTopology.Connect(d, out var before) is string error)
        {
            await ShowErrorAsync($"Couldn't connect {d.Name}", error + " You can also connect it in Windows Settings → Display.");
            return;
        }
        await KeepOrRevertAsync(before);
    }

    /// <summary>
    /// "Keep these display settings?", like Windows asks after changing the resolution: without an answer within 15
    /// seconds (or with Revert), the previous display settings are put back exactly.
    /// </summary>
    private async Task KeepOrRevertAsync(DisplayTopology.Snapshot? before)
    {
        if (before == null) return;
        int left = KeepDisplaySeconds;
        string Message() => $"If you don't keep them, the previous display settings come back in {left} second{(left == 1 ? "" : "s")}.";
        var text = DialogText(Message());
        var box = new MessageBox { Title = "Keep these display settings?", Content = text, PrimaryButtonText = "Keep changes", CloseButtonText = "Revert" };
        using var timeUp = new System.Threading.CancellationTokenSource();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            left--;
            text.Text = Message();
            if (left > 0) return;
            timer.Stop();
            timeUp.Cancel();           // no answer counts as Revert…
            ((Window)box).Close();     // …and the question goes away
        };
        timer.Start();
        var result = MessageBoxResult.None;
        try { result = await box.ShowDialogAsync(true, timeUp.Token); }
        catch (OperationCanceledException) { }
        finally { timer.Stop(); }
        if (result == MessageBoxResult.Primary && !timeUp.IsCancellationRequested) { Log.Write("Kept the new display settings."); return; }
        if (DisplayTopology.Restore(before) is string error)
            await ShowErrorAsync("Couldn't put the previous display settings back", error + " You can change them in Windows Settings → Display.");
    }
}

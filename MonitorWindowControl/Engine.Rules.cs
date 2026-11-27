using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MonitorWindowControl;

/// <summary>
/// The engine's per-window choices, on top of each app's own choice: a launch shortcut's monitor for this once, rules
/// for some windows of an app (title or browser profile), and each monitor setup's own monitors for apps. Also app
/// groups and launch shortcuts' extra files, which are only settings here.
/// </summary>
public sealed partial class Engine
{
    private const int LaunchOnceSeconds = 30;     // a launch shortcut's monitor applies to its app's windows this long…
    private const int LaunchOnceQuietMs = 4000;   // …or this long after its first window was placed (splash, then main window)
    private const int LaunchRequestMaxAgeSeconds = 60; // an older launch.json is from another time
    private const int TitleSettleMs = 1500;       // a new window's title may still change this long (blank, then "New Tab")
    private const int MaxSetups = 12;             // "Your setups" keeps at most this many

    // "Open on" of a launch shortcut or group: the app's next windows open there, this once (in memory only).
    private readonly Dictionary<string, (MonitorRef Monitor, DateTime Until)> _launchOnce = new(StringComparer.OrdinalIgnoreCase);
    private EventWaitHandle? _launchEvent;
    private RegisteredWaitHandle? _launchWait;

    // The monitor setup connected now (see SetupIdOf), and the last one noted in "Your setups".
    private string _setupId = "";
    private List<string> _setupMonitorNames = new();
    private string? _notedSetup;

    // ------------------------------------------------------------------ the choice for one new window

    /// <summary>
    /// The choice for one new window of an app: a launch shortcut's monitor (this once), else the first of its rules
    /// for some windows that matches, else the app's own choice (with this monitor setup's own monitor, if it has one).
    /// <paramref name="note"/> says why in plain words when it isn't the app's own choice.
    /// </summary>
    private AppRule? RuleForWindow(AppIdentity app, AppRule? rule, IntPtr hwnd, string title, out string? note)
    {
        note = null;
        if (_launchOnce.TryGetValue(app.Key, out var once))
        {
            if (DateTime.UtcNow <= once.Until)
            {
                note = "Opened from a launch shortcut for this monitor";
                return OverrideRule(app, rule, once.Monitor, never: false);
            }
            _launchOnce.Remove(app.Key);
        }
        if (rule != null && MatchingWindowRule(rule, hwnd, title) is WindowRule w)
        {
            note = $"Rule for some of its windows: {DescribeMatch(w)}";
            return OverrideRule(app, rule, w.Monitor, w.NeverMove);
        }
        return WithSetupHome(rule);
    }

    /// <summary>A copy of the app's choice that opens it on one monitor ("set by you"), or never moves it.</summary>
    private static AppRule OverrideRule(AppIdentity app, AppRule? rule, MonitorRef? monitor, bool never)
    {
        var r = rule == null ? new AppRule { Key = app.Key, DisplayName = app.DisplayName } : CopyRule(rule);
        r.Pinned = true;
        r.NeverMove = never;
        r.FollowLastUsed = false;
        r.FollowMouse = false;
        r.AllowedMonitors = new();
        r.MonitorDevice = never ? null : monitor?.Device;
        r.MonitorName = never ? null : monitor?.Name;
        r.MonitorHardware = never ? null : monitor?.Hardware;
        r.WindowRules = new();
        return r;
    }

    /// <summary>A placed window of an app opened from a launch shortcut: its monitor still applies briefly (splash, then main window).</summary>
    private void LaunchWindowPlaced(string key)
    {
        if (!_launchOnce.TryGetValue(key, out var once)) return;
        var quiet = DateTime.UtcNow.AddMilliseconds(LaunchOnceQuietMs);
        if (quiet < once.Until) _launchOnce[key] = (once.Monitor, quiet);
    }

    /// <summary>The first of the app's rules for some windows that this window matches, or null.</summary>
    private static WindowRule? MatchingWindowRule(AppRule rule, IntPtr hwnd, string title)
    {
        if (rule.WindowRules.Count == 0) return null;
        string? profile = rule.WindowRules.Any(w => w.Match == WindowMatches.Profile) ? WindowInfo.BrowserProfileOf(hwnd) : null;
        return rule.WindowRules.FirstOrDefault(w => w.Matches(title, profile));
    }

    /// <summary>
    /// A new window of an app with rules for window titles, whose title doesn't match yet: titles often change right
    /// after a window opens (blank, then "New Tab"), so it's checked again for up to 1.5 s before the app's own choice
    /// is used.
    /// </summary>
    private static bool WaitForTitle(AppRule? rule, IntPtr hwnd, DateTime firstSeen, DateTime now) =>
        rule != null && rule.WindowRules.Any(w => w.Match != WindowMatches.Profile)
        && firstSeen != default && now < firstSeen.AddMilliseconds(TitleSettleMs)
        && MatchingWindowRule(rule, hwnd, WindowInfo.GetTitle(hwnd)) == null;

    /// <summary>"title contains "Work"", "browser profile Profile 1".</summary>
    public static string DescribeMatch(WindowRule w) => w.Match switch
    {
        WindowMatches.TitleStartsWith => $"title starts with \"{w.Text}\"",
        WindowMatches.TitleIs => $"title is \"{w.Text}\"",
        WindowMatches.Profile => $"browser profile {WindowInfo.ProfileLabel(w.Text)}",
        _ => $"title contains \"{w.Text}\"",
    };

    /// <summary>Where a rule for some windows opens them, in words: "2 · Side", "Never moved".</summary>
    public string DescribeTarget(WindowRule w)
    {
        if (w.NeverMove || w.Monitor == null) return "Never moved";
        var m = FindMonitor(w.Monitor);
        return m == null ? $"{w.Monitor.Name ?? w.Monitor.Device} (not connected)" : m.Label;
    }

    /// <summary>Does this window of an app match one of its rules for some windows? (Those windows teach nothing.)</summary>
    private bool MatchesWindowRule(AppRule? rule, IntPtr hwnd) =>
        rule is { WindowRules.Count: > 0 } && MatchingWindowRule(rule, hwnd, WindowInfo.GetTitle(hwnd)) != null;

    /// <summary>The titles (and browser profiles) of an app's windows that are open now, for "Add a rule for some of its windows".</summary>
    public List<(string Title, string? Profile)> OpenWindowsOf(string key)
    {
        var list = new List<(string, string?)>();
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            if (WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow || !WindowInfo.IsOnCurrentDesktop(h)) continue;
            var app = WindowInfo.Identify(h);
            if (app == null || !string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            string title = WindowInfo.GetTitle(h).Trim();
            if (title.Length > 0) list.Add((title, WindowInfo.BrowserProfileOf(h)));
        }
        return list;
    }

    /// <summary>Saves an app's rules for some of its windows (the app must be saved already). Returns false if it isn't.</summary>
    public bool SetWindowRules(string key, IReadOnlyList<WindowRule> rules)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null || Policy.IsAppLocked(key)) return false;
        rule.WindowRules = rules.ToList();
        rule.UpdatedUtc = DateTime.UtcNow;
        Save();
        Log.Write($"Rules for some windows of {rule.DisplayName}: " +
                  (rules.Count == 0 ? "none" : string.Join("; ", rules.Select(w => $"{DescribeMatch(w)} → {DescribeTarget(w)}"))) + $"  [{key}]");
        Changed?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ launch shortcuts for a chosen monitor

    /// <summary>The app's next windows open on this monitor, this once (launch shortcut or group in this process).</summary>
    public void OpenOnceOn(string key, MonitorRef monitor) =>
        _launchOnce[key] = (monitor, DateTime.UtcNow.AddSeconds(LaunchOnceSeconds));

    /// <summary>The program that places windows listens for launch shortcuts that choose a monitor (nothing runs until one does).</summary>
    private void ListenForLaunches()
    {
        if (_launchWait != null) return;
        try
        {
            _launchEvent = new EventWaitHandle(false, EventResetMode.AutoReset, HelperChannel.LaunchEventName);
            _launchWait = ThreadPool.RegisterWaitForSingleObject(_launchEvent, (_, _) => _host.Post(ReadLaunchRequest),
                                                                 null, Timeout.Infinite, false);
        }
        catch (Exception ex)
        {
            Log.Write("Launch shortcuts for a chosen monitor won't work while this runs: " + ex.Message);
            _launchEvent?.Dispose(); _launchEvent = null;
        }
    }

    private void StopListeningForLaunches()
    {
        _launchWait?.Unregister(null); _launchWait = null;
        _launchEvent?.Dispose(); _launchEvent = null;
        _launchOnce.Clear();
    }

    /// <summary>A launch shortcut said it opens apps on chosen monitors: note them (read once, on the engine's thread).</summary>
    private void ReadLaunchRequest()
    {
        if (_disposed || !IsActive) return;
        var r = HelperChannel.ReadLaunch();
        if (r == null || DateTime.UtcNow - r.CreatedUtc > TimeSpan.FromSeconds(LaunchRequestMaxAgeSeconds)) return;
        foreach (var item in r.Items.Where(i => i != null && !string.IsNullOrWhiteSpace(i.Key) && i.Monitor != null))
        {
            OpenOnceOn(item.Key, item.Monitor);
            Log.Write($"Launch shortcut: the next window of {item.Key} opens on {item.Monitor.Name ?? item.Monitor.Device}, this once.");
        }
    }

    /// <summary>Notes another launch shortcut of an app (one for a chosen monitor, or with a web address or file).</summary>
    public void AddMoreShortcut(string key, string file)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null || rule.MoreShortcuts.Contains(file, StringComparer.OrdinalIgnoreCase)) return;
        rule.MoreShortcuts.Add(file);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Forgets one of an app's other launch shortcuts (after its file was deleted).</summary>
    public void RemoveMoreShortcut(string key, string file)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null || rule.MoreShortcuts.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ app groups

    /// <summary>Adds or changes an app group (matched by its id). Returns its id.</summary>
    public string SaveGroup(AppGroup group)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (string.IsNullOrEmpty(group.Id)) group.Id = Guid.NewGuid().ToString("N");
        int i = Settings.Groups.FindIndex(g => g.Id == group.Id);
        if (i >= 0) { group.ShortcutFile ??= Settings.Groups[i].ShortcutFile; Settings.Groups[i] = group; }
        else Settings.Groups.Add(group);
        Save();
        Log.Write($"App group \"{group.Name}\": {string.Join(", ", group.Apps.Select(a => a.Key + (a.Monitor == null ? "" : " on " + (a.Monitor.Name ?? a.Monitor.Device))))}.");
        Changed?.Invoke();
        return group.Id;
    }

    /// <summary>Removes an app group (its shortcut is deleted by the caller).</summary>
    public void DeleteGroup(string id)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.Groups.RemoveAll(g => g.Id == id) == 0) return;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Notes (or clears) the file name of a group's launch shortcut.</summary>
    public void SetGroupShortcutFile(string id, string? file)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var g = Settings.Groups.FirstOrDefault(x => x.Id == id);
        if (g == null) return;
        g.ShortcutFile = file;
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ monitor setups

    /// <summary>
    /// The name of a set of monitors: the sorted model ids of the monitors (temporary ones don't count), so the same
    /// monitors at home and at work make the same setup, wherever they're plugged in.
    /// </summary>
    private static string SetupIdOf(IEnumerable<MonitorInfo> monitors) =>
        string.Join("+", monitors.SelectMany(m => m.Targets)
                                 .Select(t => string.IsNullOrEmpty(t.Model) ? t.FriendlyName : t.Model!)
                                 .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Works out which setup is connected now (called whenever monitors are read).</summary>
    private void UpdateSetupId(IReadOnlyList<MonitorInfo> list)
    {
        var real = list.Where(m => !m.IsTemporary).ToList();
        _setupId = real.Count == 0 ? "" : SetupIdOf(real);
        _setupMonitorNames = real.SelectMany(m => m.Targets).Select(t => t.FriendlyName).ToList();
    }

    /// <summary>The setups seen so far ("Your setups"; shown once there are two).</summary>
    public IReadOnlyList<MonitorSetup> Setups => Settings.Setups;

    /// <summary>The id of the setup connected now.</summary>
    public string CurrentSetupId => _setupId;

    /// <summary>True while a setup other than the usual one is connected: choices made now are for these monitors only.</summary>
    public bool InOtherSetup => Settings.UsualSetup != null && _setupId.Length > 0 && _setupId != Settings.UsualSetup
                                && Settings.Setups.Count > 1;

    private MonitorSetup? SetupById(string id) => Settings.Setups.FirstOrDefault(s => s.Id == id);

    /// <summary>"Home", or "3 monitors" for a setup you haven't named.</summary>
    public static string SetupName(MonitorSetup s)
    {
        if (!string.IsNullOrWhiteSpace(s.Name)) return s.Name!;
        int n = Math.Max(1, s.Monitors.Count);
        return n == 1 ? "1 monitor" : $"{n} monitors";
    }

    /// <summary>
    /// Notes the setup connected now in "Your setups" once monitors have settled (the first one seen is the usual one).
    /// <paramref name="force"/> skips the wait (at startup, when no setup is known yet).
    /// </summary>
    private void NoteSetup(bool force = false)
    {
        if (_setupId.Length == 0 || _setupId == _notedSetup) return;
        if (IsRemoteSession || HasDuplicatedMonitors) return;
        if (!force && DateTime.UtcNow - _lastMonitorChangeUtc < TimeSpan.FromSeconds(MonitorSettleSeconds)) return;
        _notedSetup = _setupId;
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (!EnsureSetup(out _)) return;
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// The current setup's entry, added if it's new (and made the usual one if it's the first). Returns true if
    /// something changed that needs saving. Call it holding the settings lock.
    /// </summary>
    private bool EnsureSetup(out MonitorSetup? setup)
    {
        setup = null;
        if (_setupId.Length == 0) return false;
        bool changed = false;
        setup = SetupById(_setupId);
        if (setup == null)
        {
            setup = new MonitorSetup { Id = _setupId, Monitors = _setupMonitorNames.ToList() };
            Settings.Setups.Add(setup);
            changed = true;
            Log.Write($"New monitor setup: {string.Join(", ", setup.Monitors)}.");
            TrimSetups();
        }
        else if (DateTime.UtcNow - setup.LastSeenUtc > TimeSpan.FromDays(1) || !setup.Monitors.SequenceEqual(_setupMonitorNames))
        {
            setup.LastSeenUtc = DateTime.UtcNow;
            setup.Monitors = _setupMonitorNames.ToList();
            changed = true;
        }
        if (Settings.UsualSetup == null) { Settings.UsualSetup = _setupId; changed = true; }
        return changed;
    }

    /// <summary>Keeps "Your setups" short: drops the oldest setups that have no monitors of their own for apps.</summary>
    private void TrimSetups()
    {
        while (Settings.Setups.Count > MaxSetups)
        {
            var old = Settings.Setups.Where(s => s.Id != Settings.UsualSetup && s.Id != _setupId)
                                     .OrderBy(s => s.Homes.Count > 0).ThenBy(s => s.LastSeenUtc).FirstOrDefault();
            if (old == null) return;
            Settings.Setups.Remove(old);
        }
    }

    /// <summary>The app's own monitor in the setup connected now (only when that isn't the usual setup).</summary>
    private SetupHome? CurrentSetupHome(string key) =>
        InOtherSetup ? SetupById(_setupId)?.Homes.FirstOrDefault(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>
    /// An app's choice as it applies now: when the setup connected now has its own monitor for the app (and the app
    /// opens on one monitor), a copy pointed at that monitor. Otherwise the choice itself.
    /// </summary>
    private AppRule? WithSetupHome(AppRule? rule)
    {
        if (rule == null || rule.NeverMove || rule.FollowLastUsed || rule.FollowMouse) return rule;
        var home = CurrentSetupHome(rule.Key);
        if (home == null) return rule;
        var copy = CopyRule(rule);
        copy.MonitorDevice = home.Monitor.Device;
        copy.MonitorName = home.Monitor.Name;
        copy.MonitorHardware = home.Monitor.Hardware;
        copy.Pinned = rule.Pinned || home.Pinned;
        return copy;
    }

    /// <summary>An app's choice as it applies with the monitors connected now (for the Apps page).</summary>
    public AppRule? EffectiveRule(string key) => WithSetupHome(Settings.Find(key));

    /// <summary>The setups (other than the usual one) where an app has its own monitor, with that monitor.</summary>
    public List<(MonitorSetup Setup, SetupHome Home)> SetupHomesOf(string key) =>
        Settings.Setups.Where(s => s.Id != Settings.UsualSetup)
            .SelectMany(s => s.Homes.Where(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)).Select(h => (s, h)))
            .ToList();

    /// <summary>
    /// "Set by you" while another setup than the usual one is connected: for an app that opens on one monitor, the
    /// monitor is saved for this setup only (and its open windows move there). Returns false when the app's own choice
    /// should change instead (it isn't saved yet, doesn't open on one monitor, or it's the same monitor as usual).
    /// Call it holding the settings lock.
    /// </summary>
    private bool PinForSetup(string key, MonitorInfo monitor)
    {
        if (!InOtherSetup) return false;
        var rule = Settings.Find(key);
        if (rule == null || rule.NeverMove || rule.FollowLastUsed || rule.FollowMouse) return false;
        EnsureSetup(out var setup);
        if (setup == null) return false;
        setup.Homes.RemoveAll(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));
        if (Resolve(rule)?.DeviceName == monitor.DeviceName) { Save(); return false; } // the same as usual: no exception needed
        setup.Homes.Add(new SetupHome { Key = rule.Key, Monitor = RefTo(monitor), Pinned = true });
        Save();
        ResetStreak();
        var undo = new UndoData();
        undo.Windows.AddRange(MoveOpenWindows(rule.Key, WithSetupHome(rule), monitor, "set by you, for these monitors"));
        string name = SetupName(setup);
        Record(ActivityKinds.SetByYou, $"{rule.DisplayName} opens on {monitor.Name} with {name}", $"Set by you, for {name}", undo,
               $"Set by you: {rule.DisplayName} opens on {monitor.Name} while {name} are connected.  [{rule.Key} {monitor.DeviceName} setup {setup.Id}]");
        return true;
    }

    /// <summary>
    /// Learning while another setup than the usual one is connected: the monitor is learned for this setup only.
    /// A monitor you set for this setup is never changed. Call it holding the settings lock.
    /// </summary>
    private void LearnForSetup(AppRule rule, MonitorInfo mon)
    {
        EnsureSetup(out var setup);
        if (setup == null) return;
        var home = setup.Homes.FirstOrDefault(h => string.Equals(h.Key, rule.Key, StringComparison.OrdinalIgnoreCase));
        if (home?.Pinned == true) return;
        var now = home != null ? FindMonitor(home.Monitor) : Resolve(rule);
        if (now?.DeviceName == mon.DeviceName) return;
        setup.Homes.RemoveAll(h => string.Equals(h.Key, rule.Key, StringComparison.OrdinalIgnoreCase));
        if (Resolve(rule)?.DeviceName != mon.DeviceName) setup.Homes.Add(new SetupHome { Key = rule.Key, Monitor = RefTo(mon) });
        Save();
        string name = SetupName(setup);
        Record(ActivityKinds.Learned, $"{rule.DisplayName} opens on {mon.Name} with {name}", $"Learned: you used it there with {name}", null,
               $"Learned for {name}: {rule.DisplayName} opens on {mon.Name} while these monitors are connected.  [{rule.Key} {mon.DeviceName} setup {setup.Id}]",
               NoticeLearned, rule.Key);
    }

    /// <summary>Removes an app's own monitor in the setup connected now (you chose something else for it). Hold the settings lock.</summary>
    private void ForgetCurrentSetupHome(string key)
    {
        if (_setupId.Length > 0) SetupById(_setupId)?.Homes.RemoveAll(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Renames a setup ("Your setups"); an empty name goes back to "3 monitors".</summary>
    public void RenameSetup(string id, string? name)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var s = SetupById(id);
        if (s == null) return;
        s.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Forgets a setup and the monitors apps had in it (not the usual setup).</summary>
    public void ForgetSetup(string id)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (id == Settings.UsualSetup) return;
        var s = SetupById(id);
        if (s == null) return;
        Settings.Setups.Remove(s);
        if (id == _setupId) _notedSetup = id; // not noted again until monitors change
        Save();
        Note($"Forgot the monitor setup {SetupName(s)}.", $"[{id}]");
        Changed?.Invoke();
    }
}

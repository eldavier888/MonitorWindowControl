using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MonitorWindowControl;

/// <summary>
/// Watches for new app windows, moves them to their home monitor, and learns homes
/// from the foreground window. Everything runs on the host's thread (see <see cref="IEngineHost"/>).
/// The same engine runs in the settings app (tray mode) and in the tiny helper.
/// </summary>
public sealed class Engine : IDisposable
{
    /// <summary>Held by whichever process is currently watching windows, so two never move the same window.</summary>
    public const string ActiveMutexName = @"Local\MonitorWindowControl.Active";

    private const int PlaceDelayMs = 400;     // apps often reposition themselves right after opening
    private const int RetryMs = 250;          // window still cloaked / untitled
    private const int GiveUpMs = 4000;
    private const int VerifyDelayMs = 800;    // check once that the app didn't move itself back
    private const int LearnIntervalMs = 2000;
    private const int LearnChecksNeeded = 2;  // 2 consecutive checks ≈ 4 s
    private const int MonitorSettleSeconds = 30; // no learning this long after monitors change

    private readonly IEngineHost _host;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly bool _selfElevated;
    private readonly Native.WinEventDelegate _hookProc; // keep a reference so the GC can't collect it
    private IntPtr _hook;
    private Mutex? _activeMutex;

    private readonly HashSet<IntPtr> _handled = new();
    private readonly Dictionary<IntPtr, Pending> _pending = new();
    private readonly IEngineTimer _placeTimer;
    private readonly IEngineTimer _learnTimer;
    private readonly IEngineTimer _displayTimer;
    private List<MonitorInfo> _monitors = new();
    private string _monitorSignature = "";
    private DateTime _lastMonitorChangeUtc = DateTime.MinValue;

    private string? _streakKey, _streakDevice;
    private string? _learnedKey, _learnedDevice; // what the current streak already taught (don't redo it every 2 s)
    private int _streakCount, _tickCount;
    private bool _started, _disposed, _learning;

    public AppSettings Settings { get; private set; } = new();
    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public MonitorInfo? MainMonitor => _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();
    public string LastAction { get; private set; } = "";

    /// <summary>True while this engine watches for new windows (see <see cref="Activate"/>).</summary>
    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Launch-only sessions: only windows of this app are placed (null = all apps).</summary>
    public string? OnlyAppKey { get; set; }

    /// <summary>Raised (on the engine thread) when settings, rules or monitors change.</summary>
    public event Action? Changed;

    /// <summary>Raised when a new window of an app has been dealt with (moved, or already in place). Argument: app key.</summary>
    public event Action<string>? WindowHandled;

    /// <summary>Raised when an app window appears or one seen this session closes (so a list of open apps can update).</summary>
    public event Action? AppWindowsChanged;
    private readonly HashSet<IntPtr> _appWindows = new();
    private readonly HashSet<IntPtr> _fallbackPlaced = new(); // put on main only because its monitor isn't connected

    private sealed class Pending
    {
        public DateTime Due;
        public DateTime GiveUp;
        public bool Verify;
        public string TargetDevice = "";
        public string AppKey = "";
        public string FromDevice = "";   // the monitor it was on before we moved it
    }

    public Engine(IEngineHost host)
    {
        _host = host;
        _hookProc = OnWinEvent;
        _selfElevated = WindowInfo.IsCurrentProcessElevated();
        _placeTimer = host.CreateTimer(100, ProcessPending);
        _learnTimer = host.CreateTimer(LearnIntervalMs, LearnTick);
        IEngineTimer? display = null;
        display = host.CreateTimer(1500, () => { display!.Stop(); RefreshMonitors(); });
        _displayTimer = display;
    }

    /// <summary>
    /// Loads settings and monitors (and, in the settings app, imports the old .ini on first run).
    /// Doesn't watch windows yet: call <see cref="Activate"/> for that. Returns true on first run.
    /// </summary>
    public bool Start(bool importLegacy, string who)
    {
        if (importLegacy) Paths.MigrateFromOldName();
        Settings = SettingsStore.Load(out bool existed);
        RefreshMonitors();
        if (!existed && importLegacy)
        {
            IniImporter.Import(Settings, _monitors);
            Save();
        }
        _started = true;
        if (importLegacy) ForgetLearnedOneOffApps();
        Log.Write($"{who} started (mode={Settings.RunMode}, enabled={Settings.Enabled}, learn={Settings.LearnAutomatically}, " +
                  $"unknown apps={(Settings.MoveUnknownToMain ? "main monitor" : "leave")}, admin={_selfElevated}).");
        return !existed;
    }

    /// <summary>
    /// Starts watching for new windows (and learning, if <paramref name="learn"/>).
    /// Returns false if another process is already watching (it holds <see cref="ActiveMutexName"/>).
    /// </summary>
    public bool Activate(bool learn, bool claimActive = true)
    {
        if (IsActive) return true;
        if (claimActive)
        {
            // Tray app and background helper: only one of them may place windows at a time.
            // (Launch-shortcut sessions don't claim this; they only place their own app's new window.)
            _activeMutex = Native.TryCreateOwnedMutex(ActiveMutexName, out bool createdNew);
            if (!createdNew)
            {
                _activeMutex?.Dispose(); _activeMutex = null;
                return false;
            }
        }

        // Windows already on screen are not "new": never move them. Hidden ones (apps sitting in the tray)
        // count as new when they're shown, like an app being opened.
        _handled.Clear();
        foreach (var h in WindowInfo.EnumTopLevel())
            if (Native.IsWindowVisible(h)) _handled.Add(h);

        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_SHOW, IntPtr.Zero,
            _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero)
        {
            Log.Write("ERROR: could not install the window hook; new windows won't be detected.");
            ReleaseActiveMutex();
            return false;
        }
        _learning = learn;
        _learnTimer.Start(); // also keeps settings fresh when another process changes them
        return true;
    }

    /// <summary>Stops watching windows (settings stay loaded; the window/UI keeps working).</summary>
    public void Deactivate()
    {
        _placeTimer.Stop(); _learnTimer.Stop();
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _pending.Clear(); _handled.Clear(); _appWindows.Clear(); _fallbackPlaced.Clear(); ResetStreak();
        ReleaseActiveMutex();
    }

    private void ReleaseActiveMutex()
    {
        if (_activeMutex == null) return;
        try { _activeMutex.ReleaseMutex(); } catch { }
        _activeMutex.Dispose(); _activeMutex = null;
    }

    /// <summary>True if some process (this one or another) is currently watching windows.</summary>
    public static bool AnyProcessActive() => Native.NamedMutexExists(ActiveMutexName);

    // ------------------------------------------------------------------ settings shared between processes

    /// <summary>
    /// Re-reads settings.json if another process (settings window or helper) changed it.
    /// Returns true if something was reloaded. A file that can't be read right now is simply retried later.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (!_started || !SettingsStore.ChangedOnDisk()) return false;
        var fresh = SettingsStore.TryReload();
        if (fresh == null) return false;
        Settings = fresh;
        _monitorSignature = ""; // names/numbers may have changed
        RefreshMonitors();
        ResetStreak();
        Changed?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ monitors

    /// <summary>The host calls this when Windows reports a display change. Refreshes now and again once Windows has settled.</summary>
    public void OnDisplaySettingsChanged()
    {
        if (_disposed) return;
        RefreshMonitors();
        _displayTimer.Stop();
        _displayTimer.Start();
    }

    public void RefreshMonitors()
    {
        var list = MonitorService.Enumerate();
        _monitors = list;
        foreach (var m in list)
        {
            var custom = Settings.MonitorNicknames.FirstOrDefault(n => Matches(n.Monitor, m));
            m.Nickname = string.IsNullOrWhiteSpace(custom?.Nickname) ? null : custom!.Nickname;
            m.Number = custom?.Number;
        }
        // Your numbers decide the order (numbered first, 1, 2, 3…); otherwise main first, then left to right.
        list = list.OrderBy(m => m.Number ?? int.MaxValue).ThenByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
        _monitors = list;
        string sig = string.Join("|", list.Select(m => $"{m.DeviceName}={m.Name}{(m.IsPrimary ? "*" : "")}@{m.Bounds}/{m.Work}"));
        if (sig == _monitorSignature) return;
        if (_monitorSignature.Length > 0) _lastMonitorChangeUtc = DateTime.UtcNow; // (not the first read at startup)
        _monitorSignature = sig;
        Log.Write("Monitors: " + string.Join(", ", list.Select(m => $"{m.Label} [{m.DeviceName}]")));
        if (_started) Changed?.Invoke();
    }

    private MonitorInfo? MonitorByDevice(string? device) =>
        device == null ? null : _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connected monitor an app rule points to, or null if it isn't connected.</summary>
    public MonitorInfo? Resolve(AppRule rule) => FindMonitor(rule.MonitorName, rule.MonitorDevice);

    private bool Matches(MonitorRef r, MonitorInfo m) =>
        string.Equals(FindMonitor(r.Name, r.Device)?.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds a saved monitor among the connected ones. The model name wins: it survives Windows renumbering displays
    /// (a TV switched on or off). The saved device ("DISPLAY3") is only trusted when no name was saved, when Windows
    /// couldn't read a model name, or for two identical models; otherwise a monitor that isn't connected stays
    /// "not connected" instead of being confused with whichever screen now has its number.
    /// </summary>
    private MonitorInfo? FindMonitor(string? name, string? device)
    {
        var byDevice = MonitorByDevice(device);
        if (string.IsNullOrEmpty(name)) return byDevice;
        var byName = _monitors.Where(m => m.FriendlyName == name).ToList();
        if (byName.Count == 1) return byName[0];
        if (byDevice == null) return null;
        if (byName.Count > 1) return byDevice;
        static bool Generic(string n) => n.StartsWith("Display ", StringComparison.Ordinal);
        static string Model(string n) { int i = n.LastIndexOf(" (DISPLAY", StringComparison.Ordinal); return i > 0 ? n[..i] : n; }
        if (Generic(name) || Generic(byDevice.FriendlyName)) return byDevice;          // no model name to go by
        return Model(byDevice.FriendlyName) == Model(name) ? byDevice : null;          // same model, renumbered
    }

    /// <summary>For "Where I used it last" apps: is this one of the monitors it may open on?</summary>
    public bool IsAllowed(AppRule rule, MonitorInfo m) =>
        rule.AllowedMonitors.Count == 0 || rule.AllowedMonitors.Any(a => Matches(a, m));

    /// <summary>The connected monitors a "Where I used it last" app may open on.</summary>
    public List<MonitorInfo> AllowedConnected(AppRule rule) => _monitors.Where(m => IsAllowed(rule, m)).ToList();

    /// <summary>Where a saved app's new windows go right now (null = not connected / no choice possible).</summary>
    public MonitorInfo? ResolveTarget(AppRule rule)
    {
        var m = Resolve(rule);
        if (!rule.FollowLastUsed) return m;
        if (m != null && IsAllowed(rule, m)) return m;           // the monitor you used it on last
        return rule.AllowedMonitors.Count > 0 ? AllowedConnected(rule).FirstOrDefault() : null; // else another of its monitors
    }

    private MonitorInfo? MonitorOfWindow(IntPtr hwnd)
    {
        string? dev = MonitorService.DeviceOfWindow(hwnd);
        var m = MonitorByDevice(dev);
        if (m == null && dev != null) { RefreshMonitors(); m = MonitorByDevice(dev); }
        return m;
    }

    // ------------------------------------------------------------------ new windows

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;

        if (eventType == Native.EVENT_OBJECT_DESTROY)
        {
            _handled.Remove(hwnd);
            _pending.Remove(hwnd);
            _fallbackPlaced.Remove(hwnd);
            if (_appWindows.Remove(hwnd)) AppWindowsChanged?.Invoke();
            return;
        }

        if (_handled.Contains(hwnd) || _pending.ContainsKey(hwnd)) return;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return; // not top-level
        // (Enabled is checked when the window is placed, after settings are refreshed from disk.)

        var now = DateTime.UtcNow;
        _pending[hwnd] = new Pending { Due = now.AddMilliseconds(PlaceDelayMs), GiveUp = now.AddMilliseconds(GiveUpMs) };
        if (!_placeTimer.IsEnabled) _placeTimer.Start();
    }

    private void ProcessPending()
    {
        var now = DateTime.UtcNow;
        foreach (var (hwnd, p) in _pending.Where(kv => kv.Value.Due <= now).ToList())
        {
            try
            {
                if (p.Verify) Verify(hwnd, p);
                else TryPlace(hwnd, p, now);
            }
            catch (Exception ex)
            {
                _pending.Remove(hwnd);
                _handled.Add(hwnd);
                Log.Write($"Error handling window 0x{hwnd.ToInt64():X}: {ex.Message}");
            }
        }
        if (_pending.Count == 0) _placeTimer.Stop();
    }

    private void TryPlace(IntPtr hwnd, Pending p, DateTime now)
    {
        switch (WindowInfo.Classify(hwnd, _ownPid))
        {
            case WindowInfo.Kind.Hidden:
                _pending.Remove(hwnd);           // it may be shown again later; we'll see it then
                return;
            case WindowInfo.Kind.Never:
                _pending.Remove(hwnd);
                _handled.Add(hwnd);
                return;
            case WindowInfo.Kind.NotYet:
                if (now >= p.GiveUp) { _pending.Remove(hwnd); _handled.Add(hwnd); }
                else p.Due = now.AddMilliseconds(RetryMs);
                return;
        }

        // A real app window: handle it exactly once.
        _pending.Remove(hwnd);
        _handled.Add(hwnd);
        if (_appWindows.Add(hwnd)) AppWindowsChanged?.Invoke();
        ReloadIfChanged();
        if (!Settings.Enabled) return;

        var app = WindowInfo.Identify(hwnd);
        if (app == null) return;
        if (OnlyAppKey != null && !string.Equals(app.Key, OnlyAppKey, StringComparison.OrdinalIgnoreCase)) return;
        string title = WindowInfo.GetTitle(hwnd);

        var rule = Settings.Find(app.Key);
        if (rule != null) rule = RefreshRuleDetails(rule, app, hwnd);
        try { PlaceNewWindow(hwnd, app, rule, title); }
        finally { WindowHandled?.Invoke(app.Key); }
    }

    /// <summary>
    /// Keeps a saved app's details current from one of its windows: the friendly name (imported entries only know
    /// the exe), and how to start it (for launch shortcuts; apps that update themselves can move to a new folder).
    /// </summary>
    private AppRule RefreshRuleDetails(AppRule rule, AppIdentity app, IntPtr hwnd)
    {
        bool nameStale = rule.DisplayName != app.DisplayName && string.Equals(rule.DisplayName, rule.Key, StringComparison.OrdinalIgnoreCase);
        string? target = WindowInfo.GetLaunchTarget(hwnd);
        bool targetStale = target != null && !string.Equals(target, rule.LaunchTarget, StringComparison.OrdinalIgnoreCase);
        if (!nameStale && !targetStale) return rule;

        using var _ = SettingsStore.Lock();
        ReloadIfChanged();
        var fresh = Settings.Find(rule.Key);
        if (fresh == null) return rule;
        if (nameStale) fresh.DisplayName = app.DisplayName;
        if (targetStale) fresh.LaunchTarget = target;
        Save();
        Changed?.Invoke();
        return fresh;
    }

    private void PlaceNewWindow(IntPtr hwnd, AppIdentity app, AppRule? rule, string title)
    {
        MonitorInfo? target;
        string reason;
        if (rule != null && rule.NeverMove) return;
        if (rule != null)
        {
            target = ResolveTarget(rule);
            reason = rule.FollowLastUsed ? "where you used it last" : rule.Pinned ? "set by you" : "learned";
            if (target == null && rule.FollowLastUsed && rule.MonitorDevice == null) return; // never used yet: leave it
            if (target == null)
            {
                target = MainMonitor;
                reason += $"; its monitor ({rule.MonitorName ?? rule.MonitorDevice}) isn't connected, so main monitor";
            }
        }
        else
        {
            if (!Settings.MoveUnknownToMain) return;
            target = MainMonitor;
            reason = "new app → main monitor";
        }
        if (target == null) return;

        // The taskbar may have moved or changed size since monitors were last read: use the current work area.
        string targetDevice = target.DeviceName;
        RefreshMonitors();
        target = MonitorByDevice(targetDevice);
        if (target == null) return;

        var current = MonitorOfWindow(hwnd);
        if (current != null && current.DeviceName == target.DeviceName) return; // already home

        if (Native.IsIconic(hwnd)) { Log.Write($"Skipped {app.Key} \"{title}\": minimized."); return; }
        if (WindowMover.IsFullscreen(hwnd, current)) { Log.Write($"Skipped {app.Key} \"{title}\": fullscreen."); return; }
        if (!_selfElevated && WindowInfo.IsProcessElevated(WindowInfo.GetPid(hwnd)))
        {
            Log.Write($"Skipped {app.Key} \"{title}\": it runs as administrator, so Windows doesn't allow Monitor Window Control to move it.");
            return;
        }
        if (Native.IsHungAppWindow(hwnd)) { Log.Write($"Skipped {app.Key} \"{title}\": not responding."); return; }

        bool ok = WindowMover.MoveTo(hwnd, target);
        string from = current?.Name ?? "?";
        if (ok)
        {
            if (reason.Contains("isn't connected")) _fallbackPlaced.Add(hwnd);
            Note($"Moved {app.DisplayName} from {from} to {target.Name} ({reason}).", $"{app.Key} \"{title}\"");
            // Check once more shortly: some apps restore their saved position after showing.
            _pending[hwnd] = new Pending
            {
                Verify = true, Due = DateTime.UtcNow.AddMilliseconds(VerifyDelayMs),
                TargetDevice = target.DeviceName, AppKey = app.Key, FromDevice = current?.DeviceName ?? "",
            };
        }
        else
        {
            Log.Write($"Could not move {app.Key} \"{title}\" from {from} to {target.Name}.");
        }
    }

    private void Verify(IntPtr hwnd, Pending p)
    {
        _pending.Remove(hwnd);
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return;
        if ((Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0) return; // user is dragging: hands off
        var target = MonitorByDevice(p.TargetDevice);
        var current = MonitorOfWindow(hwnd);
        if (target == null || current == null || current.DeviceName == target.DeviceName) return;
        // Apps that restore their saved position jump back to where they opened. Anywhere else, you moved it: leave it.
        if (!string.Equals(current.DeviceName, p.FromDevice, StringComparison.OrdinalIgnoreCase)) return;
        if (WindowMover.MoveTo(hwnd, target))
            Log.Write($"{p.AppKey} moved itself back to {current.Name}; moved it to {target.Name} again.");
    }

    // ------------------------------------------------------------------ learning

    private void LearnTick()
    {
        if (++_tickCount % 30 == 0) // safety net, ~every minute
        {
            _handled.RemoveWhere(h => !Native.IsWindow(h));
            _fallbackPlaced.RemoveWhere(h => !Native.IsWindow(h));
        }
        ReloadIfChanged(); // pick up changes made in the settings window / by the helper
        if (!_learning) return;

        // Don't learn while monitors are changing (sleep, display driver reset, a game switching resolution…) or
        // when only one monitor is connected: then you use apps where they *have* to be, not where you want them.
        if (_monitors.Count < 2 || DateTime.UtcNow - _lastMonitorChangeUtc < TimeSpan.FromSeconds(MonitorSettleSeconds))
        { ResetStreak(); return; }

        // "Where I used it last" apps are tracked even with automatic learning off: you asked for it per app.
        if (!Settings.Enabled || (!Settings.LearnAutomatically && !Settings.Apps.Any(a => a.FollowLastUsed)))
        { ResetStreak(); return; }

        // Only the window you're actually using counts. Background windows never teach anything.
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || _pending.ContainsKey(fg)
            || Native.IsIconic(fg)
            || WindowInfo.Classify(fg, _ownPid) != WindowInfo.Kind.AppWindow) { ResetStreak(); return; }

        var app = WindowInfo.Identify(fg);
        var mon = app == null ? null : MonitorOfWindow(fg);
        if (app == null || mon == null) { ResetStreak(); return; }

        if (app.Key == _streakKey && mon.DeviceName == _streakDevice) _streakCount++;
        else { _streakKey = app.Key; _streakDevice = mon.DeviceName; _streakCount = 1; }

        // Learn once per streak: using the same app on the same monitor for an hour doesn't redo it every 2 s.
        if (_streakCount >= LearnChecksNeeded && (app.Key != _learnedKey || mon.DeviceName != _learnedDevice))
        {
            _learnedKey = app.Key; _learnedDevice = mon.DeviceName;
            Learn(app, mon, fg);
        }
    }

    private void ResetStreak() { _streakKey = null; _streakDevice = null; _streakCount = 0; _learnedKey = null; _learnedDevice = null; }

    private void Learn(AppIdentity app, MonitorInfo mon, IntPtr hwnd)
    {
        using var _ = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(app.Key);
        if (rule != null) rule = RefreshRuleDetails(rule, app, hwnd);
        // It's on the main monitor only because its own monitor is off: that's not a choice. (Drag it elsewhere and it is.)
        if (_fallbackPlaced.Contains(hwnd) && mon.DeviceName == MainMonitor?.DeviceName) return;
        if (rule != null && rule.FollowLastUsed)
        {
            // Remember the last monitor you used it on, if it's one of the monitors you picked for it.
            if (!IsAllowed(rule, mon) || Resolve(rule)?.DeviceName == mon.DeviceName) return;
            string? was = rule.MonitorName ?? rule.MonitorDevice;
            rule.MonitorDevice = mon.DeviceName; rule.MonitorName = mon.FriendlyName; rule.UpdatedUtc = DateTime.UtcNow;
            Save();
            Log.Write($"Last used: {rule.DisplayName} is now on {mon.Name}{(was == null ? "" : $" (was {was})")}, so it opens there next.  [{rule.Key} {mon.DeviceName}]");
            Changed?.Invoke();
            return;
        }
        if (!Settings.LearnAutomatically) return;
        if (rule != null && (rule.Pinned || rule.NeverMove)) return;
        if (rule == null && WindowInfo.IsOneOffApp(app.Key)) return; // setup / uninstall / update programs
        if (rule != null && Resolve(rule)?.DeviceName == mon.DeviceName)
        {
            if (rule.MonitorDevice != mon.DeviceName || rule.MonitorName != mon.FriendlyName)
            {   // same screen, Windows just renamed it: keep the record current quietly
                rule.MonitorDevice = mon.DeviceName; rule.MonitorName = mon.FriendlyName; Save();
            }
            return;
        }

        string? old = rule == null ? null : (rule.MonitorName ?? rule.MonitorDevice);
        if (rule == null) { rule = new AppRule { Key = app.Key, LaunchTarget = WindowInfo.GetLaunchTarget(hwnd) }; Settings.Apps.Add(rule); }
        bool keepName = app.Key.StartsWith(WindowInfo.WebAppKeyPrefix, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(rule.DisplayName) && rule.DisplayName != rule.Key;
        if (!keepName) rule.DisplayName = app.DisplayName;
        rule.MonitorDevice = mon.DeviceName;
        rule.MonitorName = mon.FriendlyName;
        rule.UpdatedUtc = DateTime.UtcNow;
        Save();
        Note(old == null
            ? $"Learned: {app.DisplayName} opens on {mon.Name}."
            : $"Learned: {app.DisplayName} now opens on {mon.Name} (was {old}).", $"[{app.Key} {mon.DeviceName}]");
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ user actions

    public void SetEnabled(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.Enabled == on) return;
        Settings.Enabled = on; Save();
        Log.Write(on ? "Enabled." : "Disabled.");
        Changed?.Invoke();
    }

    public void SetLearn(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.LearnAutomatically == on) return;
        Settings.LearnAutomatically = on; Save(); ResetStreak();
        Log.Write("Learn automatically: " + (on ? "on" : "off"));
        Changed?.Invoke();
    }

    public void SetMoveUnknownToMain(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.MoveUnknownToMain == on) return;
        Settings.MoveUnknownToMain = on; Save();
        Log.Write("Apps it hasn't learned yet: " + (on ? "open on main monitor" : "leave where Windows puts them"));
        Changed?.Invoke();
    }

    /// <summary>
    /// Pin an app to a monitor, or (monitor == null) never move it. Works for apps it hasn't learned yet too.
    /// With a monitor, the app's windows that are open right now move there as well.
    /// </summary>
    public void Pin(string key, MonitorInfo? monitor, string? displayName = null)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null)
        {
            rule = new AppRule { Key = key, DisplayName = displayName ?? key };
            Settings.Apps.Add(rule);
        }
        rule.LaunchTarget ??= FindLaunchTarget(key);
        rule.Pinned = true;
        rule.NeverMove = monitor == null;
        rule.FollowLastUsed = false;
        rule.AllowedMonitors.Clear();
        if (monitor != null) { rule.MonitorDevice = monitor.DeviceName; rule.MonitorName = monitor.FriendlyName; }
        rule.UpdatedUtc = DateTime.UtcNow;
        Save();
        ResetStreak();

        string moved = "";
        if (monitor != null)
        {
            int n = MoveOpenWindows(rule.Key, monitor);
            if (n > 0) moved = n == 1 ? " Its open window was moved there." : $" Its {n} open windows were moved there.";
        }
        Note(monitor == null ? $"Set by you: never move {rule.DisplayName}." : $"Set by you: {rule.DisplayName} opens on {monitor.Name}.{moved}",
             $"[{rule.Key}{(monitor == null ? "" : " " + monitor.DeviceName)}]");
        Changed?.Invoke();
    }

    /// <summary>
    /// "Where I used it last": the app opens on whichever of <paramref name="allowed"/> you used it on most recently.
    /// <paramref name="currentMonitor"/> (where it is now, if open) seeds that, so it works from the start.
    /// </summary>
    public void SetFollowLastUsed(string key, IReadOnlyList<MonitorInfo> allowed, MonitorInfo? currentMonitor, string? displayName = null)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null)
        {
            rule = new AppRule { Key = key, DisplayName = displayName ?? key };
            Settings.Apps.Add(rule);
        }
        rule.LaunchTarget ??= FindLaunchTarget(key);
        rule.Pinned = true;
        rule.NeverMove = false;
        rule.FollowLastUsed = true;
        rule.AllowedMonitors = allowed.Select(m => new MonitorRef { Device = m.DeviceName, Name = m.FriendlyName }).ToList();
        // Start from where it is now / was last, if that's allowed; otherwise the first allowed monitor.
        var start = new[] { currentMonitor, Resolve(rule) }.FirstOrDefault(m => m != null && IsAllowed(rule, m))
                    ?? AllowedConnected(rule).FirstOrDefault();
        rule.MonitorDevice = start?.DeviceName; rule.MonitorName = start?.FriendlyName;
        rule.UpdatedUtc = DateTime.UtcNow;
        Save();
        ResetStreak();
        Note($"Set by you: {rule.DisplayName} opens where you used it last ({string.Join(" or ", allowed.Select(m => m.Name))}).",
             $"[{rule.Key} {string.Join(",", allowed.Select(m => m.DeviceName))}]");
        Changed?.Invoke();
    }

    /// <summary>
    /// Makes a monitor the main display in Windows itself (same as Settings → Display → "Make this my main display").
    /// Windows requires the main display to sit at (0,0), so every display is shifted by the same amount;
    /// the physical arrangement stays the same. Returns an error message, or null on success.
    /// </summary>
    public string? SetMainMonitor(MonitorInfo target)
    {
        var dm = NewDevMode();
        if (!Native.EnumDisplaySettings(target.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref dm))
            return "Windows didn't return the settings of that monitor.";
        int dx = dm.dmPositionX, dy = dm.dmPositionY;

        // Every display's current settings (new main first).
        var displays = new List<(MonitorInfo m, Native.DEVMODE original)>();
        foreach (var m in _monitors.OrderByDescending(m => m.DeviceName == target.DeviceName))
        {
            var d = NewDevMode();
            if (!Native.EnumDisplaySettings(m.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref d))
                return $"Windows didn't return the settings of {m.Name}, so nothing was changed.";
            displays.Add((m, d));
        }

        Native.DEVMODE Shifted(Native.DEVMODE d) { d.dmPositionX -= dx; d.dmPositionY -= dy; d.dmFields = Native.DM_POSITION; return d; }
        uint PrimaryFlag(MonitorInfo m, string primary) => m.DeviceName == primary ? Native.CDS_SET_PRIMARY : 0;

        // Queue the change for every display, then apply them all at once. (A CDS_TEST pre-check can't be used:
        // Windows judges each display on its own and rejects moving the current main display away from 0,0.)
        foreach (var (m, original) in displays)
        {
            var d = Shifted(original);
            uint flags = Native.CDS_UPDATEREGISTRY | Native.CDS_NORESET | PrimaryFlag(m, target.DeviceName);
            int r = Native.ChangeDisplaySettingsEx(m.DeviceName, ref d, IntPtr.Zero, flags, IntPtr.Zero);
            if (r != Native.DISP_CHANGE_SUCCESSFUL)
            {
                // Put the queued displays back where they were (and the old main back as main), so nothing is half-changed.
                string oldMain = MainMonitor?.DeviceName ?? "";
                foreach (var (m2, o2) in displays)
                {
                    var back = o2; back.dmFields = Native.DM_POSITION;
                    Native.ChangeDisplaySettingsEx(m2.DeviceName, ref back, IntPtr.Zero,
                        Native.CDS_UPDATEREGISTRY | Native.CDS_NORESET | PrimaryFlag(m2, oldMain), IntPtr.Zero);
                }
                Native.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                Log.Write($"Could not make {target.Name} the main monitor: Windows said {r} for {m.DeviceName}. The old layout was kept.");
                return $"Windows refused the change for {m.Name} (code {r}). Your monitors were left as they were.";
            }
        }
        int apply = Native.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (apply != Native.DISP_CHANGE_SUCCESSFUL)
        {
            Log.Write($"Could not make {target.Name} the main monitor: Windows said {apply}.");
            return $"Windows refused the change (code {apply}).";
        }
        RefreshMonitors();
        Note($"Main monitor is now {target.Name} (changed in Windows).", $"[{target.DeviceName}]");
        return null;
    }

    private static Native.DEVMODE NewDevMode() =>
        new() { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    /// <summary>
    /// Gives a monitor your own name and/or number (null/empty = none: back to its model name, no number).
    /// Only used inside this app; Windows keeps its own numbering. A number already used by another monitor moves here.
    /// </summary>
    public void RenameMonitor(MonitorInfo m, string? nickname, int? number)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        string old = m.Name;
        Settings.MonitorNicknames.RemoveAll(n => Matches(n.Monitor, m));
        if (number != null)
            foreach (var other in Settings.MonitorNicknames.Where(n => n.Number == number)) other.Number = null;
        Settings.MonitorNicknames.RemoveAll(n => string.IsNullOrWhiteSpace(n.Nickname) && n.Number == null);
        if (nickname != null || number != null)
            Settings.MonitorNicknames.Add(new MonitorNickname
            {
                Monitor = new MonitorRef { Device = m.DeviceName, Name = m.FriendlyName },
                Nickname = nickname ?? "", Number = number,
            });
        Save();
        RefreshMonitors();
        var now = _monitors.FirstOrDefault(x => x.DeviceName == m.DeviceName);
        Note($"Monitor {old} is now shown as {now?.Name ?? m.FriendlyName}.", $"[{m.DeviceName}]");
        Changed?.Invoke();
    }

    /// <summary>How to start an app (for launch-only shortcuts), taken from one of its open windows. Null if none is open.</summary>
    public string? FindLaunchTarget(string key)
    {
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            if (WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            var app = WindowInfo.Identify(h);
            if (app != null && string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase))
                return WindowInfo.GetLaunchTarget(h);
        }
        return null;
    }

    /// <summary>Records how to start a saved app (from one of its open windows), if not known yet.</summary>
    public void UpdateLaunchTarget(string key)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null || rule.LaunchTarget != null) return;
        rule.LaunchTarget = FindLaunchTarget(key);
        if (rule.LaunchTarget != null) Save();
    }

    /// <summary>Records the file name of an app's launch shortcut (null when it's removed).</summary>
    public void SetShortcutFile(string key, string? file)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null || rule.ShortcutFile == file) return;
        rule.ShortcutFile = file; Save();
        Changed?.Invoke();
    }

    /// <summary>Stores how the app runs (see <see cref="RunModes"/>). The program applies it (tray, helper, shortcuts).</summary>
    public void SetRunMode(string mode)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        mode = RunModes.Normalize(mode);
        if (Settings.RunMode == mode) return;
        Settings.RunMode = mode; Save();
        Log.Write("How it runs: " + mode);
        Changed?.Invoke();
    }

    public void SetStartWithWindows(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.StartWithWindows == on) return;
        Settings.StartWithWindows = on; Save();
        Log.Write("Start with Windows: " + (on ? "on" : "off"));
        Changed?.Invoke();
    }

    /// <summary>An app that has at least one window open right now.</summary>
    public sealed record OpenApp(AppIdentity App, MonitorInfo? Monitor, int Windows);

    /// <summary>Apps with real windows open right now (one entry per app; monitor of its first window).</summary>
    public List<OpenApp> GetOpenApps()
    {
        var result = new Dictionary<string, OpenApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in WindowInfo.EnumTopLevel()) // EnumWindows returns windows front to back
        {
            if (WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            var app = WindowInfo.Identify(h);
            if (app == null) continue;
            if (result.TryGetValue(app.Key, out var existing)) result[app.Key] = existing with { Windows = existing.Windows + 1 };
            else result[app.Key] = new OpenApp(app, MonitorOfWindow(h), 1);
        }
        return result.Values.ToList();
    }

    /// <summary>Moves the app's open windows to the monitor (you asked for it, so this is the one time we move existing windows).</summary>
    private int MoveOpenWindows(string key, MonitorInfo target)
    {
        int moved = 0;
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            if (WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            var app = WindowInfo.Identify(h);
            if (app == null || !string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            _pending.Remove(h); _handled.Add(h);

            var current = MonitorOfWindow(h);
            if (current?.DeviceName == target.DeviceName) continue;
            string title = WindowInfo.GetTitle(h);
            if (Native.IsIconic(h)) { Log.Write($"Not moved now: {app.Key} \"{title}\" is minimized (it will open on {target.Name} next time)."); continue; }
            if (WindowMover.IsFullscreen(h, current)) { Log.Write($"Not moved now: {app.Key} \"{title}\" is fullscreen."); continue; }
            if (!_selfElevated && WindowInfo.IsProcessElevated(WindowInfo.GetPid(h)))
            {
                Log.Write($"Not moved now: {app.Key} \"{title}\" runs as administrator, so Windows doesn't allow it.");
                continue;
            }
            if (Native.IsHungAppWindow(h)) continue;
            if (WindowMover.MoveTo(h, target))
            {
                moved++;
                Log.Write($"Moved open window {app.Key} \"{title}\" from {current?.Name ?? "?"} to {target.Name} (set by you).");
            }
        }
        return moved;
    }

    /// <summary>Older versions also learned installers and uninstallers; drop those (never ones you set yourself).</summary>
    private void ForgetLearnedOneOffApps()
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var junk = Settings.Apps.Where(a => !a.Pinned && !a.NeverMove && !a.FollowLastUsed && a.ShortcutFile == null
                                            && WindowInfo.IsOneOffApp(a.Key)).ToList();
        if (junk.Count == 0) return;
        foreach (var a in junk) Settings.Apps.Remove(a);
        Save();
        Log.Write("Removed learned setup/uninstall programs: " + string.Join(", ", junk.Select(a => a.Key)));
    }

    public void Forget(string key)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null) return;
        Settings.Apps.Remove(rule);
        Save(); ResetStreak();
        Note($"Forgot {rule.DisplayName}.", $"[{rule.Key}]");
        Changed?.Invoke();
    }

    public void ForgetAll()
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        int n = Settings.Apps.Count;
        Settings.Apps.Clear();
        Save(); ResetStreak();
        Note($"Forgot all {n} apps.", "");
        Changed?.Invoke();
    }

    private void Note(string message, string detail)
    {
        LastAction = $"{DateTime.Now:HH:mm}  {message}";
        Log.Write(detail.Length > 0 ? $"{message}  {detail}" : message);
        Changed?.Invoke();
    }

    private void Save() => SettingsStore.Save(Settings);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        _displayTimer.Stop();
        if (_started) Log.Write("Stopped.");
    }
}

internal static class WindowMover
{
    public static bool IsFullscreen(IntPtr hwnd, MonitorInfo? mon)
    {
        if (mon == null || Native.IsZoomed(hwnd)) return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        var b = mon.Bounds;
        return (style & Native.WS_CAPTION) != Native.WS_CAPTION
               && r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    /// <summary>
    /// True if the window's app answers within half a second. Moving a window waits for its app, so a busy app
    /// (still loading) would otherwise freeze Monitor Window Control too. Windows of this process always count.
    /// </summary>
    public static bool Responds(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return true;
        return Native.SendMessageTimeout(hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero,
                   Native.SMTO_ABORTIFHUNG | Native.SMTO_BLOCK, 500, out _) != IntPtr.Zero;
    }

    /// <summary>Centers the window on the target's work area (size clamped to fit). Keeps it maximized if it was.</summary>
    public static bool MoveTo(IntPtr hwnd, MonitorInfo target)
    {
        if (!Responds(hwnd)) return false;
        const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
        bool wasMaximized = Native.IsZoomed(hwnd);
        if (wasMaximized) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        var wa = target.Work;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;

        // Step 1: jump onto the target keeping the size, so the app can rescale for that monitor's DPI.
        int w = Math.Min(r.Width, wa.Width), h = Math.Min(r.Height, wa.Height);
        Native.SetWindowPos(hwnd, IntPtr.Zero, wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, 0, 0, flags | Native.SWP_NOSIZE);

        // Step 2: with the size it has now, clamp and center.
        if (Native.GetWindowRect(hwnd, out r))
        {
            w = Math.Min(r.Width, wa.Width); h = Math.Min(r.Height, wa.Height);
            Native.SetWindowPos(hwnd, IntPtr.Zero, wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h, flags);
        }

        if (wasMaximized) Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
        return string.Equals(MonitorService.DeviceOfWindow(hwnd), target.DeviceName, StringComparison.OrdinalIgnoreCase);
    }
}

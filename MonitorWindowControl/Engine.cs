using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MonitorWindowControl;

/// <summary>
/// Watches for new app windows, moves them to their home monitor, and learns homes
/// from the foreground window. Everything runs on the UI thread.
/// </summary>
public sealed class Engine : IDisposable
{
    private const int PlaceDelayMs = 400;     // apps often reposition themselves right after opening
    private const int RetryMs = 250;          // window still cloaked / untitled
    private const int GiveUpMs = 4000;
    private const int VerifyDelayMs = 800;    // check once that the app didn't move itself back
    private const int LearnIntervalMs = 2000;
    private const int LearnChecksNeeded = 2;  // 2 consecutive checks ≈ 4 s

    private readonly Dispatcher _ui;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly bool _selfElevated;
    private readonly Native.WinEventDelegate _hookProc; // keep a reference so the GC can't collect it
    private IntPtr _hook;

    private readonly HashSet<IntPtr> _handled = new();
    private readonly Dictionary<IntPtr, Pending> _pending = new();
    private readonly DispatcherTimer _placeTimer;
    private readonly DispatcherTimer _learnTimer;
    private readonly DispatcherTimer _displayTimer;
    private List<MonitorInfo> _monitors = new();
    private string _monitorSignature = "";

    private string? _streakKey, _streakDevice;
    private int _streakCount, _tickCount;
    private bool _started, _disposed;

    public AppSettings Settings { get; private set; } = new();
    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public MonitorInfo? MainMonitor => _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();
    public string LastAction { get; private set; } = "";

    /// <summary>Raised (on the UI thread) when settings, rules or monitors change.</summary>
    public event Action? Changed;

    private sealed class Pending
    {
        public DateTime Due;
        public DateTime GiveUp;
        public bool Verify;
        public string TargetDevice = "";
        public string AppKey = "";
    }

    public Engine(Dispatcher ui)
    {
        _ui = ui;
        _hookProc = OnWinEvent;
        _selfElevated = WindowInfo.IsCurrentProcessElevated();
        _placeTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _placeTimer.Tick += (_, _) => ProcessPending();
        _learnTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(LearnIntervalMs) };
        _learnTimer.Tick += (_, _) => LearnTick();
        _displayTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(1500) };
        _displayTimer.Tick += (_, _) => { _displayTimer.Stop(); RefreshMonitors(); };
    }

    /// <summary>Loads settings, imports the old .ini on first run, installs the hook. Returns true on first run.</summary>
    public bool Start()
    {
        Paths.MigrateFromOldName();
        Settings = SettingsStore.Load(out bool existed);
        RefreshMonitors();
        if (!existed)
        {
            IniImporter.Import(Settings, _monitors);
            Save();
        }

        // Windows that already exist are not "new": never move them.
        foreach (var h in WindowInfo.EnumTopLevel()) _handled.Add(h);

        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_SHOW, IntPtr.Zero,
            _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) Log.Write("ERROR: could not install the window hook; new windows won't be detected.");

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _learnTimer.Start();
        _started = true;
        Log.Write($"Monitor Window Control started (enabled={Settings.Enabled}, learn={Settings.LearnAutomatically}, " +
                  $"unknown apps={(Settings.MoveUnknownToMain ? "main monitor" : "leave")}, admin={_selfElevated}).");
        return !existed;
    }

    // ------------------------------------------------------------------ monitors

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Can arrive on another thread. Refresh now and again once Windows has settled.
        _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            RefreshMonitors();
            _displayTimer.Stop();
            _displayTimer.Start();
        });
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
        string sig = string.Join("|", list.Select(m => $"{m.DeviceName}={m.Name}{(m.IsPrimary ? "*" : "")}@{m.Bounds}"));
        if (sig == _monitorSignature) return;
        _monitorSignature = sig;
        Log.Write("Monitors: " + string.Join(", ", list.Select(m => $"{m.Label} [{m.DeviceName}]")));
        if (_started) Changed?.Invoke();
    }

    private MonitorInfo? MonitorByDevice(string? device) =>
        device == null ? null : _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connected monitor an app rule points to, or null if it isn't connected.</summary>
    public MonitorInfo? Resolve(AppRule rule)
    {
        // Prefer the model name: survives Windows renumbering displays when the TV is switched on/off.
        if (!string.IsNullOrEmpty(rule.MonitorName))
        {
            var byName = _monitors.Where(m => m.FriendlyName == rule.MonitorName).ToList();
            if (byName.Count == 1) return byName[0];
        }
        return MonitorByDevice(rule.MonitorDevice);
    }

    private bool Matches(MonitorRef r, MonitorInfo m)
    {
        if (!string.IsNullOrEmpty(r.Name) && _monitors.Count(x => x.FriendlyName == r.Name) == 1)
            return m.FriendlyName == r.Name;
        return string.Equals(r.Device, m.DeviceName, StringComparison.OrdinalIgnoreCase);
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
            return;
        }

        if (!Settings.Enabled || _handled.Contains(hwnd) || _pending.ContainsKey(hwnd)) return;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return; // not top-level

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
        if (!Settings.Enabled) return;

        var app = WindowInfo.Identify(hwnd);
        if (app == null) return;
        string title = WindowInfo.GetTitle(hwnd);

        var rule = Settings.Find(app.Key);
        if (rule != null && rule.DisplayName != app.DisplayName && string.Equals(rule.DisplayName, rule.Key, StringComparison.OrdinalIgnoreCase))
        {   // imported entries only know the exe name: pick up the friendly name now
            rule.DisplayName = app.DisplayName; Save(); Changed?.Invoke();
        }
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
            Note($"Moved {app.DisplayName} from {from} to {target.Name} ({reason}).", $"{app.Key} \"{title}\"");
            // Check once more shortly: some apps restore their saved position after showing.
            _pending[hwnd] = new Pending
            {
                Verify = true, Due = DateTime.UtcNow.AddMilliseconds(VerifyDelayMs),
                TargetDevice = target.DeviceName, AppKey = app.Key,
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
        if (WindowMover.MoveTo(hwnd, target))
            Log.Write($"{p.AppKey} moved itself back to {current.Name}; moved it to {target.Name} again.");
    }

    // ------------------------------------------------------------------ learning

    private void LearnTick()
    {
        if (++_tickCount % 30 == 0) _handled.RemoveWhere(h => !Native.IsWindow(h)); // safety net, ~every minute

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

        if (_streakCount >= LearnChecksNeeded) Learn(app, mon);
    }

    private void ResetStreak() { _streakKey = null; _streakDevice = null; _streakCount = 0; }

    private void Learn(AppIdentity app, MonitorInfo mon)
    {
        var rule = Settings.Find(app.Key);
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
        if (rule != null && Resolve(rule)?.DeviceName == mon.DeviceName)
        {
            if (rule.MonitorDevice != mon.DeviceName || rule.MonitorName != mon.FriendlyName)
            {   // same screen, Windows just renamed it: keep the record current quietly
                rule.MonitorDevice = mon.DeviceName; rule.MonitorName = mon.FriendlyName; Save();
            }
            return;
        }

        string? old = rule == null ? null : (rule.MonitorName ?? rule.MonitorDevice);
        if (rule == null) { rule = new AppRule { Key = app.Key }; Settings.Apps.Add(rule); }
        rule.DisplayName = app.DisplayName;
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
        if (Settings.Enabled == on) return;
        Settings.Enabled = on; Save();
        Log.Write(on ? "Enabled." : "Disabled.");
        Changed?.Invoke();
    }

    public void SetLearn(bool on)
    {
        if (Settings.LearnAutomatically == on) return;
        Settings.LearnAutomatically = on; Save(); ResetStreak();
        Log.Write("Learn automatically: " + (on ? "on" : "off"));
        Changed?.Invoke();
    }

    public void SetMoveUnknownToMain(bool on)
    {
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
        var rule = Settings.Find(key);
        if (rule == null)
        {
            rule = new AppRule { Key = key, DisplayName = displayName ?? key };
            Settings.Apps.Add(rule);
        }
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
        var rule = Settings.Find(key);
        if (rule == null)
        {
            rule = new AppRule { Key = key, DisplayName = displayName ?? key };
            Settings.Apps.Add(rule);
        }
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

        // Queue the change for every display (new main first), then apply them all at once.
        foreach (var m in _monitors.OrderByDescending(m => m.DeviceName == target.DeviceName))
        {
            var d = NewDevMode();
            if (!Native.EnumDisplaySettings(m.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref d)) continue;
            d.dmPositionX -= dx; d.dmPositionY -= dy;
            d.dmFields = Native.DM_POSITION;
            uint flags = Native.CDS_UPDATEREGISTRY | Native.CDS_NORESET
                         | (m.DeviceName == target.DeviceName ? Native.CDS_SET_PRIMARY : 0);
            int r = Native.ChangeDisplaySettingsEx(m.DeviceName, ref d, IntPtr.Zero, flags, IntPtr.Zero);
            if (r != Native.DISP_CHANGE_SUCCESSFUL)
            {
                Native.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); // apply whatever is queued
                Log.Write($"Could not make {target.Name} the main monitor: Windows said {r} for {m.DeviceName}.");
                return $"Windows refused the change (code {r}).";
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

    public void Forget(string key)
    {
        var rule = Settings.Find(key);
        if (rule == null) return;
        Settings.Apps.Remove(rule);
        Save(); ResetStreak();
        Note($"Forgot {rule.DisplayName}.", $"[{rule.Key}]");
        Changed?.Invoke();
    }

    public void ForgetAll()
    {
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
        _placeTimer.Stop(); _learnTimer.Stop(); _displayTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _pending.Clear(); _handled.Clear();
        if (_started) { Save(); Log.Write("Monitor Window Control stopped."); }
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

    /// <summary>Centers the window on the target's work area (size clamped to fit). Keeps it maximized if it was.</summary>
    public static bool MoveTo(IntPtr hwnd, MonitorInfo target)
    {
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

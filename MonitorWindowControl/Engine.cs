using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    private const int HoldAfterChangeMs = 2000;  // new windows wait this long after the last display change…
    private const int MaxHoldMs = 3000;          // …but never longer than this in all
    private const int MaxRequeues = 2;           // a move cut short by a display change is tried again this often

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
    private string _layoutSignature = "";
    private DateTime _lastMonitorChangeUtc = DateTime.MinValue;
    private DateTime _lastDisplayEventUtc = DateTime.MinValue; // last "display changed" from Windows or a new layout
    private int _topologyGeneration;                            // +1 on every display change: moves started before are stale
    private bool _identityBackfillDue;                          // save hardware identity into older saved monitors
    private bool? _wasRemote;

    private string? _streakKey, _streakDevice;
    private string? _learnedKey, _learnedDevice; // what the current streak already taught (don't redo it every 2 s)
    private int _streakCount, _tickCount;
    private bool _started, _disposed, _learning;

    public AppSettings Settings { get; private set; } = new();
    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public MonitorInfo? MainMonitor => _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    /// <summary>
    /// Where windows go when their monitor isn't connected, and where apps it hasn't learned yet open: the main
    /// monitor, unless that one is temporary (then the first monitor that isn't).
    /// </summary>
    public MonitorInfo? FallbackMonitor =>
        MainMonitor is { IsTemporary: false } main ? main : _monitors.FirstOrDefault(m => !m.IsTemporary) ?? MainMonitor;

    /// <summary>True while this PC is used through Remote Desktop (its own monitors are replaced by the remote ones).</summary>
    public static bool IsRemoteSession => Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0;

    /// <summary>True while windows aren't moved because of a Remote Desktop session (see <see cref="AppSettings.PauseDuringRemoteDesktop"/>).</summary>
    public bool IsPausedForRemoteDesktop => Settings.PauseDuringRemoteDesktop && IsRemoteSession;

    /// <summary>True while Windows duplicates a display on several monitors (learning is paused then).</summary>
    public bool HasDuplicatedMonitors => _monitors.Any(m => m.IsDuplicated);

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
        public int Generation;           // _topologyGeneration when it was placed (Verify only)
        public DateTime HeldSinceUtc;    // when it was first held back because monitors were changing
        public bool WasHeld;
        public int Requeues;             // times a move was cut short by a display change
        public Native.RECT PlacedRect;   // where the move left it (Verify only)
    }

    public Engine(IEngineHost host)
    {
        _host = host;
        _hookProc = OnWinEvent;
        _selfElevated = WindowInfo.IsCurrentProcessElevated();
        _placeTimer = host.CreateTimer(100, ProcessPending);
        _learnTimer = host.CreateTimer(LearnIntervalMs, LearnTick);
        IEngineTimer? display = null;
        display = host.CreateTimer(1500, () =>
        {
            display!.Stop();
            RefreshMonitors();
            if (_identityBackfillDue) BackfillIdentity();
        });
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
        BackfillIdentity();
        _wasRemote = IsRemoteSession;
        if (_wasRemote == true) Log.Write(RemoteMessage(true));
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
        // Placements in progress are now stale, and new windows wait until Windows has settled (see HoldIfChanging).
        _lastDisplayEventUtc = DateTime.UtcNow;
        _topologyGeneration++;
        MonitorService.ClearIdentityCache(); // another monitor may be on a port now
        OnSessionChanged();                  // Remote Desktop replaces the monitors: notice it here too
        RefreshMonitors();
        _displayTimer.Stop();
        _displayTimer.Start();
    }

    /// <summary>
    /// The host calls this when the session changes (Remote Desktop connected or disconnected, console reconnected).
    /// While connected through Remote Desktop, nothing is learned and (unless you chose otherwise) nothing is moved.
    /// </summary>
    public void OnSessionChanged()
    {
        if (_disposed) return;
        bool remote = IsRemoteSession;
        if (_wasRemote == remote) return;
        _wasRemote = remote;
        _lastMonitorChangeUtc = DateTime.UtcNow; // back on this PC's monitors: let them settle before learning again
        ResetStreak();
        if (_started) Log.Write(RemoteMessage(remote));
        Changed?.Invoke();
    }

    private string RemoteMessage(bool remote) => remote
        ? (Settings.PauseDuringRemoteDesktop
            ? "Remote Desktop session: not moving windows or learning until you're back on this PC."
            : "Remote Desktop session: still moving windows (you chose that), but not learning.")
        : "Back on this PC's own monitors (Remote Desktop ended): moving windows again.";

    public void RefreshMonitors()
    {
        var list = MonitorService.Enumerate();
        _monitors = list;
        foreach (var m in list)
        {
            foreach (var t in m.Targets)
            {
                var custom = Settings.MonitorNicknames.FirstOrDefault(n => MatchesTarget(n.Monitor, t));
                t.Nickname = string.IsNullOrWhiteSpace(custom?.Nickname) ? null : custom!.Nickname;
                t.Number = custom?.Number;
            }
            // A duplicated display is temporary only if every monitor showing it is (casting a copy of the main monitor isn't).
            m.IsTemporary = m.Targets.All(t => IsTemporaryTarget(t));
        }
        // Your numbers decide the order (numbered first, 1, 2, 3…); otherwise main first, then left to right.
        list = list.OrderBy(m => m.Number ?? int.MaxValue).ThenByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
        _monitors = list;
        foreach (var m in list) m.IdentityNote = IdentityNote(m);

        string layout = MonitorService.LayoutSignature(list);
        if (layout != _layoutSignature)
        {
            if (_layoutSignature.Length > 0) // (not the first read at startup)
            {
                var now = DateTime.UtcNow;
                _lastMonitorChangeUtc = now;
                _lastDisplayEventUtc = now;
                _topologyGeneration++;
                _identityBackfillDue = true;
            }
            _layoutSignature = layout;
            if (list.FirstOrDefault(m => m.IsDuplicated) is MonitorInfo dup)
                Log.Write($"Duplicated monitors ({dup.Name}): learning paused while they show the same picture.");
        }

        // Only refreshing names and notes (and Changed) for a change you can see: names, numbers, temporary, main.
        string sig = string.Join("|", list.Select(m => $"{m.DeviceName}={m.Name}{(m.IsPrimary ? "*" : "")}{(m.IsTemporary ? "~" : "")}@{m.Bounds}/{m.Work}"));
        if (sig == _monitorSignature) return;
        _monitorSignature = sig;
        Log.Write("Monitors: " + string.Join(", ", list.Select(m =>
            $"{m.Label} [{m.DeviceName}{string.Concat(m.Targets.Select(t => $" {t.Connection} {t.Model} {t.Serial}".TrimEnd()))}" +
            $"{(m.IsTemporary ? ", temporary" : "")}]")));
        if (_started) Changed?.Invoke();
    }

    /// <summary>"Same model as 1 · Main: told apart by…", for a monitor that shares its model with another one.</summary>
    private string? IdentityNote(MonitorInfo m)
    {
        if (m.IsDuplicated) return null;
        var t = m.Targets[0];
        var twin = _monitors.Where(o => !ReferenceEquals(o, m))
            .FirstOrDefault(o => o.Targets.Any(x => string.IsNullOrEmpty(t.Model) ? SameModelName(x, t) : t.Model == x.Model));
        if (twin == null) return null;
        var other = twin.Targets.First(x => string.IsNullOrEmpty(t.Model) ? SameModelName(x, t) : t.Model == x.Model);
        string how = !string.IsNullOrEmpty(t.Serial) && !string.IsNullOrEmpty(other.Serial) && t.Serial != other.Serial
            ? "its serial number"
            : !string.IsNullOrEmpty(t.DevicePath) && !string.IsNullOrEmpty(other.DevicePath) ? "the port it's plugged into"
            : "Windows' display number";
        return $"Same model as {twin.Name}: told apart by {how}.";
    }

    private static bool SameModelName(MonitorTarget a, MonitorTarget b)
    {
        static string Model(string n) { int i = n.LastIndexOf(" (DISPLAY", StringComparison.Ordinal); return i > 0 ? n[..i] : n; }
        return !a.FriendlyName.StartsWith("Display ", StringComparison.Ordinal) && Model(a.FriendlyName) == Model(b.FriendlyName);
    }

    /// <summary>Is this monitor temporary: your choice for it, else wireless and virtual monitors are.</summary>
    private bool IsTemporaryTarget(MonitorTarget t) =>
        Settings.MonitorOptions.FirstOrDefault(o => MatchesTarget(o.Monitor, t))?.Temporary ?? t.IsTemporaryByDefault;

    private MonitorInfo? MonitorByDevice(string? device) =>
        device == null ? null : _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connected monitor an app rule points to, or null if it isn't connected.</summary>
    public MonitorInfo? Resolve(AppRule rule) => MonitorMatcher.Find(_monitors, rule.MonitorName, rule.MonitorDevice, rule.MonitorHardware);

    private bool Matches(MonitorRef r, MonitorInfo m) =>
        string.Equals(MonitorMatcher.Find(_monitors, r.Name, r.Device, r.Hardware)?.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase);

    private bool MatchesTarget(MonitorRef r, MonitorTarget t) =>
        ReferenceEquals(MonitorMatcher.FindTarget(_monitors, r.Name, r.Device, r.Hardware)?.Target, t);

    /// <summary>A saved reference to a connected monitor (device, model name and hardware identity).</summary>
    private static MonitorRef RefTo(MonitorInfo m) => new() { Device = m.DeviceName, Name = m.FriendlyName, Hardware = m.Hardware };

    /// <summary>A saved reference to one physical monitor (one of a duplicated display's monitors).</summary>
    private static MonitorRef RefTo(MonitorInfo m, MonitorTarget t) => new() { Device = m.DeviceName, Name = t.FriendlyName, Hardware = t.Hardware };

    /// <summary>Points a rule at a monitor: its device, model name and hardware identity.</summary>
    private static void SetMonitor(AppRule rule, MonitorInfo? m)
    {
        rule.MonitorDevice = m?.DeviceName;
        rule.MonitorName = m?.FriendlyName;
        rule.MonitorHardware = m == null ? null : MonitorHardware.Clean(m.Hardware);
    }

    /// <summary>
    /// Older versions saved monitors by Windows' display number and model name only. The first time each saved monitor is
    /// seen connected, add its hardware identity (keeping the old fields, so an older version still finds it), and keep
    /// the port path current when a cable has moved. Runs at startup and after monitors change, never while placing a window.
    /// </summary>
    private void BackfillIdentity()
    {
        _identityBackfillDue = false;
        if (_monitors.Count == 0 || IsRemoteSession) return; // the remote session's monitors aren't this PC's
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        bool changed = false;
        foreach (var rule in Settings.Apps)
        {
            if (rule.MonitorDevice == null && rule.MonitorName == null) continue;
            var hw = IdentityToSave(rule.MonitorName, rule.MonitorDevice, rule.MonitorHardware);
            if (hw == null) continue;
            rule.MonitorHardware = hw;
            changed = true;
        }
        foreach (var r in Settings.Apps.SelectMany(a => a.AllowedMonitors)
                     .Concat(Settings.MonitorNicknames.Select(n => n.Monitor))
                     .Concat(Settings.MonitorOptions.Select(o => o.Monitor)))
        {
            var hw = IdentityToSave(r.Name, r.Device, r.Hardware);
            if (hw == null) continue;
            r.Hardware = hw;
            changed = true;
        }
        if (!changed) return;
        Save();
        Log.Write("Saved the hardware identity of your monitors, so they're recognised even if Windows renumbers them.");
    }

    /// <summary>
    /// The hardware identity to save for a saved monitor that is connected now, or null to leave it as it is.
    /// Only a sure match is saved: the same port, or the same model and serial number (the cable moved). An entry
    /// with no port or serial saved yet is only filled in when its display number and model name both agree, so a
    /// guess between two identical monitors is never made permanent.
    /// </summary>
    private MonitorHardware? IdentityToSave(string? name, string? device, MonitorHardware? saved)
    {
        var hit = MonitorMatcher.FindTarget(_monitors, name, device, saved);
        if (hit is not { } h || h.Monitor.IsDuplicated) return null;
        var t = h.Target;
        bool samePort = !string.IsNullOrEmpty(saved?.DevicePath)
                        && string.Equals(saved!.DevicePath, t.DevicePath, StringComparison.OrdinalIgnoreCase);
        bool sameSerial = !string.IsNullOrEmpty(saved?.Serial) && saved!.Serial == t.Serial
                          && string.Equals(saved.Model, t.Model, StringComparison.OrdinalIgnoreCase);
        bool nothingSure = string.IsNullOrEmpty(saved?.DevicePath) && string.IsNullOrEmpty(saved?.Serial);
        bool sameNumberAndName = string.Equals(h.Monitor.DeviceName, device, StringComparison.OrdinalIgnoreCase)
                                 && (name == null || t.FriendlyName == name);
        if (!samePort && !sameSerial && !(nothingSure && sameNumberAndName)) return null;
        var hw = MonitorHardware.Clean(t.Hardware);
        return hw == null || hw.SameAs(saved) ? null : hw;
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
        if (rule.AllowedMonitors.Count == 0) return null;
        var others = AllowedConnected(rule);                     // else another of its monitors, a permanent one first
        return others.FirstOrDefault(o => !o.IsTemporary) ?? others.FirstOrDefault();
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

    /// <summary>
    /// While monitors are changing (a TV switching on, waking from sleep, a dock reconnecting), new windows wait until
    /// about 2 s after the last change, 3 s at most, so they aren't placed on a monitor that is about to go away or
    /// centred on an out-of-date work area. Returns true if the window should wait (its due time is set).
    /// </summary>
    private bool HoldIfChanging(Pending p, DateTime now)
    {
        var settledAt = _lastDisplayEventUtc.AddMilliseconds(HoldAfterChangeMs);
        if (now >= settledAt) return false;
        if (p.HeldSinceUtc == default)
        {
            p.HeldSinceUtc = now;
            p.GiveUp = p.GiveUp.AddMilliseconds(MaxHoldMs); // waiting doesn't count against the window
        }
        var limit = p.HeldSinceUtc.AddMilliseconds(MaxHoldMs);
        if (now >= limit) return false;
        p.WasHeld = true;
        p.Due = settledAt < limit ? settledAt : limit;
        return true;
    }

    private void TryPlace(IntPtr hwnd, Pending p, DateTime now)
    {
        if (HoldIfChanging(p, now)) return;
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
        if (!Settings.Enabled || IsPausedForRemoteDesktop) return;

        var app = WindowInfo.Identify(hwnd);
        if (app == null) return;
        if (OnlyAppKey != null && !string.Equals(app.Key, OnlyAppKey, StringComparison.OrdinalIgnoreCase)) return;
        string title = WindowInfo.GetTitle(hwnd);

        var rule = Settings.Find(app.Key);
        if (rule != null) rule = RefreshRuleDetails(rule, app, hwnd);
        try { PlaceNewWindow(hwnd, app, rule, title, p); }
        finally { WindowHandled?.Invoke(app.Key); }
    }

    /// <summary>A move was cut short because monitors changed: try the window again once Windows has settled.</summary>
    private void Requeue(IntPtr hwnd, Pending p, string what)
    {
        if (p.Requeues >= MaxRequeues || !Native.IsWindow(hwnd)) { Log.Write($"Gave up on {what}: monitors kept changing."); return; }
        var now = DateTime.UtcNow;
        _handled.Remove(hwnd);
        _pending[hwnd] = new Pending
        {
            Due = now.AddMilliseconds(HoldAfterChangeMs), GiveUp = now.AddMilliseconds(GiveUpMs + MaxHoldMs),
            Requeues = p.Requeues + 1, WasHeld = true, HeldSinceUtc = now,
        };
        if (!_placeTimer.IsEnabled) _placeTimer.Start();
        Log.Write($"Monitors changed while placing {what}; trying again when they've settled.");
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

    private void PlaceNewWindow(IntPtr hwnd, AppIdentity app, AppRule? rule, string title, Pending p)
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
                target = FallbackMonitor;
                reason += $"; its monitor ({rule.MonitorName ?? rule.MonitorDevice}) isn't connected, so " +
                          (target?.IsPrimary == true ? "main monitor" : target?.Name);
            }
        }
        else
        {
            if (!Settings.MoveUnknownToMain) return;
            target = FallbackMonitor;
            reason = target?.IsPrimary == true ? "new app → main monitor" : $"new app → {target?.Name} (the main monitor is temporary)";
        }
        if (target == null) return;
        if (p.WasHeld) reason += "; held during a display change";

        // The taskbar may have moved or changed size since monitors were last read: use the current work area.
        string targetDevice = target.DeviceName;
        int before = _topologyGeneration;
        RefreshMonitors();
        if (_topologyGeneration != before)
        {
            // The monitors changed just now (Windows hasn't said so yet): the target may have been renumbered. Wait.
            Requeue(hwnd, p, $"{app.Key} \"{title}\"");
            return;
        }
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

        int generation = _topologyGeneration;
        var result = WindowMover.Move(hwnd, target, () => generation == _topologyGeneration);
        string from = current?.Name ?? "?";
        if (result == WindowMover.Result.Aborted)
        {
            Requeue(hwnd, p, $"{app.Key} \"{title}\"");
            return;
        }
        if (result == WindowMover.Result.Moved)
        {
            if (reason.Contains("isn't connected")) _fallbackPlaced.Add(hwnd);
            Note($"Moved {app.DisplayName} from {from} to {target.Name} ({reason}).", $"{app.Key} \"{title}\"");
            // Check once more shortly: some apps restore their saved position after showing.
            Native.GetWindowRect(hwnd, out var placed);
            _pending[hwnd] = new Pending
            {
                Verify = true, Due = DateTime.UtcNow.AddMilliseconds(VerifyDelayMs),
                TargetDevice = target.DeviceName, AppKey = app.Key, FromDevice = current?.DeviceName ?? "",
                Generation = generation, PlacedRect = placed, Requeues = p.Requeues,
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
        if (p.Generation != _topologyGeneration)
        {
            // Monitors changed since it was placed: where it is now says nothing about the app. Place it again once settled.
            Requeue(hwnd, p, p.AppKey);
            return;
        }
        var target = MonitorByDevice(p.TargetDevice);
        var current = MonitorOfWindow(hwnd);
        if (target == null || current == null) return;
        if (current.DeviceName == target.DeviceName)
        {
            // Still where we put it. An app that rescaled itself for the monitor's DPI after the move can now stick out
            // past the edge: pull it back in, but only if it's still where we left it (otherwise you moved it).
            Native.GetWindowRect(hwnd, out var now);
            if (Math.Abs(now.Left - p.PlacedRect.Left) <= 1 && Math.Abs(now.Top - p.PlacedRect.Top) <= 1
                && WindowMover.KeepInside(hwnd, target))
                Log.Write($"{p.AppKey} changed size after the move; kept it fully on {target.Name}.");
            return;
        }
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
        if (_identityBackfillDue && DateTime.UtcNow - _lastDisplayEventUtc > TimeSpan.FromMilliseconds(HoldAfterChangeMs))
            BackfillIdentity();
        if (!_learning) return;

        // Don't learn while monitors are changing (sleep, display driver reset, a game switching resolution…) or
        // when only one monitor is connected: then you use apps where they *have* to be, not where you want them.
        // (Temporary monitors don't count.) The same goes for a Remote Desktop session (the remote PC's monitors
        // replace this one's) and for duplicated monitors (a "home" there would really be two monitors).
        if (_monitors.Count(m => !m.IsTemporary) < 2 || IsRemoteSession || HasDuplicatedMonitors
            || DateTime.UtcNow - _lastMonitorChangeUtc < TimeSpan.FromSeconds(MonitorSettleSeconds))
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
        if (_fallbackPlaced.Contains(hwnd) && mon.DeviceName == FallbackMonitor?.DeviceName) return;
        if (rule != null && rule.FollowLastUsed)
        {
            // Remember the last monitor you used it on, if it's one of the monitors you picked for it.
            // A temporary monitor only counts if you ticked it yourself.
            if (!IsAllowed(rule, mon) || Resolve(rule)?.DeviceName == mon.DeviceName) return;
            if (mon.IsTemporary && rule.AllowedMonitors.Count == 0) return;
            string? was = rule.MonitorName ?? rule.MonitorDevice;
            SetMonitor(rule, mon); rule.UpdatedUtc = DateTime.UtcNow;
            Save();
            Log.Write($"Last used: {rule.DisplayName} is now on {mon.Name}{(was == null ? "" : $" (was {was})")}, so it opens there next.  [{rule.Key} {mon.DeviceName}]");
            Changed?.Invoke();
            return;
        }
        if (!Settings.LearnAutomatically) return;
        if (rule != null && (rule.Pinned || rule.NeverMove)) return;
        if (rule == null && WindowInfo.IsOneOffApp(app.Key)) return; // setup / uninstall / update programs
        if (mon.IsTemporary) return; // wireless, virtual…: never an app's home unless you set it
        if (rule != null && Resolve(rule)?.DeviceName == mon.DeviceName)
        {
            if (rule.MonitorDevice != mon.DeviceName || rule.MonitorName != mon.FriendlyName
                || !mon.Hardware.SameAs(rule.MonitorHardware ?? new MonitorHardware()))
            {   // same screen, Windows just renamed it: keep the record current quietly
                SetMonitor(rule, mon); Save();
            }
            return;
        }

        string? old = rule == null ? null : (rule.MonitorName ?? rule.MonitorDevice);
        if (rule == null) { rule = new AppRule { Key = app.Key, LaunchTarget = WindowInfo.GetLaunchTarget(hwnd) }; Settings.Apps.Add(rule); }
        bool keepName = app.Key.StartsWith(WindowInfo.WebAppKeyPrefix, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(rule.DisplayName) && rule.DisplayName != rule.Key;
        if (!keepName) rule.DisplayName = app.DisplayName;
        SetMonitor(rule, mon);
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
        if (monitor != null) SetMonitor(rule, monitor);
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
        rule.AllowedMonitors = allowed.Select(RefTo).ToList();
        // Start from where it is now / was last, if that's allowed; otherwise the first allowed monitor.
        var start = new[] { currentMonitor, Resolve(rule) }.FirstOrDefault(m => m != null && IsAllowed(rule, m))
                    ?? AllowedConnected(rule).FirstOrDefault();
        SetMonitor(rule, start);
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
        m = MonitorByDevice(m.DeviceName) ?? m; // the window's copy may be from before the last refresh
        nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        string old = m.Name;
        Settings.MonitorNicknames.RemoveAll(n => MatchesTarget(n.Monitor, m.Targets[0]));
        if (number != null)
            foreach (var other in Settings.MonitorNicknames.Where(n => n.Number == number)) other.Number = null;
        Settings.MonitorNicknames.RemoveAll(n => string.IsNullOrWhiteSpace(n.Nickname) && n.Number == null);
        if (nickname != null || number != null)
            Settings.MonitorNicknames.Add(new MonitorNickname
            {
                Monitor = RefTo(m),
                Nickname = nickname ?? "", Number = number,
            });
        Save();
        RefreshMonitors();
        var now = _monitors.FirstOrDefault(x => x.DeviceName == m.DeviceName);
        Note($"Monitor {old} is now shown as {now?.Name ?? m.FriendlyName}.", $"[{m.DeviceName}]");
        Changed?.Invoke();
    }

    /// <summary>
    /// Marks a monitor as temporary (apps aren't learned there, and it's never used instead of a monitor that's off)
    /// or as a normal one. Wireless and virtual monitors are temporary until you say otherwise.
    /// </summary>
    public void SetMonitorTemporary(MonitorInfo m, bool temporary)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        m = MonitorByDevice(m.DeviceName) ?? m; // the window's copy may be from before the last refresh
        var t = m.Targets[0];
        Settings.MonitorOptions.RemoveAll(o => MatchesTarget(o.Monitor, t));
        if (temporary != t.IsTemporaryByDefault)
            Settings.MonitorOptions.Add(new MonitorOptions { Monitor = RefTo(m, t), Temporary = temporary });
        Save();
        RefreshMonitors();
        Note(temporary ? $"{m.Name} is a temporary monitor: apps aren't learned there."
                       : $"{m.Name} is a normal monitor: apps can be learned there.", $"[{m.DeviceName}]");
        Changed?.Invoke();
    }

    /// <summary>Whether to stop moving windows while this PC is used through Remote Desktop.</summary>
    public void SetPauseDuringRemoteDesktop(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.PauseDuringRemoteDesktop == on) return;
        Settings.PauseDuringRemoteDesktop = on; Save();
        Log.Write("While connected with Remote Desktop: " + (on ? "pause" : "keep moving windows"));
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

    /// <summary>What a move did.</summary>
    public enum Result
    {
        /// <summary>The window is on the target monitor now.</summary>
        Moved,
        /// <summary>It couldn't be moved (the app doesn't respond, or stayed where it was).</summary>
        Failed,
        /// <summary>Monitors changed while moving it; it can be tried again once they've settled.</summary>
        Aborted,
    }

    private const uint MoveFlags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
    private const int DpiWaitMs = 200;    // how long to wait for an app to rescale for the target's DPI
    private const int MaxFrameBorder = 64; // larger "invisible borders" are bogus values; ignore them

    /// <summary>Centers the window on the target's work area (size clamped to fit). Keeps it maximized if it was.</summary>
    public static bool MoveTo(IntPtr hwnd, MonitorInfo target) => Move(hwnd, target, null) == Result.Moved;

    /// <summary>
    /// Moves the window onto the target monitor, centred on its work area and sized to fit, keeping it maximized if it
    /// was. Before each step it checks that the monitor is still there as expected (Windows' own monitor list, and
    /// <paramref name="stillValid"/>): if monitors changed in between, it stops and returns <see cref="Result.Aborted"/>.
    /// </summary>
    public static Result Move(IntPtr hwnd, MonitorInfo target, Func<bool>? stillValid)
    {
        if (!Responds(hwnd)) return Result.Failed;
        if (!LiveWorkArea(target, out var wa) || stillValid?.Invoke() == false) return Result.Aborted;
        bool wasMaximized = Native.IsZoomed(hwnd);
        if (wasMaximized) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        try
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return Result.Failed;
            uint startDpi = Native.GetDpiForWindow(hwnd), targetDpi = MonitorDpi(target);

            // Step 1: jump onto the target keeping the size, so the app can rescale for that monitor's DPI.
            var step1 = Fit(r, Frame(hwnd, r), wa);
            Native.SetWindowPos(hwnd, IntPtr.Zero, step1.Left, step1.Top, 0, 0, MoveFlags | Native.SWP_NOSIZE);
            WaitForDpi(hwnd, startDpi, targetDpi);

            // Step 2: with the size it has now, clamp and center (the visible frame, not the invisible resize borders).
            if (!LiveWorkArea(target, out wa) || stillValid?.Invoke() == false) return Result.Aborted;
            if (Native.GetWindowRect(hwnd, out r))
            {
                var step2 = Fit(r, Frame(hwnd, r), wa);
                Native.SetWindowPos(hwnd, IntPtr.Zero, step2.Left, step2.Top, step2.Width, step2.Height, MoveFlags);
            }
            if (!wasMaximized) KeepInside(hwnd, wa); // an app with a minimum size larger than the monitor: keep its title bar on screen
        }
        finally
        {
            if (wasMaximized) Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
        }
        return string.Equals(MonitorService.DeviceOfWindow(hwnd), target.DeviceName, StringComparison.OrdinalIgnoreCase)
            ? Result.Moved : Result.Failed;
    }

    /// <summary>
    /// If the window (its visible frame) sticks out past the monitor's work area, shrinks it to fit and moves it just
    /// far enough to be fully on screen. Returns true if it had to. Maximized and minimized windows are left alone.
    /// </summary>
    public static bool KeepInside(IntPtr hwnd, MonitorInfo target) =>
        LiveWorkArea(target, out var wa) && KeepInside(hwnd, wa);

    private static bool KeepInside(IntPtr hwnd, Native.RECT wa)
    {
        if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd) || !Native.GetWindowRect(hwnd, out var r)) return false;
        var frame = Frame(hwnd, r);
        var inside = Clamp(r, frame, wa);
        if (inside.Left == r.Left && inside.Top == r.Top && inside.Width == r.Width && inside.Height == r.Height) return false;
        Native.SetWindowPos(hwnd, IntPtr.Zero, inside.Left, inside.Top, inside.Width, inside.Height, MoveFlags);
        return true;
    }

    /// <summary>
    /// Where a window goes to be centred on a work area: its visible part (window rect minus the invisible
    /// <paramref name="frame"/> borders) is shrunk to fit and centred. Returns the window rect to set.
    /// </summary>
    public static Native.RECT Fit(Native.RECT window, Native.RECT frame, Native.RECT work)
    {
        int vw = Math.Min(window.Width - frame.Left - frame.Right, work.Width);
        int vh = Math.Min(window.Height - frame.Top - frame.Bottom, work.Height);
        int left = work.Left + (work.Width - vw) / 2, top = work.Top + (work.Height - vh) / 2;
        return Outer(left, top, vw, vh, frame);
    }

    /// <summary>
    /// The window rect that keeps its visible part fully inside the work area, moving it as little as possible
    /// (and shrinking it only if it's larger than the work area).
    /// </summary>
    public static Native.RECT Clamp(Native.RECT window, Native.RECT frame, Native.RECT work)
    {
        int vLeft = window.Left + frame.Left, vTop = window.Top + frame.Top;
        int vw = Math.Min(window.Width - frame.Left - frame.Right, work.Width);
        int vh = Math.Min(window.Height - frame.Top - frame.Bottom, work.Height);
        int left = Math.Max(work.Left, Math.Min(vLeft, work.Right - vw));
        int top = Math.Max(work.Top, Math.Min(vTop, work.Bottom - vh));
        if (vw == window.Width - frame.Left - frame.Right && vh == window.Height - frame.Top - frame.Bottom
            && left == vLeft && top == vTop) return window;
        return Outer(left, top, vw, vh, frame);
    }

    private static Native.RECT Outer(int visibleLeft, int visibleTop, int visibleWidth, int visibleHeight, Native.RECT frame) => new()
    {
        Left = visibleLeft - frame.Left, Top = visibleTop - frame.Top,
        Right = visibleLeft + visibleWidth + frame.Right, Bottom = visibleTop + visibleHeight + frame.Bottom,
    };

    /// <summary>
    /// The window's invisible resize borders (left, top, right, bottom thickness): the difference between its window
    /// rect and its visible frame. Zero when Windows doesn't say.
    /// </summary>
    private static Native.RECT Frame(IntPtr hwnd, Native.RECT window)
    {
        if (Native.DwmGetWindowRect(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var v, Marshal.SizeOf<Native.RECT>()) != 0)
            return default;
        var f = new Native.RECT
        {
            Left = v.Left - window.Left, Top = v.Top - window.Top, Right = window.Right - v.Right, Bottom = window.Bottom - v.Bottom,
        };
        bool sane = f.Left is >= 0 and <= MaxFrameBorder && f.Top is >= 0 and <= MaxFrameBorder
                    && f.Right is >= 0 and <= MaxFrameBorder && f.Bottom is >= 0 and <= MaxFrameBorder;
        return sane ? f : default;
    }

    /// <summary>
    /// The target's current work area, straight from Windows. False if that monitor isn't where it was (monitors
    /// changed since they were last read).
    /// </summary>
    private static bool LiveWorkArea(MonitorInfo target, out Native.RECT work)
    {
        work = target.Work;
        var b = target.Bounds;
        IntPtr hMon = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, Native.MONITOR_DEFAULTTONULL);
        if (hMon == IntPtr.Zero) return false;
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(hMon, ref mi)) return false;
        var m = mi.rcMonitor;
        if (!string.Equals(mi.szDevice, target.DeviceName, StringComparison.OrdinalIgnoreCase)
            || m.Left != b.Left || m.Top != b.Top || m.Right != b.Right || m.Bottom != b.Bottom) return false;
        work = mi.rcWork;
        return true;
    }

    /// <summary>The target monitor's DPI (96 = 100 % scale), or 0 if Windows doesn't say.</summary>
    private static uint MonitorDpi(MonitorInfo target)
    {
        var b = target.Bounds;
        IntPtr hMon = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, Native.MONITOR_DEFAULTTONULL);
        try { return hMon != IntPtr.Zero && Native.GetDpiForMonitor(hMon, Native.MDT_EFFECTIVE_DPI, out uint x, out _) == 0 ? x : 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    /// <summary>
    /// Moving between monitors with different scaling: give a per-monitor-aware app up to 200 ms to rescale for the
    /// new DPI before its size is measured. (Other apps are scaled by Windows itself, at once.)
    /// </summary>
    private static void WaitForDpi(IntPtr hwnd, uint startDpi, uint targetDpi)
    {
        if (targetDpi == 0 || startDpi == 0 || startDpi == targetDpi) return;
        try
        {
            if (Native.GetAwarenessFromDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(hwnd)) != Native.DPI_AWARENESS_PER_MONITOR_AWARE)
                return;
            int start = Environment.TickCount;
            while (Native.GetDpiForWindow(hwnd) != targetDpi && unchecked(Environment.TickCount - start) < DpiWaitMs)
                Thread.Sleep(10);
        }
        catch (EntryPointNotFoundException) { /* older Windows: no DPI per window */ }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace MonitorWindowControl;

/// <summary>
/// Watches for new app windows, moves them to their home monitor, and learns homes
/// from the foreground window. Everything runs on the host's thread (see <see cref="IEngineHost"/>).
/// The same engine runs in the settings app (tray mode) and in the tiny helper.
/// </summary>
public sealed partial class Engine : IDisposable
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
    private const int SettledMs = 3000;          // monitors count as settled this long after the last display change
    private const int SnapshotIntervalMs = 5 * 60 * 1000; // how often to note where windows are (in memory only)
    private const int RestoreWaitSeconds = 60;   // after waking up, wait at most this long for every monitor to come back
    private const int RectSlack = 2;             // pixels a window may differ by and still count as "where it was left"
    private const int KeepMainMaxFixes = 2;      // "Keep my main monitor" changes it back at most this often…
    private static readonly TimeSpan KeepMainPeriod = TimeSpan.FromMinutes(5); // …in this long, then stops
    private const int UncloakNewSeconds = 30;    // a window that Windows shows (uncloaks) this soon after it appeared is new
    private const int SignInMaxSeconds = 180;    // "opened while signing in": this program started at most this long after sign-in…
    private const int SignInAppAgeSeconds = 120; // …and the window's app started at most this long ago
    private const int SignInInUseMs = 3000;      // the window in front, used this long after its app started, is left alone
    private const double PositionChange = 0.03;  // "Where I last had it" is saved again only when the window moved this much (3 %)
    private const int MouseIdleSeconds = 120;    // the pointer hasn't moved this long: use the monitor of the window you're using
    private static readonly TimeSpan GameHoldMax = TimeSpan.FromMinutes(15); // windows held during a game older than this stay put
    private const int MaxCorrections = 3;        // "Keep correcting for": an app is moved back at most this often per window
    private const double DialogMaxShare = 0.6;   // a dialog this large (of the monitor's width or height) or larger is left alone
    private const int RecentMax = 10;            // Recent activity keeps this many events (in memory only)
    private const int ParkedCoordinate = -30000; // windows Windows parks here (minimized) aren't "off-screen"
    private const int HotKeyBaseId = 0x100;      // keyboard shortcut ids: base + action index * 2 (+1 for the Shift version)

    /// <summary>"Treat as switched off: for 1 hour".</summary>
    public static readonly TimeSpan OffForAnHour = TimeSpan.FromHours(1);

    private readonly IEngineHost _host;
    private readonly IMonitorSource _monitorSource;
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
    private readonly IEngineTimer _snapshotTimer;
    private List<MonitorInfo> _monitors = new();  // as the app uses them: a split monitor is one entry per area
    private List<MonitorInfo> _physical = new();  // as Windows has them (Make main, Disconnect, monitor changes)
    private string _monitorSignature = "";
    private string _layoutSignature = "";
    private DateTime _lastMonitorChangeUtc = DateTime.MinValue;
    private DateTime _lastDisplayEventUtc = DateTime.MinValue; // last "display changed" from Windows or a new layout
    private int _topologyGeneration;                            // +1 on every display change: moves started before are stale
    private bool _identityBackfillDue;                          // save hardware identity into older saved monitors
    private bool? _wasRemote;
    private DateTime _nextOffExpiryUtc = DateTime.MaxValue;     // when a "switched off for 1 hour" ends

    // Work done once monitors have settled after a change (see AfterMonitorChanges).
    private bool _vanishScanDue, _returnCheckDue, _keepMainDue;
    private DateTime _returnNotBeforeUtc;

    // Where app windows are (in memory only, never saved): taken every few minutes, when the session locks and before sleep.
    private Dictionary<IntPtr, WindowSnap> _snapshot = new();
    private IntPtr _lastSnapForeground;
    private bool _locked;

    // "Put windows back after sleep".
    private Dictionary<IntPtr, WindowSnap>? _sleepSnapshot;
    private Dictionary<IntPtr, Native.RECT>? _restoreBaseline; // where windows were once things settled after waking
    private DateTime _restoreBaselineUtc, _restoreDeadlineUtc;
    private int _restoreBaselineGeneration;
    private bool _restoreDue;
    private IntPtr _maximizeHook;                           // a window was maximized (only while a monitor is split)
    private readonly Dictionary<IntPtr, (DateTime First, int Count)> _maximizedFills = new();
    private IntPtr _moveHook;                               // you finished dragging or resizing a window…
    private readonly HashSet<IntPtr> _movedSinceWake = new(); // …while windows wait to be put back after sleep

    // "Keep my main monitor".
    private readonly List<DateTime> _mainFixes = new();
    private bool _keepMainNoticeShown;

    // Late uncloaking: new windows Windows still hides (cloaked), with when they were first seen.
    private IntPtr _uncloakHook;
    private readonly Dictionary<IntPtr, DateTime> _cloakedNew = new();

    // "Also place apps that opened while signing in".
    private readonly HashSet<IntPtr> _signInWindows = new();
    private readonly List<string> _signInPlaced = new();

    // "Pause while a game or presentation is running".
    private bool _gamePaused;
    private readonly Dictionary<IntPtr, DateTime> _heldForGame = new(); // new windows that wait until the game ends

    // "The monitor I'm using (mouse pointer)".
    private Native.POINT _lastCursor;
    private DateTime _lastCursorMoveUtc = DateTime.UtcNow;

    // "Where I last had it": the last sample of the window you're using (saved once it stays put).
    private IntPtr _positionWindow;
    private WindowPosition? _positionSample;

    // Advanced timing: how often in a row each app moved itself back after being moved.
    private readonly Dictionary<string, int> _jumpBacks = new(StringComparer.OrdinalIgnoreCase);

    // Recent activity, with what's needed to undo each event (in memory only; nothing about windows is saved).
    private readonly List<ActivityEntry> _recent = new();
    private readonly Dictionary<int, UndoData> _undo = new();
    private int _nextActivityId = 1;
    private readonly Dictionary<string, int> _placementUndos = new(StringComparer.OrdinalIgnoreCase); // undone placements per app
    private readonly HashSet<string> _unlearned = new(StringComparer.OrdinalIgnoreCase); // "app|device" you undid a learn of
    private readonly Dictionary<string, DateTime> _noticeDay = new(StringComparer.OrdinalIgnoreCase); // a notice per app per day

    // Tidy up: windows you moved yourself this session (left alone unless you ask), and the off-screen check.
    private readonly HashSet<IntPtr> _movedByYou = new();
    private bool _rescueDue;

    // Keyboard shortcuts: what's registered now (id → keys), and the last app window in front (for the tray menu).
    private readonly Dictionary<int, (int Modifiers, int Key)> _hotKeys = new();
    private readonly HashSet<string> _hotKeyProblems = new(StringComparer.Ordinal);
    private IntPtr _foregroundHook;
    private IntPtr _lastAppWindow;

    private string? _streakKey, _streakDevice;
    private string? _learnedKey, _learnedDevice; // what the current streak already taught (don't redo it every 2 s)
    private int _streakCount, _tickCount;
    private bool _started, _disposed, _learning;

    public AppSettings Settings { get; private set; } = new();
    /// <summary>Settings your organisation manages (read at startup and whenever settings are re-read); they're locked.</summary>
    public PolicySettings Policy { get; private set; } = new();
    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public MonitorInfo? MainMonitor => _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    /// <summary>
    /// Where windows go when their monitor can't be used (and nothing else was chosen), and where apps it hasn't learned
    /// yet open: the main monitor, unless that one is temporary, switched off or kept for chosen apps (then the first
    /// monitor that isn't).
    /// </summary>
    public MonitorInfo? FallbackMonitor
    {
        get
        {
            static bool Open(MonitorInfo m) => !m.IsSwitchedOff && !m.OnlyChosenApps;
            var main = MainMonitor;
            if (main != null && Open(main) && !main.IsTemporary) return main;
            return _monitors.FirstOrDefault(m => Open(m) && !m.IsTemporary) ?? _monitors.FirstOrDefault(Open) ?? main;
        }
    }

    /// <summary>Raised for something worth a tray notice (title, text), such as the main monitor being changed back.</summary>
    public event Action<string, string>? Notice;

    /// <summary>
    /// Raised (only in the program that places windows) for a Recent activity event that should show a notification:
    /// one you turned on under Notifications, or feedback for a keyboard shortcut.
    /// </summary>
    public event Action<ActivityEntry>? ActivityNotice;

    /// <summary>The last events, newest first (see <see cref="Undo"/>).</summary>
    public IReadOnlyList<ActivityEntry> Recent => _recent;

    /// <summary>Keyboard shortcuts that another app already uses: an action, or an action + "+Shift".</summary>
    public IReadOnlyCollection<string> HotKeyProblems => _hotKeyProblems;

    /// <summary>True when "Keep my main monitor" stopped because Windows kept making another monitor main.</summary>
    public bool KeepMainStopped => Settings.KeepMainMonitor != null && Settings.KeepMainStopped;

    /// <summary>True while this PC is used through Remote Desktop (its own monitors are replaced by the remote ones).</summary>
    public static bool IsRemoteSession => Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0;

    /// <summary>True while windows aren't moved because of a Remote Desktop session (see <see cref="AppSettings.PauseDuringRemoteDesktop"/>).</summary>
    public bool IsPausedForRemoteDesktop => Settings.PauseDuringRemoteDesktop && IsRemoteSession;

    /// <summary>True while Windows duplicates a display on several monitors (learning is paused then).</summary>
    public bool HasDuplicatedMonitors => _monitors.Any(m => m.IsDuplicated);

    public string LastAction { get; private set; } = "";

    /// <summary>True while this engine watches for new windows (see <see cref="Activate"/>).</summary>
    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Launch shortcut and group sessions: only windows of these apps are placed (null = all apps).</summary>
    public HashSet<string>? OnlyAppKeys { get; set; }

    /// <summary>Are this app's windows placed by this engine (see <see cref="OnlyAppKeys"/>)?</summary>
    private bool PlacesApp(string key) => OnlyAppKeys == null || OnlyAppKeys.Contains(key);

    /// <summary>Raised (on the engine thread) when settings, rules or monitors change.</summary>
    public event Action? Changed;

    /// <summary>Raised when a new window of an app has been dealt with (moved, or already in place). Argument: app key.</summary>
    public event Action<string>? WindowHandled;

    /// <summary>Raised when an app window appears or one seen this session closes (so a list of open apps can update).</summary>
    public event Action? AppWindowsChanged;
    private readonly HashSet<IntPtr> _appWindows = new();
    private readonly Dictionary<IntPtr, Displaced> _displaced = new(); // windows that go back when their monitor is back

    /// <summary>
    /// A window that isn't on its monitor only because that monitor couldn't be used: a new window placed elsewhere
    /// (its app's monitor was disconnected, switched off or kept for chosen apps), or a window Windows moved off a
    /// monitor that went away. When the monitor is back, it moves back, unless you've moved it since.
    /// </summary>
    private sealed class Displaced
    {
        public string? AppKey;           // goes back to this app's monitor…
        public MonitorRef? Home;         // …or to this monitor (the one it was on when that went away)
        public WindowSnap? Saved;        // where it was on that monitor, to put it back exactly
        public string DroppedDevice = ""; // where it was left
        public Native.RECT DroppedRect;
        public long ProcessStart;        // a reused window handle belongs to another program
    }

    /// <summary>
    /// How to undo one Recent activity event: windows to put back where they were, and/or an app's choice to put back
    /// as it was. Kept in memory only.
    /// </summary>
    private sealed class UndoData
    {
        public readonly List<(IntPtr Hwnd, WindowSnap Was)> Windows = new();
        public string? RuleKey;       // this app's choice is put back…
        public AppRule? RuleBefore;   // …as it was (null = it wasn't saved: forget it again)
        public bool OnlyMonitor;      // (learned: only its monitor is put back)
        public string? LearnedOn;     // (learned: the monitor it was learned on, so it isn't learned there again)
        public string? PlacedApp;     // a new window of this app was placed: undoing that twice asks to stop moving it
        public string? PlacedAppName;
        public bool IsEmpty => Windows.Count == 0 && RuleKey == null;
    }

    /// <summary>What <see cref="Undo"/> did: an error to show, or an app to offer "Never move" for.</summary>
    public sealed record UndoOutcome(string? Error, string? StopMovingKey, string? StopMovingName);

    /// <summary>Where one app window was (kept in memory only).</summary>
    private sealed class WindowSnap
    {
        public MonitorRef Monitor = new();
        public Native.RECT MonitorBounds;
        public Native.RECT Rect;
        public bool Maximized;
        public long ProcessStart;
    }

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
        public DateTime FirstSeen;       // when the window first appeared (late uncloaking, "Wait before moving")
        public bool AtSignIn;            // opened while signing in, before this program started
        public Native.POINT Pointer;     // where the mouse pointer was when the window appeared
        public string? FocusDevice;      // the monitor of the window you were using then (if the pointer sat still for long)
        public bool IsDialog;            // a dialog centred over its app's window (Verify only)
        public IntPtr Owner;             // that app window (Verify only)
        public DateTime VerifyUntil;     // "Keep correcting for": check again until then (Verify only)
        public int Corrections;          // times it was moved back after moving itself (Verify only)
        public bool JumpedBack;          // it moved itself back at least once (Verify only)
        public WindowPosition? Wanted;   // "Where I last had it": the spot it was put (Verify only)
        public bool WantMaximized;       // "Maximized" (Verify only)
        public bool SizeChecked;         // its size was checked once (Verify only)
    }

    /// <summary>True while a game or presentation runs and "Pause while a game or presentation is running" is on.</summary>
    public bool IsPausedForGame => IsActive ? _gamePaused : GameOrPresentationRunning();

    /// <param name="host">Timers, keyboard shortcuts and posting to the engine's thread.</param>
    /// <param name="monitors">Where monitors are read from: Windows itself unless a test gives a made-up list.</param>
    public Engine(IEngineHost host, IMonitorSource? monitors = null)
    {
        _host = host;
        _monitorSource = monitors ?? SystemMonitorSource.Instance;
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
        _snapshotTimer = host.CreateTimer(SnapshotIntervalMs, TakeSnapshot);
    }

    /// <summary>The engine that places windows for this session (not a launch shortcut's, not a settings window's).</summary>
    private bool IsMainEngine => _activeMutex != null && IsActive;

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
        ApplyPolicy(save: true);
        if (importLegacy) ForgetLearnedOneOffApps();
        Log.Write($"{who} started (mode={Settings.RunMode}, enabled={Settings.Enabled}, learn={Settings.LearnAutomatically}, " +
                  $"unknown apps={(Settings.MoveUnknownToMain ? "main monitor" : "leave")}, admin={_selfElevated}).");
        BackfillIdentity();
        if (Settings.UsualSetup == null) NoteSetup(force: true); // (the setup you use now is the usual one)
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
        // count as new when they're shown, like an app being opened. Right after you sign in, windows of apps that
        // started with Windows before this program did are placed too (see QueueSignInWindows).
        _handled.Clear();
        foreach (var h in WindowInfo.EnumTopLevel())
            if (Native.IsWindowVisible(h)) _handled.Add(h);
        Native.GetCursorPos(out _lastCursor);
        _lastCursorMoveUtc = DateTime.UtcNow;
        if (claimActive && learn && OnlyAppKeys == null) QueueSignInWindows();

        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_SHOW, IntPtr.Zero,
            _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero)
        {
            Log.Write("ERROR: could not install the window hook; new windows won't be detected.");
            _pending.Clear(); _signInWindows.Clear();
            ReleaseActiveMutex();
            return false;
        }
        // New windows that Windows shows late (still cloaked when they appeared: Store apps, splash screens, other desktops).
        _uncloakHook = Native.SetWinEventHook(Native.EVENT_OBJECT_UNCLOAKED, Native.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero,
            _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_pending.Count > 0) _placeTimer.Start();
        _learning = learn;
        _learnTimer.Start(); // also keeps settings fresh when another process changes them
        if (IsMainEngine)
        {
            TakeSnapshot();
            _snapshotTimer.Start();
            _keepMainDue = true; // Windows may have made another monitor main before this started
            // Windows you drag after waking up are left where you put them (only fires when you drag one).
            _moveHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZEEND, Native.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero,
                _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            // The app window in front, for the tray menu's "Always open on…" (only fires when you switch windows).
            _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            NoteForeground(Native.GetForegroundWindow());
            UpdateHotKeys();
            UpdateMaximizeHook(); // (only while a monitor is split into areas)
            ListenForLaunches(); // launch shortcuts that open an app on a chosen monitor
        }
        return true;
    }

    /// <summary>Stops watching windows (settings stay loaded; the window/UI keeps working).</summary>
    public void Deactivate()
    {
        _placeTimer.Stop(); _learnTimer.Stop(); _snapshotTimer.Stop();
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (_moveHook != IntPtr.Zero) { Native.UnhookWinEvent(_moveHook); _moveHook = IntPtr.Zero; }
        if (_uncloakHook != IntPtr.Zero) { Native.UnhookWinEvent(_uncloakHook); _uncloakHook = IntPtr.Zero; }
        if (_foregroundHook != IntPtr.Zero) { Native.UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_maximizeHook != IntPtr.Zero) { Native.UnhookWinEvent(_maximizeHook); _maximizeHook = IntPtr.Zero; }
        _maximizedFills.Clear();
        UnregisterHotKeys();
        StopListeningForLaunches();
        _movedByYou.Clear(); _rescueDue = false; _lastAppWindow = IntPtr.Zero;
        _pending.Clear(); _handled.Clear(); _appWindows.Clear(); _displaced.Clear(); ResetStreak();
        _cloakedNew.Clear(); _signInWindows.Clear(); _signInPlaced.Clear(); _heldForGame.Clear(); _gamePaused = false;
        _positionWindow = IntPtr.Zero; _positionSample = null;
        _snapshot.Clear(); _sleepSnapshot = null; _restoreBaseline = null; _restoreDue = false; _movedSinceWake.Clear();
        _vanishScanDue = _returnCheckDue = _keepMainDue = false;
        ReleaseActiveMutex();
    }

    private void ReleaseActiveMutex()
    {
        if (_activeMutex == null) return;
        try { _activeMutex.ReleaseMutex(); } catch { }
        _activeMutex.Dispose(); _activeMutex = null;
    }

    /// <summary>
    /// "Also place apps that opened while signing in": apps that start with Windows (Discord, Steam, Spotify…) often
    /// show their windows before this program runs. Only right after you sign in (this program started at most 3
    /// minutes after sign-in), windows whose app started at most 2 minutes ago are placed like new windows. Restarting
    /// this program later in the session never does this, because sign-in was longer ago. The window in front is left
    /// alone if you've already used it, and windows on other virtual desktops are left alone too.
    /// </summary>
    private void QueueSignInWindows()
    {
        if (!Settings.PlaceAppsOpenedAtSignIn || !Settings.Enabled) return;
        long now = DateTime.UtcNow.ToFileTimeUtc();
        long logon = SignInTime(now);
        if (logon == 0 || now < logon || now - logon > SignInMaxSeconds * TimeSpan.TicksPerSecond) return;
        if (!FirstStartSinceSignIn(logon)) return; // (switching how it runs right after signing in isn't signing in)
        IntPtr fg = Native.GetForegroundWindow();
        long lastInput = LastInputTime(now);
        var utcNow = DateTime.UtcNow;
        foreach (var h in _handled.ToList())
        {
            if (Native.IsIconic(h) || WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            long started = ProcessStartOf(h);
            if (started == 0 || started < logon - 10 * TimeSpan.TicksPerSecond
                || now - started > SignInAppAgeSeconds * TimeSpan.TicksPerSecond) continue;
            if (h == fg && lastInput > started + SignInInUseMs * TimeSpan.TicksPerMillisecond) continue; // you're using it
            if (!WindowInfo.IsOnCurrentDesktop(h)) continue;
            _handled.Remove(h);
            _signInWindows.Add(h);
            var p = new Pending
            {
                Due = utcNow.AddMilliseconds(PlaceDelayMs), GiveUp = utcNow.AddMilliseconds(GiveUpMs), FirstSeen = utcNow, AtSignIn = true,
            };
            NotePointer(p, h, utcNow);
            _pending[h] = p;
        }
        _signInPlaced.Clear();
        if (_signInWindows.Count > 0)
            Log.Write($"Signed in {TimeSpan.FromTicks(now - logon).TotalSeconds:0} s ago: checking {_signInWindows.Count} " +
                      $"window{(_signInWindows.Count == 1 ? "" : "s")} of apps that opened before Monitor Window Control did.");
    }

    /// <summary>When you signed in to this session (a FILETIME), else when the PC started; 0 if Windows doesn't say.</summary>
    private static long SignInTime(long now)
    {
        try
        {
            if (Native.WTSQuerySessionInformation(IntPtr.Zero, Native.WTS_CURRENT_SESSION, Native.WTSSessionInfo, out IntPtr buf, out int bytes))
            {
                try
                {
                    if (buf != IntPtr.Zero && bytes >= Native.WTSINFO_Size)
                    {
                        long logon = Marshal.ReadInt64(buf, Native.WTSINFO_LogonTime);
                        if (logon > 0) return logon;
                    }
                }
                finally { Native.WTSFreeMemory(buf); }
            }
        }
        catch (Exception) { /* fall back to the time the PC started */ }
        return now - Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// True the first time windows are watched after this sign-in (by the tray app or the helper): notes the sign-in
    /// time in a small file next to the helper. Only read and written right after sign-in, never while placing windows.
    /// </summary>
    private static bool FirstStartSinceSignIn(long logon)
    {
        string file = Path.Combine(Paths.HelperDir, "signin.txt");
        try
        {
            if (File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), out long done)
                && Math.Abs(done - logon) < 10 * TimeSpan.TicksPerSecond) return false;
            Directory.CreateDirectory(Paths.HelperDir);
            File.WriteAllText(file, logon.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception) { /* the time limits still apply */ }
        return true;
    }

    /// <summary>When you last used the keyboard or mouse (a FILETIME), or 0 if Windows doesn't say.</summary>
    private static long LastInputTime(long now)
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return 0;
        uint agoMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return now - agoMs * TimeSpan.TicksPerMillisecond;
    }

    /// <summary>A window placed (or left) after sign-in is done: once all are, say how many apps were moved.</summary>
    private void SignInWindowDone(IntPtr hwnd)
    {
        if (!_signInWindows.Remove(hwnd) || _signInWindows.Count > 0) return;
        if (_signInPlaced.Count > 0)
            Note($"Placed {_signInPlaced.Count} app{(_signInPlaced.Count == 1 ? "" : "s")} that opened while you were signing in " +
                 $"({string.Join(", ", _signInPlaced.Distinct())}).", "");
        _signInPlaced.Clear();
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
        if (!_started) return false;
        if (DateTime.UtcNow >= _nextOffExpiryUtc) OffTimeOver();
        if (!SettingsStore.ChangedOnDisk()) return false;
        var fresh = SettingsStore.TryReload();
        if (fresh == null) return false;
        // "Keep my main monitor" turned off, or started again in another process: count its changes from zero.
        if (fresh.KeepMainMonitor == null || (Settings.KeepMainStopped && !fresh.KeepMainStopped)) _mainFixes.Clear();
        Settings = fresh;
        _monitorSignature = ""; // names/numbers may have changed
        RefreshMonitors();
        ApplyPolicy(save: false);
        ResetStreak();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Reads your organisation's policies again and puts them over the settings. With <paramref name="save"/>, a change is
    /// saved too (at startup), so the other program sees the same values.
    /// </summary>
    private void ApplyPolicy(bool save)
    {
        Policy = Policies.Read();
        if (!Policy.HasAny) return;
        if (!save) { Policies.Apply(Settings, Policy, _monitors, MainMonitor); return; }
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (!Policies.Apply(Settings, Policy, _monitors, MainMonitor)) return;
        Save();
        Log.Write("Applied the settings your organization manages.");
    }

    // ------------------------------------------------------------------ monitors

    /// <summary>The host calls this when Windows reports a display change. Refreshes now and again once Windows has settled.</summary>
    public void OnDisplaySettingsChanged()
    {
        if (_disposed) return;
        // Placements in progress are now stale, and new windows wait until Windows has settled (see HoldIfChanging).
        _lastDisplayEventUtc = DateTime.UtcNow;
        _topologyGeneration++;
        _monitorSource.ClearIdentityCache(); // another monitor may be on a port now
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
        var wasClosed = _monitors.Where(m => m.IsSwitchedOff || m.OnlyChosenApps)
                                 .Select(m => m.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var physical = _monitorSource.Enumerate();
        foreach (var p in physical) p.PhysicalCount = physical.Count;
        _physical = physical;
        // "Split into areas": each split monitor is replaced by its areas, which work like monitors of their own.
        var list = MonitorAreas.Split(physical, p => SplitOf(physical, p));
        _monitors = list;
        var now = DateTime.UtcNow;
        var nextExpiry = DateTime.MaxValue;
        foreach (var m in list)
        {
            m.PhysicalCount = physical.Count;
            foreach (var t in m.Targets)
            {
                var custom = Settings.MonitorNicknames.FirstOrDefault(n => MatchesTarget(n.Monitor, t));
                t.Nickname = string.IsNullOrWhiteSpace(custom?.Nickname) ? null : custom!.Nickname;
                t.Number = custom?.Number;
            }
            // A duplicated display is temporary only if every monitor showing it is (casting a copy of the main monitor isn't).
            m.IsTemporary = m.Targets.All(t => IsTemporaryTarget(m, t));
            // The same goes for "switched off" and "only apps I choose": every monitor showing it must be.
            var options = m.Targets.Select(t => OptionsOf(m, t)).ToList();
            m.IsSwitchedOff = options.All(o => o != null && o.IsOffAt(now));
            m.SwitchedOffUntilUtc = !m.IsSwitchedOff || options.Any(o => o!.OffUntilUtc == null) ? null : options.Max(o => o!.OffUntilUtc);
            if (m.SwitchedOffUntilUtc is DateTime until && until < nextExpiry) nextExpiry = until;
            m.OnlyChosenApps = options.All(o => o?.OnlyChosenApps == true);
            m.HasOtherMonitors = list.Count > 1;
        }
        _nextOffExpiryUtc = nextExpiry;
        // A monitor you switched back on (or opened to every app again): windows that had to go elsewhere can go back.
        if (list.Any(m => wasClosed.Contains(m.DeviceName) && !m.IsSwitchedOff && !m.OnlyChosenApps)) ScheduleReturnCheck();
        // Your numbers decide the order (numbered first, 1, 2, 3…); otherwise main first, then left to right.
        list = list.OrderBy(m => m.Number ?? int.MaxValue).ThenByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left)
                   .ThenBy(m => m.Bounds.Top).ToList();
        _monitors = list;
        // With a split monitor and no numbers of your own, every monitor and area shows its place: "1 · Left", "2 · Right".
        bool autoNumbers = list.Any(m => m.IsArea) && list.All(m => m.Number == null);
        for (int i = 0; i < list.Count; i++) list[i].AutoNumber = autoNumbers ? i + 1 : null;
        foreach (var m in list) m.IdentityNote = IdentityNote(m);
        // (A setup is the monitors Windows has: splitting one into areas doesn't make it another setup.)
        UpdateSetupId(list.Where(m => !m.IsTemporary && (m.Area ?? 1) == 1).Select(m => m.Physical ?? m).ToList());
        UpdateMaximizeHook(); // (a split may have been saved by the other program)

        // (Monitor changes are about Windows' own monitors: splitting one into areas isn't one.)
        string layout = MonitorService.LayoutSignature(physical);
        if (layout != _layoutSignature)
        {
            if (_layoutSignature.Length > 0) // (not the first read at startup)
            {
                now = DateTime.UtcNow;
                _lastMonitorChangeUtc = now;
                _lastDisplayEventUtc = now;
                _topologyGeneration++;
                _identityBackfillDue = true;
                // Once things settle: note the windows of a monitor that went away, move windows back to one that
                // came back, and check the main monitor.
                _vanishScanDue = _keepMainDue = _rescueDue = true;
                ScheduleReturnCheck();
            }
            _layoutSignature = layout;
            if (list.FirstOrDefault(m => m.IsDuplicated) is MonitorInfo dup)
                Log.Write($"Duplicated monitors ({dup.Name}): learning paused while they show the same picture.");
        }

        // Only refreshing names and notes (and Changed) for a change you can see: names, numbers, temporary, main.
        string sig = string.Join("|", list.Select(m => $"{m.DeviceName}={m.Name}{(m.IsPrimary ? "*" : "")}{(m.IsTemporary ? "~" : "")}" +
                                                       $"{(m.IsSwitchedOff ? "!" + m.SwitchedOffUntilUtc : "")}{(m.OnlyChosenApps ? "#" : "")}@{m.Bounds}/{m.Work}"));
        if (sig == _monitorSignature) return;
        _monitorSignature = sig;
        Log.Write("Monitors: " + string.Join(", ", list.Select(m =>
            $"{m.Label} [{m.DeviceName}{string.Concat(m.Targets.Select(t => $" {t.Connection} {t.Model} {t.Serial}".TrimEnd()))}" +
            $"{(m.IsTemporary ? ", temporary" : "")}{(m.IsSwitchedOff ? ", switched off" : "")}{(m.OnlyChosenApps ? ", only chosen apps" : "")}]")));
        if (IsMainEngine) UpdateHotKeys(); // (a send-to-monitor shortcut exists for each connected monitor; settings may have changed)
        if (_started) Changed?.Invoke();
    }

    /// <summary>"Same model as 1 · Main: told apart by…", for a monitor that shares its model with another one.</summary>
    private string? IdentityNote(MonitorInfo m)
    {
        if (m.IsDuplicated) return null;
        var t = m.Targets[0];
        var twin = _monitors.Where(o => !ReferenceEquals(o, m) && o.PhysicalDevice != m.PhysicalDevice) // (not another area of it)
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
    private bool IsTemporaryTarget(MonitorInfo m, MonitorTarget t) => OptionsOf(m, t)?.Temporary ?? t.IsTemporaryByDefault;

    /// <summary>
    /// Your saved choices for one physical monitor, or null if you made none. They're about the physical monitor, so
    /// every area of a split monitor shares them.
    /// </summary>
    private MonitorOptions? OptionsOf(MonitorInfo m, MonitorTarget t)
    {
        if (m.Physical is not { } p) return Settings.MonitorOptions.FirstOrDefault(o => MatchesTarget(o.Monitor, t));
        var whole = p.Targets[0];
        return Settings.MonitorOptions.FirstOrDefault(o =>
            ReferenceEquals(MonitorMatcher.FindTarget(_physical, o.Monitor.Name, o.Monitor.Device, o.Monitor.Hardware)?.Target, whole));
    }

    /// <summary>Your saved choices for a monitor (its first physical monitor, or the whole monitor of an area), added if there are none yet.</summary>
    private MonitorOptions OptionsFor(MonitorInfo m)
    {
        var t = m.Targets[0];
        var o = OptionsOf(m, t);
        if (o == null)
        {
            var p = m.Physical;
            o = new MonitorOptions { Monitor = p == null ? RefTo(m, t) : RefTo(p, p.Targets[0]) };
            Settings.MonitorOptions.Add(o);
        }
        return o;
    }

    /// <summary>The split saved for a physical monitor, or null if it isn't split.</summary>
    private MonitorSplit? SplitOf(IReadOnlyList<MonitorInfo> physical, MonitorInfo p) =>
        Settings.MonitorSplits.LastOrDefault(s =>
            ReferenceEquals(MonitorMatcher.Find(physical, s.Monitor.Name, s.Monitor.Device, s.Monitor.Hardware), p));

    /// <summary>The whole monitor a monitor or area is on, as Windows has it (for Make main, Disconnect and DPI).</summary>
    public MonitorInfo PhysicalOf(MonitorInfo m) =>
        _physical.FirstOrDefault(p => string.Equals(p.DeviceName, m.PhysicalDevice, StringComparison.OrdinalIgnoreCase)) ?? m.Physical ?? m;

    /// <summary>The monitors as Windows has them (a split monitor once, not per area).</summary>
    public IReadOnlyList<MonitorInfo> PhysicalMonitors => _physical;

    /// <summary>The split saved for this monitor (or the monitor of this area), or null if it isn't split.</summary>
    public MonitorSplit? SplitFor(MonitorInfo m) => SplitOf(_physical, PhysicalOf(m));

    /// <summary>
    /// "Split into areas": divides a monitor into areas that work like separate monitors (<see cref="SplitPresets.None"/>
    /// = back to one). <paramref name="names"/> are the areas' names (empty ones get the default). Windows still sees one
    /// monitor. Apps set to the whole monitor open in its first area.
    /// </summary>
    public void SetMonitorSplit(MonitorInfo m, string preset, IReadOnlyList<string>? names)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var p = PhysicalOf(m);
        if (p.IsDuplicated) return;
        Settings.MonitorSplits.RemoveAll(s =>
            ReferenceEquals(MonitorMatcher.Find(_physical, s.Monitor.Name, s.Monitor.Device, s.Monitor.Hardware), p));
        string was = p.FriendlyName;
        if (SplitPresets.IsKnown(preset) && preset != SplitPresets.None)
        {
            var hw = MonitorHardware.Clean(p.Hardware);
            if (hw != null) hw.Area = null;
            var split = new MonitorSplit
            {
                Monitor = new MonitorRef { Device = p.DeviceName, Name = p.FriendlyName, Hardware = hw },
                Preset = preset, Ratios = SplitPresets.DefaultRatios(preset)!.ToList(),
                Names = (names ?? Array.Empty<string>()).Select(n => n?.Trim() ?? "").ToList(),
            };
            split.Names = SplitPresets.Names(split).ToList();
            Settings.MonitorSplits.Add(split);
        }
        Save();
        RefreshMonitors();
        UpdateMaximizeHook();
        Note(preset == SplitPresets.None || !SplitPresets.IsKnown(preset)
                 ? $"{was} is one monitor again (not split into areas)."
                 : $"{was} is split into {SplitPresets.Describe(preset).ToLowerInvariant()}: " +
                   string.Join(", ", _monitors.Where(x => x.PhysicalDevice == p.DeviceName).Select(x => x.Name)) + ".",
             $"[{p.DeviceName}]");
        Changed?.Invoke();
    }

    /// <summary>"Maximize fills the area" (split monitors): on = a maximized window fills its area instead of the whole monitor.</summary>
    public void SetMaximizeFillsArea(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.MaximizeFillsArea == on) return;
        Settings.MaximizeFillsArea = on;
        Save();
        UpdateMaximizeHook();
        Log.Write(on ? "Maximize fills the area: on." : "Maximize fills the area: off (maximized windows fill the whole monitor).");
        Changed?.Invoke();
    }

    /// <summary>The connected monitor a saved reference points to, or null if it isn't connected.</summary>
    public MonitorInfo? FindMonitor(MonitorRef? r) => r == null ? null : MonitorMatcher.Find(_monitors, r.Name, r.Device, r.Hardware);

    /// <summary>A short key for a saved monitor reference (to tell whether a choice changed).</summary>
    private static string RefKey(MonitorRef r) => $"{r.Device}|{r.Name}|{r.Hardware?.DevicePath}|{r.Hardware?.Model}|{r.Hardware?.Serial}";

    /// <summary>
    /// Can the app's windows open on this monitor right now? Not while it's switched off, and on a monitor kept for
    /// chosen apps only if you chose it for this app.
    /// </summary>
    private bool CanUse(AppRule? rule, MonitorInfo m) => !m.IsSwitchedOff && (!m.OnlyChosenApps || ChoseMonitor(rule, m));

    /// <summary>
    /// Did you choose this monitor for the app yourself: as its monitor ("Set by you"), ticked for "Where I used it
    /// last", or as its "If it's off, open on"? Learned monitors don't count.
    /// </summary>
    private bool ChoseMonitor(AppRule? rule, MonitorInfo m)
    {
        if (rule == null || rule.NeverMove) return false;
        if (rule.Fallback == FallbackModes.Monitor && rule.FallbackMonitor != null && Matches(rule.FallbackMonitor, m)) return true;
        if (!rule.Pinned) return false;
        return rule.FollowLastUsed || rule.FollowMouse
            ? rule.AllowedMonitors.Any(a => Matches(a, m))
            : string.Equals(Resolve(rule)?.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why a monitor can't be used for an app right now, for the log.</summary>
    private string WhyNotUsable(AppRule? rule, MonitorInfo m) =>
        m.IsSwitchedOff ? "is switched off" : !CanUse(rule, m) ? "only opens apps you choose" : "can't be used";

    private MonitorInfo? MonitorByDevice(string? device) =>
        device == null ? null : _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connected monitor an app rule points to, or null if it isn't connected.</summary>
    public MonitorInfo? Resolve(AppRule rule) => MonitorMatcher.Find(_monitors, rule.MonitorName, rule.MonitorDevice, rule.MonitorHardware);

    private bool Matches(MonitorRef r, MonitorInfo m) =>
        string.Equals(MonitorMatcher.Find(_monitors, r.Name, r.Device, r.Hardware)?.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase);

    private bool MatchesTarget(MonitorRef r, MonitorTarget t) =>
        ReferenceEquals(MonitorMatcher.FindTarget(_monitors, r.Name, r.Device, r.Hardware)?.Target, t);

    /// <summary>A saved reference to a connected monitor (device, model name and hardware identity).</summary>
    private static MonitorRef RefTo(MonitorInfo m) =>
        new() { Device = m.DeviceName, Name = m.FriendlyName, Hardware = MonitorHardware.Clean(m.Hardware) };

    /// <summary>A saved reference to one physical monitor (one of a duplicated display's monitors).</summary>
    private static MonitorRef RefTo(MonitorInfo m, MonitorTarget t) =>
        new() { Device = m.DeviceName, Name = t.FriendlyName, Hardware = MonitorHardware.Clean(t.Hardware) };

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
                     .Concat(Settings.Apps.Select(a => a.FallbackMonitor).OfType<MonitorRef>())
                     .Concat(Settings.Apps.SelectMany(a => a.Positions).Select(p => p.Monitor))
                     .Concat(Settings.MonitorNicknames.Select(n => n.Monitor))
                     .Concat(Settings.MonitorOptions.Select(o => o.Monitor))
                     .Concat(new[] { Settings.FallbackMonitor, Settings.KeepMainMonitor }.OfType<MonitorRef>()))
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

    /// <summary>The connected monitors a "Where I used it last" app may open on, in your order.</summary>
    public List<MonitorInfo> AllowedInOrder(AppRule rule) =>
        rule.AllowedMonitors.Select(FindMonitor).OfType<MonitorInfo>().Distinct().ToList();

    /// <summary>
    /// Where a saved app's new windows go right now: its monitor, if that's connected and can be used (not switched off,
    /// not kept for other apps). Null = none of its monitors can be used now, or there's no choice to make.
    /// </summary>
    public MonitorInfo? ResolveTarget(AppRule rule)
    {
        var m = Resolve(rule);
        if (!rule.FollowLastUsed) return m != null && CanUse(rule, m) ? m : null;
        if (m != null && IsAllowed(rule, m) && CanUse(rule, m)) return m; // the monitor you used it on last
        if (rule.AllowedMonitors.Count == 0) return null;
        // Else the next of its monitors in your order, a permanent one first.
        var others = AllowedInOrder(rule).Where(o => CanUse(rule, o)).ToList();
        return others.FirstOrDefault(o => !o.IsTemporary) ?? others.FirstOrDefault();
    }

    /// <summary>
    /// Where an app goes while its own monitor can't be used (null = leave it): its "If it's off, open on", then the
    /// choice for all apps, then the main monitor. <paramref name="where"/> says which, for the log.
    /// </summary>
    private MonitorInfo? FallbackFor(AppRule rule, out string where)
    {
        if (rule.Fallback == FallbackModes.Leave) { where = "left where Windows put it (your choice for this app)"; return null; }
        if (rule.Fallback == FallbackModes.Monitor && FindMonitor(rule.FallbackMonitor) is { } own && CanUse(rule, own))
        {
            where = $"{own.Name} (your choice for this app)";
            return own;
        }
        if (rule.Fallback != FallbackModes.Main)
        {
            if (Settings.Fallback == FallbackModes.Leave) { where = "left where Windows put it"; return null; }
            if (Settings.Fallback == FallbackModes.Monitor && FindMonitor(Settings.FallbackMonitor) is { } all && CanUse(rule, all))
            {
                where = all.Name;
                return all;
            }
        }
        var main = FallbackMonitor;
        where = main == null ? "left where it is" : main.IsPrimary ? "main monitor" : main.Name;
        return main;
    }

    private MonitorInfo? MonitorOfWindow(IntPtr hwnd)
    {
        string? dev = _monitorSource.DeviceOfWindow(hwnd);
        var m = MonitorOnDevice(dev, hwnd);
        if (m == null && dev != null) { RefreshMonitors(); m = MonitorOnDevice(dev, hwnd); }
        return m;
    }

    /// <summary>
    /// The monitor a window is on, given the Windows display it's on: that monitor, or for a split monitor the area
    /// that contains the window's centre (its restored position when it's maximized).
    /// </summary>
    private MonitorInfo? MonitorOnDevice(string? device, IntPtr hwnd)
    {
        if (device == null) return null;
        if (MonitorByDevice(device) is { } whole) return whole;
        if (!_monitors.Any(m => m.IsArea && string.Equals(m.PhysicalDevice, device, StringComparison.OrdinalIgnoreCase))) return null;
        return MonitorAreas.AreaAt(_monitors, device, WindowMover.CentreOf(hwnd));
    }

    // ------------------------------------------------------------------ split monitors: maximize fills the area

    /// <summary>
    /// "Maximize fills the area": watches for windows being maximized (EVENT_OBJECT_LOCATIONCHANGE) only while a monitor
    /// is split and the option is on, in the program that places windows. Nothing is polled: with no split monitor
    /// the hook isn't installed at all.
    /// </summary>
    private void UpdateMaximizeHook()
    {
        bool want = IsMainEngine && Settings.MaximizeFillsArea && _monitors.Any(m => m.IsArea);
        if (want == (_maximizeHook != IntPtr.Zero)) return;
        if (want)
        {
            _maximizeHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            if (_maximizeHook == IntPtr.Zero) Log.Write("Could not watch for maximized windows; on a split monitor they fill the whole monitor.");
        }
        else
        {
            Native.UnhookWinEvent(_maximizeHook);
            _maximizeHook = IntPtr.Zero;
            _maximizedFills.Clear();
        }
    }

    /// <summary>
    /// A window was maximized. On a split monitor (and unless it's paused, fullscreen, can't be resized or is an app set
    /// to "Never move"), it's restored and sized to fill the area it was in instead of the whole monitor. An app that
    /// maximizes itself again and again is left maximized after a few tries.
    /// </summary>
    private void OnMaximized(IntPtr hwnd)
    {
        if (_maximizeHook == IntPtr.Zero || !Settings.Enabled || IsPausedForRemoteDesktop || _gamePaused) return;
        string? dev = _monitorSource.DeviceOfWindow(hwnd);
        if (dev == null || !_monitors.Any(m => m.IsArea && string.Equals(m.PhysicalDevice, dev, StringComparison.OrdinalIgnoreCase))) return;
        if ((Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64() & Native.WS_THICKFRAME) == 0) return;
        if (WindowInfo.Classify(hwnd, _ownPid) != WindowInfo.Kind.AppWindow) return;
        var app = WindowInfo.Identify(hwnd);
        if (app == null || Settings.Find(app.Key)?.NeverMove == true) return;
        var now = DateTime.UtcNow;
        if (_maximizedFills.TryGetValue(hwnd, out var seen) && now - seen.First < TimeSpan.FromSeconds(10))
        {
            if (seen.Count >= 3) return; // it keeps maximizing itself: let it
            _maximizedFills[hwnd] = (seen.First, seen.Count + 1);
        }
        else
        {
            if (_maximizedFills.Count > 200) _maximizedFills.Clear();
            _maximizedFills[hwnd] = (now, 1);
        }
        var area = MonitorAreas.AreaAt(_monitors, dev, WindowMover.CentreOf(hwnd)); // where it was before it was maximized
        if (area == null || !area.IsArea) return;
        string device = area.DeviceName;
        // After this event is handled: the window is done maximizing by then.
        _host.Post(() =>
        {
            if (_disposed || !Native.IsWindow(hwnd) || !Native.IsZoomed(hwnd) || MonitorByDevice(device) is not { } target) return;
            if (WindowMover.FillArea(hwnd, target, null) == WindowMover.Result.Moved)
                Log.Write($"Maximized {app.Key}: it fills {target.Name} instead of the whole monitor (maximize fills the area).");
        });
    }

    /// <summary>The device name of the monitor or area a window is on ("\\.\DISPLAY2#1" on a split monitor), or null.</summary>
    private string? DeviceOfWindow(IntPtr hwnd)
    {
        string? dev = _monitorSource.DeviceOfWindow(hwnd);
        return MonitorOnDevice(dev, hwnd)?.DeviceName ?? dev;
    }

    // ------------------------------------------------------------------ new windows

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;

        // (First, and cheap: this one fires whenever a window moves, but only while a monitor is split.)
        if (eventType == Native.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (Native.IsZoomed(hwnd)) OnMaximized(hwnd);
            return;
        }

        if (eventType == Native.EVENT_SYSTEM_MOVESIZEEND)
        {
            if (_restoreDue) _movedSinceWake.Add(hwnd); // you moved it after waking up: don't put it back
            _movedByYou.Add(hwnd);                      // "Move open windows to their monitors" leaves it alone
            if (_maximizeHook != IntPtr.Zero && Native.IsZoomed(hwnd)) OnMaximized(hwnd); // dragged to the top edge
            return;
        }

        if (eventType == Native.EVENT_SYSTEM_FOREGROUND)
        {
            NoteForeground(hwnd);
            return;
        }

        if (eventType == Native.EVENT_OBJECT_UNCLOAKED)
        {
            OnUncloaked(hwnd);
            return;
        }

        if (eventType == Native.EVENT_OBJECT_DESTROY)
        {
            _handled.Remove(hwnd);
            _pending.Remove(hwnd);
            _cloakedNew.Remove(hwnd);
            _heldForGame.Remove(hwnd);
            SignInWindowDone(hwnd);
            _displaced.Remove(hwnd);
            _snapshot.Remove(hwnd);
            _sleepSnapshot?.Remove(hwnd);
            _movedSinceWake.Remove(hwnd);
            _movedByYou.Remove(hwnd);
            if (hwnd == _lastAppWindow) _lastAppWindow = IntPtr.Zero;
            if (_appWindows.Remove(hwnd)) AppWindowsChanged?.Invoke();
            return;
        }

        if (_handled.Contains(hwnd) || _pending.ContainsKey(hwnd)) return;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return; // not top-level
        // (Enabled is checked when the window is placed, after settings are refreshed from disk.)

        var now = DateTime.UtcNow;
        // (A new window Windows still hides keeps the time it first appeared, so it can't become "new" again later.)
        var firstSeen = _cloakedNew.Remove(hwnd, out var seen) ? seen : now;
        var p = new Pending { Due = now.AddMilliseconds(PlaceDelayMs), GiveUp = now.AddMilliseconds(GiveUpMs), FirstSeen = firstSeen };
        NotePointer(p, hwnd, now);
        _pending[hwnd] = p;
        if (!_placeTimer.IsEnabled) _placeTimer.Start();
    }

    /// <summary>
    /// For "The monitor I'm using (mouse pointer)": where the pointer is the moment a window appears (not 0.4 s later).
    /// If the pointer hasn't moved for a couple of minutes (you're using the keyboard), the monitor of the window you're
    /// using counts instead.
    /// </summary>
    private void NotePointer(Pending p, IntPtr hwnd, DateTime now)
    {
        Native.GetCursorPos(out p.Pointer);
        if (now - _lastCursorMoveUtc < TimeSpan.FromSeconds(MouseIdleSeconds)
            || p.Pointer.X != _lastCursor.X || p.Pointer.Y != _lastCursor.Y) return;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != hwnd) p.FocusDevice = DeviceOfWindow(fg);
    }

    /// <summary>
    /// Windows showed (uncloaked) a window it had hidden. It's new, and placed now, only if it appeared at most 30 s ago
    /// and was still hidden then; switching virtual desktops shows old windows, which are never moved.
    /// </summary>
    private void OnUncloaked(IntPtr hwnd)
    {
        if (!_cloakedNew.Remove(hwnd, out var firstSeen) || _pending.ContainsKey(hwnd)) return;
        var now = DateTime.UtcNow;
        if (now - firstSeen > TimeSpan.FromSeconds(UncloakNewSeconds)) { _handled.Add(hwnd); return; }
        var p = new Pending { Due = now.AddMilliseconds(PlaceDelayMs), GiveUp = now.AddMilliseconds(GiveUpMs), FirstSeen = firstSeen };
        NotePointer(p, hwnd, now);
        _pending[hwnd] = p;
        if (!_placeTimer.IsEnabled) _placeTimer.Start();
    }

    private void ProcessPending()
    {
        var now = DateTime.UtcNow;
        foreach (var (hwnd, p) in _pending.Where(kv => kv.Value.Due <= now).ToList())
        {
            try
            {
                if (p.Verify) { Verify(hwnd, p); RefreshDropped(hwnd); }
                else TryPlace(hwnd, p, now);
            }
            catch (Exception ex)
            {
                _pending.Remove(hwnd);
                _handled.Add(hwnd);
                Log.Write($"Error handling window 0x{hwnd.ToInt64():X}: {ex.Message}");
            }
            if (p.AtSignIn && !(_pending.TryGetValue(hwnd, out var next) && next.AtSignIn)) SignInWindowDone(hwnd);
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
                WaitForWindow(hwnd, p, now);
                return;
            case WindowInfo.Kind.OwnedDialog:
                _pending.Remove(hwnd);
                _handled.Add(hwnd);
                ReloadIfChanged();
                PlaceDialog(hwnd);
                return;
        }

        ReloadIfChanged();
        var app = WindowInfo.Identify(hwnd);
        var rule = app == null ? null : Settings.Find(app.Key);
        // "Wait before moving" (Advanced, for apps that show a splash screen first or move themselves after opening).
        if (rule?.WaitMs is int wait && now < p.FirstSeen.AddMilliseconds(wait))
        {
            p.Due = p.FirstSeen.AddMilliseconds(wait);
            if (p.GiveUp < p.Due.AddMilliseconds(GiveUpMs)) p.GiveUp = p.Due.AddMilliseconds(GiveUpMs);
            return;
        }
        // Rules for some of its windows: its title may not be final yet.
        if (WaitForTitle(rule, hwnd, p.FirstSeen, now)) { p.Due = now.AddMilliseconds(RetryMs); return; }

        // A real app window: handle it exactly once.
        _pending.Remove(hwnd);
        _handled.Add(hwnd);
        if (_appWindows.Add(hwnd)) AppWindowsChanged?.Invoke();
        if (!Settings.Enabled || IsPausedForRemoteDesktop || app == null) return;
        if (!PlacesApp(app.Key)) return;
        if (UpdateGamePause())
        {
            // A game or presentation is running: this window waits until it ends (see ReleaseHeldWindows).
            _heldForGame[hwnd] = now;
            return;
        }
        string title = WindowInfo.GetTitle(hwnd);

        if (rule != null) rule = RefreshRuleDetails(rule, app, hwnd);
        rule = RuleForWindow(app, rule, hwnd, title, out string? note);
        try { PlaceNewWindow(hwnd, app, rule, title, p, note); }
        finally { LaunchWindowPlaced(app.Key); WindowHandled?.Invoke(app.Key); }
    }

    /// <summary>
    /// A new window that isn't ready yet (untitled, or still hidden by Windows). It's checked again shortly, up to 4 s.
    /// A window Windows still hides then, or one on another virtual desktop, waits without any checking until Windows
    /// shows it (see <see cref="OnUncloaked"/>).
    /// </summary>
    private void WaitForWindow(IntPtr hwnd, Pending p, DateTime now)
    {
        bool otherDesktop = WindowInfo.IsCloakedByShell(hwnd);
        if (otherDesktop || (now >= p.GiveUp && WindowInfo.IsCloaked(hwnd)))
        {
            _pending.Remove(hwnd);
            _cloakedNew[hwnd] = p.FirstSeen == default ? now : p.FirstSeen;
            return;
        }
        if (now >= p.GiveUp) { _pending.Remove(hwnd); _handled.Add(hwnd); }
        else p.Due = now.AddMilliseconds(RetryMs);
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
            Requeues = p.Requeues + 1, WasHeld = true, HeldSinceUtc = now, AtSignIn = p.AtSignIn,
            FirstSeen = p.FirstSeen == default ? now : p.FirstSeen, // (still new for late uncloaking, and the wait counts from then)
            Pointer = p.Pointer, FocusDevice = p.FocusDevice,
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

    /// <summary>
    /// Where a new window goes, and why (for the log). Null = leave it. "Displaced" means it goes somewhere else only
    /// because its app's own monitor can't be used right now (disconnected, switched off, or kept for chosen apps), so it
    /// moves back once that monitor can be used again. <c>ReturnTo</c> is set instead for a window with no monitor of its
    /// own that Windows opened on a switched-off monitor: it goes back there when that monitor is switched on again.
    /// <c>Plain</c> is the reason in plain words, for Recent activity.
    /// </summary>
    internal (MonitorInfo? Target, string Reason, bool Displaced, MonitorRef? ReturnTo, string Plain) ChooseTarget(AppRule? rule,
        MonitorInfo? current, MonitorInfo? pointer)
    {
        MonitorInfo? target;
        string reason, plain;
        bool displaced = false;
        MonitorRef? returnTo = null;
        if (rule is { FollowMouse: true, NeverMove: false })
        {
            (target, reason) = PointerTarget(rule, current, pointer);
            plain = target != null && pointer != null && target.DeviceName == pointer.DeviceName
                ? "Set by you: the monitor you're using" : "Set by you: one of the monitors you chose for it";
        }
        else if (rule != null)
        {
            target = ResolveTarget(rule);
            reason = rule.FollowLastUsed ? "where you used it last" : rule.Pinned ? "set by you" : "learned";
            plain = rule.FollowLastUsed ? "Where you used it last" : rule.Pinned ? "Set by you" : "Learned: you use it there";
            var home = Resolve(rule);
            if (target != null)
            {
                // "Where I used it last" on another of its monitors, because the last one is off: that one is still home.
                displaced = rule.FollowLastUsed && home != null && IsAllowed(rule, home) && !CanUse(rule, home);
                if (displaced)
                {
                    reason += $"; {home!.Name} {WhyNotUsable(rule, home)}, so the next of its monitors";
                    plain = $"{home.Name} {WhyNotUsable(rule, home)}, so the next of its monitors for now";
                }
            }
            else if (!(rule.FollowLastUsed && rule.MonitorDevice == null)) // (never used yet: leave it)
            {
                string why = home == null
                    ? $"its monitor ({rule.MonitorName ?? rule.MonitorDevice}) isn't connected"
                    : $"its monitor ({home.Name}) {WhyNotUsable(rule, home)}";
                target = FallbackFor(rule, out string where);
                reason += $"; {why}, so {where}";
                displaced = target != null;
                plain = (home == null ? $"{rule.MonitorName ?? "Its monitor"} isn't connected" : $"{home.Name} {WhyNotUsable(rule, home)}")
                        + $", so {(target == null ? "it's left where it is" : target.IsPrimary ? "the main monitor" : target.Name)} for now";
            }
            else plain = "Where you used it last (not used yet)";
        }
        else if (Settings.UnknownFollowMouse && pointer != null && CanUse(null, pointer))
        {
            target = pointer;
            reason = "new app → the monitor you're using";
            plain = "New app: the monitor you're using";
        }
        else if (Settings.MoveUnknownToMain)
        {
            target = FallbackMonitor;
            reason = target?.IsPrimary == true ? "new app → main monitor" : $"new app → {target?.Name} (the main monitor can't be used now)";
            plain = target?.IsPrimary == true ? "New app: the main monitor" : "New app: the main monitor can't be used now";
        }
        else
        {
            target = null;
            reason = "new app";
            plain = "New app";
        }

        // A window Windows opened on a monitor that's switched off (or kept for other apps) never stays there, even when
        // it would otherwise be left where it is.
        if (target == null && current != null && !CanUse(rule, current) && FallbackMonitor is { } stand && CanUse(rule, stand)
            && stand.DeviceName != current.DeviceName)
        {
            target = stand;
            reason += $"; it opened on {current.Name}, which {WhyNotUsable(rule, current)}";
            plain = $"It opened on {current.Name}, which {WhyNotUsable(rule, current)}";
            // It counts as displaced too: back to its app's monitor, or (no monitor of its own) to the one it opened on.
            if (rule != null && Resolve(rule) is { } home && !CanUse(rule, home)) displaced = true;
            else if (current.IsSwitchedOff) returnTo = RefTo(current);
        }
        return (target, reason, displaced, returnTo, plain);
    }

    /// <summary>
    /// "The monitor I'm using (mouse pointer)": the monitor with the pointer when the window appeared, if it's one of the
    /// monitors you ticked for the app and can be used. Else it stays where Windows put it, if that one is ticked, else
    /// it goes to the first of its ticked monitors (in your order) that can be used.
    /// </summary>
    private (MonitorInfo? Target, string Reason) PointerTarget(AppRule rule, MonitorInfo? current, MonitorInfo? pointer)
    {
        if (pointer != null && IsAllowed(rule, pointer) && CanUse(rule, pointer)) return (pointer, "the monitor you're using");
        string why = pointer == null ? "the pointer's monitor is unknown" : $"{pointer.Name} isn't one of its monitors";
        if (current != null && IsAllowed(rule, current) && CanUse(rule, current)) return (current, $"{why}, so where Windows put it");
        var next = AllowedInOrder(rule).Where(m => CanUse(rule, m)).ToList();
        var pick = next.FirstOrDefault(m => !m.IsTemporary) ?? next.FirstOrDefault();
        if (pick != null) return (pick, $"{why}, so the first of its monitors");
        var fallback = FallbackFor(rule, out string where);
        return (fallback, $"{why}; none of its monitors can be used, so {where}");
    }

    /// <summary>The monitor at a point on the screen (where the mouse pointer was), or null.</summary>
    private MonitorInfo? MonitorAt(Native.POINT pt)
    {
        IntPtr hMon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONULL);
        if (hMon == IntPtr.Zero) return null;
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        return Native.GetMonitorInfo(hMon, ref mi) ? MonitorByDevice(mi.szDevice) : null;
    }

    /// <summary>The monitor you were using when the window appeared: the pointer's, or (keyboard only) the focused window's.</summary>
    private MonitorInfo? MonitorInUse(Pending p) => MonitorByDevice(p.FocusDevice) ?? MonitorAt(p.Pointer);

    /// <summary>
    /// Places a new window by its app's choice (<paramref name="rule"/>, already adjusted for this window: see
    /// <see cref="RuleForWindow"/>). <paramref name="note"/> is why, when it isn't the app's own choice.
    /// </summary>
    private void PlaceNewWindow(IntPtr hwnd, AppIdentity app, AppRule? rule, string title, Pending p, string? note)
    {
        if (rule != null && rule.NeverMove)
        {
            if (note != null) Log.Write($"Left {app.Key} \"{title}\" where it is ({note}: never moved).");
            return;
        }
        bool wantsPointer = rule?.FollowMouse == true || (rule == null && Settings.UnknownFollowMouse);
        var (target, reason, displaced, returnTo, plain) = ChooseTarget(rule, MonitorOfWindow(hwnd), wantsPointer ? MonitorInUse(p) : null);
        if (target == null) return;
        if (note != null)
        {
            reason += $"; {note}";
            if (!displaced) plain = note;
        }
        if (p.WasHeld) reason += "; held during a display change";
        if (p.AtSignIn) reason += "; it opened while you were signing in";

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
        bool there = current != null && current.DeviceName == target.DeviceName;
        var place = PlacementFor(rule, target);
        if (target.IsArea && place is { Spot: null }) place = null; // "Maximized" on an area: it fills the area instead
        // Already there: a window that opened while signing in is left as it is, and so is one that's already maximized
        // (or can't be) when it only needs maximizing.
        if (there && (p.AtSignIn || (place is { Spot: null } && !target.IsArea && (Native.IsZoomed(hwnd) || !WindowMover.CanMaximize(hwnd)))))
            place = null;
        // An area of a split monitor: a new window is sized to fill its area, also when it opened in it already.
        bool fill = there && place == null && target.IsArea && !p.AtSignIn && !WindowMover.FillsArea(hwnd, target);
        if (there && place == null && !fill)
        {
            // Already there. If that's only a stand-in for its own monitor, it still goes back when that one can be used.
            RememberDisplaced(hwnd, rule, displaced, returnTo);
            return;
        }

        if (!CanMoveNew(hwnd, current, app, title, quiet: there)) return;

        int generation = _topologyGeneration;
        var was = there ? null : Snap(hwnd, ProcessStartOf(hwnd)); // (for Undo)
        var result = MovePlaced(hwnd, target, place, there && !fill, StillCurrent(generation));
        string from = current?.Name ?? "?";
        if (result == WindowMover.Result.Aborted)
        {
            Requeue(hwnd, p, $"{app.Key} \"{title}\"");
            return;
        }
        if (result == WindowMover.Result.Moved)
        {
            RememberDisplaced(hwnd, rule, displaced, returnTo);
            string how = place?.Spot != null ? ", where you last had it" : target.IsArea ? ", filling the area"
                : place == null ? "" : ", maximized";
            if (there) Log.Write($"Placed {app.DisplayName} on {target.Name}{how} ({reason}).  {app.Key} \"{title}\"");
            else
            {
                var undo = new UndoData { PlacedApp = app.Key, PlacedAppName = app.DisplayName };
                if (was != null) undo.Windows.Add((hwnd, was));
                Record(ActivityKinds.Moved, $"Moved {app.DisplayName} to {target.Name}", plain, undo,
                       $"Moved {app.DisplayName} from {from} to {target.Name}{how} ({reason}).  {app.Key} \"{title}\"",
                       displaced ? NoticeOpenedElsewhere : null, app.Key);
            }
            if (p.AtSignIn) _signInPlaced.Add(app.DisplayName);
            // Check again shortly: some apps restore their saved position after showing ("Keep correcting for").
            Native.GetWindowRect(hwnd, out var placed);
            var now = DateTime.UtcNow;
            _pending[hwnd] = new Pending
            {
                Verify = true, Due = now.AddMilliseconds(VerifyDelayMs), VerifyUntil = now.AddMilliseconds(rule?.CorrectForMs ?? 1000),
                TargetDevice = target.DeviceName, AppKey = app.Key, FromDevice = current?.DeviceName ?? "",
                Generation = generation, PlacedRect = placed, Requeues = p.Requeues, AtSignIn = false,
                Pointer = p.Pointer, FocusDevice = p.FocusDevice,
                Wanted = place?.Spot, WantMaximized = place?.Maximize == true && !target.IsArea,
            };
        }
        else
        {
            Log.Write($"Could not move {app.Key} \"{title}\" from {from} to {target.Name}.");
        }
    }

    /// <summary>Minimized, fullscreen, administrator and not-responding windows are never moved (logged unless <paramref name="quiet"/>).</summary>
    private bool CanMoveNew(IntPtr hwnd, MonitorInfo? current, AppIdentity app, string title, bool quiet)
    {
        string? why = Native.IsIconic(hwnd) ? "minimized"
            : WindowMover.IsFullscreen(hwnd, current) ? "fullscreen"
            : !_selfElevated && WindowInfo.IsProcessElevated(WindowInfo.GetPid(hwnd))
                ? "it runs as administrator, so Windows doesn't allow Monitor Window Control to move it"
            : Native.IsHungAppWindow(hwnd) ? "not responding" : null;
        if (why == null || quiet) return why == null;
        if (why.StartsWith("it runs as administrator", StringComparison.Ordinal))
            Record(ActivityKinds.Skipped, $"Didn't move {app.DisplayName}",
                   "Skipped: it runs as administrator, and Windows doesn't let other apps move it", null,
                   $"Skipped {app.Key} \"{title}\": {why}.", NoticeAdminApp, app.Key);
        else Log.Write($"Skipped {app.Key} \"{title}\": {why}.");
        return false;
    }

    /// <summary>"Place it" for one app on one monitor: a remembered spot and/or maximized. Null = centered.</summary>
    private readonly record struct Placement(WindowPosition? Spot, bool Maximize);

    private Placement? PlacementFor(AppRule? rule, MonitorInfo target)
    {
        if (rule == null || rule.NeverMove) return null;
        if (rule.Place == PlaceModes.Maximized) return new Placement(null, true);
        if (rule.Place == PlaceModes.Last && PositionOn(rule, target) is { } spot) return new Placement(spot, spot.Maximized);
        return null;
    }

    /// <summary>Where the app's window was on this monitor ("Where I last had it"), or null if it isn't known.</summary>
    private WindowPosition? PositionOn(AppRule rule, MonitorInfo m) => rule.Positions.FirstOrDefault(p => Matches(p.Monitor, m));

    /// <summary>
    /// Moves a window onto the target: centred (the default), at a remembered spot, and/or maximized. A window that's
    /// already there (<paramref name="there"/>) is only given its spot or maximized.
    /// </summary>
    private static WindowMover.Result MovePlaced(IntPtr hwnd, MonitorInfo target, Placement? place, bool there, Func<bool> stillValid)
    {
        // (On an area, Move fills the area: maximizing would cover the whole monitor, so it isn't.)
        var result = place?.Spot is { } spot ? WindowMover.PlaceAtFraction(hwnd, target, spot, stillValid)
            : there ? WindowMover.Result.Moved : WindowMover.Move(hwnd, target, stillValid);
        if (result == WindowMover.Result.Moved && place?.Maximize == true && !target.IsArea) WindowMover.Maximize(hwnd);
        return result;
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
            if (!p.IsDialog) CheckSize(hwnd, p, target);
            if (!KeepChecking(hwnd, p) && !p.JumpedBack && !p.IsDialog) _jumpBacks.Remove(p.AppKey);
            return;
        }
        // Apps that restore their saved position jump back to where they opened. Anywhere else, you moved it: leave it.
        if (!string.Equals(current.DeviceName, p.FromDevice, StringComparison.OrdinalIgnoreCase)) return;
        if (!p.JumpedBack) { p.JumpedBack = true; if (!p.IsDialog) NoteJumpBack(p.AppKey); }
        if (p.Corrections >= MaxCorrections) return;
        p.Corrections++;
        bool moved = p.IsDialog
            ? CenterDialog(hwnd, p.Owner, target)
            : MovePlaced(hwnd, target, p.Wanted != null || p.WantMaximized ? new Placement(p.Wanted, p.WantMaximized) : null,
                         false, StillCurrent(p.Generation)) == WindowMover.Result.Moved;
        if (!moved) return;
        Log.Write($"{p.AppKey} moved itself back to {current.Name}; moved it to {target.Name} again.");
        KeepChecking(hwnd, p);
    }

    /// <summary>
    /// Still where it was put. An app that rescaled itself for the monitor's DPI after the move can now stick out past
    /// the edge: pull it back in, but only if it's still where it was left (otherwise you moved it). An app that resized
    /// itself right after opening is put back at its remembered spot or maximized again, once.
    /// </summary>
    private void CheckSize(IntPtr hwnd, Pending p, MonitorInfo target)
    {
        Native.GetWindowRect(hwnd, out var now);
        bool untouched = Math.Abs(now.Left - p.PlacedRect.Left) <= 1 && Math.Abs(now.Top - p.PlacedRect.Top) <= 1;
        if (!p.SizeChecked && (p.Wanted != null || p.WantMaximized))
        {
            p.SizeChecked = true;
            bool off = p.WantMaximized ? !Native.IsZoomed(hwnd) && WindowMover.CanMaximize(hwnd)
                : !Native.IsZoomed(hwnd) && !SameRect(now, p.PlacedRect);
            if (off && MovePlaced(hwnd, target, new Placement(p.Wanted, p.WantMaximized), true,
                                  StillCurrent(p.Generation)) == WindowMover.Result.Moved)
            {
                Log.Write($"{p.AppKey} changed its size after opening; put it back {(p.Wanted != null ? "where you last had it" : "maximized")}.");
                return;
            }
        }
        if (untouched && WindowMover.KeepInside(hwnd, target))
            Log.Write($"{p.AppKey} changed size after the move; kept it fully on {target.Name}.");
    }

    /// <summary>"Keep correcting for": checks the window again shortly while that time isn't over. False when it's over.</summary>
    private bool KeepChecking(IntPtr hwnd, Pending p)
    {
        var now = DateTime.UtcNow;
        if (now.AddMilliseconds(VerifyDelayMs) > p.VerifyUntil) return false;
        p.Due = now.AddMilliseconds(VerifyDelayMs);
        Native.GetWindowRect(hwnd, out p.PlacedRect);
        _pending[hwnd] = p;
        if (!_placeTimer.IsEnabled) _placeTimer.Start();
        return true;
    }

    /// <summary>
    /// An app moved itself back after being moved. Twice in a row (two windows, or two openings) and the log and the
    /// Apps page suggest a longer "Wait before moving".
    /// </summary>
    private void NoteJumpBack(string key)
    {
        int n = _jumpBacks.TryGetValue(key, out int c) ? c + 1 : 1;
        _jumpBacks[key] = n;
        if (n != 2) return;
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        Log.Write($"{rule?.DisplayName ?? key} moved itself back twice: try a longer wait (Apps page, select it, Advanced).  [{key}]");
        if (rule == null || rule.MovedItselfBack || rule.WaitMs == 3000) return;
        rule.MovedItselfBack = true;
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ dialogs

    /// <summary>
    /// "Open dialogs on the same monitor as their app": a dialog (Save As, Print, Settings…) that opened entirely on
    /// another monitor than its app's window is centred over that window there, and never resized. Left alone: large
    /// windows (60 % of the monitor's width or height or more), dialogs that overlap the app's monitor at all, and
    /// dialogs of apps set to "Never move". Like other windows, each dialog is placed once.
    /// </summary>
    private void PlaceDialog(IntPtr hwnd)
    {
        if (!Settings.Enabled || !Settings.MoveDialogsWithApp || IsPausedForRemoteDesktop) return;
        IntPtr owner = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
        if (owner == IntPtr.Zero || owner == hwnd || !Native.IsWindowVisible(owner) || Native.IsIconic(owner)) return;
        var app = WindowInfo.Identify(owner);
        if (app == null || Settings.Find(app.Key)?.NeverMove == true) return;
        if (!PlacesApp(app.Key)) return;
        var target = MonitorOfWindow(owner);
        var current = MonitorOfWindow(hwnd);
        if (target == null || current == null || current.DeviceName == target.DeviceName) return;
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        var b = target.Bounds;
        if (r.Left < b.Right && r.Right > b.Left && r.Top < b.Bottom && r.Bottom > b.Top) return; // partly on the app's monitor
        if (r.Width >= target.Work.Width * DialogMaxShare || r.Height >= target.Work.Height * DialogMaxShare) return;
        if (UpdateGamePause() || Native.IsHungAppWindow(hwnd)) return;
        if (!_selfElevated && WindowInfo.IsProcessElevated(WindowInfo.GetPid(hwnd))) return;

        int generation = _topologyGeneration;
        if (!CenterDialog(hwnd, owner, target)) return;
        Log.Write($"Moved a dialog of {app.DisplayName} from {current.Name} to {target.Name}, over its window.  {app.Key} \"{WindowInfo.GetTitle(hwnd)}\"");
        Native.GetWindowRect(hwnd, out var placed);
        var now = DateTime.UtcNow;
        _pending[hwnd] = new Pending
        {
            Verify = true, IsDialog = true, Owner = owner, Due = now.AddMilliseconds(VerifyDelayMs), VerifyUntil = now.AddMilliseconds(1000),
            TargetDevice = target.DeviceName, AppKey = app.Key, FromDevice = current.DeviceName, Generation = generation, PlacedRect = placed,
        };
    }

    /// <summary>Centres a dialog over its app's window (which must still be open) on the target monitor.</summary>
    private static bool CenterDialog(IntPtr hwnd, IntPtr owner, MonitorInfo target) =>
        Native.IsWindow(owner) && WindowMover.CenterOver(hwnd, owner, target);

    // ------------------------------------------------------------------ games and presentations

    /// <summary>
    /// "Pause while a game or presentation is running": true while Windows says a game runs in exclusive fullscreen or
    /// presentation mode is on, or the window in front has no title bar and covers its whole monitor (a borderless game).
    /// Cheap enough to check on every new window and every learning tick.
    /// </summary>
    private bool GameOrPresentationRunning()
    {
        if (!Settings.PauseForGames) return false;
        try
        {
            if (Native.SHQueryUserNotificationState(out int state) == 0
                && state is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE) return true;
        }
        catch (EntryPointNotFoundException) { /* very old Windows */ }
        // (The lock screen, Task View and Alt+Tab are fullscreen too, but they're part of Windows, not a game.)
        if (_locked) return _gamePaused;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || WindowInfo.GetPid(fg) == _ownPid || WindowInfo.IsShellWindow(fg)) return false;
        return WindowMover.IsFullscreen(fg, MonitorOfWindow(fg));
    }

    /// <summary>Works out whether a game or presentation pauses placing and learning now (and says so when that changes).</summary>
    private bool UpdateGamePause()
    {
        bool paused = IsActive && GameOrPresentationRunning();
        if (paused == _gamePaused) return paused;
        _gamePaused = paused;
        if (paused)
        {
            ResetStreak();
            Log.Write("Paused: a game or presentation is running. New windows wait until it ends, and nothing is learned.");
        }
        else
        {
            Log.Write("The game or presentation ended: placing windows again.");
            ReleaseHeldWindows();
        }
        Changed?.Invoke();
        return paused;
    }

    /// <summary>
    /// The game or presentation ended: windows that opened meanwhile are placed now, except ones you've switched to
    /// (you're using them where they are) and ones that have waited longer than 15 minutes.
    /// </summary>
    private void ReleaseHeldWindows()
    {
        var now = DateTime.UtcNow;
        foreach (var (h, since) in _heldForGame.ToList())
        {
            if (!Native.IsWindow(h) || now - since > GameHoldMax || _pending.ContainsKey(h)) continue;
            _handled.Remove(h);
            var p = new Pending { Due = now, GiveUp = now.AddMilliseconds(GiveUpMs), FirstSeen = now };
            NotePointer(p, h, now);
            _pending[h] = p;
        }
        _heldForGame.Clear();
        if (_pending.Count > 0 && !_placeTimer.IsEnabled) _placeTimer.Start();
    }

    // ------------------------------------------------------------------ learning

    private void LearnTick()
    {
        if (++_tickCount % 30 == 0) // safety net, ~every minute
        {
            _handled.RemoveWhere(h => !Native.IsWindow(h));
            _movedByYou.RemoveWhere(h => !Native.IsWindow(h));
            foreach (var gone in _displaced.Keys.Where(h => !Native.IsWindow(h)).ToList()) _displaced.Remove(gone);
            ForgetOldCloakedWindows();
        }
        ReloadIfChanged(); // pick up changes made in the settings window / by the helper
        TrackPointer();
        bool gamePaused = UpdateGamePause();
        if (gamePaused) _heldForGame.Remove(Native.GetForegroundWindow()); // you switched to it: it stays where it is
        if (_identityBackfillDue && DateTime.UtcNow - _lastDisplayEventUtc > TimeSpan.FromMilliseconds(HoldAfterChangeMs))
            BackfillIdentity();
        if (IsMainEngine)
        {
            var now = DateTime.UtcNow;
            TrackForeground(now);
            AfterMonitorChanges(now);
            NoteSetup();
        }
        if (!_learning) return;
        if (gamePaused) { ResetStreak(); _positionSample = null; return; } // a game's own windows teach nothing
        SamplePosition(DateTime.UtcNow);

        // Don't learn while monitors are changing (sleep, display driver reset, a game switching resolution…) or
        // when only one monitor is connected: then you use apps where they *have* to be, not where you want them.
        // (Temporary monitors don't count.) The same goes for a Remote Desktop session (the remote PC's monitors
        // replace this one's) and for duplicated monitors (a "home" there would really be two monitors).
        if (LearningBlockedReason(DateTime.UtcNow) != null) { ResetStreak(); return; }

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
        // Nothing is learned on a monitor you switched off.
        if (app == null || mon == null || mon.IsSwitchedOff) { ResetStreak(); return; }

        if (app.Key == _streakKey && mon.DeviceName == _streakDevice) _streakCount++;
        else { _streakKey = app.Key; _streakDevice = mon.DeviceName; _streakCount = 1; }

        // Learn once per streak: using the same app on the same monitor for an hour doesn't redo it every 2 s.
        // (Windows on another virtual desktop never teach anything; checked only now, as it asks Windows' shell.)
        if (_streakCount >= LearnChecksNeeded && (app.Key != _learnedKey || mon.DeviceName != _learnedDevice)
            && WindowInfo.IsOnCurrentDesktop(fg))
        {
            _learnedKey = app.Key; _learnedDevice = mon.DeviceName;
            Learn(app, mon, fg);
        }
    }

    /// <summary>
    /// Why nothing may be learned right now, or null when learning may go ahead: only one (permanent) monitor, a Remote
    /// Desktop session, duplicated monitors, or monitors changed less than 30 seconds ago.
    /// </summary>
    internal string? LearningBlockedReason(DateTime now)
    {
        if (_monitors.Count(m => !m.IsTemporary) < 2) return "only one monitor"; // (each area of a split monitor counts)
        if (IsRemoteSession) return "Remote Desktop session";
        if (HasDuplicatedMonitors) return "duplicated monitors";
        if (now - _lastMonitorChangeUtc < TimeSpan.FromSeconds(MonitorSettleSeconds)) return "monitors changed just now";
        return null;
    }

    /// <summary>
    /// For a move started now: true while the monitors are still the ones it was started for (no display change since).
    /// A move that finds this false stops and is tried again once Windows has settled.
    /// </summary>
    internal Func<bool> StillCurrent(int generation) => () => generation == _topologyGeneration;

    /// <summary>Goes up by one on every display change (see <see cref="StillCurrent"/>).</summary>
    internal int TopologyGeneration => _topologyGeneration;

    /// <summary>Notes when the mouse pointer last moved (for "The monitor I'm using", with the keyboard only).</summary>
    private void TrackPointer()
    {
        if (!Native.GetCursorPos(out var pt) || (pt.X == _lastCursor.X && pt.Y == _lastCursor.Y)) return;
        _lastCursor = pt;
        _lastCursorMoveUtc = DateTime.UtcNow;
    }

    /// <summary>New windows still hidden after 30 s will never count as new: stop waiting for them.</summary>
    private void ForgetOldCloakedWindows()
    {
        var now = DateTime.UtcNow;
        foreach (var (h, seen) in _cloakedNew.ToList())
        {
            if (Native.IsWindow(h) && now - seen <= TimeSpan.FromSeconds(UncloakNewSeconds)) continue;
            _cloakedNew.Remove(h);
            if (Native.IsWindow(h)) _handled.Add(h);
        }
        foreach (var (h, since) in _heldForGame.ToList())
            if (!Native.IsWindow(h) || now - since > GameHoldMax) _heldForGame.Remove(h);
    }

    /// <summary>
    /// "Where I last had it": every learning tick, notes where the window you're using is on its monitor (as fractions of
    /// the work area), for apps set to that. It's saved only once it has stayed put for two ticks and moved more than a
    /// few percent from what's saved, so settings aren't written every 2 seconds. Nothing is noted while you drag a
    /// window, while monitors are changing, for fullscreen windows, or on a monitor that's switched off.
    /// </summary>
    private void SamplePosition(DateTime now)
    {
        if (!Settings.Enabled || !Settings.Apps.Any(a => a.Place == PlaceModes.Last && !a.NeverMove)
            || IsRemoteSession || HasDuplicatedMonitors
            || now - _lastMonitorChangeUtc < TimeSpan.FromSeconds(MonitorSettleSeconds)
            || now - _lastDisplayEventUtc < TimeSpan.FromMilliseconds(SettledMs))
        { _positionSample = null; return; }
        var sample = PositionOfForeground(out IntPtr fg, out var app, out var mon);
        bool steady = sample != null && fg == _positionWindow && _positionSample != null && SamePosition(sample, _positionSample, 0.005)
                      && Matches(_positionSample.Monitor, mon!);
        _positionWindow = fg;
        _positionSample = sample;
        if (!steady) return;
        var rule = Settings.Find(app!.Key)!;
        var saved = PositionOn(rule, mon!);
        if (saved != null && SamePosition(saved, sample!, PositionChange)) return;
        if (!WindowInfo.IsOnCurrentDesktop(fg)) return;
        SavePosition(app.Key, mon!, sample!);
    }

    /// <summary>Where the window you're using is, if its app is set to "Where I last had it"; else null.</summary>
    private WindowPosition? PositionOfForeground(out IntPtr fg, out AppIdentity? app, out MonitorInfo? mon)
    {
        app = null; mon = null;
        fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || _pending.ContainsKey(fg) || Native.IsIconic(fg)
            || (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 // dragging it right now
            || WindowInfo.Classify(fg, _ownPid) != WindowInfo.Kind.AppWindow) return null;
        app = WindowInfo.Identify(fg);
        var rule = app == null ? null : Settings.Find(app.Key);
        if (rule == null || rule.Place != PlaceModes.Last || rule.NeverMove) return null;
        mon = MonitorOfWindow(fg);
        if (mon == null || mon.IsSwitchedOff || WindowMover.IsFullscreen(fg, mon)) return null;
        var position = WindowMover.FractionOf(fg, mon);
        if (position != null) position.Monitor = RefTo(mon);
        return position;
    }

    private static bool SamePosition(WindowPosition a, WindowPosition b, double tolerance) =>
        a.Maximized == b.Maximized && Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance
        && Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;

    /// <summary>Saves where an app's window is on a monitor ("Where I last had it").</summary>
    private void SavePosition(string key, MonitorInfo mon, WindowPosition position)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null || rule.Place != PlaceModes.Last) return;
        rule.Positions.RemoveAll(p => Matches(p.Monitor, mon));
        rule.Positions.Add(position);
        Save();
        Log.Write($"Remembered where {rule.DisplayName} is on {mon.Name}{(position.Maximized ? " (maximized)" : "")}.  [{key} {mon.DeviceName}]");
        Changed?.Invoke();
    }

    private void ResetStreak() { _streakKey = null; _streakDevice = null; _streakCount = 0; _learnedKey = null; _learnedDevice = null; }

    private void Learn(AppIdentity app, MonitorInfo mon, IntPtr hwnd)
    {
        using var _ = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(app.Key);
        if (rule != null) rule = RefreshRuleDetails(rule, app, hwnd);
        // A window that one of its app's rules for some windows applies to says nothing about the app itself.
        if (MatchesWindowRule(rule, hwnd)) return;
        // It's there only because its own monitor couldn't be used: that's not a choice. (Drag it elsewhere and it is.)
        if (_displaced.TryGetValue(hwnd, out var displaced)
            && string.Equals(displaced.DroppedDevice, mon.DeviceName, StringComparison.OrdinalIgnoreCase)) return;
        // You undid learning it on this monitor: it isn't learned there again this session.
        if (_unlearned.Contains(app.Key + "|" + mon.DeviceName)) return;
        if (rule != null && rule.FollowLastUsed)
        {
            // Remember the last monitor you used it on, if it's one of the monitors you picked for it.
            // A temporary monitor, or one kept for chosen apps, only counts if you ticked it yourself.
            if (!IsAllowed(rule, mon) || Resolve(rule)?.DeviceName == mon.DeviceName) return;
            if ((mon.IsTemporary || mon.OnlyChosenApps) && rule.AllowedMonitors.Count == 0) return;
            string? was = rule.MonitorName ?? rule.MonitorDevice;
            var lastBefore = CopyRule(rule);
            SetMonitor(rule, mon); rule.UpdatedUtc = DateTime.UtcNow;
            Save();
            Record(ActivityKinds.Learned, $"{rule.DisplayName} opens on {mon.Name} next", "Where you used it last: you used it there",
                   new UndoData { RuleKey = rule.Key, RuleBefore = lastBefore, OnlyMonitor = true, LearnedOn = mon.DeviceName },
                   $"Last used: {rule.DisplayName} is now on {mon.Name}{(was == null ? "" : $" (was {was})")}, so it opens there next.  [{rule.Key} {mon.DeviceName}]",
                   NoticeLearned, rule.Key);
            return;
        }
        if (!Settings.LearnAutomatically) return;
        if (rule != null && (rule.Pinned || rule.NeverMove)) return;
        if (rule == null && WindowInfo.IsOneOffApp(app.Key)) return; // setup / uninstall / update programs
        if (mon.IsTemporary) return; // wireless, virtual…: never an app's home unless you set it
        if (mon.OnlyChosenApps) return; // "only apps I choose": learning never makes it a home
        // Another setup than the usual one: learned for these monitors only.
        if (rule != null && InOtherSetup) { LearnForSetup(rule, mon); return; }
        if (rule != null && Resolve(rule)?.DeviceName == mon.DeviceName)
        {
            if (rule.MonitorDevice != mon.DeviceName || rule.MonitorName != mon.FriendlyName
                || !mon.Hardware.SameAs(rule.MonitorHardware ?? new MonitorHardware()))
            {   // same screen, Windows just renamed it: keep the record current quietly
                SetMonitor(rule, mon); Save();
            }
            return;
        }

        string? old = rule == null ? null : (Resolve(rule)?.Name ?? rule.MonitorName ?? rule.MonitorDevice);
        var before = rule == null ? null : CopyRule(rule);
        if (rule == null) { rule = new AppRule { Key = app.Key, LaunchTarget = WindowInfo.GetLaunchTarget(hwnd) }; Settings.Apps.Add(rule); }
        bool keepName = app.Key.StartsWith(WindowInfo.WebAppKeyPrefix, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(rule.DisplayName) && rule.DisplayName != rule.Key;
        if (!keepName) rule.DisplayName = app.DisplayName;
        SetMonitor(rule, mon);
        rule.UpdatedUtc = DateTime.UtcNow;
        Save();
        Record(ActivityKinds.Learned, old == null ? $"{app.DisplayName} opens on {mon.Name}" : $"{app.DisplayName} now opens on {mon.Name}",
               old == null ? "Learned: you used it there for a few seconds" : $"Learned: you used it there for a few seconds (it was {old})",
               new UndoData { RuleKey = app.Key, RuleBefore = before, OnlyMonitor = true, LearnedOn = mon.DeviceName },
               (old == null ? $"Learned: {app.DisplayName} opens on {mon.Name}." : $"Learned: {app.DisplayName} now opens on {mon.Name} (was {old}).")
               + $"  [{app.Key} {mon.DeviceName}]",
               NoticeLearned, app.Key);
    }

    // ------------------------------------------------------------------ monitors coming back, sleep, main monitor

    /// <summary>The host calls this just before the PC goes to sleep or hibernates: remember where windows are.</summary>
    public void OnSuspending()
    {
        if (_disposed || !IsMainEngine) return;
        if (!_locked) TakeSnapshot(); // when locked, the snapshot from locking is the right one (monitors may be off since)
        _sleepSnapshot = _snapshot.ToDictionary(kv => kv.Key, kv => kv.Value);
        _restoreDue = false;
        _restoreBaseline = null;
        Log.Write($"Going to sleep: noted where {_sleepSnapshot.Count} windows are.");
    }

    /// <summary>The host calls this when the PC wakes up. Windows are put back once monitors have settled and you're signed in.</summary>
    public void OnResumed()
    {
        if (_disposed || !IsMainEngine || _sleepSnapshot == null || _restoreDue) return;
        if (!Settings.RestoreAfterSleep || _sleepSnapshot.Count == 0) { _sleepSnapshot = null; return; }
        _restoreDue = true;
        _restoreBaseline = null;
        _movedSinceWake.Clear();
        _restoreDeadlineUtc = DateTime.UtcNow.AddSeconds(RestoreWaitSeconds);
    }

    /// <summary>The host calls this when the session is locked or unlocked.</summary>
    public void OnSessionLock(bool locked)
    {
        if (_disposed) return;
        if (locked)
        {
            TakeSnapshot(); // before monitors go dark: some of them disconnect when they sleep
            _locked = true;
            return;
        }
        _locked = false;
        if (_restoreDue) { _restoreBaseline = null; _restoreDeadlineUtc = DateTime.UtcNow.AddSeconds(RestoreWaitSeconds); }
    }

    /// <summary>Monitors changed, or one can be used again: check shortly whether windows should move back.</summary>
    private void ScheduleReturnCheck()
    {
        _returnCheckDue = true;
        _returnNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(SettledMs);
    }

    /// <summary>
    /// Work that waits until monitors have settled after a change (every 2 s, on the learning tick, only in the engine
    /// that places windows): note the windows of a monitor that went away, keep the main monitor, put windows back after
    /// sleep, and move windows back to a monitor that's back.
    /// </summary>
    private void AfterMonitorChanges(DateTime now)
    {
        if (now - _lastDisplayEventUtc < TimeSpan.FromMilliseconds(SettledMs)) return;
        if (_vanishScanDue) { _vanishScanDue = false; NoteWindowsOfMissingMonitors(); }
        if (_locked) return; // nothing moves while nobody is looking
        if (_gamePaused) return; // nor while a game or presentation runs: this waits until it ends
        if (_keepMainDue) { _keepMainDue = false; EnforceMainMonitor(now); }
        if (_restoreDue) { RestoreAfterSleepTick(now); return; }
        if (_returnCheckDue && now >= _returnNotBeforeUtc) { _returnCheckDue = false; MoveDisplacedBack(); }
        if (_rescueDue && !_returnCheckDue) { _rescueDue = false; RescueOffScreenWindows(); }
    }

    /// <summary>Snapshots aren't taken while monitors are changing or just changed, so they never record a pile-up.</summary>
    private bool CanSnapshot(DateTime now) =>
        IsMainEngine && !_locked && !_restoreDue && !IsRemoteSession
        && now - _lastMonitorChangeUtc >= TimeSpan.FromSeconds(MonitorSettleSeconds)
        && now - _lastDisplayEventUtc >= TimeSpan.FromMilliseconds(SettledMs); // (Windows may be moving windows right now)

    /// <summary>Notes where every app window is (in memory only; window titles aren't kept).</summary>
    private void TakeSnapshot()
    {
        if (!CanSnapshot(DateTime.UtcNow)) return;
        var snap = new Dictionary<IntPtr, WindowSnap>();
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            if (Snap(h, ProcessStartOf(h)) is { } s) snap[h] = s;
        }
        _snapshot = snap;
    }

    /// <summary>
    /// Between snapshots, keeps the window you're using up to date (cheap: no process lookups for windows already
    /// noted), so a window you just moved to the TV is known to be there when the TV goes away.
    /// </summary>
    private void TrackForeground(DateTime now)
    {
        if (!CanSnapshot(now)) return;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || Native.IsIconic(fg)) return;
        if (_snapshot.TryGetValue(fg, out var old))
        {
            if (Snap(fg, old.ProcessStart) is { } s) _snapshot[fg] = s;
            return;
        }
        if (fg == _lastSnapForeground) return; // checked already: not an app window
        _lastSnapForeground = fg;
        if (WindowInfo.Classify(fg, _ownPid) == WindowInfo.Kind.AppWindow && Snap(fg, ProcessStartOf(fg)) is { } added)
            _snapshot[fg] = added;
    }

    private WindowSnap? Snap(IntPtr h, long processStart)
    {
        var m = MonitorOfWindow(h);
        if (m == null || !Native.GetWindowRect(h, out var r)) return null;
        return new WindowSnap { Monitor = RefTo(m), MonitorBounds = m.Bounds, Rect = r, Maximized = Native.IsZoomed(h), ProcessStart = processStart };
    }

    private static long ProcessStartOf(IntPtr h) => WindowInfo.GetProcessStartTime(WindowInfo.GetPid(h));

    private static bool SameRect(Native.RECT a, Native.RECT b) =>
        Math.Abs(a.Left - b.Left) <= RectSlack && Math.Abs(a.Top - b.Top) <= RectSlack
        && Math.Abs(a.Right - b.Right) <= RectSlack && Math.Abs(a.Bottom - b.Bottom) <= RectSlack;

    /// <summary>Is this still the same window (not closed, not a handle Windows reused for another program)?</summary>
    private static bool SameWindow(IntPtr h, long processStart) =>
        Native.IsWindow(h) && Native.IsWindowVisible(h) && (processStart == 0 || ProcessStartOf(h) == processStart);

    /// <summary>Remembers a window that isn't on its own monitor only because that monitor can't be used right now.</summary>
    private void AddDisplaced(IntPtr h, string? appKey, MonitorRef? home, WindowSnap? saved)
    {
        if (!Native.GetWindowRect(h, out var r)) return;
        _displaced[h] = new Displaced
        {
            AppKey = appKey, Home = home, Saved = saved, DroppedRect = r,
            DroppedDevice = DeviceOfWindow(h) ?? "",
            ProcessStart = saved?.ProcessStart ?? ProcessStartOf(h),
        };
    }

    /// <summary>Remembers a new window placed on a stand-in monitor (see <see cref="ChooseTarget"/>), if it was.</summary>
    private void RememberDisplaced(IntPtr h, AppRule? rule, bool displaced, MonitorRef? returnTo)
    {
        if (displaced && rule != null)
        {
            // A window rule, launch shortcut or setup chose another monitor than the app's own: back to that one.
            var saved = Settings.Find(rule.Key);
            bool own = rule.MonitorDevice != null && !rule.FollowLastUsed && saved != null
                       && (saved.MonitorDevice != rule.MonitorDevice || saved.MonitorName != rule.MonitorName);
            if (own) AddDisplaced(h, null, new MonitorRef { Device = rule.MonitorDevice!, Name = rule.MonitorName, Hardware = rule.MonitorHardware }, null);
            else AddDisplaced(h, rule.Key, null, null);
        }
        else if (returnTo != null) AddDisplaced(h, null, returnTo, null);
    }

    /// <summary>After the move is checked, note where a displaced window really ended up (the app may have adjusted it).</summary>
    private void RefreshDropped(IntPtr h)
    {
        if (!_displaced.TryGetValue(h, out var d) || !Native.IsWindow(h) || !Native.GetWindowRect(h, out var r)) return;
        d.DroppedRect = r;
        d.DroppedDevice = DeviceOfWindow(h) ?? d.DroppedDevice;
    }

    /// <summary>
    /// A monitor went away: the windows that were on it (per the last snapshot) were moved by Windows. Note them, so they
    /// go back when the monitor returns.
    /// </summary>
    private void NoteWindowsOfMissingMonitors()
    {
        int n = 0;
        foreach (var (h, s) in _snapshot.ToList())
        {
            if (_displaced.ContainsKey(h) || FindMonitor(s.Monitor) != null) continue; // its monitor is still there
            if (!SameWindow(h, s.ProcessStart) || Native.IsIconic(h)) continue;
            AddDisplaced(h, null, s.Monitor, s);
            n++;
        }
        if (n > 0) Log.Write($"{n} window{(n == 1 ? " was" : "s were")} on a monitor that went away; {(n == 1 ? "it goes" : "they go")} back when it returns.");
    }

    /// <summary>Where a displaced window belongs now: (monitor, false) if it can go back, (null, false) to keep waiting, (null, true) to forget it.</summary>
    private (MonitorInfo? Home, bool Forget) HomeOf(Displaced d)
    {
        if (d.AppKey != null)
        {
            var rule = WithSetupHome(Settings.Find(d.AppKey));
            if (rule == null || rule.NeverMove || rule.FollowMouse) return (null, true);
            var m = Resolve(rule);
            return m != null && IsAllowed(rule, m) && CanUse(rule, m) ? (m, false) : (null, false);
        }
        var home = FindMonitor(d.Home);
        return home != null && !home.IsSwitchedOff ? (home, false) : (null, false);
    }

    /// <summary>
    /// "When a monitor comes back": windows that had to go elsewhere go back to their monitor, once it can be used again.
    /// Skipped: windows Windows already put back, windows you moved since, and minimized, fullscreen, not-responding or
    /// administrator windows.
    /// </summary>
    private void MoveDisplacedBack()
    {
        if (_displaced.Count == 0 || !Settings.Enabled || IsRemoteSession) return;
        int generation = _topologyGeneration;
        var movedTo = new Dictionary<string, List<string>>();
        var undoFor = new Dictionary<string, UndoData>();
        foreach (var (h, d) in _displaced.ToList())
        {
            var (home, forget) = HomeOf(d);
            if (forget) { _displaced.Remove(h); continue; }
            if (home == null) continue; // its monitor still can't be used: keep waiting
            _displaced.Remove(h);
            if (!Settings.MoveBackWhenMonitorReturns || !SameWindow(h, d.ProcessStart)) continue;
            var current = MonitorOfWindow(h);
            if (current != null && current.DeviceName == home.DeviceName) continue;               // already back
            if (!Native.GetWindowRect(h, out var now) || !SameRect(now, d.DroppedRect)) continue; // you moved it
            if (!CanMoveNow(h, current)) continue;
            var before = Snap(h, d.ProcessStart);
            var result = PutBack(h, d.Saved, home, generation);
            if (result == WindowMover.Result.Aborted)
            {
                _displaced[h] = d; // monitors changed again: try once they've settled
                ScheduleReturnCheck();
                break;
            }
            if (result != WindowMover.Result.Moved) continue;
            if (!movedTo.TryGetValue(home.Name, out var names)) movedTo[home.Name] = names = new List<string>();
            names.Add(WindowInfo.Identify(h)?.DisplayName ?? "a window");
            if (!undoFor.TryGetValue(home.Name, out var undo)) undoFor[home.Name] = undo = new UndoData();
            if (before != null) undo.Windows.Add((h, before));
        }
        foreach (var (monitor, names) in movedTo)
        {
            string list = string.Join(", ", names.Distinct());
            Record(ActivityKinds.Back, $"{monitor} is back: moved {list} back there", "When a monitor comes back, its windows move back",
                   undoFor.TryGetValue(monitor, out var undo) && !undo.IsEmpty ? undo : null,
                   $"{monitor} is back: moved {list} back there.", NoticeMonitorBack, monitor);
        }
    }

    /// <summary>Windows that are never moved behind your back: minimized, fullscreen, not responding, or run as administrator.</summary>
    private bool CanMoveNow(IntPtr h, MonitorInfo? current) =>
        !Native.IsIconic(h) && !Native.IsHungAppWindow(h) && !WindowMover.IsFullscreen(h, current)
        && (_selfElevated || !WindowInfo.IsProcessElevated(WindowInfo.GetPid(h)));

    /// <summary>
    /// Puts a window back on a monitor: exactly where it was (<paramref name="saved"/>) if that monitor still has the
    /// same size and position, else centred on it. Maximized windows are maximized there again.
    /// </summary>
    private WindowMover.Result PutBack(IntPtr h, WindowSnap? saved, MonitorInfo target, int generation)
    {
        var stillValid = StillCurrent(generation);
        bool sameMonitor = saved != null && SameRect(saved.MonitorBounds, target.Bounds);
        bool onTarget = string.Equals(DeviceOfWindow(h), target.DeviceName, StringComparison.OrdinalIgnoreCase);
        if (saved == null || !sameMonitor) return onTarget ? WindowMover.Result.Moved : WindowMover.Move(h, target, stillValid);
        if (!saved.Maximized) return WindowMover.PlaceAt(h, target, saved.Rect, stillValid);
        if (!onTarget)
        {
            var r = WindowMover.Move(h, target, stillValid);
            if (r != WindowMover.Result.Moved) return r;
        }
        if (!Native.IsZoomed(h) && WindowMover.Responds(h)) Native.ShowWindow(h, Native.SW_MAXIMIZE);
        return WindowMover.Result.Moved;
    }

    /// <summary>
    /// "Put windows back after sleep": once you're signed in again and monitors have settled (and every monitor from
    /// before is back, or a minute has passed), note where windows are, wait a few seconds, then put back the ones that
    /// moved during sleep and that you haven't moved since.
    /// </summary>
    private void RestoreAfterSleepTick(DateTime now)
    {
        var snap = _sleepSnapshot;
        if (snap == null || !Settings.Enabled || !Settings.RestoreAfterSleep || IsRemoteSession) { EndRestore(); return; }
        bool allBack = snap.Values.All(s => FindMonitor(s.Monitor) != null);
        if (!allBack && now < _restoreDeadlineUtc) return;
        if (_restoreBaseline == null || _restoreBaselineGeneration != _topologyGeneration)
        {
            _restoreBaseline = new Dictionary<IntPtr, Native.RECT>();
            foreach (var h in snap.Keys.ToList())
                if (Native.IsWindow(h) && Native.GetWindowRect(h, out var r)) _restoreBaseline[h] = r;
            _restoreBaselineUtc = now;
            _restoreBaselineGeneration = _topologyGeneration;
            return;
        }
        if (now - _restoreBaselineUtc < TimeSpan.FromMilliseconds(SettledMs)) return;
        RestoreWindows(snap, _restoreBaseline);
        EndRestore();
        ScheduleReturnCheck(); // windows of a monitor that isn't back yet go back when it is
    }

    private void EndRestore()
    {
        _restoreDue = false;
        _sleepSnapshot = null;
        _restoreBaseline = null;
        _movedSinceWake.Clear();
    }

    private void RestoreWindows(Dictionary<IntPtr, WindowSnap> snap, Dictionary<IntPtr, Native.RECT> baseline)
    {
        int generation = _topologyGeneration;
        var names = new List<string>();
        var undo = new UndoData();
        foreach (var (h, s) in snap.ToList()) // (a copy: window events can arrive while a move waits for an app)
        {
            if (!SameWindow(h, s.ProcessStart) || !baseline.TryGetValue(h, out var before)) continue;
            if (_movedSinceWake.Contains(h)) continue; // you dragged it after waking up
            if (!Native.GetWindowRect(h, out var now) || !SameRect(now, before)) continue; // you moved it after waking up
            var target = FindMonitor(s.Monitor);
            if (target == null || target.IsSwitchedOff)
            {
                if (!_displaced.ContainsKey(h)) AddDisplaced(h, null, s.Monitor, s); // goes back when its monitor does
                continue;
            }
            var current = MonitorOfWindow(h);
            bool onTarget = current?.DeviceName == target.DeviceName;
            if (onTarget && (s.Maximized ? Native.IsZoomed(h) : SameRect(now, s.Rect))) continue; // already where it was
            if (onTarget && !SameRect(s.MonitorBounds, target.Bounds)) continue; // that monitor changed size: can't tell where
            if (!CanMoveNow(h, current)) continue;
            var was = Snap(h, s.ProcessStart);
            var result = PutBack(h, s, target, generation);
            if (result == WindowMover.Result.Aborted) break; // monitors changed again
            if (result != WindowMover.Result.Moved) continue;
            _displaced.Remove(h);
            names.Add(WindowInfo.Identify(h)?.DisplayName ?? "a window");
            if (was != null) undo.Windows.Add((h, was));
        }
        if (names.Count > 0)
        {
            string text = $"After sleep: put {names.Count} window{(names.Count == 1 ? "" : "s")} back where {(names.Count == 1 ? "it was" : "they were")}";
            Record(ActivityKinds.Back, text, $"Put windows back after sleep ({string.Join(", ", names.Distinct())})",
                   undo.IsEmpty ? null : undo, $"{text} ({string.Join(", ", names.Distinct())}).");
        }
    }

    /// <summary>
    /// "Keep my main monitor": if Windows made another monitor main, make the chosen one main again. At most twice in
    /// five minutes; after that it stops (a driver that keeps changing it back would otherwise cause a loop).
    /// </summary>
    private void EnforceMainMonitor(DateTime now)
    {
        var want = Settings.KeepMainMonitor;
        if (want == null || !Settings.Enabled || IsRemoteSession || HasDuplicatedMonitors || _physical.Count < 2) return;
        if (Settings.KeepMainStopped) return;
        var keep = FindMonitor(want);
        if (keep == null || keep.IsPhysicalPrimary) return;
        _mainFixes.RemoveAll(t => now - t > KeepMainPeriod);
        if (_mainFixes.Count >= KeepMainMaxFixes)
        {
            StopKeepingMain();
            Note($"Keep my main monitor: Windows keeps making another monitor main, so it's no longer changed back to {keep.Name}. " +
                 "Turn \"Keep my main monitor\" off and on to try again.", "");
            Notice?.Invoke("Main monitor not changed back",
                $"Windows keeps making another monitor main, so Monitor Window Control stopped changing it back to {keep.Name}.");
            return;
        }
        _mainFixes.Add(now);
        string was = MainMonitor?.Name ?? "another monitor";
        string? error = ChangeMainMonitor(keep);
        if (error != null) { Log.Write($"Keep my main monitor: couldn't make {keep.Name} main again. {error}"); return; }
        Note($"Windows made {was} the main monitor; changed it back to {keep.Name} (keep my main monitor).", $"[{keep.DeviceName}]");
        if (_keepMainNoticeShown) return;
        _keepMainNoticeShown = true;
        Notice?.Invoke("Main monitor changed back", $"Windows made {was} the main monitor; Monitor Window Control changed it back to {keep.Name}.");
    }

    /// <summary>Saves that "Keep my main monitor" stopped, so the settings window (and the helper) can say so.</summary>
    private void StopKeepingMain()
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.KeepMainMonitor == null) return;
        Settings.KeepMainStopped = true;
        Save();
        Changed?.Invoke();
    }

    /// <summary>A "switched off for 1 hour" is over: the monitor counts as on again (and windows can move back to it).</summary>
    private void OffTimeOver()
    {
        var wasOff = _monitors.Where(m => m.IsSwitchedOff).ToList();
        _nextOffExpiryUtc = DateTime.MaxValue;
        RefreshMonitors(); // works out "switched off" again with the time now
        var back = wasOff.Where(m => MonitorByDevice(m.DeviceName) is { IsSwitchedOff: false }).Select(m => m.Name).ToList();
        if (back.Count == 0) return;
        if (IsMainEngine) Note($"{string.Join(", ", back)}: the hour is over, so it's treated as switched on again.", "");
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Recent activity and Undo

    /// <summary>Which Notifications choice an event belongs to (see <see cref="NoticeSettings"/>).</summary>
    private const string NoticeLearned = "Learned", NoticeOpenedElsewhere = "OpenedElsewhere",
                         NoticeMonitorBack = "MonitorBack", NoticeAdminApp = "AdminApp";

    /// <summary>
    /// Adds an event to Recent activity (the last 10, in memory only) and writes <paramref name="log"/> to the log.
    /// With <paramref name="undo"/>, the event can be undone. <paramref name="notice"/> names the Notifications choice
    /// that shows it from the tray icon (at most once a day per app or monitor, <paramref name="noticeKey"/>).
    /// </summary>
    private ActivityEntry Record(string kind, string text, string reason, UndoData? undo, string log,
        string? notice = null, string? noticeKey = null)
    {
        if (undo is { IsEmpty: true }) undo = null;
        var entry = new ActivityEntry { Id = _nextActivityId++, Time = DateTime.Now, Kind = kind, Text = text, Reason = reason, CanUndo = undo != null };
        _recent.Insert(0, entry);
        if (undo != null) _undo[entry.Id] = undo;
        while (_recent.Count > RecentMax)
        {
            _undo.Remove(_recent[^1].Id);
            _recent.RemoveAt(_recent.Count - 1);
        }
        LastAction = $"{entry.Time:HH:mm}  {text}";
        Log.Write(log);
        Changed?.Invoke();
        if (notice != null) MaybeNotify(entry, notice, noticeKey ?? "");
        return entry;
    }

    /// <summary>Shows a notification for an event, if you turned that kind on (only in tray mode, at most once a day per app).</summary>
    private void MaybeNotify(ActivityEntry entry, string notice, string key)
    {
        if (!IsMainEngine || ActivityNotice == null) return; // the helper has no tray icon: it's in Recent activity instead
        var n = Settings.Notices;
        bool on = notice switch
        {
            NoticeLearned => n.Learned, NoticeOpenedElsewhere => n.OpenedElsewhere,
            NoticeMonitorBack => n.MonitorBack, NoticeAdminApp => n.AdminApp, _ => false,
        };
        if (!on) return;
        string k = notice + "|" + key;
        var today = DateTime.Now.Date;
        if (_noticeDay.TryGetValue(k, out var shown) && shown == today) return;
        _noticeDay[k] = today;
        ActivityNotice(entry);
    }

    /// <summary>Feedback for something you did with a keyboard shortcut: always shown (in tray mode).</summary>
    private void Announce(ActivityEntry entry)
    {
        if (IsMainEngine) ActivityNotice?.Invoke(entry);
    }

    /// <summary>"1 window", "3 windows".</summary>
    private static string Windows(int n) => n == 1 ? "1 window" : $"{n} windows";

    /// <summary>A copy of a saved app's choices (to put them back on Undo).</summary>
    private static AppRule CopyRule(AppRule rule) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(rule, SettingsJson.Default.AppRule), SettingsJson.Default.AppRule)!;

    /// <summary>
    /// Undoes a Recent activity event: windows go back where they were (if they're still open, belong to the same
    /// program, and their monitor is connected), and an app's choice goes back to what it was. Undoing the placement of
    /// an app's new windows twice offers to stop moving that app (<see cref="UndoOutcome.StopMovingKey"/>).
    /// </summary>
    public UndoOutcome Undo(int id)
    {
        var entry = _recent.FirstOrDefault(e => e.Id == id);
        if (entry == null || entry.Undone || !_undo.Remove(id, out var u))
            return new UndoOutcome("This can't be undone any more.", null, null);
        entry.CanUndo = false;
        string? error = u.RuleKey != null ? RestoreRule(u) : null;
        bool ruleDone = u.RuleKey != null && error == null;
        int back = error == null ? PutWindowsBack(u) : 0;
        if (error == null && u.Windows.Count > 0 && back == 0)
            error = u.Windows.Count == 1
                ? "The window couldn't be moved back. It may have been closed, or the monitor it was on isn't connected any more."
                : "The windows couldn't be moved back. They may have been closed, or the monitor they were on isn't connected any more.";
        entry.Undone = ruleDone || back > 0;
        Log.Write($"Undo: {entry.Text}" + (error == null ? "." : $". {error}"));

        string? askKey = null, askName = null;
        if (u.PlacedApp != null && back > 0)
        {
            int n = (_placementUndos.TryGetValue(u.PlacedApp, out int c) ? c : 0) + 1;
            if (n >= 2 && Settings.Find(u.PlacedApp)?.NeverMove != true) { askKey = u.PlacedApp; askName = u.PlacedAppName; n = 0; }
            _placementUndos[u.PlacedApp] = n;
        }
        Changed?.Invoke();
        return new UndoOutcome(error, askKey, askName);
    }

    /// <summary>Puts an app's choice back as it was before the event. Returns a problem to show, or null.</summary>
    private string? RestoreRule(UndoData u)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var now = Settings.Find(u.RuleKey!);
        var before = u.RuleBefore;
        static string Choice(AppRule? r) => r == null ? "" : $"{r.Pinned}{r.NeverMove}{r.FollowLastUsed}{r.FollowMouse}";
        if (u.OnlyMonitor)
        {
            // Learned: only its monitor goes back, and only if you haven't chosen something yourself since.
            if (now == null) return "It was forgotten since, so there's nothing to undo.";
            if (Choice(now) != Choice(before ?? new AppRule())) return $"You've chosen where {now.DisplayName} opens since, so your choice was kept.";
            if (before == null) Settings.Apps.Remove(now);
            else
            {
                now.MonitorDevice = before.MonitorDevice; now.MonitorName = before.MonitorName; now.MonitorHardware = before.MonitorHardware;
                now.UpdatedUtc = DateTime.UtcNow;
            }
            if (u.LearnedOn != null) _unlearned.Add(u.RuleKey + "|" + u.LearnedOn);
        }
        else
        {
            if (before == null && now?.ShortcutFile != null)
                return $"{now.DisplayName} has a launch shortcut now. To remove it, select it on the Apps page and press Forget.";
            if (now != null) Settings.Apps.Remove(now);
            if (before != null)
            {
                var restored = CopyRule(before);
                if (now != null) { restored.ShortcutFile = now.ShortcutFile; restored.LaunchTarget ??= now.LaunchTarget; }
                Settings.Apps.Add(restored);
            }
        }
        Save();
        ResetStreak();
        return null;
    }

    /// <summary>Moves the windows of an Undo back where they were. Returns how many are back.</summary>
    private int PutWindowsBack(UndoData u)
    {
        int back = 0, generation = _topologyGeneration;
        foreach (var (h, was) in u.Windows)
        {
            if (!SameWindow(h, was.ProcessStart)) continue; // closed, or the handle now belongs to another program
            var m = FindMonitor(was.Monitor);
            if (m == null || Native.IsIconic(h)) continue;
            var current = MonitorOfWindow(h);
            if (current?.DeviceName == m.DeviceName && Native.GetWindowRect(h, out var r) && SameRect(r, was.Rect)) { back++; continue; }
            if (!CanMoveNow(h, null) || PutBack(h, was, m, generation) != WindowMover.Result.Moved) continue;
            back++;
            _pending.Remove(h); _handled.Add(h); _displaced.Remove(h);
            _movedByYou.Add(h); // where you wanted it: "Move open windows to their monitors" leaves it there
        }
        return back;
    }

    // ------------------------------------------------------------------ tidy up

    /// <summary>One window "Move open windows to their monitors" would move.</summary>
    private sealed record TidyMove(IntPtr Hwnd, AppIdentity App, AppRule? Rule, MonitorInfo From, MonitorInfo To,
        bool Displaced, MonitorRef? ReturnTo, string Reason);

    /// <summary>
    /// The windows that aren't on their app's monitor: each app's choice applied once, like for a new window. Left alone:
    /// apps set to "Never move", apps it hasn't learned (unless they're on a monitor that can't be used), minimized,
    /// fullscreen, not-responding and administrator windows, windows on other virtual desktops, and (unless
    /// <paramref name="includeMoved"/>) windows you moved yourself this session.
    /// </summary>
    private List<TidyMove> TidyPlan(bool includeMoved)
    {
        var plan = new List<TidyMove>();
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            if (!includeMoved && _movedByYou.Contains(h)) continue;
            if (Native.IsIconic(h) || WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            var app = WindowInfo.Identify(h);
            if (app == null) continue;
            var rule = RuleForWindow(app, Settings.Find(app.Key), h, WindowInfo.GetTitle(h), out _);
            if (rule?.NeverMove == true) continue;
            var current = MonitorOfWindow(h);
            if (current == null) continue;
            var (to, reason, displaced, returnTo, _) = rule != null || !CanUse(null, current) ? ChooseTarget(rule, current, null) : default;
            if (to == null || to.DeviceName == current.DeviceName) continue;
            if (!CanMoveNow(h, current) || !WindowInfo.IsOnCurrentDesktop(h)) continue;
            plan.Add(new TidyMove(h, app, rule, current, to, displaced, returnTo, reason));
        }
        return plan;
    }

    /// <summary>How many windows "Move open windows to their monitors" would move now (the preview).</summary>
    public int TidyCount(bool includeMoved) => TidyPlan(includeMoved).Count;

    /// <summary>"Move open windows to their monitors": moves each window in <see cref="TidyPlan"/> once. Returns how many moved.</summary>
    public int TidyUp(bool includeMoved)
    {
        var undo = new UndoData();
        var names = new List<string>();
        int generation = _topologyGeneration;
        foreach (var t in TidyPlan(includeMoved))
        {
            var before = Snap(t.Hwnd, ProcessStartOf(t.Hwnd));
            var result = MovePlaced(t.Hwnd, t.To, PlacementFor(t.Rule, t.To), false, StillCurrent(generation));
            if (result == WindowMover.Result.Aborted) break; // monitors are changing: stop here
            if (result != WindowMover.Result.Moved) continue;
            _pending.Remove(t.Hwnd); _handled.Add(t.Hwnd); _displaced.Remove(t.Hwnd);
            RememberDisplaced(t.Hwnd, t.Rule, t.Displaced, t.ReturnTo);
            if (before != null) undo.Windows.Add((t.Hwnd, before));
            names.Add(t.App.DisplayName);
            Log.Write($"Tidy up: moved {t.App.Key} \"{WindowInfo.GetTitle(t.Hwnd)}\" from {t.From.Name} to {t.To.Name} ({t.Reason}).");
        }
        if (names.Count == 0)
        {
            Log.Write("Move open windows to their monitors: nothing to move.");
            return 0;
        }
        string list = string.Join(", ", names.Distinct());
        Record(ActivityKinds.Moved, $"Moved {Windows(names.Count)} to their monitors", $"You chose Move open windows to their monitors ({list})",
               undo, $"Moved {Windows(names.Count)} to their monitors ({list}).");
        return names.Count;
    }

    /// <summary>
    /// "Bring all windows to this monitor" (<paramref name="from"/> null) or "Move its windows to…" (only the windows on
    /// <paramref name="from"/>): moves those app windows to <paramref name="target"/>, without changing any app's
    /// monitor. Apps set to "Never move", minimized, fullscreen, administrator windows and other virtual desktops are left
    /// alone. Returns how many moved.
    /// </summary>
    public int MoveWindowsTo(MonitorInfo target, MonitorInfo? from)
    {
        target = MonitorByDevice(target.DeviceName) ?? target;
        var undo = new UndoData();
        var names = new List<string>();
        int generation = _topologyGeneration;
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            if (Native.IsIconic(h) || WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            var current = MonitorOfWindow(h);
            if (current?.DeviceName == target.DeviceName) continue;
            if (from != null && current?.DeviceName != from.DeviceName) continue;
            var app = WindowInfo.Identify(h);
            if (app == null) continue;
            var rule = Settings.Find(app.Key);
            if (rule?.NeverMove == true || !CanMoveNow(h, current) || !WindowInfo.IsOnCurrentDesktop(h)) continue;
            var before = Snap(h, ProcessStartOf(h));
            var result = MovePlaced(h, target, PlacementFor(rule, target), false, StillCurrent(generation));
            if (result == WindowMover.Result.Aborted) break;
            if (result != WindowMover.Result.Moved) continue;
            _pending.Remove(h); _handled.Add(h); _displaced.Remove(h);
            _movedByYou.Add(h);
            if (before != null) undo.Windows.Add((h, before));
            names.Add(app.DisplayName);
        }
        if (names.Count == 0) return 0;
        string list = string.Join(", ", names.Distinct());
        string text = from == null ? $"Brought {Windows(names.Count)} to {target.Name}" : $"Moved {Windows(names.Count)} from {from.Name} to {target.Name}";
        Record(ActivityKinds.Moved, text, $"You asked for it ({list}). Apps still open on their own monitors next time.", undo, $"{text} ({list}).");
        return names.Count;
    }

    /// <summary>Moves one app's open windows to a monitor now, without saving it as the app's monitor. Returns how many moved.</summary>
    public int MoveAppWindowsTo(string key, string? displayName, MonitorInfo target)
    {
        target = MonitorByDevice(target.DeviceName) ?? target;
        var rule = Settings.Find(key);
        var undo = new UndoData();
        undo.Windows.AddRange(MoveOpenWindows(key, rule, target, "you asked for it"));
        foreach (var (h, _) in undo.Windows) _movedByYou.Add(h);
        if (undo.Windows.Count == 0) return 0;
        string name = rule?.DisplayName ?? displayName ?? key;
        Record(ActivityKinds.Moved, $"Moved {name} to {target.Name}", "You asked for it on the Apps page; where it opens next time didn't change",
               undo, $"Moved {Windows(undo.Windows.Count)} of {name} to {target.Name} (you asked for it).  [{key}]");
        return undo.Windows.Count;
    }

    /// <summary>
    /// After monitors have settled from a change: app windows that ended up outside every monitor (a resolution change,
    /// a monitor that went away) are moved to their app's monitor, or the main monitor. Minimized ones get a new place to
    /// open from, without being restored. Apps set to "Never move" and windows on other virtual desktops are left alone.
    /// </summary>
    private void RescueOffScreenWindows()
    {
        if (!Settings.Enabled || IsRemoteSession || _monitors.Count == 0) return;
        int generation = _topologyGeneration;
        var names = new List<string>();
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            bool minimized = Native.IsIconic(h);
            var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (minimized ? !Native.GetWindowPlacement(h, ref wp) || !OffScreen(wp.rcNormalPosition) : !IsOffScreen(h)) continue;
            if (WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            var app = WindowInfo.Identify(h);
            if (app == null) continue;
            var rule = Settings.Find(app.Key);
            if (rule?.NeverMove == true || !WindowInfo.IsOnCurrentDesktop(h)) continue;
            var target = (rule == null ? null : ResolveTarget(rule)) ?? FallbackMonitor;
            if (target == null || Native.IsHungAppWindow(h) || (!_selfElevated && WindowInfo.IsProcessElevated(WindowInfo.GetPid(h)))) continue;
            bool moved = minimized
                ? MoveMinimizedOnto(h, wp, target)
                : WindowMover.Move(h, target, StillCurrent(generation)) == WindowMover.Result.Moved;
            if (!moved) continue;
            _displaced.Remove(h);
            names.Add(app.DisplayName);
            Log.Write($"{app.Key} \"{WindowInfo.GetTitle(h)}\" was outside every monitor; moved it to {target.Name}{(minimized ? " (it's still minimized)" : "")}.");
        }
        if (names.Count == 0) return;
        string list = string.Join(", ", names.Distinct());
        Record(ActivityKinds.Moved, $"Moved {list} back onto your monitors", "After monitors changed, it was outside every monitor", null,
               $"Moved {Windows(names.Count)} that were outside every monitor back onto your monitors ({list}).");
    }

    /// <summary>True if the window (not minimized) is outside every monitor. Windows Windows parks far away don't count.</summary>
    private static bool IsOffScreen(IntPtr h) => Native.GetWindowRect(h, out var r) && OffScreen(r);

    private static bool OffScreen(Native.RECT r)
    {
        if (r.Width <= 0 || r.Height <= 0 || r.Left <= ParkedCoordinate || r.Top <= ParkedCoordinate) return false;
        return Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONULL) == IntPtr.Zero;
    }

    /// <summary>
    /// A minimized window whose restored position is outside every monitor: its restored position is centred on the
    /// target (sized to fit), and it stays minimized.
    /// </summary>
    private static bool MoveMinimizedOnto(IntPtr h, Native.WINDOWPLACEMENT wp, MonitorInfo target)
    {
        if (!WindowMover.Responds(h)) return false;
        var work = target.Work;
        var r = wp.rcNormalPosition;
        int w = Math.Min(r.Width, work.Width), ht = Math.Min(r.Height, work.Height);
        // (Restored positions are in "workspace" coordinates: screen coordinates shifted by a taskbar at the top or left.)
        int left = work.Left + (work.Width - w) / 2 - (target.PhysicalWork.Left - target.PhysicalBounds.Left);
        int top = work.Top + (work.Height - ht) / 2 - (target.PhysicalWork.Top - target.PhysicalBounds.Top);
        wp.rcNormalPosition = new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + ht };
        wp.showCmd = Native.SW_SHOWMINNOACTIVE;
        wp.flags = 0;
        return Native.SetWindowPlacement(h, ref wp);
    }

    // ------------------------------------------------------------------ keyboard shortcuts

    private static int HotKeyId(int index, bool save) => HotKeyBaseId + index * 2 + (save ? 1 : 0);

    /// <summary>
    /// Registers the keyboard shortcuts you turned on, and only those (and only in the program that places windows):
    /// a send-to-monitor shortcut for each connected monitor, next and previous, and "Move open windows to their
    /// monitors", each send shortcut also with Shift. Shortcuts another app already uses are noted in
    /// <see cref="HotKeyProblems"/> and tried again the next time monitors or settings change.
    /// </summary>
    private void UpdateHotKeys()
    {
        var want = new Dictionary<int, (int Modifiers, int Key)>();
        var hk = Settings.HotKeys;
        if (IsMainEngine && hk.Enabled && OnlyAppKeys == null && _monitors.Count > 1)
        {
            for (int i = 0; i < HotKeyActions.All.Length; i++)
            {
                string action = HotKeyActions.All[i];
                if (HotKeyActions.MonitorNumber(action) > _monitors.Count) continue;
                var (mods, key) = HotKeyActions.KeysFor(hk, action);
                if (key == 0) continue;
                want[HotKeyId(i, false)] = (mods, key);
                if (HotKeyActions.HasSaveVersion(action)) want[HotKeyId(i, true)] = (mods | HotKeyActions.Shift, key);
            }
        }
        foreach (var id in _hotKeys.Keys.ToList())
        {
            if (want.TryGetValue(id, out var k) && k == _hotKeys[id]) continue;
            _host.UnregisterHotKey(id);
            _hotKeys.Remove(id);
        }
        var problems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, k) in want)
        {
            if (_hotKeys.ContainsKey(id)) continue;
            if (_host.RegisterHotKey(id, k.Modifiers | Native.MOD_NOREPEAT, k.Key)) _hotKeys[id] = k;
            else problems.Add(HotKeyProblemName(id));
        }
        if (problems.SetEquals(_hotKeyProblems)) return;
        foreach (var p in problems.Except(_hotKeyProblems))
            Log.Write($"Keyboard shortcut {HotKeyText(p)} is in use by another app, so it doesn't work here.");
        _hotKeyProblems.Clear();
        _hotKeyProblems.UnionWith(problems);
        Changed?.Invoke();
    }

    /// <summary>"Monitor2" or "Monitor2+Shift" for a shortcut id.</summary>
    private static string HotKeyProblemName(int id)
    {
        int n = id - HotKeyBaseId;
        return HotKeyActions.All[n / 2] + (n % 2 == 1 ? "+Shift" : "");
    }

    /// <summary>The keys of a <see cref="HotKeyProblems"/> entry, in words.</summary>
    private string HotKeyText(string problem)
    {
        bool shift = problem.EndsWith("+Shift", StringComparison.Ordinal);
        var (mods, key) = HotKeyActions.KeysFor(Settings.HotKeys, shift ? problem[..^6] : problem);
        return HotKeyActions.Text(mods | (shift ? HotKeyActions.Shift : 0), key);
    }

    private void UnregisterHotKeys()
    {
        foreach (var id in _hotKeys.Keys) _host.UnregisterHotKey(id);
        _hotKeys.Clear();
        _hotKeyProblems.Clear();
    }

    /// <summary>The host calls this when one of the keyboard shortcuts is pressed.</summary>
    public void OnHotKey(int id)
    {
        if (_disposed || !IsMainEngine) return;
        int n = id - HotKeyBaseId;
        if (n < 0 || n / 2 >= HotKeyActions.All.Length) return;
        string action = HotKeyActions.All[n / 2];
        ReloadIfChanged();
        if (action == HotKeyActions.Tidy)
        {
            int moved = TidyUp(includeMoved: false);
            Announce(moved > 0 ? _recent[0] : new ActivityEntry { Text = "Open windows are already on their monitors", Kind = ActivityKinds.Info });
            return;
        }
        SendForeground(action, save: n % 2 == 1);
    }

    /// <summary>
    /// The monitor for "send to monitor <paramref name="n"/>": the monitor you gave number n, else the n-th monitor in
    /// the order the Monitors page shows them.
    /// </summary>
    public MonitorInfo? MonitorForShortcut(int n) =>
        _monitors.FirstOrDefault(m => m.Number == n) ?? (n >= 1 && n <= _monitors.Count ? _monitors[n - 1] : null);

    /// <summary>The next (or previous) monitor after <paramref name="current"/>, in your order, going round.</summary>
    private MonitorInfo? NextMonitor(MonitorInfo? current, int step)
    {
        if (_monitors.Count < 2) return null;
        int i = current == null ? 0 : _monitors.FindIndex(m => m.DeviceName == current.DeviceName);
        return _monitors[((Math.Max(i, 0) + step) % _monitors.Count + _monitors.Count) % _monitors.Count];
    }

    /// <summary>
    /// A send-to-monitor shortcut: moves the window you're using to that monitor (sized for it, keeping it maximized,
    /// at the spot you last had it there if the app is set to that). With <paramref name="save"/> (Shift), that monitor
    /// also becomes the app's monitor ("Set by you"), and its other open windows move there too.
    /// </summary>
    private void SendForeground(string action, bool save)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || WindowInfo.Classify(fg, _ownPid) != WindowInfo.Kind.AppWindow || Native.IsIconic(fg))
        {
            Log.Write("Keyboard shortcut: the window in front isn't an app's window, so nothing was moved.");
            return;
        }
        var app = WindowInfo.Identify(fg);
        if (app == null) return;
        var current = MonitorOfWindow(fg);
        int number = HotKeyActions.MonitorNumber(action);
        var target = number > 0 ? MonitorForShortcut(number) : NextMonitor(current, action == HotKeyActions.Next ? 1 : -1);
        if (target == null) return;
        if (!_selfElevated && WindowInfo.IsProcessElevated(WindowInfo.GetPid(fg)))
        {
            Announce(Record(ActivityKinds.Skipped, $"Couldn't move {app.DisplayName}",
                            "It runs as administrator, and Windows doesn't let other apps move it", null,
                            $"Keyboard shortcut: couldn't move {app.Key}: it runs as administrator."));
            return;
        }

        var undo = new UndoData();
        if (current?.DeviceName != target.DeviceName)
        {
            if (Native.IsHungAppWindow(fg)) return;
            var before = Snap(fg, ProcessStartOf(fg));
            int generation = _topologyGeneration;
            var result = MovePlaced(fg, target, PlacementFor(Settings.Find(app.Key), target), false, StillCurrent(generation));
            if (result != WindowMover.Result.Moved)
            {
                Log.Write($"Keyboard shortcut: couldn't move {app.Key} to {target.Name}.");
                return;
            }
            _pending.Remove(fg); _handled.Add(fg); _displaced.Remove(fg);
            _movedByYou.Add(fg);
            if (before != null) undo.Windows.Add((fg, before));
        }
        if (!save)
        {
            if (!undo.IsEmpty)
                Record(ActivityKinds.Moved, $"Sent {app.DisplayName} to {target.Name}", "Keyboard shortcut", undo,
                       $"Keyboard shortcut: sent {app.DisplayName} to {target.Name}.  [{app.Key} {target.DeviceName}]");
            return;
        }
        using (SettingsStore.Lock())
        {
            ReloadIfChanged();
            var pinned = PinCore(app.Key, target, app.DisplayName, out _, out _);
            undo.RuleKey = pinned.RuleKey; undo.RuleBefore = pinned.RuleBefore;
            undo.Windows.AddRange(pinned.Windows);
        }
        Announce(Record(ActivityKinds.SetByYou, $"{app.DisplayName} → {target.Name} (saved)", "Keyboard shortcut with Shift: it opens there from now on",
                        undo, $"Keyboard shortcut: {app.DisplayName} opens on {target.Name} from now on (set by you).  [{app.Key} {target.DeviceName}]"));
    }

    /// <summary>"Keyboard shortcuts" on or off.</summary>
    public void SetHotKeysEnabled(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.HotKeys.Enabled == on) return;
        Settings.HotKeys.Enabled = on; Save();
        Log.Write("Keyboard shortcuts: " + (on ? "on" : "off"));
        UpdateHotKeys();
        Changed?.Invoke();
    }

    /// <summary>Changes the keys of one shortcut (<paramref name="key"/> 0 = no shortcut). The default keys aren't saved.</summary>
    public void SetHotKey(string action, int modifiers, int key)
    {
        if (!HotKeyActions.IsKnown(action) || !HotKeyActions.IsValid(modifiers, key)) return;
        if (key == 0) modifiers = 0;
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var keys = Settings.HotKeys.Keys;
        keys.RemoveAll(k => k.Action == action);
        if ((modifiers, key) != HotKeyActions.DefaultKeys(action)) keys.Add(new HotKeyChoice { Action = action, Modifiers = modifiers, Key = key });
        Save();
        Log.Write($"Keyboard shortcut for {action}: {HotKeyActions.Text(modifiers, key)}");
        UpdateHotKeys();
        Changed?.Invoke();
    }

    /// <summary>Another shortcut that already has these keys (with or without Shift), or null.</summary>
    public string? HotKeyUsedBy(string action, int modifiers, int key)
    {
        if (key == 0) return null;
        int Plain(int m) => m & ~HotKeyActions.Shift;
        return HotKeyActions.All.FirstOrDefault(a => a != action && HotKeyActions.KeysFor(Settings.HotKeys, a) is var k
                                                    && k.Key == key && Plain(k.Modifiers) == Plain(modifiers));
    }

    /// <summary>One of the "Notifications" choices on or off (see <see cref="NoticeSettings"/>).</summary>
    public void SetNotice(string which, bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var n = Settings.Notices;
        switch (which)
        {
            case NoticeLearned: n.Learned = on; break;
            case NoticeOpenedElsewhere: n.OpenedElsewhere = on; break;
            case NoticeMonitorBack: n.MonitorBack = on; break;
            case NoticeAdminApp: n.AdminApp = on; break;
            default: return;
        }
        Save();
        Log.Write($"Notification \"{which}\": {(on ? "on" : "off")}");
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ the app window in front (tray menu)

    /// <summary>Notes the app window you switched to (for the tray menu's "Always open on…").</summary>
    private void NoteForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _lastAppWindow) return;
        if (WindowInfo.Classify(hwnd, _ownPid) == WindowInfo.Kind.AppWindow) _lastAppWindow = hwnd;
    }

    /// <summary>The last app you used (not this one), its monitor, and its program file (null for Store apps).</summary>
    public sealed record ForegroundApp(AppIdentity App, MonitorInfo? Monitor, string? ExePath);

    /// <summary>The last app window you used, if it's still open (only in the program that places windows).</summary>
    public ForegroundApp? LastForegroundApp()
    {
        var h = _lastAppWindow;
        if (h == IntPtr.Zero || !Native.IsWindow(h) || !Native.IsWindowVisible(h)) return null;
        var app = WindowInfo.Identify(h);
        if (app == null) return null;
        return new ForegroundApp(app, MonitorOfWindow(h), app.IsStoreApp ? null : WindowInfo.GetProcessPath(WindowInfo.GetPid(h)));
    }

    // ------------------------------------------------------------------ requests from the settings window

    /// <summary>
    /// Does what the settings window asks (see <see cref="HelperRequest"/>) and returns the answer, with Recent
    /// activity and the keyboard shortcut problems.
    /// </summary>
    public HelperReply Answer(HelperRequest req)
    {
        var reply = new HelperReply { Id = req.Id };
        try
        {
            switch (req.Command)
            {
                case HelperRequest.Status:
                    reply.Count = TidyCount(req.IncludeMoved);
                    break;
                case HelperRequest.Tidy:
                    reply.Count = TidyUp(req.IncludeMoved);
                    break;
                case HelperRequest.Gather:
                case HelperRequest.MoveApp:
                    var to = MonitorByDevice(req.ToDevice);
                    var from = MonitorByDevice(req.FromDevice);
                    if (to == null || (req.FromDevice != null && from == null)) { reply.Error = "That monitor isn't connected any more."; break; }
                    reply.Count = req.Command == HelperRequest.Gather ? MoveWindowsTo(to, from)
                        : req.AppKey == null ? 0 : MoveAppWindowsTo(req.AppKey, req.AppName, to);
                    break;
                case HelperRequest.Undo:
                    var o = Undo(req.ActivityId);
                    reply.Error = o.Error; reply.StopMovingKey = o.StopMovingKey; reply.StopMovingName = o.StopMovingName;
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Unexpected error ({req.Command}): {ex.Message}");
            reply.Error = "Something went wrong. The details are in the activity log.";
        }
        reply.Recent = _recent.ToList();
        reply.HotKeyProblems = _hotKeyProblems.ToList();
        return reply;
    }

    /// <summary>The helper's side of <see cref="HelperChannel"/>: reads the request, answers it (on the engine's thread).</summary>
    public void AnswerRequest()
    {
        if (_disposed) return;
        var req = HelperChannel.ReadRequest();
        if (req == null) return;
        ReloadIfChanged();
        HelperChannel.WriteReply(Answer(req));
    }

    // ------------------------------------------------------------------ user actions

    public void SetEnabled(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.Enabled == on || Policy.Enabled != null) return; // (managed by your organization: locked)
        Settings.Enabled = on; Save();
        Log.Write(on ? "Enabled." : "Disabled.");
        Changed?.Invoke();
    }

    public void SetLearn(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.LearnAutomatically == on || Policy.LearnAutomatically != null) return;
        Settings.LearnAutomatically = on; Save(); ResetStreak();
        Log.Write("Learn automatically: " + (on ? "on" : "off"));
        Changed?.Invoke();
    }

    public void SetMoveUnknownToMain(bool on) => SetUnknownApps(on, followMouse: false);

    /// <summary>
    /// "Apps it hasn't learned yet": the main monitor, where Windows puts them, or (<paramref name="followMouse"/>) the
    /// monitor with the mouse pointer. With the pointer, "main monitor" is saved too, for older versions.
    /// </summary>
    public void SetUnknownApps(bool toMain, bool followMouse)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Policy.UnknownApps != null) return;
        toMain |= followMouse;
        if (Settings.MoveUnknownToMain == toMain && Settings.UnknownFollowMouse == followMouse) return;
        Settings.MoveUnknownToMain = toMain;
        Settings.UnknownFollowMouse = followMouse;
        Save();
        Log.Write("Apps it hasn't learned yet: " + (followMouse ? "open on the monitor with the mouse pointer"
            : toMain ? "open on main monitor" : "leave where Windows puts them"));
        Changed?.Invoke();
    }

    /// <summary>"Open dialogs on the same monitor as their app".</summary>
    public void SetMoveDialogsWithApp(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.MoveDialogsWithApp == on) return;
        Settings.MoveDialogsWithApp = on; Save();
        Log.Write("Open dialogs on the same monitor as their app: " + (on ? "on" : "off"));
        Changed?.Invoke();
    }

    /// <summary>"Pause while a game or presentation is running".</summary>
    public void SetPauseForGames(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.PauseForGames == on) return;
        Settings.PauseForGames = on; Save();
        Log.Write("Pause while a game or presentation is running: " + (on ? "on" : "off"));
        if (IsActive) UpdateGamePause(); // turning it off while a game runs places the windows that waited
        Changed?.Invoke();
    }

    /// <summary>"Also place apps that opened while signing in".</summary>
    public void SetPlaceAppsOpenedAtSignIn(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.PlaceAppsOpenedAtSignIn == on) return;
        Settings.PlaceAppsOpenedAtSignIn = on; Save();
        Log.Write("Also place apps that opened while signing in: " + (on ? "on" : "off"));
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
        if (Policy.IsAppLocked(key)) return;
        if (monitor != null && PinForSetup(key, monitor)) return; // another setup than the usual one: for these monitors only
        var undo = PinCore(key, monitor, displayName, out var rule, out int n);
        string moved = n == 0 ? "" : n == 1 ? " Its open window was moved there." : $" Its {n} open windows were moved there.";
        string text = monitor == null ? $"Never move {rule.DisplayName}" : $"{rule.DisplayName} opens on {monitor.Name}";
        Record(ActivityKinds.SetByYou, text, "Set by you" + (n == 0 ? "" : n == 1 ? "; its open window moved there" : $"; its {n} open windows moved there"),
               undo, (monitor == null ? $"Set by you: never move {rule.DisplayName}." : $"Set by you: {rule.DisplayName} opens on {monitor.Name}.{moved}")
                     + $"  [{rule.Key}{(monitor == null ? "" : " " + monitor.DeviceName)}]");
    }

    /// <summary>
    /// Pins an app (see <see cref="Pin"/>) without writing to Recent activity. Returns how to undo it; <paramref name="moved"/>
    /// is how many of its open windows were moved. Call it holding the settings lock.
    /// </summary>
    private UndoData PinCore(string key, MonitorInfo? monitor, string? displayName, out AppRule rule, out int moved)
    {
        rule = RuleToChange(key, displayName, out var undo);
        rule.LaunchTarget ??= FindLaunchTarget(key);
        rule.Pinned = true;
        rule.NeverMove = monitor == null;
        rule.FollowLastUsed = false;
        rule.FollowMouse = false;
        rule.AllowedMonitors.Clear();
        if (monitor != null) SetMonitor(rule, monitor);
        rule.UpdatedUtc = DateTime.UtcNow;
        ForgetCurrentSetupHome(key); // (your choice now applies here too)
        Save();
        ResetStreak();
        if (monitor != null) undo.Windows.AddRange(MoveOpenWindows(rule.Key, rule, monitor, "set by you"));
        moved = undo.Windows.Count;
        return undo;
    }

    /// <summary>
    /// The saved choice for an app you're about to change (added if it isn't saved yet), and how to undo the change.
    /// Call it holding the settings lock.
    /// </summary>
    private AppRule RuleToChange(string key, string? displayName, out UndoData undo)
    {
        var rule = Settings.Find(key);
        undo = new UndoData { RuleKey = key, RuleBefore = rule == null ? null : CopyRule(rule) };
        if (rule != null) return rule;
        rule = new AppRule { Key = key, DisplayName = displayName ?? key };
        Settings.Apps.Add(rule);
        return rule;
    }

    /// <summary>
    /// "Where I used it last": the app opens on whichever of <paramref name="allowed"/> you used it on most recently.
    /// <paramref name="currentMonitor"/> (where it is now, if open) seeds that, so it works from the start.
    /// </summary>
    public void SetFollowLastUsed(string key, IReadOnlyList<MonitorInfo> allowed, MonitorInfo? currentMonitor, string? displayName = null)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Policy.IsAppLocked(key)) return;
        var rule = RuleToChange(key, displayName, out var undo);
        rule.LaunchTarget ??= FindLaunchTarget(key);
        rule.Pinned = true;
        rule.NeverMove = false;
        rule.FollowLastUsed = true;
        rule.FollowMouse = false;
        rule.AllowedMonitors = allowed.Select(RefTo).ToList();
        // Start from where it is now / was last, if that's allowed; otherwise the first allowed monitor.
        var start = new[] { currentMonitor, Resolve(rule) }.FirstOrDefault(m => m != null && IsAllowed(rule, m))
                    ?? AllowedInOrder(rule).FirstOrDefault();
        SetMonitor(rule, start);
        rule.UpdatedUtc = DateTime.UtcNow;
        ForgetCurrentSetupHome(key);
        Save();
        ResetStreak();
        string where = string.Join(" or ", allowed.Select(m => m.Name));
        Record(ActivityKinds.SetByYou, $"{rule.DisplayName} opens where you used it last", $"Set by you: {where}", undo,
               $"Set by you: {rule.DisplayName} opens where you used it last ({where}).  [{rule.Key} {string.Join(",", allowed.Select(m => m.DeviceName))}]");
    }

    /// <summary>
    /// "The monitor I'm using (mouse pointer)": the app's new windows open on the monitor with the mouse pointer, if
    /// it's one of <paramref name="allowed"/> (in your order: the first one that can be used is taken otherwise).
    /// Its open windows aren't moved.
    /// </summary>
    public void SetFollowMouse(string key, IReadOnlyList<MonitorInfo> allowed, string? displayName = null)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Policy.IsAppLocked(key)) return;
        var rule = RuleToChange(key, displayName, out var undo);
        rule.LaunchTarget ??= FindLaunchTarget(key);
        rule.Pinned = true;
        rule.NeverMove = false;
        rule.FollowLastUsed = false;
        rule.FollowMouse = true;
        // Every monitor ticked = any monitor (also ones connected later).
        rule.AllowedMonitors = allowed.Count >= _monitors.Count ? new List<MonitorRef>() : allowed.Select(RefTo).ToList();
        rule.UpdatedUtc = DateTime.UtcNow;
        ForgetCurrentSetupHome(key);
        Save();
        ResetStreak();
        string which = rule.AllowedMonitors.Count == 0 ? "any monitor" : string.Join(" or ", allowed.Select(m => m.Name));
        Record(ActivityKinds.SetByYou, $"{rule.DisplayName} opens on the monitor you're using", $"Set by you: {which}", undo,
               $"Set by you: {rule.DisplayName} opens on the monitor you're using ({which}).  [{rule.Key}]");
    }

    /// <summary>
    /// "Place it" and the Advanced timing for one saved app: <paramref name="place"/> is null (centered) or one of
    /// <see cref="PlaceModes"/>; <paramref name="waitMs"/> and <paramref name="correctForMs"/> are null (automatic, 1 s)
    /// or one of the choices in <see cref="AppTimings"/>.
    /// </summary>
    public void SetAppPlacement(string key, string? place, int? waitMs, int? correctForMs)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null) return;
        place = PlaceModes.Normalize(place);
        waitMs = AppTimings.NormalizeWait(waitMs);
        correctForMs = AppTimings.NormalizeCorrect(correctForMs);
        bool timingChanged = rule.WaitMs != waitMs || rule.CorrectForMs != correctForMs;
        if (rule.Place == place && !timingChanged) return;
        if (rule.Place != place) Log.Write($"Set by you: {rule.DisplayName} is placed {PlaceText(place)}.  [{rule.Key}]");
        if (timingChanged)
        {
            Log.Write($"Set by you: {rule.DisplayName} waits {(waitMs == null ? "the usual time" : $"{waitMs / 1000} s")} before moving, " +
                      $"and is kept in place for {(correctForMs ?? 1000) / 1000} s.  [{rule.Key}]");
            rule.MovedItselfBack = false; // you've acted on the tip
            _jumpBacks.Remove(rule.Key);
        }
        rule.Place = place; rule.WaitMs = waitMs; rule.CorrectForMs = correctForMs;
        rule.UpdatedUtc = DateTime.UtcNow;
        Save();
        _positionSample = null;
        Changed?.Invoke();
    }

    /// <summary>"Place it" in words, for the log.</summary>
    private static string PlaceText(string? place) => place switch
    {
        PlaceModes.Last => "where you last had it",
        PlaceModes.Maximized => "maximized",
        _ => "in the center of its monitor",
    };

    /// <summary>"Where I last had it" → Reset: forgets where the app's window was on every monitor.</summary>
    public void ResetPositions(string key)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null || rule.Positions.Count == 0) return;
        rule.Positions.Clear();
        Save();
        _positionSample = null;
        Note($"Forgot where {rule.DisplayName} was on each monitor; it opens in the center until you've used it again.", $"[{rule.Key}]");
        Changed?.Invoke();
    }

    /// <summary>
    /// Makes a monitor the main display in Windows itself (same as Settings → Display → "Make this my main display").
    /// Windows requires the main display to sit at (0,0), so every display is shifted by the same amount;
    /// the physical arrangement stays the same. Returns an error message, or null on success.
    /// With "Keep my main monitor" on, the new main monitor becomes the one that's kept.
    /// </summary>
    public string? SetMainMonitor(MonitorInfo target)
    {
        string? error = ChangeMainMonitor(target);
        if (error != null) return error;
        Note($"Main monitor is now {target.Name} (changed in Windows).", $"[{target.DeviceName}]");
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.KeepMainMonitor == null) return null;
        var main = MainMonitor ?? target;
        Settings.KeepMainMonitor = RefTo(main);
        Settings.KeepMainStopped = false; _mainFixes.Clear();
        Save();
        Log.Write($"Keep my main monitor: now keeps {main.Name}.");
        Changed?.Invoke();
        return null;
    }

    /// <summary>Makes a monitor the main display in Windows (see <see cref="SetMainMonitor"/>). Returns an error message, or null.</summary>
    private string? ChangeMainMonitor(MonitorInfo target)
    {
        if (!Policy.MakeMainAllowed) return "Your organization doesn't allow changing the main monitor.";
        target = PhysicalOf(target); // (Windows' own display: for an area, the whole monitor)
        var dm = NewDevMode();
        if (!Native.EnumDisplaySettings(target.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref dm))
            return "Windows didn't return the settings of that monitor.";
        int dx = dm.dmPositionX, dy = dm.dmPositionY;

        // Every display's current settings (new main first).
        var displays = new List<(MonitorInfo m, Native.DEVMODE original)>();
        foreach (var m in _physical.OrderByDescending(m => m.DeviceName == target.DeviceName))
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
                string oldMain = MainMonitor?.PhysicalDevice ?? "";
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
        OptionsFor(m).Temporary = temporary != m.Targets[0].IsTemporaryByDefault ? temporary : null;
        Settings.MonitorOptions.RemoveAll(o => o.IsEmpty);
        Save();
        RefreshMonitors();
        Note(temporary ? $"{m.Name} is a temporary monitor: apps aren't learned there."
                       : $"{m.Name} is a normal monitor: apps can be learned there.", $"[{m.DeviceName}]");
        Changed?.Invoke();
    }

    /// <summary>
    /// "Treat as switched off": while on, new windows don't open on this monitor (they go where they would go if it
    /// were disconnected) and nothing is learned there. <paramref name="duration"/> null = until you turn it back on.
    /// Turning it back on moves back the windows that had to go elsewhere (see "When a monitor comes back").
    /// </summary>
    public void SetMonitorSwitchedOff(MonitorInfo m, bool off, TimeSpan? duration = null)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        m = MonitorByDevice(m.DeviceName) ?? m;
        var o = OptionsFor(m);
        o.Off = off;
        o.OffUntilUtc = off && duration != null ? DateTime.UtcNow + duration.Value : null;
        Settings.MonitorOptions.RemoveAll(x => x.IsEmpty);
        Save();
        RefreshMonitors();
        ResetStreak();
        Note(!off ? $"{m.Name} is treated as switched on again."
                  : duration == null ? $"{m.Name} is treated as switched off until you turn it back on: new windows open elsewhere."
                  : $"{m.Name} is treated as switched off until {o.OffUntilUtc!.Value.ToLocalTime():t}: new windows open elsewhere.",
             $"[{m.DeviceName}]");
        Changed?.Invoke();
    }

    /// <summary>
    /// "What can open here": any app, or only apps you chose for this monitor (learning never makes it a home, and
    /// other apps' new windows that Windows opens there are moved off it).
    /// </summary>
    public void SetMonitorOnlyChosenApps(MonitorInfo m, bool onlyChosen)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        m = MonitorByDevice(m.DeviceName) ?? m;
        OptionsFor(m).OnlyChosenApps = onlyChosen;
        Settings.MonitorOptions.RemoveAll(x => x.IsEmpty);
        Save();
        RefreshMonitors();
        ResetStreak();
        Note(onlyChosen ? $"{m.Name}: only apps you choose open here." : $"{m.Name}: any app can open here.", $"[{m.DeviceName}]");
        Changed?.Invoke();
    }

    /// <summary>"Keep my main monitor": on = keep the current main monitor as main; off = let Windows decide.</summary>
    public void SetKeepMainMonitor(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var main = MainMonitor;
        if (on && (main == null || !Policy.MakeMainAllowed)) return;
        Settings.KeepMainMonitor = on ? RefTo(main!) : null;
        Settings.KeepMainStopped = false; _mainFixes.Clear();
        Save();
        Note(on ? $"Keep my main monitor: {main!.Name} stays the main monitor." : "Keep my main monitor: off.", "");
        Changed?.Invoke();
    }

    /// <summary>"When an app's monitor is off", for all apps: <paramref name="mode"/> is null (main monitor) or one of <see cref="FallbackModes"/>.</summary>
    public void SetDefaultFallback(string? mode, MonitorInfo? monitor)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        (Settings.Fallback, Settings.FallbackMonitor) = FallbackChoice(mode == FallbackModes.Main ? null : mode, monitor);
        Save();
        Log.Write("When an app's monitor is off: " + FallbackText(Settings.Fallback, Settings.FallbackMonitor));
        Changed?.Invoke();
    }

    /// <summary>"If it's off, open on" for one saved app: null = the choice for all apps, or one of <see cref="FallbackModes"/>.</summary>
    public void SetAppFallback(string key, string? mode, MonitorInfo? monitor)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        var rule = Settings.Find(key);
        if (rule == null) return;
        var (newMode, newMonitor) = FallbackChoice(mode, monitor);
        if (rule.Fallback == newMode && (newMonitor == null ? rule.FallbackMonitor == null
                                          : rule.FallbackMonitor != null && RefKey(rule.FallbackMonitor) == RefKey(newMonitor))) return;
        rule.Fallback = newMode; rule.FallbackMonitor = newMonitor;
        rule.UpdatedUtc = DateTime.UtcNow;
        Save();
        Log.Write($"Set by you: if its monitor is off, {rule.DisplayName} opens on {FallbackText(rule.Fallback, rule.FallbackMonitor, forApp: true)}.  [{rule.Key}]");
        Changed?.Invoke();
    }

    /// <summary>A fallback choice as it's saved: a known mode, with a monitor only for <see cref="FallbackModes.Monitor"/>.</summary>
    private static (string? mode, MonitorRef? monitor) FallbackChoice(string? mode, MonitorInfo? monitor)
    {
        if (mode != FallbackModes.Monitor) return (FallbackModes.Normalize(mode, null), null);
        return monitor == null ? (null, null) : (FallbackModes.Monitor, RefTo(monitor));
    }

    /// <summary>A fallback choice in words ("main monitor", "2 · Side", "where Windows puts it").</summary>
    public string FallbackText(string? mode, MonitorRef? monitor, bool forApp = false) => mode switch
    {
        FallbackModes.Leave => "where Windows puts it",
        FallbackModes.Monitor => FindMonitor(monitor)?.Name ?? $"{monitor?.Name ?? monitor?.Device} (not connected)",
        FallbackModes.Main => "the main monitor",
        _ => forApp ? "the same monitor as other apps" : "the main monitor",
    };

    /// <summary>"When a monitor comes back": move windows that had to go elsewhere back to it, or leave them.</summary>
    public void SetMoveBackWhenMonitorReturns(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.MoveBackWhenMonitorReturns == on) return;
        Settings.MoveBackWhenMonitorReturns = on; Save();
        Log.Write("When a monitor comes back: " + (on ? "move its windows back" : "leave them"));
        Changed?.Invoke();
    }

    /// <summary>"Put windows back after sleep".</summary>
    public void SetRestoreAfterSleep(bool on)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        if (Settings.RestoreAfterSleep == on) return;
        Settings.RestoreAfterSleep = on; Save();
        Log.Write("Put windows back after sleep: " + (on ? "on" : "off"));
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
        if (Settings.RunMode == mode || (Policy.RunMode != null && Policy.RunMode != mode)) return;
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
            if (WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow || !WindowInfo.IsOnCurrentDesktop(h)) continue;
            var app = WindowInfo.Identify(h);
            if (app == null) continue;
            if (result.TryGetValue(app.Key, out var existing)) result[app.Key] = existing with { Windows = existing.Windows + 1 };
            else result[app.Key] = new OpenApp(app, MonitorOfWindow(h), 1);
        }
        return result.Values.ToList();
    }

    /// <summary>
    /// Moves the app's open windows to the monitor (you asked for it, so existing windows are moved). Returns where each
    /// moved window was, for Undo. <paramref name="why"/> is for the log.
    /// </summary>
    private List<(IntPtr, WindowSnap)> MoveOpenWindows(string key, AppRule? rule, MonitorInfo target, string why)
    {
        var moved = new List<(IntPtr, WindowSnap)>();
        var place = PlacementFor(rule, target);
        foreach (var h in WindowInfo.EnumTopLevel())
        {
            if (WindowInfo.Classify(h, _ownPid) != WindowInfo.Kind.AppWindow) continue;
            var app = WindowInfo.Identify(h);
            if (app == null || !string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (!WindowInfo.IsOnCurrentDesktop(h)) continue; // windows on other virtual desktops aren't disturbed
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
            var before = Snap(h, ProcessStartOf(h));
            if (MovePlaced(h, target, place, false, () => true) == WindowMover.Result.Moved)
            {
                if (before != null) moved.Add((h, before));
                _displaced.Remove(h);
                Log.Write($"Moved open window {app.Key} \"{title}\" from {current?.Name ?? "?"} to {target.Name} ({why}).");
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
        if (rule == null || Policy.IsAppLocked(key)) return;
        Settings.Apps.Remove(rule);
        foreach (var setup in Settings.Setups) setup.Homes.RemoveAll(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));
        Save(); ResetStreak();
        Note($"Forgot {rule.DisplayName}.", $"[{rule.Key}]");
        Changed?.Invoke();
    }

    public void ForgetAll()
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        SettingsStore.WriteBackup(force: true); // "Forget all" can always be undone with Restore a backup
        int n = Settings.Apps.Count(a => !Policy.IsAppLocked(a.Key));
        Settings.Apps.RemoveAll(a => !Policy.IsAppLocked(a.Key)); // (apps your organization sets stay)
        foreach (var setup in Settings.Setups) setup.Homes.RemoveAll(h => Settings.Find(h.Key) == null);
        Save(); ResetStreak();
        Note($"Forgot all {n} apps.", "");
        Changed?.Invoke();
    }

    /// <summary>
    /// "Export settings": writes your settings to <paramref name="file"/>, with this PC's main monitor noted so an import
    /// on another PC can match it. Returns an error message, or null.
    /// </summary>
    public string? ExportSettings(string file)
    {
        var copy = SettingsStore.Copy(Settings);
        copy.ExportedMainMonitor = MainMonitor is MonitorInfo main ? RefTo(main) : null;
        return SettingsStore.Export(copy, file);
    }

    /// <summary>
    /// "Import settings" and "Restore a backup": <paramref name="build"/> gets a copy of the current settings and returns
    /// the new ones. A backup of the current settings is kept first. How it runs, Start with Windows and Keep my main
    /// monitor belong to this PC and stay as they are; settings your organization manages are put back over the result.
    /// </summary>
    public void ImportSettings(Func<AppSettings, AppSettings> build, string note)
    {
        using var _lock = SettingsStore.Lock();
        ReloadIfChanged();
        SettingsStore.WriteBackup(force: true);
        var result = build(SettingsStore.Copy(Settings));
        result.RunMode = Settings.RunMode;
        result.StartWithWindows = Settings.StartWithWindows;
        result.KeepMainMonitor = Settings.KeepMainMonitor;
        result.KeepMainStopped = Settings.KeepMainStopped;
        result.ExportedMainMonitor = null;
        Settings = result;
        Policies.Apply(Settings, Policy, _monitors, MainMonitor);
        Save();
        _monitorSignature = ""; // names and numbers may have changed
        RefreshMonitors();
        ResetStreak();
        Note(note, "");
        Changed?.Invoke();
    }

    /// <summary>Writes something worth knowing to the log and to Recent activity (nothing to undo).</summary>
    private void Note(string message, string detail) =>
        Record(ActivityKinds.Info, message.TrimEnd('.'), "", null, detail.Length > 0 ? $"{message}  {detail}" : message);

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
        var b = mon.PhysicalBounds; // (fullscreen covers the whole monitor, also when it's split into areas)
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
        if (target.IsArea) return FillArea(hwnd, target, stillValid); // an area of a split monitor: fill it
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
        return IsOn(hwnd, target) ? Result.Moved : Result.Failed;
    }

    /// <summary>
    /// Puts a (not maximized) window back at an exact spot on the target monitor, in the same two steps as
    /// <see cref="Move"/>: first only its position, so the app can rescale for that monitor's DPI, then its size.
    /// Stops with <see cref="Result.Aborted"/> if monitors changed in between.
    /// </summary>
    public static Result PlaceAt(IntPtr hwnd, MonitorInfo target, Native.RECT rect, Func<bool>? stillValid)
    {
        if (!Responds(hwnd)) return Result.Failed;
        if (!LiveWorkArea(target, out var wa) || stillValid?.Invoke() == false) return Result.Aborted;
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        uint startDpi = Native.GetDpiForWindow(hwnd), targetDpi = MonitorDpi(target);
        Native.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, 0, 0, MoveFlags | Native.SWP_NOSIZE);
        WaitForDpi(hwnd, startDpi, targetDpi);
        if (!LiveWorkArea(target, out wa) || stillValid?.Invoke() == false) return Result.Aborted;
        Native.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, MoveFlags);
        KeepInside(hwnd, wa);
        return IsOn(hwnd, target) ? Result.Moved : Result.Failed;
    }

    /// <summary>True if the window has a maximize button.</summary>
    public static bool CanMaximize(IntPtr hwnd) =>
        (Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64() & Native.WS_MAXIMIZEBOX) != 0;

    /// <summary>Maximizes a window that can be maximized (it has a maximize button) and isn't yet.</summary>
    public static void Maximize(IntPtr hwnd)
    {
        if (!CanMaximize(hwnd) || Native.IsZoomed(hwnd) || !Responds(hwnd)) return;
        Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
    }

    /// <summary>
    /// "Where I last had it": puts a window at a remembered spot on the target monitor (fractions of its work area, for
    /// the visible frame), in the same two steps as <see cref="Move"/> so the app can rescale for that monitor's DPI
    /// first. A window that can't be resized keeps its size. Stops with <see cref="Result.Aborted"/> if monitors changed.
    /// </summary>
    public static Result PlaceAtFraction(IntPtr hwnd, MonitorInfo target, WindowPosition spot, Func<bool>? stillValid)
    {
        if (!Responds(hwnd)) return Result.Failed;
        if (!LiveWorkArea(target, out var wa) || stillValid?.Invoke() == false) return Result.Aborted;
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (!Native.GetWindowRect(hwnd, out var r)) return Result.Failed;
        uint startDpi = Native.GetDpiForWindow(hwnd), targetDpi = MonitorDpi(target);

        // Step 1: only the position, so the app can rescale for that monitor's DPI.
        var frame = Frame(hwnd, r);
        int left = wa.Left + (int)Math.Round(spot.X * wa.Width), top = wa.Top + (int)Math.Round(spot.Y * wa.Height);
        Native.SetWindowPos(hwnd, IntPtr.Zero, left - frame.Left, top - frame.Top, 0, 0, MoveFlags | Native.SWP_NOSIZE);
        WaitForDpi(hwnd, startDpi, targetDpi);

        // Step 2: the size, measured again (its invisible borders may have been rescaled too), kept fully on the monitor.
        if (!LiveWorkArea(target, out wa) || stillValid?.Invoke() == false) return Result.Aborted;
        if (!Native.GetWindowRect(hwnd, out r)) return Result.Failed;
        frame = Frame(hwnd, r);
        bool resizable = (Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64() & Native.WS_THICKFRAME) != 0;
        int vw = resizable ? (int)Math.Round(spot.Width * wa.Width) : r.Width - frame.Left - frame.Right;
        int vh = resizable ? (int)Math.Round(spot.Height * wa.Height) : r.Height - frame.Top - frame.Bottom;
        left = wa.Left + (int)Math.Round(spot.X * wa.Width);
        top = wa.Top + (int)Math.Round(spot.Y * wa.Height);
        var want = Clamp(Outer(left, top, vw, vh, frame), frame, wa);
        Native.SetWindowPos(hwnd, IntPtr.Zero, want.Left, want.Top, want.Width, want.Height, MoveFlags);
        KeepInside(hwnd, wa); // an app with a minimum size: keep it on screen
        return IsOn(hwnd, target) ? Result.Moved : Result.Failed;
    }

    /// <summary>
    /// Where a window is on its monitor, as fractions of the monitor's work area (its visible frame; for a maximized
    /// window, the size it has when restored). Null if Windows doesn't say.
    /// </summary>
    public static WindowPosition? FractionOf(IntPtr hwnd, MonitorInfo m)
    {
        if (!LiveWorkArea(m, out var wa) || wa.Width <= 0 || wa.Height <= 0) return null;
        bool maximized = Native.IsZoomed(hwnd);
        Native.RECT v;
        if (maximized)
        {
            var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (!Native.GetWindowPlacement(hwnd, ref wp)) return null;
            v = WorkspaceToScreen(hwnd, wp.rcNormalPosition);
        }
        else
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return null;
            var f = Frame(hwnd, r);
            v = new Native.RECT { Left = r.Left + f.Left, Top = r.Top + f.Top, Right = r.Right - f.Right, Bottom = r.Bottom - f.Bottom };
        }
        var p = new WindowPosition
        {
            X = (double)(v.Left - wa.Left) / wa.Width, Y = (double)(v.Top - wa.Top) / wa.Height,
            Width = (double)v.Width / wa.Width, Height = (double)v.Height / wa.Height, Maximized = maximized,
        };
        return p.Repair() ? p : null;
    }

    /// <summary>
    /// GetWindowPlacement gives "workspace" coordinates: screen coordinates shifted by the taskbar of the window's
    /// monitor when it's at the top or left.
    /// </summary>
    private static Native.RECT WorkspaceToScreen(IntPtr hwnd, Native.RECT r)
    {
        IntPtr hMon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (hMon == IntPtr.Zero || !Native.GetMonitorInfo(hMon, ref mi)) return r;
        int dx = mi.rcWork.Left - mi.rcMonitor.Left, dy = mi.rcWork.Top - mi.rcMonitor.Top;
        return new Native.RECT { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };
    }

    /// <summary>
    /// Centres a dialog over its app's window on the target monitor, without resizing it, and keeps it fully on that
    /// monitor's work area. In two steps, like <see cref="Move"/>, so the dialog can rescale for that monitor's DPI.
    /// </summary>
    public static bool CenterOver(IntPtr hwnd, IntPtr owner, MonitorInfo target)
    {
        if (!Responds(hwnd) || !LiveWorkArea(target, out var wa) || !Native.GetWindowRect(owner, out var o)) return false;
        uint startDpi = Native.GetDpiForWindow(hwnd), targetDpi = MonitorDpi(target);
        for (int step = 0; step < 2; step++)
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return false;
            var f = Frame(hwnd, r);
            int vw = r.Width - f.Left - f.Right, vh = r.Height - f.Top - f.Bottom;
            int left = Math.Max(wa.Left, Math.Min(o.Left + (o.Width - vw) / 2, wa.Right - vw));
            int top = Math.Max(wa.Top, Math.Min(o.Top + (o.Height - vh) / 2, wa.Bottom - vh));
            Native.SetWindowPos(hwnd, IntPtr.Zero, left - f.Left, top - f.Top, 0, 0, MoveFlags | Native.SWP_NOSIZE);
            if (step == 0) WaitForDpi(hwnd, startDpi, targetDpi);
        }
        return IsOn(hwnd, target);
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
        var b = target.PhysicalBounds; // (an area: its whole monitor is checked, then the area is taken from its work area)
        IntPtr hMon = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, Native.MONITOR_DEFAULTTONULL);
        if (hMon == IntPtr.Zero) return false;
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(hMon, ref mi)) return false;
        var m = mi.rcMonitor;
        if (!string.Equals(mi.szDevice, target.PhysicalDevice, StringComparison.OrdinalIgnoreCase)
            || m.Left != b.Left || m.Top != b.Top || m.Right != b.Right || m.Bottom != b.Bottom) return false;
        work = target.IsArea ? MonitorAreas.AreaRect(mi.rcWork, target.AreaStacked, target.AreaStart, target.AreaEnd) : mi.rcWork;
        return true;
    }

    /// <summary>
    /// Is the window on the target? On its Windows display, and for an area of a split monitor, with its centre in that
    /// area (or, for a window larger than the area, its left and top edge in it).
    /// </summary>
    public static bool IsOn(IntPtr hwnd, MonitorInfo target)
    {
        if (!string.Equals(MonitorService.DeviceOfWindow(hwnd), target.PhysicalDevice, StringComparison.OrdinalIgnoreCase)) return false;
        if (!target.IsArea) return true;
        if (!LiveWorkArea(target, out var wa) || CentreOf(hwnd) is not Native.POINT c) return false;
        if (c.X >= wa.Left && c.X < wa.Right && c.Y >= wa.Top && c.Y < wa.Bottom) return true;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var f = Frame(hwnd, r);
        int left = r.Left + f.Left, top = r.Top + f.Top;
        return left >= wa.Left && left < wa.Right && top >= wa.Top && top < wa.Bottom;
    }

    /// <summary>
    /// The centre of a window (its visible frame), or of where it goes when restored if it's maximized or minimized:
    /// what decides which area of a split monitor it's in. Null if Windows doesn't say.
    /// </summary>
    public static Native.POINT? CentreOf(IntPtr hwnd)
    {
        if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd))
        {
            var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            return Native.GetWindowPlacement(hwnd, ref wp) ? MonitorAreas.Centre(WorkspaceToScreen(hwnd, wp.rcNormalPosition)) : null;
        }
        if (!Native.GetWindowRect(hwnd, out var r)) return null;
        var f = Frame(hwnd, r);
        return MonitorAreas.Centre(new Native.RECT { Left = r.Left + f.Left, Top = r.Top + f.Top, Right = r.Right - f.Right, Bottom = r.Bottom - f.Bottom });
    }

    /// <summary>True when a window already fills the area (within a few pixels), not maximized.</summary>
    public static bool FillsArea(IntPtr hwnd, MonitorInfo target)
    {
        if (Native.IsZoomed(hwnd) || !LiveWorkArea(target, out var wa) || !Native.GetWindowRect(hwnd, out var r)) return false;
        var f = Frame(hwnd, r);
        return Math.Abs(r.Left + f.Left - wa.Left) <= 2 && Math.Abs(r.Top + f.Top - wa.Top) <= 2
            && Math.Abs(r.Right - f.Right - wa.Right) <= 2 && Math.Abs(r.Bottom - f.Bottom - wa.Bottom) <= 2;
    }

    /// <summary>
    /// Puts a window in an area of a split monitor, sized to fill it (in the normal state, never maximized): first only
    /// its position, so the app can rescale for that monitor's DPI, then its size. A window that can't be resized is
    /// centred in the area; one with a minimum size larger than the area is kept as far inside it as it allows.
    /// </summary>
    public static Result FillArea(IntPtr hwnd, MonitorInfo target, Func<bool>? stillValid)
    {
        if (!Responds(hwnd)) return Result.Failed;
        if (!LiveWorkArea(target, out var wa) || stillValid?.Invoke() == false) return Result.Aborted;
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (!Native.GetWindowRect(hwnd, out var r)) return Result.Failed;
        uint startDpi = Native.GetDpiForWindow(hwnd), targetDpi = MonitorDpi(target);

        // Step 1: only the position, so the app can rescale for that monitor's DPI.
        var step1 = Fit(r, Frame(hwnd, r), wa);
        Native.SetWindowPos(hwnd, IntPtr.Zero, step1.Left, step1.Top, 0, 0, MoveFlags | Native.SWP_NOSIZE);
        WaitForDpi(hwnd, startDpi, targetDpi);

        // Step 2: its visible frame exactly over the area (a window that can't be resized is only centred).
        if (!LiveWorkArea(target, out wa) || stillValid?.Invoke() == false) return Result.Aborted;
        if (!Native.GetWindowRect(hwnd, out r)) return Result.Failed;
        var frame = Frame(hwnd, r);
        bool resizable = (Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64() & Native.WS_THICKFRAME) != 0;
        var want = resizable ? Outer(wa.Left, wa.Top, wa.Width, wa.Height, frame) : Fit(r, frame, wa);
        Native.SetWindowPos(hwnd, IntPtr.Zero, want.Left, want.Top, want.Width, want.Height, MoveFlags);
        KeepInside(hwnd, wa); // a minimum size larger than the area: as far inside it as it goes
        return IsOn(hwnd, target) ? Result.Moved : Result.Failed;
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

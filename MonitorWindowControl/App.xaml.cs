using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace MonitorWindowControl;

/// <summary>
/// The settings app. What it does in the background depends on "How it runs":
///   Tray        — stays in the tray and places windows itself (closing the window keeps it running).
///   Helper      — the tiny MonitorWindowControlHelper.exe places windows; this app only runs while its window is open.
///   LaunchOnly  — nothing runs in the background; shortcuts made here place apps when you launch them.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\MonitorWindowControl.SingleInstance";
    private const string ShowEventName = @"Local\MonitorWindowControl.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private WpfHost? _host;
    private Engine? _engine;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _activateRetry;
    private bool _showWelcome; // first start: the window opens on the welcome

    public static bool IsExiting { get; private set; }

    /// <summary>Set when the helper should be running but couldn't be started (shown in the window).</summary>
    public string? HelperProblem { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = Native.TryCreateOwnedMutex(MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask that copy to show its window, then quit.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            _mutex?.Dispose(); _mutex = null;
            IsExiting = true;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("FATAL: " + a.ExceptionObject);
        // The tray icon's menu runs in WinForms, which has its own error path (and would show its own error dialog).
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, a) => Log.Write("Unexpected error (tray): " + a.Exception);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, false);

        // A simple settings window doesn't need the GPU; skipping it keeps the graphics driver
        // (and the ~200 MB it reserves) out of this app.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        ApplicationThemeManager.ApplySystemTheme(true);

        _host = new WpfHost(Dispatcher);
        _engine = new Engine(_host);
        _host.Attach(_engine);
        bool firstRun = _engine.Start(importLegacy: true, who: "Monitor Window Control");
        _showWelcome = firstRun;
        StartupManager.RemoveOldNameEntry();
        // Versions before 1.4 kept "Start with Windows" only in the registry.
        if (_engine.Settings.StartWithWindows == null) _engine.SetStartWithWindows(StartupManager.IsEnabled());

        bool fromAutostart = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        ApplyRunMode();

        if (_engine.Settings.RunMode == RunModes.Tray)
        {
            if (firstRun || !fromAutostart) ShowMainWindow();
        }
        else if (fromAutostart)
        {
            // An old "start with Windows" entry in a mode where this app shouldn't stay running.
            ExitApp();
        }
        else ShowMainWindow();
    }

    /// <summary>Makes the background part match the chosen mode (tray, helper or nothing) and fixes the autostart entry.</summary>
    private void ApplyRunMode()
    {
        if (_engine == null) return;
        var s = _engine.Settings;
        HelperProblem = null;

        if (s.RunMode == RunModes.Tray)
        {
            HelperManager.Stop(); // it would otherwise also place windows
            TryActivate();
            _tray ??= new TrayIcon(_engine, ShowMainWindow, ExitApp, ShowApp, OnUndoneFromTray, IdentifyMonitors);
        }
        else
        {
            _activateRetry?.Stop();
            if (_engine.IsActive) _engine.Deactivate();
            _tray?.Dispose(); _tray = null;
            if (s.RunMode == RunModes.Helper)
            {
                if (!HelperManager.EnsureRunning())
                {
                    // Never leave nothing running by accident: fall back to the tray.
                    string why = HelperManager.IsAvailable ? "the helper couldn't be started (see the log)" : "this build doesn't include the helper";
                    Log.Write($"Helper mode isn't possible ({why}); using the tray instead.");
                    _engine.SetRunMode(RunModes.Tray);
                    ApplyRunMode();
                    HelperProblem = $"The tiny helper couldn't run ({why}), so Monitor Window Control is running in the tray instead.";
                    return;
                }
            }
            else HelperManager.Stop();
        }

        if (s.RunMode == RunModes.Helper && s.StartWithWindows == true) HelperManager.EnsureExtracted(); // autostart points at it
        StartupManager.Apply(s.RunMode, s.StartWithWindows == true, Environment.ProcessPath ?? "", Paths.HelperExe);
    }

    /// <summary>Start placing windows in this process; if the helper is still shutting down, try again shortly.</summary>
    private void TryActivate()
    {
        if (_engine == null || _engine.IsActive || _engine.Activate(learn: true)) return;
        _activateRetry ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _activateRetry.Tick -= RetryActivate;
        _activateRetry.Tick += RetryActivate;
        _activateRetry.Start();
    }

    private void RetryActivate(object? sender, EventArgs e)
    {
        if (_engine == null || _engine.Settings.RunMode != RunModes.Tray || _engine.IsActive || _engine.Activate(learn: true))
            _activateRetry?.Stop();
    }

    /// <summary>Called by the window when you pick "How it runs".</summary>
    public void ChangeRunMode(string mode)
    {
        if (_engine == null) return;
        _engine.SetRunMode(mode);
        ApplyRunMode();
    }

    /// <summary>Called by the window's "Start with Windows" switch.</summary>
    public void ChangeStartWithWindows(bool on)
    {
        if (_engine == null) return;
        _engine.SetStartWithWindows(on);
        ApplyRunMode();
    }

    /// <summary>One line for the window: what is running in the background right now.</summary>
    public string BackgroundStatus()
    {
        if (_engine == null) return "";
        switch (_engine.Settings.RunMode)
        {
            case RunModes.Helper:
                if (HelperProblem != null) return HelperProblem;
                var helpers = Process.GetProcessesByName("MonitorWindowControlHelper");
                try
                {
                    // (A launch shortcut can briefly run a second copy; the background one is the oldest.)
                    var bg = helpers.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
                    if (bg == null || !HelperManager.IsRunning()) return "The helper isn't running.";
                    bg.Refresh();
                    return $"Running in the background: the helper only, using {bg.PrivateMemorySize64 / 1048576.0:0.#} MB. " +
                           "Closing this window exits it completely.";
                }
                finally { foreach (var p in helpers) p.Dispose(); }
            case RunModes.LaunchOnly:
                return "Nothing runs in the background. Apps are placed when you open them from the shortcuts you create below.";
            default:
                if (HelperProblem != null) return HelperProblem;
                return _engine.IsActive
                    ? "Running in the tray and placing new windows. Closing this window keeps it there."
                    : "Starting up…";
        }
    }

    private bool _errorShown;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unexpected error: " + e.Exception);
        e.Handled = true;
        if (_errorShown || _window == null || IsExiting) return; // tell the user once, and only while they're looking
        _errorShown = true;
        try
        {
            System.Windows.MessageBox.Show(_window,
                "Something went wrong, but Monitor Window Control kept running. The details are in the activity log:\n" + Paths.LogFile,
                "Monitor Window Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }

    private bool _trayHintShown;

    public void ShowMainWindow()
    {
        if (IsExiting || _engine == null) return;
        if (_window == null)
        {
            var w = new MainWindow(_engine, this);
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, w)) _window = null;
                if (_engine?.Settings.RunMode != RunModes.Tray)
                {
                    // Helper / launch-only: nothing of this app should keep running.
                    Dispatcher.BeginInvoke(ExitApp);
                    return;
                }
                // Tray: the window is gone, the app stays in the tray. Say so once, and give its memory back.
                if (!_trayHintShown && !IsExiting)
                {
                    _trayHintShown = true;
                    _tray?.ShowBalloon("Still placing your windows", "Monitor Window Control keeps running in the tray. Double-click its icon to open it again.");
                }
                Dispatcher.BeginInvoke(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); },
                    DispatcherPriority.ApplicationIdle);
            };
            _window = w;
            // Fit small screens (for example a 1366×768 laptop at 125 %).
            double maxH = SystemParameters.WorkArea.Height - 24;
            if (maxH > w.MinHeight && w.Height > maxH) w.Height = maxH;
            double maxW = SystemParameters.WorkArea.Width - 24;
            if (maxW > w.MinWidth && w.Width > maxW) w.Width = maxW;
            if (_engine.MainMonitor is MonitorInfo main)
            {
                // Open centered on the main monitor (WPF's "center screen" can pick the TV).
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
                WindowMover.MoveTo(hwnd, main);
            }
            w.Show();
            if (_showWelcome) { _showWelcome = false; w.ShowWelcome(); }
            // Windows may apply the launcher's "start maximized/minimized" request to a shown window; undo it.
            if (w.WindowState != WindowState.Normal) w.WindowState = WindowState.Normal;
        }
        else if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true; _window.Topmost = false; // bring to front reliably
    }

    /// <summary>Tray menu → Identify monitors: each monitor shows its number and name on it for 3 seconds.</summary>
    private void IdentifyMonitors()
    {
        if (_engine != null) IdentifyOverlay.ShowLabels(_engine.Monitors);
    }

    /// <summary>Tray menu → More choices…: the window's Apps page with that app selected.</summary>
    private void ShowApp(string key, string name)
    {
        ShowMainWindow();
        _window?.ShowApp(key, name);
    }

    /// <summary>A tray notification was selected to undo something: say what went wrong, or offer to stop moving the app.</summary>
    private void OnUndoneFromTray(Engine.UndoOutcome outcome)
    {
        if (outcome.Error != null) { _tray?.ShowBalloon("Couldn't undo that", outcome.Error); return; }
        if (outcome.StopMovingKey == null) return;
        ShowMainWindow();
        _window?.AskStopMoving(outcome.StopMovingKey, outcome.StopMovingName ?? outcome.StopMovingKey);
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        Cleanup();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        Cleanup();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
        _activateRetry?.Stop();
        _showWait?.Unregister(null); _showWait = null;
        _showEvent?.Dispose(); _showEvent = null;
        _tray?.Dispose(); _tray = null;
        if (_window != null) { var w = _window; _window = null; w.Close(); }
        _engine?.Dispose(); _engine = null;
        _host?.Dispose(); _host = null;
        if (_mutex != null) { try { _mutex.ReleaseMutex(); } catch { } _mutex.Dispose(); _mutex = null; }
    }
}

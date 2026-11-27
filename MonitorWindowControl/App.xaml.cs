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

    public static bool IsExiting { get; private set; }

    /// <summary>Set when the helper should be running but couldn't be started (shown in the window).</summary>
    public string? HelperProblem { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask that copy to show its window, then quit.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            _mutex.Dispose(); _mutex = null;
            IsExiting = true;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("FATAL: " + a.ExceptionObject);

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
        StartupManager.RemoveOldNameEntry();
        // Versions before 1.4 kept "Start with Windows" only in the registry.
        if (_engine.Settings.StartWithWindows == null) _engine.SetStartWithWindows(StartupManager.IsEnabled());

        bool fromAutostart = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        ApplyRunMode();

        if (_engine.Settings.RunMode == RunModes.Tray)
        {
            if (firstRun) ShowMainWindow();
            else if (!fromAutostart) _tray?.ShowBalloon("Monitor Window Control is running", "It's in the tray. Double-click the icon to open it.");
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
            _tray ??= new TrayIcon(_engine, ShowMainWindow, ExitApp);
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
                           "This window closes completely when you close it.";
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

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unexpected error: " + e.Exception);
        e.Handled = true;
    }

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
                // Tray: the window is gone, the app stays in the tray. Give its memory back.
                Dispatcher.BeginInvoke(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); },
                    DispatcherPriority.ApplicationIdle);
            };
            _window = w;
            if (_engine.MainMonitor is MonitorInfo main)
            {
                // Open centered on the main monitor (WPF's "center screen" can pick the TV).
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
                WindowMover.MoveTo(hwnd, main);
            }
            w.Show();
            // Windows may apply the launcher's "start maximized/minimized" request to a shown window; undo it.
            if (w.WindowState != WindowState.Normal) w.WindowState = WindowState.Normal;
        }
        else if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true; _window.Topmost = false; // bring to front reliably
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

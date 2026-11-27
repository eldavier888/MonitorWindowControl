using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace MonitorWindowControl;

public partial class App : Application
{
    private const string MutexName = @"Local\MonitorWindowControl.SingleInstance";
    private const string ShowEventName = @"Local\MonitorWindowControl.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private Engine? _engine;
    private TrayIcon? _tray;
    private MainWindow? _window;

    public static bool IsExiting { get; private set; }

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
        // (and the ~200 MB it reserves) out of this always-running tray app.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        ApplicationThemeManager.ApplySystemTheme(true);

        _engine = new Engine(Dispatcher);
        bool firstRun = _engine.Start();
        StartupManager.RefreshPathIfEnabled();

        _tray = new TrayIcon(_engine, ShowMainWindow, ExitApp);

        bool fromAutostart = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (firstRun) ShowMainWindow();
        else if (!fromAutostart) _tray.ShowBalloon("Monitor Window Control is running", "It's in the tray. Double-click the icon to open it.");
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
            var w = new MainWindow(_engine);
            w.Closed += (_, _) =>
            {
                // The window is gone, the app stays in the tray. Give its memory back.
                if (ReferenceEquals(_window, w)) _window = null;
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
        _showWait?.Unregister(null); _showWait = null;
        _showEvent?.Dispose(); _showEvent = null;
        _tray?.Dispose(); _tray = null;
        if (_window != null) { var w = _window; _window = null; w.Close(); }
        _engine?.Dispose(); _engine = null;
        if (_mutex != null) { try { _mutex.ReleaseMutex(); } catch { } _mutex.Dispose(); _mutex = null; }
    }
}

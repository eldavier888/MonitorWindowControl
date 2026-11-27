using System;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MonitorWindowControl;

/// <summary>Runs the engine on the settings app's WPF thread.</summary>
public sealed class WpfHost : IEngineHost, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private Engine? _engine;

    public WpfHost(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Forward Windows' "monitors changed", "session changed" (Remote Desktop, lock and unlock) and sleep / wake-up
    /// notifications to the engine.
    /// </summary>
    public void Attach(Engine engine)
    {
        _engine = engine;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    // Can arrive on another thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => _engine?.OnDisplaySettingsChanged());

    // Remote Desktop connected or disconnected, this PC's own screen reconnected, or the session locked or unlocked.
    // (Can arrive on another thread.)
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.RemoteDisconnect
                     or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.ConsoleDisconnect)
            _dispatcher.BeginInvoke(() => _engine?.OnSessionChanged());
        else if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
        {
            bool locked = e.Reason == SessionSwitchReason.SessionLock;
            _dispatcher.BeginInvoke(() => _engine?.OnSessionLock(locked));
        }
    }

    // Going to sleep must be handled before the PC sleeps, so wait for it; waking up can be handled whenever.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Suspend) _dispatcher.Invoke(() => _engine?.OnSuspending());
            else if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(() => _engine?.OnResumed());
        }
        catch (Exception ex) { Log.Write("Unexpected error (sleep): " + ex.Message); }
    }

    public IEngineTimer CreateTimer(int intervalMs, Action tick) => new Timer(_dispatcher, intervalMs, tick);

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _engine = null;
    }

    private sealed class Timer : IEngineTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(Dispatcher dispatcher, int ms, Action tick)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
            // A DispatcherTimer whose Tick throws never ticks again: keep it alive whatever happens.
            _timer.Tick += (_, _) =>
            {
                try { tick(); }
                catch (Exception ex) { Log.Write("Unexpected error (timer): " + ex); }
            };
        }

        public bool IsEnabled => _timer.IsEnabled;
        public void Start() { _timer.Stop(); _timer.Start(); }
        public void Stop() => _timer.Stop();
    }
}

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

    /// <summary>Forward Windows' "monitors changed" and "session changed" (Remote Desktop) notifications to the engine.</summary>
    public void Attach(Engine engine)
    {
        _engine = engine;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    // Can arrive on another thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => _engine?.OnDisplaySettingsChanged());

    // Remote Desktop connected or disconnected, or this PC's own screen reconnected. (Can arrive on another thread.)
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.RemoteDisconnect
                     or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.ConsoleDisconnect)
            _dispatcher.BeginInvoke(() => _engine?.OnSessionChanged());
    }

    public IEngineTimer CreateTimer(int intervalMs, Action tick) => new Timer(_dispatcher, intervalMs, tick);

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
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

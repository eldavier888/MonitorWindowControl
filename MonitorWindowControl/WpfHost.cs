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

    /// <summary>Forward Windows' "monitors changed" notifications to the engine.</summary>
    public void Attach(Engine engine)
    {
        _engine = engine;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // Can arrive on another thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => _engine?.OnDisplaySettingsChanged());

    public IEngineTimer CreateTimer(int intervalMs, Action tick) => new Timer(_dispatcher, intervalMs, tick);

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MonitorWindowControl;

/// <summary>
/// Runs the engine on a plain Windows message loop: an invisible window receives timer ticks,
/// posted work and "display changed" notifications. No UI framework, no tray icon.
/// </summary>
internal sealed class NativeHost : IEngineHost, IDisposable
{
    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016,
                       WM_TIMER = 0x0113, WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A, WM_APP_POST = 0x8001,
                       WM_WTSSESSION_CHANGE = 0x02B1, WM_POWERBROADCAST = 0x0218;
    private const int WTS_CONSOLE_CONNECT = 1, WTS_CONSOLE_DISCONNECT = 2, WTS_REMOTE_CONNECT = 3, WTS_REMOTE_DISCONNECT = 4,
                      WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;
    private const int PBT_APMSUSPEND = 0x4, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    // The window class is Unicode, so the message functions must be the Unicode ("W") ones too.
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    private readonly WndProc _wndProc; // keep a reference so the GC can't collect it
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Timer> _timers = new();
    private readonly ConcurrentQueue<Action> _posted = new();
    private int _nextTimerId = 1;

    /// <summary>Called when Windows reports that monitors changed (also the taskbar / work area).</summary>
    public Action? DisplayChanged { get; set; }

    /// <summary>Called when Remote Desktop connects or disconnects, or this PC's own screen is reconnected.</summary>
    public Action? SessionChanged { get; set; }

    /// <summary>Called when the session is locked (true) or unlocked (false).</summary>
    public Action<bool>? LockChanged { get; set; }

    /// <summary>Called just before the PC goes to sleep (true) and when it wakes up (false).</summary>
    public Action<bool>? PowerChanged { get; set; }

    /// <summary>Called when a keyboard shortcut registered with <see cref="RegisterHotKey"/> is pressed (its id).</summary>
    public Action<int>? HotKey { get; set; }

    private readonly bool _sessionNotifications;

    public NativeHost()
    {
        _wndProc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "MonitorWindowControlHelper",
        };
        RegisterClassEx(ref wc);
        // An ordinary but never-shown top-level window: unlike message-only windows it receives WM_DISPLAYCHANGE.
        const uint WS_EX_TOOLWINDOW = 0x80, WS_POPUP = 0x80000000;
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, wc.lpszClassName, "Monitor Window Control helper", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the helper's message window.");
        // Remote Desktop connecting / disconnecting (not critical: the engine also checks on every new window).
        try { _sessionNotifications = WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION); }
        catch (Exception) { _sessionNotifications = false; }
    }

    public IEngineTimer CreateTimer(int intervalMs, Action tick)
    {
        var t = new Timer(this, _nextTimerId++, intervalMs, tick);
        _timers[t.Id] = t;
        return t;
    }

    public bool RegisterHotKey(int id, int modifiers, int key) =>
        Native.RegisterHotKey(_hwnd, id, (uint)modifiers, (uint)key);

    public void UnregisterHotKey(int id) => Native.UnregisterHotKey(_hwnd, id);

    public void Post(Action action)
    {
        _posted.Enqueue(action);
        PostMessage(_hwnd, WM_APP_POST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Ends <see cref="Run"/> (call on the loop thread, e.g. via <see cref="Post"/>).</summary>
    public void Quit() => PostQuitMessage(0);

    /// <summary>Pumps messages until <see cref="Quit"/>.</summary>
    public void Run()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_TIMER:
                    if (_timers.TryGetValue((int)wParam, out var t) && t.IsEnabled) t.Tick();
                    return IntPtr.Zero;
                case WM_APP_POST:
                    while (_posted.TryDequeue(out var a)) a();
                    return IntPtr.Zero;
                case WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke();
                    break;
                case Native.WM_HOTKEY:
                    HotKey?.Invoke((int)wParam);
                    return IntPtr.Zero;
                case WM_SETTINGCHANGE when wParam == (IntPtr)0x2F: // SPI_SETWORKAREA: taskbar moved / resized
                    DisplayChanged?.Invoke();
                    break;
                case WM_WTSSESSION_CHANGE when (int)wParam is WTS_CONSOLE_CONNECT or WTS_CONSOLE_DISCONNECT
                                                         or WTS_REMOTE_CONNECT or WTS_REMOTE_DISCONNECT:
                    SessionChanged?.Invoke();
                    break;
                case WM_WTSSESSION_CHANGE when (int)wParam is WTS_SESSION_LOCK or WTS_SESSION_UNLOCK:
                    LockChanged?.Invoke((int)wParam == WTS_SESSION_LOCK);
                    break;
                case WM_POWERBROADCAST when (int)wParam == PBT_APMSUSPEND:
                    PowerChanged?.Invoke(true);
                    break;
                case WM_POWERBROADCAST when (int)wParam is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC:
                    PowerChanged?.Invoke(false); // (both can arrive for one wake-up; the engine handles it once)
                    break;
                case WM_CLOSE:
                    // Someone asked the helper to close (taskkill without /f, an installer…): exit cleanly
                    // instead of letting Windows destroy the window under a still-running loop.
                    Log.Write("Helper: asked to close; stopping.");
                    Quit();
                    return IntPtr.Zero;
                case WM_DESTROY:
                    Quit();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Helper error: " + ex.Message);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var t in _timers.Values) t.Stop();
        if (_sessionNotifications) WTSUnRegisterSessionNotification(_hwnd);
        DestroyWindow(_hwnd);
    }

    private sealed class Timer : IEngineTimer
    {
        private readonly NativeHost _host;
        private readonly int _ms;
        public readonly int Id;
        public readonly Action Tick;
        public bool IsEnabled { get; private set; }

        public Timer(NativeHost host, int id, int ms, Action tick) { _host = host; Id = id; _ms = ms; Tick = tick; }

        public void Start()
        {
            // Restarting resets the countdown, like WPF's DispatcherTimer.
            SetTimer(_host._hwnd, (IntPtr)Id, (uint)_ms, IntPtr.Zero);
            IsEnabled = true;
        }

        public void Stop()
        {
            if (!IsEnabled) return;
            KillTimer(_host._hwnd, (IntPtr)Id);
            IsEnabled = false;
        }
    }
}

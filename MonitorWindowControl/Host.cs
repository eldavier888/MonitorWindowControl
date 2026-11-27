using System;

namespace MonitorWindowControl;

/// <summary>A repeating timer that fires on the engine's thread.</summary>
public interface IEngineTimer
{
    bool IsEnabled { get; }
    void Start();
    void Stop();
}

/// <summary>
/// What the engine needs from the program it runs in: timers on its thread.
/// The settings app provides it with WPF; the tiny helper with a plain Windows message loop.
/// The host also calls <see cref="Engine.OnDisplaySettingsChanged"/> when monitors change, and
/// <see cref="Engine.OnSessionChanged"/> when Remote Desktop connects or disconnects.
/// </summary>
public interface IEngineHost
{
    IEngineTimer CreateTimer(int intervalMs, Action tick);
}

/// <summary>How Monitor Window Control runs in the background.</summary>
public static class RunModes
{
    /// <summary>The app stays running in the tray and catches every new window.</summary>
    public const string Tray = "Tray";
    /// <summary>A tiny invisible helper catches every new window; the settings window only runs while open.</summary>
    public const string Helper = "Helper";
    /// <summary>Nothing runs in the background; apps are placed when launched from this app's shortcuts.</summary>
    public const string LaunchOnly = "LaunchOnly";

    public static string Normalize(string? mode) =>
        mode == Helper || mode == LaunchOnly ? mode : Tray;
}

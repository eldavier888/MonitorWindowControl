using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

// The engine and the settings store keep some state in static fields (the settings file, its lock): run one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MonitorWindowControl.Tests;

/// <summary>Runs before any test: settings, the log and the helper's files go to a temporary folder, never your own.</summary>
internal static class TestFolder
{
    public static string Root { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        Root = Path.Combine(Path.GetTempPath(), "MonitorWindowControl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths.UseFolderForTests(Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { } // (best effort: it's in the temporary folder)
        };
    }

    /// <summary>Starts a test with no settings file at all (a first run).</summary>
    public static void Reset()
    {
        try { if (Directory.Exists(Paths.Dir)) Directory.Delete(Paths.Dir, recursive: true); } catch { }
        Directory.CreateDirectory(Paths.Dir);
    }
}

/// <summary>Timers that never fire by themselves: tests call the engine's methods directly.</summary>
internal sealed class FakeHost : IEngineHost
{
    private sealed class Timer : IEngineTimer
    {
        public bool IsEnabled { get; private set; }
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
    }

    public IEngineTimer CreateTimer(int intervalMs, Action tick) => new Timer();
    public bool RegisterHotKey(int id, int modifiers, int key) => true;
    public void UnregisterHotKey(int id) { }
    public void Post(Action action) => action();
}

/// <summary>A made-up set of monitors. Change <see cref="Monitors"/> to plug, unplug or renumber them.</summary>
internal sealed class FakeMonitorSource : IMonitorSource
{
    /// <summary>What's connected now. Each read returns fresh copies, like Windows does.</summary>
    public List<FakeMonitor> Monitors { get; set; } = new();

    public List<MonitorInfo> Enumerate() => Monitors.Select(m => m.ToInfo()).ToList();
    public void ClearIdentityCache() { }
    public string? DeviceOfWindow(IntPtr hwnd) => null;
}

/// <summary>One made-up monitor: its Windows number, model name, place, and hardware identity.</summary>
internal sealed record FakeMonitor(int Number, string Name, int Left, bool Primary = false, string? Port = null,
                                   string? Model = null, string? Serial = null, uint Output = MonitorTarget.OutputOther)
{
    public string Device => @"\\.\DISPLAY" + Number;

    public MonitorInfo ToInfo() => new()
    {
        DeviceName = Device,
        IsPrimary = Primary,
        Bounds = new Native.RECT { Left = Left, Top = 0, Right = Left + 1920, Bottom = 1080 },
        Work = new Native.RECT { Left = Left, Top = 0, Right = Left + 1920, Bottom = 1040 },
        Targets = new[]
        {
            new MonitorTarget { FriendlyName = Name, DevicePath = Port, Model = Model, Serial = Serial, OutputTechnology = Output },
        },
    };
}

/// <summary>An engine on made-up monitors, started like the helper starts it (but never watching real windows).</summary>
internal sealed class TestEngine : IDisposable
{
    public FakeMonitorSource Source { get; } = new();
    public Engine Engine { get; }

    public TestEngine(params FakeMonitor[] monitors)
    {
        TestFolder.Reset();
        Source.Monitors = monitors.ToList();
        Engine = new Engine(new FakeHost(), Source);
        Engine.Start(importLegacy: false, who: "Test");
    }

    /// <summary>The connected monitor with this Windows number.</summary>
    public MonitorInfo Display(int number) => Engine.Monitors.Single(m => m.DeviceName == @"\\.\DISPLAY" + number);

    /// <summary>Saves an app as set by you to this monitor (like Save on the Apps page).</summary>
    public AppRule Pin(string key, int number)
    {
        Engine.Pin(key, Display(number), key);
        return Engine.Settings.Find(key)!;
    }

    /// <summary>Changes what's connected, then lets the engine read the monitors again.</summary>
    public void Plug(params FakeMonitor[] monitors)
    {
        Source.Monitors = monitors.ToList();
        Engine.RefreshMonitors();
    }

    public void Dispose() => Engine.Dispose();
}

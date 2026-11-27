using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MonitorWindowControl;

/// <summary>
/// MonitorWindowControlHelper.exe — no window, no tray icon.
///
///   (no arguments)                     Background helper: watches every new window and learns, like the tray app,
///                                      but in a few MB. Exits by itself if the mode is no longer "Helper".
///   --launch "exe:C:\path\app.exe"     Launch shortcut: starts the app, puts its new window(s) on the right
///   --launch "aumid:Package!App"       monitor, then exits (a few seconds; nothing keeps running).
///            --key "app key"
///            [--on-name "model name" --on-model "EDID id" --on-serial "serial" --on-path "port path"]
///                                      this once, on that monitor instead of the app's own
///            [--open "URL or file"]    and open this web address or file with it (programs only, not Store apps)
///   --group "group id"                 App group: starts each of its apps, each on its monitor, then exits.
/// </summary>
internal static class Program
{
    private const string HelperMutexName = @"Local\MonitorWindowControl.Helper";
    private const string HelperExitEventName = @"Local\MonitorWindowControl.HelperExit";
    private const int ActivateRetryMs = 2_000;         // if the tray app is still shutting down
    private const int LaunchMaxWaitMs = 30_000;        // slow apps (Steam…) can take a while to show a window
    private const int LaunchAlreadyOpenWaitMs = 6_000; // app was already running: it may just reuse its window
    private const int LaunchQuietMs = 4_000;           // after a window is placed, wait this long for more (splash → main window)

    /// <summary>The engine that's running, for a crash report (null before it starts).</summary>
    private static Engine? _engine;

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Crashed(a.ExceptionObject as Exception);
        try
        {
            int i = Array.IndexOf(args, "--launch");
            if (i >= 0 && i + 1 < args.Length)
                return Launch(new() { new LaunchJob(args[i + 1], ArgAfter(args, "--key"), MonitorFromArgs(args), ArgAfter(args, "--open")) },
                              ArgAfter(args, "--key") ?? args[i + 1]);
            if (ArgAfter(args, "--group") is string group) return LaunchGroup(group);
            return Background();
        }
        catch (Exception ex)
        {
            Log.Write("Helper stopped after an error: " + ex.Message);
            Crashed(ex);
            return 1;
        }
    }

    /// <summary>
    /// Writes a local crash report. (The helper is built without stack traces to stay small, so the report has the
    /// error and what was going on, not the line it happened on.)
    /// </summary>
    private static void Crashed(Exception? ex) =>
        CrashReports.Write("Helper", true, ex?.ToString() ?? "Unknown error", _engine?.Settings, _engine?.Monitors);

    private static string? ArgAfter(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The monitor a launch shortcut chose (--on-…), or null for the app's own monitor.</summary>
    private static MonitorRef? MonitorFromArgs(string[] args)
    {
        string? name = ArgAfter(args, "--on-name"), model = ArgAfter(args, "--on-model"),
                serial = ArgAfter(args, "--on-serial"), path = ArgAfter(args, "--on-path");
        if (name == null && model == null && serial == null && path == null) return null;
        return new MonitorRef
        {
            Device = "", Name = name,
            Hardware = MonitorHardware.Clean(new MonitorHardware { Model = model, Serial = serial, DevicePath = path }),
        };
    }

    /// <summary>One app a launch shortcut or group starts: how, which saved app it is, and (optionally) where and with what.</summary>
    private sealed record LaunchJob(string Target, string? Key, MonitorRef? Monitor, string? Open);

    // ------------------------------------------------------------------ background helper

    private static int Background()
    {
        using var single = Native.TryCreateOwnedMutex(HelperMutexName, out bool first);
        if (!first) return 0; // already running (possibly a copy started as administrator)

        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, HelperExitEventName);
        using var host = new NativeHost();
        using var engine = new Engine(host);
        _engine = engine;
        host.DisplayChanged = engine.OnDisplaySettingsChanged;
        host.SessionChanged = engine.OnSessionChanged;
        host.LockChanged = engine.OnSessionLock;
        host.PowerChanged = sleeping => { if (sleeping) engine.OnSuspending(); else engine.OnResumed(); };
        host.HotKey = engine.OnHotKey;
        engine.Start(importLegacy: false, who: "Helper");

        if (engine.Settings.RunMode != RunModes.Helper)
        {
            Log.Write("Helper: the app isn't set to run as a helper, so the helper exits.");
            return 0;
        }

        // Come back after Windows restarts for an update, or after a crash (a clean exit is never restarted).
        Native.SetRestartAfterUpdate("--background");

        // Stop when the settings window switches to another mode, or asks us to.
        engine.Changed += () => { if (engine.Settings.RunMode != RunModes.Helper) host.Quit(); };
        var wait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => host.Post(host.Quit), null, Timeout.Infinite, true);
        // The settings window asks for Recent activity, "Move open windows to their monitors", Undo… (nothing runs until it asks).
        using var requestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, HelperChannel.EventName);
        var requests = ThreadPool.RegisterWaitForSingleObject(requestEvent, (_, _) => host.Post(engine.AnswerRequest), null, Timeout.Infinite, false);

        // Normally this succeeds at once. If the tray app is still shutting down, keep trying instead of giving up.
        if (!engine.Activate(learn: true))
        {
            Log.Write("Helper: another copy is still placing windows; waiting for it to stop.");
            IEngineTimer? retry = null;
            retry = host.CreateTimer(ActivateRetryMs, () =>
            {
                engine.ReloadIfChanged(); // may quit via Changed if the mode changed
                if (engine.Settings.RunMode != RunModes.Helper) host.Quit();
                else if (engine.Activate(learn: true)) { retry!.Stop(); Log.Write("Helper: now placing windows."); }
            });
            retry.Start();
        }

        host.Run();
        wait.Unregister(null);
        requests.Unregister(null);
        engine.Deactivate();
        return 0;
    }

    // ------------------------------------------------------------------ launch shortcut

    /// <summary>"--group": starts every app of an app group that can be started, each on its monitor.</summary>
    private static int LaunchGroup(string id)
    {
        var settings = SettingsStore.Load(out _);
        var group = settings.Groups.FirstOrDefault(g => g.Id == id);
        if (group == null) { Log.Write($"App group {id} doesn't exist any more (was it deleted?)."); return 1; }
        var jobs = new List<LaunchJob>();
        foreach (var a in group.Apps)
        {
            var rule = settings.Find(a.Key);
            if (rule?.LaunchTarget == null) { Log.Write($"App group \"{group.Name}\": skipped {a.Key} (Monitor Window Control doesn't know how to start it)."); continue; }
            jobs.Add(new LaunchJob(rule.LaunchTarget, rule.Key, a.Monitor, null));
        }
        if (jobs.Count == 0) { Log.Write($"App group \"{group.Name}\" has no apps to start."); return 1; }
        return Launch(jobs, $"group \"{group.Name}\"", skipRunning: true);
    }

    /// <summary>
    /// Starts the apps and places their new windows (unless the tray app or helper does that already). With
    /// <paramref name="skipRunning"/> (groups), apps that are running already are left as they are: starting a
    /// browser again would open another window.
    /// </summary>
    private static int Launch(List<LaunchJob> jobs, string what, bool skipRunning = false)
    {
        using var host = new NativeHost();
        using var engine = new Engine(host);
        host.DisplayChanged = engine.OnDisplaySettingsChanged;
        host.SessionChanged = engine.OnSessionChanged;
        host.LockChanged = engine.OnSessionLock;
        host.PowerChanged = sleeping => { if (sleeping) engine.OnSuspending(); else engine.OnResumed(); };
        engine.Start(importLegacy: false, who: $"Shortcut for {what}");

        // The saved launch target is kept up to date (apps that update themselves can move); prefer it.
        jobs = jobs.Select(j => j.Key != null && engine.Settings.Find(j.Key)?.LaunchTarget is string saved ? j with { Target = saved } : j).ToList();
        if (skipRunning)
        {
            foreach (var j in jobs.Where(j => j.Key != null && IsRunning(j.Target, j.Key, engine)))
                Log.Write($"{what}: {j.Key} is running already, so it's left as it is.");
            jobs = jobs.Where(j => j.Key == null || !IsRunning(j.Target, j.Key, engine)).ToList();
            if (jobs.Count == 0) return 0;
        }
        var keys = new HashSet<string>(jobs.Where(j => j.Key != null).Select(j => j.Key!), StringComparer.OrdinalIgnoreCase);

        // If the tray app or background helper is running, it places windows already: just start the apps (telling it
        // about any monitor chosen for this once). Other launch shortcuts don't count: each one only places its own apps.
        bool placing = false, alreadyRunning = false;
        if (keys.Count > 0 && engine.Settings.Enabled)
        {
            if (Engine.AnyProcessActive()) TellPlacer(jobs);
            else
            {
                alreadyRunning = jobs.All(j => j.Key != null && IsRunning(j.Target, j.Key, engine));
                foreach (var j in jobs) if (j.Key != null && j.Monitor != null) engine.OpenOnceOn(j.Key, j.Monitor);
                engine.OnlyAppKeys = keys;
                placing = engine.Activate(learn: false, claimActive: false); // windows already on screen are left alone
            }
        }

        int started = jobs.Count(j => StartTarget(j.Target, j.Open));
        if (started == 0) return 1;
        if (!placing) return 0;

        var deadline = host.CreateTimer(alreadyRunning ? LaunchAlreadyOpenWaitMs : LaunchMaxWaitMs, host.Quit);
        deadline.Start();
        // Once every app has shown a window, each placed window restarts a short quiet period (a splash screen
        // followed by the main window works).
        var quiet = host.CreateTimer(LaunchQuietMs, host.Quit);
        var waiting = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        engine.WindowHandled += key =>
        {
            waiting.Remove(key);
            if (waiting.Count == 0) quiet.Start();
        };

        host.Run();
        engine.Deactivate();
        return 0;
    }

    /// <summary>
    /// The tray app or background helper places windows: tell it which apps open on a chosen monitor this once (it
    /// reads launch.json when the event is set). Nothing to say when every app opens on its own monitor.
    /// </summary>
    private static void TellPlacer(List<LaunchJob> jobs)
    {
        var items = jobs.Where(j => j.Key != null && j.Monitor != null).Select(j => new LaunchItem { Key = j.Key!, Monitor = j.Monitor! }).ToList();
        if (items.Count == 0) return;
        if (!HelperChannel.WriteLaunch(new LaunchRequest { CreatedUtc = DateTime.UtcNow, Items = items })) return;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(HelperChannel.LaunchEventName);
            ev.Set();
            Thread.Sleep(150); // let it read the file before the app's first window shows up
        }
        catch (Exception ex) { Log.Write("Shortcut: couldn't tell the program that places windows about the chosen monitor: " + ex.Message); }
    }

    /// <summary>Is the app already running (possibly hidden in the tray)? Then launching may just re-show it.</summary>
    private static bool IsRunning(string target, string key, Engine engine)
    {
        if (engine.GetOpenApps().Any(o => string.Equals(o.App.Key, key, StringComparison.OrdinalIgnoreCase))) return true;
        if (!target.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)) return false;
        string name = Path.GetFileNameWithoutExtension(target.Substring(4));
        var procs = Process.GetProcessesByName(name);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    private static bool StartTarget(string target, string? open = null)
    {
        try
        {
            if (target.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
            {
                // Store and packaged apps are started through the Apps folder, like the Start menu does.
                if (open != null) Log.Write("Shortcut: Store apps can't be given a web address or file; starting it without one.");
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + target.Substring(6)) { UseShellExecute = false })?.Dispose();
                return true;
            }
            string path = target.StartsWith("exe:", StringComparison.OrdinalIgnoreCase) ? target.Substring(4) : target;
            if (!File.Exists(path) && FindMovedExe(path) is string moved)
            {
                Log.Write($"Shortcut: {path} has moved (the app updated itself); using {moved}.");
                path = moved;
            }
            if (!File.Exists(path))
            {
                Log.Write($"Shortcut: {path} doesn't exist any more (was the app moved or uninstalled?).");
                return false;
            }
            var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" };
            if (OpenArgument(open) is string arg) start.Arguments = arg;
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Shortcut: could not start {target}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// "Open with" of a launch shortcut, as one quoted argument: only a web address (http, https) or a full path, and
    /// never anything with quotes or control characters (it came from the shortcut's command line). Null = none.
    /// </summary>
    private static string? OpenArgument(string? open)
    {
        if (string.IsNullOrWhiteSpace(open)) return null;
        string? s = LaunchOpen.Clean(open);
        if (s == null) { Log.Write("Shortcut: ignored its web address or file, because it isn't one."); return null; }
        return "\"" + s + "\"";
    }

    /// <summary>
    /// Apps that update themselves into a new versioned folder (Discord, Slack… "...\app-1.2.3\x.exe"):
    /// find the same exe in the newest sibling folder.
    /// </summary>
    private static string? FindMovedExe(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            string? parent = dir == null ? null : Path.GetDirectoryName(dir);
            if (dir == null || parent == null || !Directory.Exists(parent)) return null;
            string folder = Path.GetFileName(dir);
            int dash = folder.IndexOf('-');
            string prefix = dash > 0 ? folder[..(dash + 1)] : folder.TrimEnd("0123456789.".ToCharArray());
            if (prefix.Length == 0) return null;
            return Directory.EnumerateDirectories(parent, prefix + "*")
                .Select(d => Path.Combine(d, Path.GetFileName(path)))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}

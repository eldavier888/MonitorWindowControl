using System;
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
/// </summary>
internal static class Program
{
    private const string HelperMutexName = @"Local\MonitorWindowControl.Helper";
    private const string HelperExitEventName = @"Local\MonitorWindowControl.HelperExit";
    private const int ActivateRetryMs = 2_000;         // if the tray app is still shutting down
    private const int LaunchMaxWaitMs = 30_000;        // slow apps (Steam…) can take a while to show a window
    private const int LaunchAlreadyOpenWaitMs = 6_000; // app was already running: it may just reuse its window
    private const int LaunchQuietMs = 4_000;           // after a window is placed, wait this long for more (splash → main window)

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            int i = Array.IndexOf(args, "--launch");
            if (i >= 0 && i + 1 < args.Length) return Launch(args[i + 1], ArgAfter(args, "--key"));
            return Background();
        }
        catch (Exception ex)
        {
            Log.Write("Helper stopped after an error: " + ex.Message);
            return 1;
        }
    }

    private static string? ArgAfter(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ------------------------------------------------------------------ background helper

    private static int Background()
    {
        using var single = Native.TryCreateOwnedMutex(HelperMutexName, out bool first);
        if (!first) return 0; // already running (possibly a copy started as administrator)

        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, HelperExitEventName);
        using var host = new NativeHost();
        using var engine = new Engine(host);
        host.DisplayChanged = engine.OnDisplaySettingsChanged;
        engine.Start(importLegacy: false, who: "Helper");

        if (engine.Settings.RunMode != RunModes.Helper)
        {
            Log.Write("Helper: the app isn't set to run as a helper, so the helper exits.");
            return 0;
        }

        // Stop when the settings window switches to another mode, or asks us to.
        engine.Changed += () => { if (engine.Settings.RunMode != RunModes.Helper) host.Quit(); };
        var wait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => host.Post(host.Quit), null, Timeout.Infinite, true);

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
        engine.Deactivate();
        return 0;
    }

    // ------------------------------------------------------------------ launch shortcut

    private static int Launch(string target, string? key)
    {
        using var host = new NativeHost();
        using var engine = new Engine(host);
        host.DisplayChanged = engine.OnDisplaySettingsChanged;
        engine.Start(importLegacy: false, who: $"Shortcut for {key ?? target}");

        // The saved launch target is kept up to date (apps that update themselves can move); prefer it.
        if (key != null && engine.Settings.Find(key)?.LaunchTarget is string saved) target = saved;

        // If the tray app or background helper is running, it places windows already: just start the app.
        // (Other launch shortcuts don't count: each one only places its own app.)
        bool placing = false, alreadyRunning = false;
        if (key != null && engine.Settings.Enabled && !Engine.AnyProcessActive())
        {
            alreadyRunning = IsRunning(target, key, engine);
            engine.OnlyAppKey = key;
            placing = engine.Activate(learn: false, claimActive: false); // windows already on screen are left alone
        }

        if (!StartTarget(target)) return 1;
        if (!placing) return 0;

        var deadline = host.CreateTimer(alreadyRunning ? LaunchAlreadyOpenWaitMs : LaunchMaxWaitMs, host.Quit);
        deadline.Start();
        // Each placed window restarts a short quiet period, so a splash screen followed by the main window works.
        var quiet = host.CreateTimer(LaunchQuietMs, host.Quit);
        engine.WindowHandled += _ => quiet.Start();

        host.Run();
        engine.Deactivate();
        return 0;
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

    private static bool StartTarget(string target)
    {
        try
        {
            if (target.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
            {
                // Store and packaged apps are started through the Apps folder, like the Start menu does.
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
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Shortcut: could not start {target}: {ex.Message}");
            return false;
        }
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

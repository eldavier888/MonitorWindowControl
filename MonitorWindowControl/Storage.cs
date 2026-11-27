using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32;

namespace MonitorWindowControl;

public static class Paths
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorWindowControl");
    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string LogFile => Path.Combine(Dir, "monitorwindowcontrol.log");
    public static string LegacyIni => Path.Combine(AppContext.BaseDirectory, "WindowHome.ini");

    /// <summary>Where the tiny helper is unpacked to (not a folder Windows' "Controlled folder access" protects).</summary>
    public static string HelperDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorWindowControl");
    public static string HelperExe => Path.Combine(HelperDir, "MonitorWindowControlHelper.exe");

    /// <summary>Start menu folder for launch-only shortcuts.</summary>
    public static string ShortcutsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Monitor Window Control");

    /// <summary>Settings folder used before the app was renamed from "WindowHome".</summary>
    public static string OldDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowHome");

    /// <summary>First start under the new name: carry over settings and log from the old folder.</summary>
    public static void MigrateFromOldName()
    {
        try
        {
            if (Directory.Exists(Dir) || !File.Exists(Path.Combine(OldDir, "settings.json"))) return;
            Directory.CreateDirectory(Dir);
            File.Copy(Path.Combine(OldDir, "settings.json"), SettingsFile);
            string oldLog = Path.Combine(OldDir, "windowhome.log");
            if (File.Exists(oldLog)) File.Copy(oldLog, LogFile);
            Log.Write($"Carried over settings from {OldDir} (the app used to be called WindowHome).");
        }
        catch { /* not critical: worst case it starts fresh */ }
    }
}

/// <summary>Where one app should open.</summary>
public sealed class AppRule
{
    /// <summary>"chrome.exe", or "Netflix (Store app)" for Store apps.</summary>
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Device name, e.g. \\.\DISPLAY2.</summary>
    public string? MonitorDevice { get; set; }
    /// <summary>Friendly model name at the time it was saved (used if Windows renumbers displays).</summary>
    public string? MonitorName { get; set; }
    /// <summary>The monitor's hardware identity (EDID and connector), when known. Older files don't have it.</summary>
    public MonitorHardware? MonitorHardware { get; set; }
    /// <summary>Set by the user (not learned); learning never changes it.</summary>
    public bool Pinned { get; set; }
    /// <summary>User chose "Never move this app".</summary>
    public bool NeverMove { get; set; }
    /// <summary>
    /// User chose "Where I used it last": MonitorDevice/MonitorName then hold the monitor it was last used on,
    /// updated every time you use it on one of <see cref="AllowedMonitors"/> (empty = any monitor).
    /// </summary>
    public bool FollowLastUsed { get; set; }
    public List<MonitorRef> AllowedMonitors { get; set; } = new();
    /// <summary>How to start the app for launch-only shortcuts: "exe:C:\...\app.exe" or "aumid:Package!App". Null = unknown yet.</summary>
    public string? LaunchTarget { get; set; }
    /// <summary>File name (without .lnk) of this app's launch shortcut, fixed when it was created. Null = none made.</summary>
    public string? ShortcutFile { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A saved reference to a monitor: device name plus model name (in case Windows renumbers displays).</summary>
public sealed class MonitorRef
{
    public string Device { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>The monitor's hardware identity (EDID and connector), when known. Older files don't have it.</summary>
    public MonitorHardware? Hardware { get; set; }
}

/// <summary>
/// What identifies a physical monitor, unlike \\.\DISPLAYn (which Windows renumbers): the path of the port it's
/// plugged into, its EDID manufacturer and product code, and its EDID serial number. Any of them can be missing.
/// </summary>
public sealed class MonitorHardware
{
    /// <summary>Windows' device path for the monitor on its port (changes when the cable moves to another port).</summary>
    public string? DevicePath { get; set; }
    /// <summary>EDID manufacturer and product code, for example "4C2D-0F2E" (the same for every monitor of a model).</summary>
    public string? Model { get; set; }
    /// <summary>EDID serial number (null when the monitor doesn't report one).</summary>
    public string? Serial { get; set; }

    /// <summary>True when nothing is known.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(DevicePath) && string.IsNullOrEmpty(Model) && string.IsNullOrEmpty(Serial);

    public bool SameAs(MonitorHardware? other) =>
        other != null && string.Equals(DevicePath, other.DevicePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Model, other.Model, StringComparison.OrdinalIgnoreCase) && Serial == other.Serial;

    public MonitorHardware Copy() => new() { DevicePath = DevicePath, Model = Model, Serial = Serial };

    /// <summary>Trims the fields and returns null when nothing is left (repairs hand-edited files).</summary>
    public static MonitorHardware? Clean(MonitorHardware? h)
    {
        if (h == null) return null;
        static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        h.DevicePath = Trim(h.DevicePath); h.Model = Trim(h.Model); h.Serial = Trim(h.Serial);
        return h.IsEmpty ? null : h;
    }
}

/// <summary>Your choices for one monitor that aren't its name or number.</summary>
public sealed class MonitorOptions
{
    public MonitorRef Monitor { get; set; } = new();
    /// <summary>
    /// True = temporary (apps aren't learned there, and it's never used instead of a monitor that's off);
    /// false = a normal monitor; null = decided by how it's connected (wireless and virtual monitors are temporary).
    /// </summary>
    public bool? Temporary { get; set; }
}

/// <summary>Your own name and/or number for a monitor.</summary>
public sealed class MonitorNickname
{
    public MonitorRef Monitor { get; set; } = new();
    public string Nickname { get; set; } = "";
    public int? Number { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool LearnAutomatically { get; set; } = true;
    /// <summary>true = apps it hasn't learned yet open on the main monitor; false = leave them.</summary>
    public bool MoveUnknownToMain { get; set; } = true;
    /// <summary>How it runs in the background: see <see cref="RunModes"/>.</summary>
    public string RunMode { get; set; } = RunModes.Tray;
    /// <summary>Start with Windows (null = not decided yet; older versions kept this only in the registry).</summary>
    public bool? StartWithWindows { get; set; }
    public List<AppRule> Apps { get; set; } = new();
    /// <summary>Your own names for monitors ("1 · Main", "TV"…).</summary>
    public List<MonitorNickname> MonitorNicknames { get; set; } = new();
    /// <summary>Per-monitor choices, such as whether a wireless or USB monitor is temporary.</summary>
    public List<MonitorOptions> MonitorOptions { get; set; } = new();
    /// <summary>true = don't move windows while this PC is used through Remote Desktop (learning always pauses then).</summary>
    public bool PauseDuringRemoteDesktop { get; set; } = true;

    public AppRule? Find(string key) =>
        Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Compile-time JSON code, so settings also load in the tiny native helper (no reflection there).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(MonitorHardware))]
[JsonSerializable(typeof(MonitorOptions))]
internal partial class SettingsJson : JsonSerializerContext { }

/// <summary>
/// settings.json is shared by the settings window and the helper, which may run at the same time.
/// Writes are atomic (temp file + rename); reads retry briefly if the other process is mid-write,
/// and a file that is only temporarily unreadable never resets your settings.
/// </summary>
public static class SettingsStore
{
    private const string LockName = @"Local\MonitorWindowControl.Settings";
    private static DateTime _loadedStampUtc;
    /// <summary>Set when settings.json exists but couldn't be read at startup: never overwrite it until it's read.</summary>
    private static bool _notReadYet;

    /// <summary>
    /// Cross-process lock for "re-read, change, save", so the settings window and the helper never overwrite
    /// each other's changes. Hold it around the whole change (it's re-entrant on the same thread).
    /// </summary>
    public static IDisposable Lock() => new SettingsLock();

    private sealed class SettingsLock : IDisposable
    {
        private readonly Mutex _m = new(false, LockName);
        private readonly bool _held;
        public SettingsLock()
        {
            try { _held = _m.WaitOne(3000); }
            catch (AbandonedMutexException) { _held = true; } // the other process died holding it: it's ours now
        }
        public void Dispose()
        {
            if (_held) try { _m.ReleaseMutex(); } catch { }
            _m.Dispose();
        }
    }

    public static AppSettings Load(out bool existed)
    {
        existed = File.Exists(Paths.SettingsFile);
        if (!existed) return new AppSettings();
        var (settings, error, busy) = ReadWithRetry(busyAttempts: 50); // up to ~3 s if another program holds it
        if (settings != null) return settings;
        if (busy)
        {
            // Locked (antivirus, backup, sync…), not broken: run with defaults for now, re-read it as soon as possible,
            // and don't save over it meanwhile.
            _notReadYet = true;
            _loadedStampUtc = DateTime.MinValue;
            Log.Write($"settings.json is in use by another program ({error}); it will be read again shortly.");
            return new AppSettings();
        }

        // Really unreadable (not just busy): keep a copy so nothing is lost, start fresh.
        string bad = Paths.SettingsFile + ".broken";
        try { File.Copy(Paths.SettingsFile, bad, true); } catch { }
        Log.Write($"settings.json could not be read ({error}). A copy was saved as {bad}; starting fresh.");
        return new AppSettings();
    }

    /// <summary>True if settings.json was changed (by any process) since this process last read or wrote it.</summary>
    public static bool ChangedOnDisk()
    {
        try { return File.Exists(Paths.SettingsFile) && File.GetLastWriteTimeUtc(Paths.SettingsFile) != _loadedStampUtc; }
        catch { return false; }
    }

    /// <summary>Re-reads settings after another process changed them; null if it can't be read right now (try again later).</summary>
    public static AppSettings? TryReload() => ReadWithRetry().settings;

    private static (AppSettings? settings, string? error, bool busy) ReadWithRetry(int busyAttempts = 5)
    {
        string? error = null;
        bool busy = false;
        DateTime stamp = default;
        for (int attempt = 0; attempt < busyAttempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(60);
            try
            {
                stamp = File.GetLastWriteTimeUtc(Paths.SettingsFile);
                string json = File.ReadAllText(Paths.SettingsFile);
                var s = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings) ?? new AppSettings();
                Clean(s);
                _loadedStampUtc = stamp;
                _notReadYet = false;
                return (s, null, false);
            }
            catch (IOException ex) { error = ex.Message; busy = true; }   // being written by the other process
            catch (UnauthorizedAccessException ex) { error = ex.Message; busy = true; }
            catch (Exception ex)                                           // invalid JSON, or valid JSON with impossible values
            {
                error = ex.Message;
                busy = false;
                if (attempt >= 1)
                {
                    // Really broken, not just mid-write: don't re-try on every check until the file changes again.
                    _loadedStampUtc = stamp;
                    break;
                }
            }
        }
        return (null, error, busy);
    }

    /// <summary>
    /// Repairs what a hand-edited or damaged (but still valid JSON) file can contain: missing lists, null entries,
    /// entries without a key, duplicates. Anything that can't be repaired is dropped, never crashes the app.
    /// </summary>
    private static void Clean(AppSettings s)
    {
        s.Apps ??= new List<AppRule>();
        s.Apps.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Key));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        s.Apps.RemoveAll(a => !seen.Add(a.Key));
        foreach (var a in s.Apps)
        {
            a.DisplayName ??= a.Key;
            a.AllowedMonitors ??= new();
            a.AllowedMonitors.RemoveAll(m => m == null);
            foreach (var m in a.AllowedMonitors) CleanRef(m);
            a.MonitorHardware = MonitorHardware.Clean(a.MonitorHardware);
        }
        s.MonitorNicknames ??= new();
        s.MonitorNicknames.RemoveAll(n => n == null || n.Monitor == null);
        foreach (var n in s.MonitorNicknames) { CleanRef(n.Monitor); n.Nickname ??= ""; }
        s.MonitorOptions ??= new();
        s.MonitorOptions.RemoveAll(o => o == null || o.Monitor == null || o.Temporary == null);
        foreach (var o in s.MonitorOptions) CleanRef(o.Monitor);
        s.RunMode = RunModes.Normalize(s.RunMode);
    }

    private static void CleanRef(MonitorRef m)
    {
        m.Device ??= "";
        m.Hardware = MonitorHardware.Clean(m.Hardware);
    }

    public static void Save(AppSettings s)
    {
        if (_notReadYet && File.Exists(Paths.SettingsFile))
        {
            Log.Write("Not saving: settings.json couldn't be read yet, and saving now would replace your settings.");
            return;
        }
        string tmp = Paths.SettingsFile + "." + Environment.ProcessId + ".tmp";
        try
        {
            Directory.CreateDirectory(Paths.Dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings));
            // A rename keeps the file's time stamp: remember ours, so a later write by the other process is noticed.
            var myStamp = File.GetLastWriteTimeUtc(tmp);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, Paths.SettingsFile, true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(60); }
                catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(60); }
            }
            _loadedStampUtc = myStamp;
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
            try { File.Delete(tmp); } catch { }
        }
    }
}

/// <summary>Rolling text log, capped at about 1 MB. Shared by the settings window and the helper.</summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Paths.Dir);
                    var fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        var lines = File.ReadAllLines(Paths.LogFile);
                        var keep = lines.Skip(lines.Length / 2);
                        File.WriteAllLines(Paths.LogFile, new[] { "---- older entries removed to keep the log small ----" }.Concat(keep));
                    }
                    File.AppendAllText(Paths.LogFile, line);
                    return;
                }
                catch (IOException) { Thread.Sleep(25); } // the other process is writing a line
                catch { return; }                         // logging must never crash the app
            }
        }
    }
}

/// <summary>Start with Windows via HKCU\...\Run (no admin needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorWindowControl";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// Points the Run entry at the right program for the mode: the settings app in the tray, the tiny helper,
    /// or nothing at all in launch-only mode (nothing should start in the background then).
    /// </summary>
    public static void Apply(string mode, bool enabled, string appExe, string helperExe)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            string? command = !enabled ? null
                : mode == RunModes.Helper ? $"\"{helperExe}\""
                : mode == RunModes.LaunchOnly ? null
                : $"\"{appExe}\" --tray";
            if (command == null) k.DeleteValue(ValueName, false);
            else if (k.GetValue(ValueName) as string != command) k.SetValue(ValueName, command);
        }
        catch (Exception ex) { Log.Write("Could not update Start with Windows: " + ex.Message); }
    }

    /// <summary>Removes the autostart entry of the app's old name ("WindowHome.exe --tray"), only that exact kind.</summary>
    public static void RemoveOldNameEntry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k?.GetValue("WindowHome") is string old
                && old.Contains("WindowHome.exe", StringComparison.OrdinalIgnoreCase) && old.EndsWith("--tray"))
                k.DeleteValue("WindowHome", false);
        }
        catch { }
    }
}

/// <summary>Imports WindowHome.ini from the old AutoHotkey script ([home] and [pinned] sections).</summary>
public static class IniImporter
{
    public static int Import(AppSettings settings, IReadOnlyList<MonitorInfo> monitors)
    {
        string path = Paths.LegacyIni;
        if (!File.Exists(path)) return 0;

        var home = new List<(string key, string val)>();
        var pinned = new List<(string key, string val)>();
        var old = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim().ToLowerInvariant(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().Trim('"');
                string val = line[(eq + 1)..].Trim().Trim('"');
                if (key.Length == 0) continue;
                if (section == "home") home.Add((key, val));
                else if (section == "pinned") pinned.Add((key, val));
                else if (section == "settings") old[key] = val;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not read WindowHome.ini: " + ex.Message);
            return 0;
        }

        // [settings] enabled=1, learn=1, unknown=main|leave
        if (old.TryGetValue("enabled", out var en)) settings.Enabled = en.Trim() != "0";
        if (old.TryGetValue("learn", out var le)) settings.LearnAutomatically = le.Trim() != "0";
        if (old.TryGetValue("unknown", out var un)) settings.MoveUnknownToMain = !un.Trim().Equals("leave", StringComparison.OrdinalIgnoreCase);

        int count = 0;
        foreach (var (key, val) in home)
        {
            if (IsNever(val))
            {
                var never = GetOrAdd(settings, key);
                never.Pinned = true; never.NeverMove = true;
                count++;
                continue;
            }
            if (!IsDevice(val)) continue;
            var rule = GetOrAdd(settings, key);
            SetMonitor(rule, val, monitors);
            count++;
        }
        foreach (var (key, val) in pinned)
        {
            string v = val.ToLowerInvariant();
            var rule = GetOrAdd(settings, key);
            if (IsDevice(val)) { SetMonitor(rule, val, monitors); rule.Pinned = true; rule.NeverMove = false; }
            else if (IsNever(v)) { rule.Pinned = true; rule.NeverMove = true; }
            else if (rule.MonitorDevice != null || rule.NeverMove) rule.Pinned = true; // app=1 → keep its [home] entry, pinned
            else { settings.Apps.Remove(rule); continue; }
            count++;
        }
        int apps = home.Select(h => NormalizeKey(h.key)).Concat(pinned.Select(p => NormalizeKey(p.key)))
                       .Distinct(StringComparer.OrdinalIgnoreCase).Count(k => settings.Find(k) != null);
        Log.Write($"Imported {apps} apps from {path}");
        return apps;
    }

    private static bool IsDevice(string v) => v.StartsWith(@"\\.\", StringComparison.Ordinal);

    private static bool IsNever(string v) =>
        v.Trim().ToLowerInvariant() is "ignore" or "never" or "none" or "skip" or "leave" or "-" or "nevermove";

    /// <summary>"Chrome.EXE" → "chrome.exe"; old script's "uwp:Netflix" → "Netflix (Store app)".</summary>
    private static string NormalizeKey(string key)
    {
        if (key.StartsWith("uwp:", StringComparison.OrdinalIgnoreCase)) return key[4..].Trim() + " (Store app)";
        return key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? key.ToLowerInvariant() : key;
    }

    private static AppRule GetOrAdd(AppSettings s, string key)
    {
        key = NormalizeKey(key);
        var r = s.Find(key);
        if (r == null) { r = new AppRule { Key = key, DisplayName = key }; s.Apps.Add(r); }
        return r;
    }

    private static void SetMonitor(AppRule rule, string device, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));
        rule.MonitorDevice = device.ToUpperInvariant();
        rule.MonitorName = monitor?.FriendlyName;
        rule.MonitorHardware = monitor == null || monitor.IsDuplicated ? null : MonitorHardware.Clean(monitor.Hardware);
        rule.UpdatedUtc = DateTime.UtcNow;
    }
}

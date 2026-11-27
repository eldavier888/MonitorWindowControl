using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace MonitorWindowControl;

public static class Paths
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorWindowControl");
    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string LogFile => Path.Combine(Dir, "monitorwindowcontrol.log");
    public static string LegacyIni => Path.Combine(AppContext.BaseDirectory, "WindowHome.ini");

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
    /// <summary>Set by the user (not learned); learning never changes it.</summary>
    public bool Pinned { get; set; }
    /// <summary>User chose "Never move this app".</summary>
    public bool NeverMove { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool LearnAutomatically { get; set; } = true;
    /// <summary>true = apps it hasn't learned yet open on the main monitor; false = leave them.</summary>
    public bool MoveUnknownToMain { get; set; } = true;
    public List<AppRule> Apps { get; set; } = new();

    public AppRule? Find(string key) =>
        Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(out bool existed)
    {
        existed = File.Exists(Paths.SettingsFile);
        if (!existed) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options) ?? new AppSettings();
            s.Apps ??= new List<AppRule>();
            s.Apps.RemoveAll(a => string.IsNullOrWhiteSpace(a.Key));
            return s;
        }
        catch (Exception ex)
        {
            string bad = Paths.SettingsFile + ".broken";
            try { File.Copy(Paths.SettingsFile, bad, true); } catch { }
            Log.Write($"settings.json could not be read ({ex.Message}). A copy was saved as {bad}; starting fresh.");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Paths.Dir);
            string tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
        }
    }
}

/// <summary>Rolling text log, capped at about 1 MB.</summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Dir);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(Paths.LogFile);
                    var keep = lines.Skip(lines.Length / 2);
                    File.WriteAllLines(Paths.LogFile, new[] { "---- older entries removed to keep the log small ----" }.Concat(keep));
                }
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}

/// <summary>Start with Windows via HKCU\...\Run (no admin needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorWindowControl";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, false);
    }

    /// <summary>If the exe was moved or renamed, point the Run entry at the new location.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            // Entry written by this app under its old name ("WindowHome.exe --tray"): replace it with the new one.
            // (Only that exact kind of entry, so an old AutoHotkey autostart is never touched.)
            if (k.GetValue("WindowHome") is string old
                && old.Contains("WindowHome.exe", StringComparison.OrdinalIgnoreCase) && old.EndsWith("--tray"))
            {
                k.DeleteValue("WindowHome", false);
                k.SetValue(ValueName, Command);
            }
            if (k.GetValue(ValueName) is string current && current != Command) k.SetValue(ValueName, Command);
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
        rule.MonitorDevice = device.ToUpperInvariant();
        rule.MonitorName = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase))?.FriendlyName;
        rule.UpdatedUtc = DateTime.UtcNow;
    }
}

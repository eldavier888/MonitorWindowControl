using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace MonitorWindowControl;

/// <summary>
/// Settings an organisation sets through the registry (Group Policy, Intune or a .reg file), read from
/// HKLM\SOFTWARE\Policies\MonitorWindowControl and HKCU\SOFTWARE\Policies\MonitorWindowControl. A value set for the
/// whole PC wins over one set for the user. Anything set here is locked: the settings window shows it but can't change it.
/// Templates for Group Policy are in the policies folder of the project.
/// </summary>
public sealed class PolicySettings
{
    /// <summary>"Move new windows" (null = not managed).</summary>
    public bool? Enabled { get; set; }
    /// <summary>"Learn automatically".</summary>
    public bool? LearnAutomatically { get; set; }
    /// <summary>"Apps it hasn't learned yet": one of <see cref="Policies.UnknownMain"/>, <see cref="Policies.UnknownLeave"/>, <see cref="Policies.UnknownMouse"/>.</summary>
    public string? UnknownApps { get; set; }
    /// <summary>"How it runs": one of <see cref="RunModes"/>.</summary>
    public string? RunMode { get; set; }
    /// <summary>false = "Make main" and "Keep my main monitor" can't change the main monitor in Windows.</summary>
    public bool? AllowMakeMain { get; set; }
    /// <summary>App key ("chrome.exe") → "main", "never" or a monitor's model name.</summary>
    public Dictionary<string, string> AppRules { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the organisation manages at least one setting.</summary>
    public bool HasAny => Enabled != null || LearnAutomatically != null || UnknownApps != null || RunMode != null
                          || AllowMakeMain != null || AppRules.Count > 0;

    /// <summary>True when where this app opens is set by the organisation.</summary>
    public bool IsAppLocked(string key) => AppRules.ContainsKey(key);

    /// <summary>True when the main monitor may be changed (it can't when the organisation says so).</summary>
    public bool MakeMainAllowed => AllowMakeMain != false;
}

/// <summary>Reads organisation policies and puts them over the saved settings.</summary>
public static class Policies
{
    public const string KeyPath = @"SOFTWARE\Policies\MonitorWindowControl";
    public const string UnknownMain = "main", UnknownLeave = "leave", UnknownMouse = "mouse";
    public const string AppMain = "main", AppNever = "never";

    /// <summary>Reads the policies (machine first, then the user's for what the machine doesn't set). Never throws.</summary>
    public static PolicySettings Read()
    {
        var p = new PolicySettings();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(KeyPath);
                if (k != null) ReadKey(k, p);
            }
            catch (Exception ex) { Log.Write("Could not read organisation policies: " + ex.Message); }
        }
        return p;
    }

    /// <summary>Fills in what <paramref name="p"/> doesn't have yet from one policy key.</summary>
    private static void ReadKey(RegistryKey k, PolicySettings p)
    {
        p.Enabled ??= Bool(k.GetValue("Enabled"));
        p.LearnAutomatically ??= Bool(k.GetValue("LearnAutomatically"));
        p.AllowMakeMain ??= Bool(k.GetValue("AllowMakeMain"));
        if (p.UnknownApps == null && k.GetValue("UnknownApps") is string u
            && u.Trim().ToLowerInvariant() is UnknownMain or UnknownLeave or UnknownMouse)
            p.UnknownApps = u.Trim().ToLowerInvariant();
        if (p.RunMode == null && k.GetValue("RunMode") is string m)
        {
            string mode = m.Trim();
            foreach (var known in new[] { RunModes.Tray, RunModes.Helper, RunModes.LaunchOnly })
                if (string.Equals(mode, known, StringComparison.OrdinalIgnoreCase)) p.RunMode = known;
        }
        using var apps = k.OpenSubKey("AppRules");
        if (apps == null) return;
        foreach (string name in apps.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(name) || apps.GetValue(name) is not string value || string.IsNullOrWhiteSpace(value)) continue;
            string key = NormalizeKey(name);
            if (!p.AppRules.ContainsKey(key)) p.AppRules[key] = value.Trim();
        }
    }

    /// <summary>A DWORD (or a "0"/"1" string) as a yes or no; anything else is "not set".</summary>
    private static bool? Bool(object? v) => v switch
    {
        int i => i != 0,
        string s when s.Trim() == "0" => false,
        string s when s.Trim() == "1" => true,
        _ => null,
    };

    /// <summary>"Chrome.EXE" → "chrome.exe", like the app keys in settings.json.</summary>
    private static string NormalizeKey(string key)
    {
        key = key.Trim();
        return key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? key.ToLowerInvariant() : key;
    }

    /// <summary>
    /// Puts the policies over the settings (in memory). App rules become "Set by you" choices: "main" is the main
    /// monitor now, "never" is "Never move this app", anything else is a monitor's model name. Returns true if
    /// something changed.
    /// </summary>
    public static bool Apply(AppSettings s, PolicySettings p, IReadOnlyList<MonitorInfo> monitors, MonitorInfo? main)
    {
        bool changed = false;
        void Set<T>(T current, T? wanted, Action<T> assign) where T : struct
        {
            if (wanted is T w && !EqualityComparer<T>.Default.Equals(current, w)) { assign(w); changed = true; }
        }
        Set(s.Enabled, p.Enabled, v => s.Enabled = v);
        Set(s.LearnAutomatically, p.LearnAutomatically, v => s.LearnAutomatically = v);
        if (p.UnknownApps != null)
        {
            bool toMain = p.UnknownApps != UnknownLeave, mouse = p.UnknownApps == UnknownMouse;
            Set(s.MoveUnknownToMain, toMain, v => s.MoveUnknownToMain = v);
            Set(s.UnknownFollowMouse, mouse, v => s.UnknownFollowMouse = v);
        }
        if (p.RunMode != null && s.RunMode != p.RunMode) { s.RunMode = p.RunMode; changed = true; }
        if (p.AllowMakeMain == false && s.KeepMainMonitor != null) { s.KeepMainMonitor = null; s.KeepMainStopped = false; changed = true; }
        foreach (var (key, value) in p.AppRules) changed |= ApplyApp(s, key, value, monitors, main);
        return changed;
    }

    /// <summary>One app rule from the policy. Returns true if the saved rule changed.</summary>
    private static bool ApplyApp(AppSettings s, string key, string value, IReadOnlyList<MonitorInfo> monitors, MonitorInfo? main)
    {
        var rule = s.Find(key);
        bool isNew = rule == null;
        rule ??= new AppRule { Key = key, DisplayName = key };
        bool never = string.Equals(value, AppNever, StringComparison.OrdinalIgnoreCase);
        MonitorInfo? monitor = never ? null
            : string.Equals(value, AppMain, StringComparison.OrdinalIgnoreCase) ? main
            : monitors.FirstOrDefault(m => m.Targets.Any(t => string.Equals(t.FriendlyName, value, StringComparison.OrdinalIgnoreCase)));
        string? name = never ? null : monitor?.FriendlyName ?? (string.Equals(value, AppMain, StringComparison.OrdinalIgnoreCase) ? null : value);
        if (!never && name == null) return false; // "main" while no monitor is known yet: try again later
        string? device = monitor?.DeviceName;
        bool same = !isNew && rule.Pinned && rule.NeverMove == never && !rule.FollowLastUsed && !rule.FollowMouse
                    && (never || (string.Equals(rule.MonitorName, name, StringComparison.OrdinalIgnoreCase)
                                  && (device == null || string.Equals(rule.MonitorDevice, device, StringComparison.OrdinalIgnoreCase))));
        if (same) return false;
        rule.Pinned = true;
        rule.NeverMove = never;
        rule.FollowLastUsed = false;
        rule.FollowMouse = false;
        rule.AllowedMonitors.Clear();
        if (!never)
        {
            rule.MonitorName = name;
            rule.MonitorDevice = device;
            rule.MonitorHardware = monitor == null || monitor.IsDuplicated ? null : MonitorHardware.Clean(monitor.Hardware);
        }
        rule.UpdatedUtc = DateTime.UtcNow;
        if (isNew) s.Apps.Add(rule);
        return true;
    }
}

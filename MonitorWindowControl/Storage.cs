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
    public static string Dir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorWindowControl");
    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string LogFile => Path.Combine(Dir, "monitorwindowcontrol.log");
    public static string LegacyIni => Path.Combine(AppContext.BaseDirectory, "WindowHome.ini");

    /// <summary>Where the tiny helper is unpacked to (not a folder Windows' "Controlled folder access" protects).</summary>
    public static string HelperDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorWindowControl");
    public static string HelperExe => Path.Combine(HelperDir, "MonitorWindowControlHelper.exe");

    /// <summary>
    /// Tests only: keep settings, the log and the helper's files in this folder (and use a settings lock of their own),
    /// so a test never reads or changes the settings of the copy you use.
    /// </summary>
    internal static void UseFolderForTests(string dir)
    {
        Dir = Path.Combine(dir, "settings");
        HelperDir = Path.Combine(dir, "helper");
        SettingsStore.LockName = @"Local\MonitorWindowControl.Settings.Tests." + Environment.ProcessId;
    }

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
    /// <summary>
    /// The monitors a "Where I used it last" app may open on, in your order: when the one it was used on last is off,
    /// the first of the others that's on is used. Older files have them in any order, which keeps working.
    /// </summary>
    public List<MonitorRef> AllowedMonitors { get; set; } = new();
    /// <summary>
    /// "If it's off, open on": where it opens while its own monitor is off or disconnected. Null = the choice for all
    /// apps (<see cref="AppSettings.Fallback"/>); otherwise one of <see cref="FallbackModes"/>.
    /// </summary>
    public string? Fallback { get; set; }
    /// <summary>The monitor for <see cref="FallbackModes.Monitor"/>.</summary>
    public MonitorRef? FallbackMonitor { get; set; }
    /// <summary>
    /// User chose "The monitor I'm using (mouse pointer)": new windows open on the monitor with the mouse pointer, but
    /// only on one of <see cref="AllowedMonitors"/> (empty = any monitor). Learning never changes it.
    /// </summary>
    public bool FollowMouse { get; set; }
    /// <summary>"Place it": null = centered (what older versions always did), or one of <see cref="PlaceModes"/>.</summary>
    public string? Place { get; set; }
    /// <summary>For "Where I last had it": where its window was on each monitor, as fractions of that monitor's work area.</summary>
    public List<WindowPosition> Positions { get; set; } = new();
    /// <summary>Advanced: "Wait before moving", in milliseconds (null = automatic). See <see cref="AppTimings"/>.</summary>
    public int? WaitMs { get; set; }
    /// <summary>Advanced: "Keep correcting for", in milliseconds (null = 1 second). See <see cref="AppTimings"/>.</summary>
    public int? CorrectForMs { get; set; }
    /// <summary>The app moved itself back twice in a row after being moved: the Apps page suggests a longer wait.</summary>
    public bool MovedItselfBack { get; set; }
    /// <summary>How to start the app for launch-only shortcuts: "exe:C:\...\app.exe" or "aumid:Package!App". Null = unknown yet.</summary>
    public string? LaunchTarget { get; set; }
    /// <summary>File name (without .lnk) of this app's launch shortcut, fixed when it was created. Null = none made.</summary>
    public string? ShortcutFile { get; set; }
    /// <summary>
    /// File names (without .lnk) of this app's other launch shortcuts: ones that open it on a chosen monitor, or with a
    /// web address or file ("Netflix on 3 · TV").
    /// </summary>
    public List<string> MoreShortcuts { get; set; } = new();
    /// <summary>"Rules for some of its windows": checked, in order, before the app's own choice.</summary>
    public List<WindowRule> WindowRules { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A rule for some of an app's windows: windows whose title (or, in Chrome and Edge, browser profile) matches open on
/// their own monitor, or are never moved. Learning never changes it.
/// </summary>
public sealed class WindowRule
{
    /// <summary>What's compared: one of <see cref="WindowMatches"/>.</summary>
    public string Match { get; set; } = WindowMatches.TitleContains;
    /// <summary>The text the window title contains, starts with or is; or the browser profile's id. Case doesn't matter.</summary>
    public string Text { get; set; } = "";
    /// <summary>The monitor these windows open on (null when <see cref="NeverMove"/>).</summary>
    public MonitorRef? Monitor { get; set; }
    /// <summary>These windows are never moved.</summary>
    public bool NeverMove { get; set; }

    /// <summary>Does a window with this title and browser profile (null = not a browser window) match?</summary>
    public bool Matches(string title, string? profile)
    {
        if (Text.Length == 0) return false;
        return Match switch
        {
            WindowMatches.TitleStartsWith => title.StartsWith(Text, StringComparison.CurrentCultureIgnoreCase),
            WindowMatches.TitleIs => string.Equals(title.Trim(), Text, StringComparison.CurrentCultureIgnoreCase),
            WindowMatches.Profile => profile != null && string.Equals(profile, Text, StringComparison.OrdinalIgnoreCase),
            _ => title.Contains(Text, StringComparison.CurrentCultureIgnoreCase),
        };
    }
}

/// <summary>How a <see cref="WindowRule"/> picks windows. Plain text only, never patterns.</summary>
public static class WindowMatches
{
    public const string TitleContains = "contains";
    public const string TitleStartsWith = "starts";
    public const string TitleIs = "is";
    public const string Profile = "profile";

    public static bool IsKnown(string? m) => m is TitleContains or TitleStartsWith or TitleIs or Profile;
}

/// <summary>
/// A set of connected monitors seen before ("Your setups" on the Monitors page): named by the model of each monitor,
/// so the same monitors at home and at work count as the same setup. Apps can have their own monitor in a setup that
/// isn't the usual one.
/// </summary>
public sealed class MonitorSetup
{
    /// <summary>The sorted model ids of its monitors, joined (see Engine.SetupIdOf).</summary>
    public string Id { get; set; } = "";
    /// <summary>Your own name for it ("Home", "Laptop only"); null = "3 monitors".</summary>
    public string? Name { get; set; }
    /// <summary>Its monitors' model names when it was last seen, to describe it.</summary>
    public List<string> Monitors { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Apps that open on another monitor than usual while this setup is connected.</summary>
    public List<SetupHome> Homes { get; set; } = new();
}

/// <summary>Where one app opens while a <see cref="MonitorSetup"/> is connected.</summary>
public sealed class SetupHome
{
    public string Key { get; set; } = "";
    public MonitorRef Monitor { get; set; } = new();
    /// <summary>Set by you (learning never changes it).</summary>
    public bool Pinned { get; set; }
}

/// <summary>"App groups": a set of saved apps opened together, each on its own monitor (or the one chosen here).</summary>
public sealed class AppGroup
{
    /// <summary>A fixed id (for its shortcut and the tray menu), independent of its name.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<GroupApp> Apps { get; set; } = new();
    /// <summary>File name (without .lnk) of the group's launch shortcut. Null = none made.</summary>
    public string? ShortcutFile { get; set; }
}

/// <summary>One app in an <see cref="AppGroup"/>.</summary>
public sealed class GroupApp
{
    public string Key { get; set; } = "";
    /// <summary>Open it on this monitor when the group opens; null = its usual monitor.</summary>
    public MonitorRef? Monitor { get; set; }
}

/// <summary>
/// From a launch shortcut or group to the program that places windows: open these apps' next windows on these
/// monitors, this once (written to launch.json, see <see cref="HelperChannel.LaunchEventName"/>).
/// </summary>
public sealed class LaunchRequest
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<LaunchItem> Items { get; set; } = new();
}

/// <summary>"Open with" of a launch shortcut: a web address or a file, checked the same way where it's made and where it's used.</summary>
public static class LaunchOpen
{
    /// <summary>
    /// The trimmed text if it's a web address (http or https) or a full path to a file or folder, without quotes or
    /// control characters (it becomes one quoted argument on a command line); else null.
    /// </summary>
    public static string? Clean(string? text)
    {
        string s = text?.Trim() ?? "";
        if (s.Length == 0 || s.Length > 2000 || s.Any(c => char.IsControl(c) || c == '"')) return null;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return s;
        if (!Path.IsPathFullyQualified(s)) return null;
        return s.EndsWith('\\') ? s + "." : s; // (a backslash before the closing quote would escape it)
    }
}

/// <summary>One app of a <see cref="LaunchRequest"/>.</summary>
public sealed class LaunchItem
{
    public string Key { get; set; } = "";
    public MonitorRef Monitor { get; set; } = new();
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
    /// <summary>"Treat as switched off": new windows open on another monitor, and nothing is learned here.</summary>
    public bool Off { get; set; }
    /// <summary>When "switched off" ends by itself (null = until you turn it back on).</summary>
    public DateTime? OffUntilUtc { get; set; }
    /// <summary>
    /// "What can open here: only apps I choose": apps aren't learned here, and other apps' new windows that Windows
    /// opens here are moved to another monitor.
    /// </summary>
    public bool OnlyChosenApps { get; set; }

    /// <summary>True while "switched off" applies at this moment.</summary>
    public bool IsOffAt(DateTime utc) => Off && (OffUntilUtc == null || OffUntilUtc > utc);

    /// <summary>True when nothing is chosen (the entry can be removed).</summary>
    [JsonIgnore]
    public bool IsEmpty => Temporary == null && !Off && !OnlyChosenApps;
}

/// <summary>
/// Where an app's window was on one monitor ("Where I last had it"): its visible frame as fractions (0 to 1) of that
/// monitor's work area, so it fits again when the monitor's resolution or scale changes.
/// </summary>
public sealed class WindowPosition
{
    public MonitorRef Monitor { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Puts impossible values (hand-edited files) back in range. False if nothing usable is left.</summary>
    public bool Repair()
    {
        static double Fix(double v, double min) => double.IsFinite(v) ? Math.Clamp(v, min, 1) : -1;
        X = Fix(X, 0); Y = Fix(Y, 0); Width = Fix(Width, 0.02); Height = Fix(Height, 0.02);
        return X >= 0 && Y >= 0 && Width > 0 && Height > 0;
    }
}

/// <summary>"Place it": where on its monitor an app's new window goes (see <see cref="AppRule.Place"/>).</summary>
public static class PlaceModes
{
    /// <summary>Where I last had it on that monitor (centered until it's known).</summary>
    public const string Last = "Last";
    /// <summary>Maximized.</summary>
    public const string Maximized = "Maximized";

    /// <summary>A known mode, or null (centered) for anything else.</summary>
    public static string? Normalize(string? mode) => mode is Last or Maximized ? mode : null;
}

/// <summary>The Advanced timing choices for stubborn apps (see <see cref="AppRule.WaitMs"/>).</summary>
public static class AppTimings
{
    /// <summary>"Wait before moving": 1 s or 3 s (null = automatic, about 0.4 s).</summary>
    public static int? NormalizeWait(int? ms) => ms is 1000 or 3000 ? ms : null;
    /// <summary>"Keep correcting for": 5 s (null = 1 s).</summary>
    public static int? NormalizeCorrect(int? ms) => ms is 5000 ? ms : null;
}

/// <summary>Where an app opens while its own monitor is off or disconnected (see <see cref="AppRule.Fallback"/>).</summary>
public static class FallbackModes
{
    /// <summary>The main monitor (what older versions always did).</summary>
    public const string Main = "Main";
    /// <summary>A monitor you chose.</summary>
    public const string Monitor = "Monitor";
    /// <summary>Leave it where Windows puts it.</summary>
    public const string Leave = "Leave";

    /// <summary>A known mode, or null for anything else (and for a monitor choice without a monitor).</summary>
    public static string? Normalize(string? mode, MonitorRef? monitor) =>
        mode is Main or Leave ? mode : mode == Monitor && monitor != null ? Monitor : null;
}

/// <summary>"Keyboard shortcuts" (off by default): send the window you're using to another monitor.</summary>
public sealed class HotKeySettings
{
    /// <summary>The shortcuts work (only in the program that places windows: the tray app or the helper).</summary>
    public bool Enabled { get; set; }
    /// <summary>Shortcuts you changed. An action that isn't listed has its default keys.</summary>
    public List<HotKeyChoice> Keys { get; set; } = new();
}

/// <summary>Your keys for one keyboard shortcut.</summary>
public sealed class HotKeyChoice
{
    /// <summary>What it does: one of <see cref="HotKeyActions"/>.</summary>
    public string Action { get; set; } = "";
    /// <summary>
    /// Ctrl, Alt and/or the Windows key (<see cref="HotKeyActions.Ctrl"/>, <see cref="HotKeyActions.Alt"/>,
    /// <see cref="HotKeyActions.Win"/>). Never Shift: the same keys with Shift also save the monitor as the app's home.
    /// </summary>
    public int Modifiers { get; set; }
    /// <summary>The key (a Windows virtual-key code). 0 = no shortcut for this action.</summary>
    public int Key { get; set; }
}

/// <summary>The keyboard shortcuts, their default keys, and how keys are written.</summary>
public static class HotKeyActions
{
    /// <summary>Modifier flags, as RegisterHotKey takes them.</summary>
    public const int Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;
    /// <summary>Send the window you're using to the next or previous monitor, in your order.</summary>
    public const string Next = "Next", Previous = "Previous";
    /// <summary>"Move open windows to their monitors".</summary>
    public const string Tidy = "Tidy";
    /// <summary>Send-to-monitor shortcuts exist for monitors 1 to 9.</summary>
    public const int MaxMonitors = 9;
    private const int Default = Ctrl | Alt | Win;

    /// <summary>Every action, in the order they're registered and shown.</summary>
    public static readonly string[] All =
        Enumerable.Range(1, MaxMonitors).Select(Monitor).Concat(new[] { Next, Previous, Tidy }).ToArray();

    /// <summary>"Send to monitor n".</summary>
    public static string Monitor(int n) => "Monitor" + n;

    /// <summary>The monitor number of a "send to monitor n" action, or 0 for any other action.</summary>
    public static int MonitorNumber(string? action) =>
        action != null && action.StartsWith("Monitor", StringComparison.Ordinal) && int.TryParse(action.AsSpan(7), out int n)
        && n >= 1 && n <= MaxMonitors ? n : 0;

    public static bool IsKnown(string? action) => action is Next or Previous or Tidy || MonitorNumber(action) > 0;

    /// <summary>True for actions that have a Shift version which also saves the monitor as the app's home.</summary>
    public static bool HasSaveVersion(string action) => action != Tidy;

    /// <summary>The default keys: Windows key + Ctrl + Alt + 1…9, Right, Left, Home.</summary>
    public static (int Modifiers, int Key) DefaultKeys(string action) => action switch
    {
        Next => (Default, 0x27),     // Right arrow
        Previous => (Default, 0x25), // Left arrow
        Tidy => (Default, 0x24),     // Home
        _ => (Default, 0x30 + MonitorNumber(action)),
    };

    /// <summary>The keys for an action: yours if you changed them, else the default.</summary>
    public static (int Modifiers, int Key) KeysFor(HotKeySettings s, string action) =>
        s.Keys.FirstOrDefault(k => k.Action == action) is { } k ? (k.Modifiers, k.Key) : DefaultKeys(action);

    /// <summary>True for keys that can be a shortcut: at least one of Ctrl, Alt or the Windows key, and a known key (or none).</summary>
    public static bool IsValid(int modifiers, int key) =>
        key == 0 || ((modifiers & (Ctrl | Alt | Win)) != 0 && (modifiers & ~(Ctrl | Alt | Win)) == 0 && KeyName(key) != null);

    /// <summary>Keys in words, the way Windows writes them: "Windows key + Ctrl + Alt + 1" ("None" for no shortcut).</summary>
    public static string Text(int modifiers, int key)
    {
        if (key == 0) return "None";
        var parts = new List<string>();
        if ((modifiers & Win) != 0) parts.Add("Windows key");
        if ((modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(key) ?? $"key {key}");
        return string.Join(" + ", parts);
    }

    /// <summary>The keys you can choose, in the order they're listed.</summary>
    public static readonly int[] Choices =
        Enumerable.Range(0x30, 10).Concat(Enumerable.Range(0x41, 26)).Concat(Enumerable.Range(0x70, 12))
            .Concat(new[] { 0x25, 0x27, 0x26, 0x28, 0x24, 0x23, 0x21, 0x22, 0x2D, 0x2E }).ToArray();

    /// <summary>A key's name ("1", "A", "F5", "Right arrow"…), or null if it can't be chosen.</summary>
    public static string? KeyName(int key) => key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x7B => "F" + (key - 0x6F),
        0x25 => "Left arrow", 0x26 => "Up arrow", 0x27 => "Right arrow", 0x28 => "Down arrow",
        0x24 => "Home", 0x23 => "End", 0x21 => "Page Up", 0x22 => "Page Down", 0x2D => "Insert", 0x2E => "Delete",
        _ => null,
    };
}

/// <summary>"Notifications": which events show a notification from the tray icon (all off by default).</summary>
public sealed class NoticeSettings
{
    /// <summary>When it learns a new monitor for an app.</summary>
    public bool Learned { get; set; }
    /// <summary>When an app opens on another monitor because its own is off.</summary>
    public bool OpenedElsewhere { get; set; }
    /// <summary>When a monitor comes back and windows move back to it.</summary>
    public bool MonitorBack { get; set; }
    /// <summary>When it can't move an app that runs as administrator.</summary>
    public bool AdminApp { get; set; }
}

/// <summary>One line in Recent activity (kept in memory only).</summary>
public sealed class ActivityEntry
{
    public int Id { get; set; }
    /// <summary>Local time.</summary>
    public DateTime Time { get; set; }
    /// <summary>One of <see cref="ActivityKinds"/> (picks the icon).</summary>
    public string Kind { get; set; } = ActivityKinds.Info;
    /// <summary>What happened: "Moved Spotify to 2 · Side".</summary>
    public string Text { get; set; } = "";
    /// <summary>Why, in plain words: "Learned: you use it there".</summary>
    public string Reason { get; set; } = "";
    public bool CanUndo { get; set; }
    public bool Undone { get; set; }
}

/// <summary>What kind of event a Recent activity line is.</summary>
public static class ActivityKinds
{
    public const string Moved = "Moved", Learned = "Learned", SetByYou = "SetByYou", Skipped = "Skipped", Back = "Back", Info = "Info";
}

/// <summary>
/// A request from the settings window to the background helper, which knows what happens in the background (Recent
/// activity, which windows you moved yourself, keyboard shortcuts) and holds what's needed to undo a move.
/// </summary>
public sealed class HelperRequest
{
    public const string Status = "Status", Tidy = "Tidy", Gather = "Gather", MoveApp = "MoveApp", Undo = "Undo";
    public int Id { get; set; }
    /// <summary>One of the commands above.</summary>
    public string Command { get; set; } = Status;
    /// <summary>"Move open windows to their monitors": also windows you moved yourself.</summary>
    public bool IncludeMoved { get; set; }
    /// <summary>The Recent activity line to undo.</summary>
    public int ActivityId { get; set; }
    /// <summary>The app whose windows move (<see cref="MoveApp"/>), and its name.</summary>
    public string? AppKey { get; set; }
    public string? AppName { get; set; }
    /// <summary>Move only windows on this monitor (<see cref="Gather"/>; null = all).</summary>
    public string? FromDevice { get; set; }
    /// <summary>The monitor windows move to.</summary>
    public string? ToDevice { get; set; }
}

/// <summary>The helper's answer to a <see cref="HelperRequest"/>.</summary>
public sealed class HelperReply
{
    public int Id { get; set; }
    /// <summary>A problem to show (null = it worked).</summary>
    public string? Error { get; set; }
    /// <summary>How many windows would move (status) or moved.</summary>
    public int Count { get; set; }
    /// <summary>After an undo: this app was moved back twice, so ask whether to stop moving it.</summary>
    public string? StopMovingKey { get; set; }
    public string? StopMovingName { get; set; }
    public List<ActivityEntry> Recent { get; set; } = new();
    /// <summary>Keyboard shortcuts that couldn't be registered: an action, or an action + "+Shift".</summary>
    public List<string> HotKeyProblems { get; set; } = new();
}

/// <summary>
/// Passes <see cref="HelperRequest"/>s from the settings window to the helper and its answers back: two small files
/// next to the helper and an event that wakes the helper up. Nothing runs or polls when no request is made.
/// </summary>
public static class HelperChannel
{
    public const string EventName = @"Local\MonitorWindowControl.HelperRequest";
    private static string RequestFile => Path.Combine(Paths.HelperDir, "request.json");
    private static string ReplyFile => Path.Combine(Paths.HelperDir, "reply.json");

    public static bool WriteRequest(HelperRequest r) => Write(RequestFile, JsonSerializer.Serialize(r, SettingsJson.Default.HelperRequest));

    public static HelperRequest? ReadRequest() =>
        Read(RequestFile) is string json ? Try(() => JsonSerializer.Deserialize(json, SettingsJson.Default.HelperRequest)) : null;

    public static bool WriteReply(HelperReply r) => Write(ReplyFile, JsonSerializer.Serialize(r, SettingsJson.Default.HelperReply));

    public static HelperReply? ReadReply() =>
        Read(ReplyFile) is string json ? Try(() => JsonSerializer.Deserialize(json, SettingsJson.Default.HelperReply)) : null;

    /// <summary>
    /// Signalled by a launch shortcut (or group) that opens an app on a chosen monitor while the tray app or background
    /// helper places windows: it reads <see cref="ReadLaunch"/> then, once (nothing polls the file).
    /// </summary>
    public const string LaunchEventName = @"Local\MonitorWindowControl.LaunchOnce";
    private static string LaunchFile => Path.Combine(Paths.HelperDir, "launch.json");

    public static bool WriteLaunch(LaunchRequest r) => Write(LaunchFile, JsonSerializer.Serialize(r, SettingsJson.Default.LaunchRequest));

    public static LaunchRequest? ReadLaunch() =>
        Read(LaunchFile) is string json ? Try(() => JsonSerializer.Deserialize(json, SettingsJson.Default.LaunchRequest)) : null;

    private static T? Try<T>(Func<T?> f) where T : class
    {
        try { return f(); } catch (Exception) { return null; }
    }

    private static string? Read(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception) { return null; } // being written right now: read again shortly
    }

    /// <summary>Writes atomically (temp file + rename), so the other process never reads half a file.</summary>
    private static bool Write(string file, string json)
    {
        string tmp = file + "." + Environment.ProcessId + ".tmp";
        try
        {
            Directory.CreateDirectory(Paths.HelperDir);
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't pass a request to the helper: {ex.Message}");
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }
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
    /// <summary>
    /// true = apps it hasn't learned yet open on the monitor with the mouse pointer (then <see cref="MoveUnknownToMain"/>
    /// is also true, so an older version opens them on the main monitor).
    /// </summary>
    public bool UnknownFollowMouse { get; set; }
    /// <summary>"Open dialogs on the same monitor as their app".</summary>
    public bool MoveDialogsWithApp { get; set; } = true;
    /// <summary>"Pause while a game or presentation is running".</summary>
    public bool PauseForGames { get; set; } = true;
    /// <summary>"Also place apps that opened while signing in" (apps that start with Windows before this app does).</summary>
    public bool PlaceAppsOpenedAtSignIn { get; set; } = true;
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
    /// <summary>
    /// "When an app's monitor is off", for all apps: null or <see cref="FallbackModes.Main"/> = the main monitor,
    /// <see cref="FallbackModes.Monitor"/> = <see cref="FallbackMonitor"/>, <see cref="FallbackModes.Leave"/> = leave it.
    /// </summary>
    public string? Fallback { get; set; }
    /// <summary>The monitor for <see cref="FallbackModes.Monitor"/>.</summary>
    public MonitorRef? FallbackMonitor { get; set; }
    /// <summary>"When a monitor comes back": true = move windows that had to go elsewhere back to it; false = leave them.</summary>
    public bool MoveBackWhenMonitorReturns { get; set; } = true;
    /// <summary>"Put windows back after sleep": after waking up, windows go back where they were before sleep.</summary>
    public bool RestoreAfterSleep { get; set; } = true;
    /// <summary>"Keep my main monitor": the monitor to keep as main in Windows (null = off).</summary>
    public MonitorRef? KeepMainMonitor { get; set; }
    /// <summary>
    /// "Keep my main monitor" stopped because Windows kept making another monitor main (turning it off and on, or
    /// pressing Make main, starts it again). Saved, so the helper and the settings window agree.
    /// </summary>
    public bool KeepMainStopped { get; set; }
    /// <summary>"Keyboard shortcuts" (off by default).</summary>
    public HotKeySettings HotKeys { get; set; } = new();
    /// <summary>"Notifications" (all off by default).</summary>
    public NoticeSettings Notices { get; set; } = new();
    /// <summary>The monitor setups seen so far (see <see cref="MonitorSetup"/>).</summary>
    public List<MonitorSetup> Setups { get; set; } = new();
    /// <summary>The id of the usual setup: there, apps use their own choice; other setups can have their own monitors.</summary>
    public string? UsualSetup { get; set; }
    /// <summary>"App groups" (Apps page).</summary>
    public List<AppGroup> Groups { get; set; } = new();
    /// <summary>
    /// Only in a file made with "Export settings": the main monitor of the PC it came from, so "Import settings" can
    /// match it to this PC's main monitor. Never written to settings.json itself.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MonitorRef? ExportedMainMonitor { get; set; }

    public AppRule? Find(string key) =>
        Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Compile-time JSON code, so settings also load in the tiny native helper (no reflection there).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(MonitorHardware))]
[JsonSerializable(typeof(MonitorOptions))]
[JsonSerializable(typeof(WindowPosition))]
[JsonSerializable(typeof(AppRule))]
[JsonSerializable(typeof(HotKeySettings))]
[JsonSerializable(typeof(HotKeyChoice))]
[JsonSerializable(typeof(NoticeSettings))]
[JsonSerializable(typeof(ActivityEntry))]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperReply))]
[JsonSerializable(typeof(WindowRule))]
[JsonSerializable(typeof(MonitorSetup))]
[JsonSerializable(typeof(SetupHome))]
[JsonSerializable(typeof(AppGroup))]
[JsonSerializable(typeof(GroupApp))]
[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(LaunchItem))]
internal partial class SettingsJson : JsonSerializerContext { }

/// <summary>
/// settings.json is shared by the settings window and the helper, which may run at the same time.
/// Writes are atomic (temp file + rename); reads retry briefly if the other process is mid-write,
/// and a file that is only temporarily unreadable never resets your settings.
/// </summary>
public static class SettingsStore
{
    internal static string LockName = @"Local\MonitorWindowControl.Settings"; // (tests use their own: see Paths.UseFolderForTests)
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
            if (string.IsNullOrWhiteSpace(a.DisplayName)) a.DisplayName = a.Key; // (missing, null or empty: show the key)
            a.AllowedMonitors ??= new();
            a.AllowedMonitors.RemoveAll(m => m == null);
            foreach (var m in a.AllowedMonitors) CleanRef(m);
            a.MonitorHardware = MonitorHardware.Clean(a.MonitorHardware);
            (a.Fallback, a.FallbackMonitor) = CleanFallback(a.Fallback, a.FallbackMonitor);
            a.Place = PlaceModes.Normalize(a.Place);
            a.Positions ??= new();
            a.Positions.RemoveAll(p => p == null || p.Monitor == null || !p.Repair());
            foreach (var p in a.Positions) CleanRef(p.Monitor);
            a.WaitMs = AppTimings.NormalizeWait(a.WaitMs);
            a.CorrectForMs = AppTimings.NormalizeCorrect(a.CorrectForMs);
            if (a.FollowMouse && (a.NeverMove || a.FollowLastUsed)) a.FollowMouse = false; // one choice only
            a.MoreShortcuts ??= new();
            a.MoreShortcuts.RemoveAll(string.IsNullOrWhiteSpace);
            CleanWindowRules(a);
        }
        CleanSetups(s);
        CleanGroups(s);
        if (s.UnknownFollowMouse) s.MoveUnknownToMain = true;
        s.MonitorNicknames ??= new();
        s.MonitorNicknames.RemoveAll(n => n == null || n.Monitor == null);
        foreach (var n in s.MonitorNicknames) { CleanRef(n.Monitor); n.Nickname ??= ""; }
        s.MonitorOptions ??= new();
        s.MonitorOptions.RemoveAll(o => o == null || o.Monitor == null);
        var now = DateTime.UtcNow;
        foreach (var o in s.MonitorOptions)
        {
            CleanRef(o.Monitor);
            if (!o.IsOffAt(now)) { o.Off = false; o.OffUntilUtc = null; } // "for 1 hour" is over
        }
        s.MonitorOptions.RemoveAll(o => o.IsEmpty);
        (s.Fallback, s.FallbackMonitor) = CleanFallback(s.Fallback, s.FallbackMonitor);
        if (s.KeepMainMonitor != null) CleanRef(s.KeepMainMonitor);
        else s.KeepMainStopped = false;
        s.RunMode = RunModes.Normalize(s.RunMode);
        if (s.ExportedMainMonitor != null) CleanRef(s.ExportedMainMonitor);
        s.Notices ??= new();
        CleanHotKeys(s.HotKeys ??= new());
    }

    /// <summary>Rules for some windows: drops unknown kinds, empty texts and rules without a monitor that aren't "never move".</summary>
    private static void CleanWindowRules(AppRule a)
    {
        a.WindowRules ??= new();
        a.WindowRules.RemoveAll(w => w == null || !WindowMatches.IsKnown(w.Match) || string.IsNullOrWhiteSpace(w.Text)
                                     || (!w.NeverMove && w.Monitor == null));
        foreach (var w in a.WindowRules)
        {
            w.Text = w.Text.Trim();
            if (w.NeverMove) w.Monitor = null;
            else CleanRef(w.Monitor!);
        }
    }

    /// <summary>Monitor setups: drops entries without an id, duplicates, and homes without an app or monitor.</summary>
    private static void CleanSetups(AppSettings s)
    {
        s.Setups ??= new();
        s.Setups.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Id));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        s.Setups.RemoveAll(x => !seen.Add(x.Id));
        foreach (var x in s.Setups)
        {
            x.Monitors ??= new();
            x.Monitors.RemoveAll(m => m == null);
            if (string.IsNullOrWhiteSpace(x.Name)) x.Name = null;
            x.Homes ??= new();
            x.Homes.RemoveAll(h => h == null || string.IsNullOrWhiteSpace(h.Key) || IsEmptyRef(h.Monitor));
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            x.Homes.RemoveAll(h => !keys.Add(h.Key));
            foreach (var h in x.Homes) CleanRef(h.Monitor);
        }
        if (s.UsualSetup != null && !s.Setups.Any(x => x.Id == s.UsualSetup)) s.UsualSetup = s.Setups.FirstOrDefault()?.Id;
    }

    /// <summary>App groups: drops groups without an id or name, and apps without a key (each app once per group).</summary>
    private static void CleanGroups(AppSettings s)
    {
        s.Groups ??= new();
        s.Groups.RemoveAll(g => g == null || string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Name));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        s.Groups.RemoveAll(g => !ids.Add(g.Id));
        foreach (var g in s.Groups)
        {
            g.Apps ??= new();
            g.Apps.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Key));
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            g.Apps.RemoveAll(a => !keys.Add(a.Key));
            foreach (var a in g.Apps) if (a.Monitor != null) CleanRef(a.Monitor);
        }
    }

    /// <summary>Keyboard shortcuts: drops unknown actions, duplicates and keys that can't be a shortcut (they get their default again).</summary>
    private static void CleanHotKeys(HotKeySettings h)
    {
        h.Keys ??= new();
        h.Keys.RemoveAll(k => k == null || !HotKeyActions.IsKnown(k.Action) || !HotKeyActions.IsValid(k.Modifiers, k.Key));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        h.Keys.RemoveAll(k => !seen.Add(k.Action));
        foreach (var k in h.Keys) if (k.Key == 0) k.Modifiers = 0;
    }

    /// <summary>A known fallback mode with its monitor (only kept for <see cref="FallbackModes.Monitor"/>).</summary>
    private static (string? mode, MonitorRef? monitor) CleanFallback(string? mode, MonitorRef? monitor)
    {
        mode = FallbackModes.Normalize(mode, monitor);
        if (mode != FallbackModes.Monitor) return (mode, null);
        CleanRef(monitor!);
        return (mode, monitor);
    }

    /// <summary>True for a missing monitor, or one saved with nothing to find it by (no number, name or hardware).</summary>
    private static bool IsEmptyRef(MonitorRef? m) =>
        m == null || (string.IsNullOrWhiteSpace(m.Device) && string.IsNullOrWhiteSpace(m.Name) && (m.Hardware == null || m.Hardware.IsEmpty));

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
            WriteBackup(force: false); // the first save of the day keeps yesterday's settings
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

    // ------------------------------------------------------------------ backups, export and import

    /// <summary>Daily copies kept by <see cref="WriteBackup"/>.</summary>
    public const int BackupsKept = 5;
    /// <summary>Files bigger than this are never read as settings (a real one is a few hundred KB at most).</summary>
    public const long MaxImportBytes = 4_000_000;
    private static DateTime _backupDay;

    private static string BackupFile(DateTime day) => Path.Combine(Paths.Dir, $"settings.{day:yyyyMMdd}.json");

    /// <summary>
    /// Keeps a copy of settings.json as it is now: settings.YYYYMMDD.json, once a day (<paramref name="force"/>: now, even
    /// if today's copy exists, as "Forget all" and imports do). Only the newest <see cref="BackupsKept"/> are kept.
    /// Called while saving, holding the settings lock; it does nothing (no file access) after today's copy is made.
    /// </summary>
    public static void WriteBackup(bool force)
    {
        var today = DateTime.Today;
        if (!force && _backupDay == today) return;
        try
        {
            if (!File.Exists(Paths.SettingsFile) || _notReadYet) return;
            string file = BackupFile(today);
            if (force || !File.Exists(file)) File.Copy(Paths.SettingsFile, file, true);
            _backupDay = today;
            foreach (var old in ListBackups().Skip(BackupsKept))
                try { File.Delete(old.File); } catch { }
        }
        catch (Exception ex) { Log.Write("Could not keep a backup of the settings: " + ex.Message); }
    }

    /// <summary>The daily copies of settings.json, newest first.</summary>
    public static List<(DateTime Day, string File)> ListBackups()
    {
        var list = new List<(DateTime, string)>();
        try
        {
            if (!Directory.Exists(Paths.Dir)) return list;
            foreach (var f in Directory.EnumerateFiles(Paths.Dir, "settings.????????.json"))
            {
                string stamp = Path.GetFileName(f).Substring(9, 8);
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                                           System.Globalization.DateTimeStyles.None, out var day))
                    list.Add((day, f));
            }
        }
        catch { }
        return list.OrderByDescending(b => b.Item1).ToList();
    }

    /// <summary>
    /// Reads a settings file that may come from anywhere (an export, another PC, a backup): never bigger than
    /// <see cref="MaxImportBytes"/>, it must be valid settings with a known "how it runs", and it's repaired like
    /// settings.json is. Returns null with a plain <paramref name="error"/> if it can't be used.
    /// </summary>
    public static AppSettings? ReadUntrusted(string file, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) { error = "The file doesn't exist any more."; return null; }
            if (info.Length > MaxImportBytes) { error = "The file is too big to be Monitor Window Control settings."; return null; }
            string json = File.ReadAllText(file);
            var s = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings);
            if (s == null) { error = "The file is empty."; return null; }
            if (s.RunMode is not (RunModes.Tray or RunModes.Helper or RunModes.LaunchOnly))
            {
                error = "The file has a choice this version doesn't know, so it isn't used.";
                return null;
            }
            Clean(s);
            return s;
        }
        catch (JsonException) { error = "The file isn't Monitor Window Control settings, or it's damaged."; return null; }
        catch (Exception ex) { error = "The file couldn't be read: " + ex.Message; return null; }
    }

    /// <summary>A separate copy of the settings (changing it doesn't change the original).</summary>
    public static AppSettings Copy(AppSettings s) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings), SettingsJson.Default.AppSettings) ?? new AppSettings();

    /// <summary>Writes settings to a file of your choice ("Export settings"). Returns an error message, or null.</summary>
    public static string? Export(AppSettings s, string file)
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings));
            Log.Write($"Exported settings to {file}.");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
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

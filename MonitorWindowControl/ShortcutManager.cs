using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MonitorWindowControl;

/// <summary>
/// Launch-only mode: shortcuts that start an app through the helper, which puts its window on the right monitor
/// and exits. They live in Start menu → "Monitor Window Control" (pin them to the taskbar/Start as you like),
/// and optionally on the desktop. Your existing shortcuts are never changed.
/// </summary>
public static class ShortcutManager
{
    /// <summary>"Netflix (Store app)" → "Netflix"; strips characters Windows doesn't allow in file names.</summary>
    public static string ShortcutName(AppRule rule)
    {
        string name = string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.Key : rule.DisplayName;
        if (name.EndsWith(" (Store app)", StringComparison.Ordinal)) name = name[..^" (Store app)".Length];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Trim();
        if (name.Length > 60) name = name[..60].TrimEnd() + "…"; // long Store-app titles would break the path
        return name.Length == 0 ? "App" : name;
    }

    /// <summary>
    /// The shortcut's file name for this app: fixed once created (stored in <see cref="AppRule.ShortcutFile"/>, so a
    /// later rename of the app doesn't orphan it), and unique among your apps (two apps with the same name get
    /// their exe or a number added).
    /// </summary>
    public static string FileName(AppRule rule, IEnumerable<AppRule> all)
    {
        if (!string.IsNullOrEmpty(rule.ShortcutFile)) return rule.ShortcutFile!;
        var taken = new HashSet<string>(all.Where(a => a != rule && !string.IsNullOrEmpty(a.ShortcutFile)).Select(a => a.ShortcutFile!),
                                        StringComparer.OrdinalIgnoreCase);
        string name = ShortcutName(rule);
        if (!taken.Contains(name)) return name;
        string suffix = rule.Key.StartsWith(WindowInfo.WebAppKeyPrefix, StringComparison.Ordinal)
            ? "web app" : Path.GetFileNameWithoutExtension(rule.Key);
        foreach (char c in Path.GetInvalidFileNameChars()) suffix = suffix.Replace(c, ' ');
        string withKey = $"{name} ({suffix.Trim()})";
        if (!taken.Contains(withKey)) return withKey;
        for (int n = 2; ; n++) if (!taken.Contains($"{name} {n}")) return $"{name} {n}";
    }

    private static string StartMenuPath(string file) => Path.Combine(Paths.ShortcutsDir, file + ".lnk");
    private static string DesktopPath(string file) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), file + " (Monitor Window Control).lnk");

    public static bool Exists(AppRule rule) => rule.ShortcutFile != null && File.Exists(StartMenuPath(rule.ShortcutFile));

    /// <summary>Does this shortcut (a file name without .lnk) exist in Start menu → Monitor Window Control?</summary>
    public static bool FileExists(string file) => File.Exists(StartMenuPath(file));

    /// <summary>Is the app started from its program file (so a web address or file can be opened with it)?</summary>
    public static bool CanOpenWith(AppRule rule) => rule.LaunchTarget?.StartsWith("exe:", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A file name for a new shortcut, from the name you typed: without characters Windows doesn't allow, and not the
    /// same as another shortcut's (a number is added).
    /// </summary>
    public static string UniqueFileName(string wanted, AppSettings settings)
    {
        string name = wanted;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Trim().TrimEnd('.');
        if (name.Length > 80) name = name[..80].TrimEnd();
        if (name.Length == 0) name = "Shortcut";
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in settings.Apps)
        {
            if (a.ShortcutFile != null) taken.Add(a.ShortcutFile);
            foreach (var f in a.MoreShortcuts) taken.Add(f);
        }
        foreach (var g in settings.Groups) if (g.ShortcutFile != null) taken.Add(g.ShortcutFile);
        bool Free(string n) => !taken.Contains(n) && !File.Exists(StartMenuPath(n));
        if (Free(name)) return name;
        for (int n = 2; ; n++) if (Free($"{name} {n}")) return $"{name} {n}";
    }

    /// <summary>
    /// Creates the shortcut(s). Returns an error message, or null on success; <paramref name="fileName"/> is the name
    /// used (store it in the app's settings). With <paramref name="on"/> the app opens on that monitor (this once, each
    /// time the shortcut is used), with <paramref name="open"/> it opens that web address or file, and
    /// <paramref name="name"/> is the shortcut's own name (pass it for anything but the app's usual shortcut).
    /// </summary>
    public static string? Create(AppRule rule, IEnumerable<AppRule> all, bool alsoOnDesktop, out string fileName,
        MonitorRef? on = null, string? open = null, string? name = null)
    {
        fileName = name ?? FileName(rule, all);
        if (rule.LaunchTarget == null) return "Open the app once first, so Monitor Window Control learns how to start it.";
        if (open != null && (LaunchOpen.Clean(open) is not string cleaned || !CanOpenWith(rule)))
            return "That isn't a web address (starting with https://) or the full path of a file.";
        string? helper = HelperManager.EnsureExtracted();
        if (helper == null) return "The helper that the shortcut uses couldn't be unpacked (see the log).";

        string args = "--launch " + QuoteArg(rule.LaunchTarget) + " --key " + QuoteArg(rule.Key) + MonitorArgs(on);
        if (open != null) args += " --open " + QuoteArg(LaunchOpen.Clean(open)!);
        string icon = rule.LaunchTarget.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)
            ? rule.LaunchTarget.Substring(4)
            : helper; // Store apps: their icon lives inside the package; use ours
        string where = on == null ? "on its monitor" : $"on {on.Name ?? "the monitor you chose"}";
        string description = $"Opens {ShortcutName(rule)} {where} (Monitor Window Control).";
        return WriteBoth(fileName, helper, args, icon, description, alsoOnDesktop, $"[{rule.Key} → {rule.LaunchTarget}]");
    }

    /// <summary>Creates the launch shortcut for an app group ("Start Streaming"). Returns an error message, or null.</summary>
    public static string? CreateForGroup(AppGroup group, AppSettings settings, bool alsoOnDesktop, out string fileName)
    {
        fileName = group.ShortcutFile ?? UniqueFileName(group.Name, settings);
        string? helper = HelperManager.EnsureExtracted();
        if (helper == null) return "The helper that the shortcut uses couldn't be unpacked (see the log).";
        return WriteBoth(fileName, helper, "--group " + QuoteArg(group.Id), helper,
                         $"Opens the apps of {group.Name}, each on its monitor (Monitor Window Control).", alsoOnDesktop, $"[group {group.Id}]");
    }

    /// <summary>The arguments that tell the helper which monitor to use: its model name and hardware identity.</summary>
    private static string MonitorArgs(MonitorRef? on)
    {
        if (on == null) return "";
        string s = "";
        if (!string.IsNullOrEmpty(on.Name)) s += " --on-name " + QuoteArg(on.Name!);
        if (!string.IsNullOrEmpty(on.Hardware?.Model)) s += " --on-model " + QuoteArg(on.Hardware!.Model!);
        if (!string.IsNullOrEmpty(on.Hardware?.Serial)) s += " --on-serial " + QuoteArg(on.Hardware!.Serial!);
        if (!string.IsNullOrEmpty(on.Hardware?.DevicePath)) s += " --on-path " + QuoteArg(on.Hardware!.DevicePath!);
        return s;
    }

    private static string? WriteBoth(string fileName, string helper, string args, string icon, string description, bool alsoOnDesktop, string what)
    {
        try
        {
            Directory.CreateDirectory(Paths.ShortcutsDir);
            Write(StartMenuPath(fileName), helper, args, icon, description);
            Log.Write($"Created shortcut {StartMenuPath(fileName)}  {what}");
        }
        catch (Exception ex)
        {
            return "Couldn't create the Start menu shortcut: " + ex.Message;
        }

        if (alsoOnDesktop)
        {
            try
            {
                Write(DesktopPath(fileName), helper, args, icon, description);
                Log.Write($"Created shortcut {DesktopPath(fileName)}");
            }
            catch (Exception ex)
            {
                // Windows Defender's "Controlled folder access" protects the Desktop from unknown apps.
                return "The Start menu shortcut was created, but not the desktop one: " + ex.Message +
                       " (Windows Defender's \"Controlled folder access\" may be protecting your Desktop.)";
            }
        }
        return null;
    }

    /// <summary>Removes this app's shortcuts (its usual one and any others, Start menu and desktop) if they exist.</summary>
    public static void Delete(AppRule rule)
    {
        if (!string.IsNullOrEmpty(rule.ShortcutFile)) DeleteFile(rule.ShortcutFile!);
        foreach (var f in rule.MoreShortcuts) DeleteFile(f);
    }

    /// <summary>Removes an app group's shortcut (Start menu and desktop) if it exists.</summary>
    public static void Delete(AppGroup group)
    {
        if (!string.IsNullOrEmpty(group.ShortcutFile)) DeleteFile(group.ShortcutFile!);
    }

    /// <summary>Removes one shortcut (a file name without .lnk) from the Start menu and the desktop, if it's there.</summary>
    public static void DeleteFile(string file)
    {
        foreach (var p in new[] { StartMenuPath(file), DesktopPath(file) })
        {
            try { if (File.Exists(p)) { File.Delete(p); Log.Write($"Removed shortcut {p}"); } }
            catch (Exception ex) { Log.Write($"Could not remove shortcut {p}: {ex.Message}"); }
        }
        try
        {
            if (Directory.Exists(Paths.ShortcutsDir) && !Directory.EnumerateFileSystemEntries(Paths.ShortcutsDir).Any())
                Directory.Delete(Paths.ShortcutsDir);
        }
        catch { }
    }

    /// <summary>Quotes one command-line argument the way Windows programs parse them (handles quotes and backslashes).</summary>
    public static string QuoteArg(string s)
    {
        const char Backslash = (char)92;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == Backslash) { slashes++; continue; }
            if (c == '"') sb.Append(Backslash, slashes * 2 + 1);   // escape the backslashes and the quote
            else sb.Append(Backslash, slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append(Backslash, slashes * 2).Append('"');             // backslashes before the closing quote
        return sb.ToString();
    }

    private static void Write(string path, string target, string args, string icon, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(args);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            link.SetIconLocation(icon, 0);
            link.SetDescription(description);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

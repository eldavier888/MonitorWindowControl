using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MonitorWindowControl;

/// <summary>Who an app is: exe name, or window title for Store apps.</summary>
public sealed record AppIdentity(string Key, string DisplayName, bool IsStoreApp);

internal static class WindowInfo
{
    public enum Kind
    {
        /// <summary>A real app window we can work with.</summary>
        AppWindow,
        /// <summary>Might become an app window shortly (still cloaked or untitled).</summary>
        NotYet,
        /// <summary>Not visible (any more).</summary>
        Hidden,
        /// <summary>Never an app window (tool window, owned, shell, ours…).</summary>
        Never,
    }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "Shell_InputSwitchTopLevelWindow",
        "#32768", "#32770_SystemTray", "tooltips_class32", "SysShadow",
    };

    private const string StoreHostExe = "applicationframehost.exe";
    private static readonly Dictionary<string, string> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);

    // Our own programs: the settings window must never be moved or "learned" by the helper (and vice versa).
    private static readonly HashSet<string> OwnExes = new(StringComparer.OrdinalIgnoreCase)
        { "monitorwindowcontrol.exe", "monitorwindowcontrolhelper.exe" };
    private static bool IsOwnProcess(int pid, int ownPid)
    {
        if (pid == ownPid) return true;
        string? path = GetProcessPath(pid); // cheap; no cache, since process ids get reused
        return path != null && OwnExes.Contains(Path.GetFileName(path));
    }

    public static Kind Classify(IntPtr h, int ownPid)
    {
        if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) return Kind.Hidden;
        if (IsOwnProcess(GetPid(h), ownPid)) return Kind.Never;
        if (Native.GetAncestor(h, Native.GA_ROOT) != h) return Kind.Never;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return Kind.Never;

        long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((style & Native.WS_CHILD) != 0) return Kind.Never;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return Kind.Never;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0) return Kind.Never;
        if (ShellClasses.Contains(GetClass(h))) return Kind.Never;

        bool hasTitleBar = (style & Native.WS_CAPTION) == Native.WS_CAPTION
                           || (ex & Native.WS_EX_APPWINDOW) != 0
                           // apps that draw their own title bar (Electron etc.) keep the system menu + minimize button
                           || ((style & Native.WS_SYSMENU) != 0 && (style & Native.WS_MINIMIZEBOX) != 0);
        if (!hasTitleBar) return Kind.Never;

        if (IsCloaked(h)) return Kind.NotYet;
        if (GetTitle(h).Trim().Length == 0) return Kind.NotYet;
        return Kind.AppWindow;
    }

    public static AppIdentity? Identify(IntPtr h)
    {
        int pid = GetPid(h);
        string? path = GetProcessPath(pid);
        if (path == null) return null;
        string exe = Path.GetFileName(path).ToLowerInvariant();

        if (exe == StoreHostExe)
        {
            string title = GetTitle(h).Trim();
            if (title.Length == 0) return null;
            string key = title + " (Store app)";
            return new AppIdentity(key, key, true);
        }

        // Web apps (Netflix, Disney+, Prime Video, Crave… installed from the Store or a website) run inside the
        // browser's process, so the exe can't tell them apart. Each of their windows carries its own app id, though.
        if (WebAppId(h, exe) is string webId)
        {
            string name = CleanWebAppTitle(GetTitle(h));
            return new AppIdentity(WebAppKeyPrefix + webId, name.Length > 0 ? name : webId, false);
        }

        if (!DescriptionCache.TryGetValue(path, out var desc))
        {
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
            catch { desc = ""; }
            if (DescriptionCache.Count > 500) DescriptionCache.Clear();
            DescriptionCache[path] = desc;
        }
        return new AppIdentity(exe, string.IsNullOrWhiteSpace(desc) ? exe : desc, false);
    }

    /// <summary>
    /// How to start this window's app again, for launch-only shortcuts:
    /// "aumid:Package!App" for Store/packaged apps (they can't be started from their exe), else "exe:full path".
    /// Null if it can't be determined (e.g. the process is protected).
    /// </summary>
    public static string? GetLaunchTarget(IntPtr h)
    {
        int pid = GetPid(h);
        string? path = GetProcessPath(pid);
        if (path == null) return null;

        if (string.Equals(Path.GetFileName(path), StoreHostExe, StringComparison.OrdinalIgnoreCase))
        {
            // Store app: the frame belongs to ApplicationFrameHost; the app itself owns a child window.
            int childPid = 0;
            Native.EnumChildWindows(h, (c, _) =>
            {
                int p = GetPid(c);
                if (p != pid) { childPid = p; return false; }
                return true;
            }, IntPtr.Zero);
            string? storeId = childPid == 0 ? null : GetAppUserModelId(childPid);
            return storeId == null ? null : "aumid:" + storeId;
        }

        // Web apps inside a browser start through their own app id, like from the Start menu.
        if (WebAppId(h, Path.GetFileName(path)) is string webId) return "aumid:" + webId;

        string? id = GetAppUserModelId(pid); // packaged desktop apps (new Notepad, Paint, Terminal…)
        if (id != null) return "aumid:" + id;

        // Some apps show their windows from a sub-process (Steam's web helper, Java, embedded browsers):
        // starting that sub-process on its own doesn't start the app, so use the program that launched it.
        if (HostExes.Contains(Path.GetFileName(path)) && ParentProcessPath(pid) is string parent
            && !HostExes.Contains(Path.GetFileName(parent))
            && !string.Equals(Path.GetFileName(parent), "explorer.exe", StringComparison.OrdinalIgnoreCase))
            return "exe:" + parent;
        return "exe:" + path;
    }

    private static readonly HashSet<string> HostExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamwebhelper.exe", "msedgewebview2.exe", "javaw.exe", "java.exe", "rundll32.exe", "dllhost.exe",
        "cefsharp.browsersubprocess.exe", "qtwebengineprocess.exe", "electron.exe",
    };

    /// <summary>Full path of the process that started <paramref name="pid"/> (if it's still running).</summary>
    private static string? ParentProcessPath(int pid)
    {
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
        try
        {
            var e = new Native.PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESSENTRY32>() };
            if (!Native.Process32First(snap, ref e)) return null;
            do
            {
                if (e.th32ProcessID == (uint)pid)
                {
                    if (e.th32ParentProcessID == 0) return null;
                    // Windows reuses process ids: the "parent" only counts if it started before this process.
                    long child = StartTime(pid), parent = StartTime((int)e.th32ParentProcessID);
                    if (child == 0 || parent == 0 || parent > child) return null;
                    return GetProcessPath((int)e.th32ParentProcessID);
                }
            } while (Native.Process32Next(snap, ref e));
            return null;
        }
        finally { Native.CloseHandle(snap); }
    }

    private static long StartTime(int pid)
    {
        IntPtr hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hp == IntPtr.Zero) return 0;
        try { return Native.GetProcessTimes(hp, out long created, out _, out _, out _) ? created : 0; }
        finally { Native.CloseHandle(hp); }
    }

    private static string? GetAppUserModelId(int pid)
    {
        IntPtr hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            uint len = 256;
            var sb = new StringBuilder((int)len);
            return Native.GetApplicationUserModelId(hp, ref len, sb) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(hp); }
    }

    // ------------------------------------------------------------------ web apps inside a browser

    /// <summary>Key prefix for web apps: "app:" + their app id (e.g. "app:4DF9E0F8.Netflix_mcm4njqhnhss8!Netflix.App").</summary>
    /// <summary>
    /// Programs you run once (installers, uninstallers, updaters): not worth remembering, they'd only fill the list.
    /// They're still placed like any app you haven't set; you can also set one yourself.
    /// </summary>
    public static bool IsOneOffApp(string key)
    {
        if (key.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true; // setup programs unpacked to Temp
        if (!key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        string n = key[..^4].ToLowerInvariant();
        return n.Contains("setup") || n.Contains("install") || n.StartsWith("unins") || n.Contains("uninstall")
               || n == "update" || n.EndsWith("updater") || n.Contains("_update") || n == "msiexec";
    }

    public const string WebAppKeyPrefix = "app:";

    private static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase)
        { "msedge.exe", "chrome.exe", "brave.exe", "vivaldi.exe", "opera.exe" };

    /// <summary>
    /// The app id of a web-app window hosted by a browser, or null for ordinary browser windows.
    /// Installed web apps report a Store-style id ("Package!App") or a browser app id ("Chrome._crx_…");
    /// the browser itself reports just "MSEdge", "Chrome", etc.
    /// </summary>
    private static string? WebAppId(IntPtr h, string exe)
    {
        if (!BrowserExes.Contains(exe)) return null;
        string? id = GetWindowAppId(h);
        if (id == null) return null;
        return id.Contains('!') || id.Contains("_crx_", StringComparison.OrdinalIgnoreCase) ? id : null;
    }

    /// <summary>"Prime Video: Watch movies…" → "Prime Video"; "Crave | Watch HBO…" → "Crave".</summary>
    private static string CleanWebAppTitle(string title)
    {
        string t = title.Trim();
        foreach (var sep in new[] { " | ", ": ", " - ", " – ", " — " })
        {
            int i = t.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) t = t[..i];
        }
        return t.Trim();
    }

    /// <summary>The window's own app id (what the taskbar groups it by), via its shell property store.</summary>
    private static unsafe string? GetWindowAppId(IntPtr h)
    {
        var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"); // IPropertyStore
        if (Native.SHGetPropertyStoreForWindow(h, ref iid, out IntPtr store) != 0 || store == IntPtr.Zero) return null;
        try
        {
            // Call IPropertyStore::GetValue (vtable slot 5) directly; this also works in the native helper.
            void** vtbl = *(void***)store;
            var getValue = (delegate* unmanaged[Stdcall]<IntPtr, Native.PROPERTYKEY*, Native.PROPVARIANT*, int>)vtbl[5];
            var key = new Native.PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // AppUserModel.ID
            Native.PROPVARIANT value = default;
            if (getValue(store, &key, &value) != 0) return null;
            string? id = value.vt == 31 /* VT_LPWSTR */ ? System.Runtime.InteropServices.Marshal.PtrToStringUni(value.p) : null;
            Native.PropVariantClear(ref value);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        finally
        {
            void** vtbl = *(void***)store;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2])(store); // Release
        }
    }

    public static bool IsCloaked(IntPtr h)
        => Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static string GetTitle(IntPtr h)
    {
        int len = Native.GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int GetPid(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        return (int)pid;
    }

    public static string? GetProcessPath(int pid)
    {
        IntPtr hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(hp); }
    }

    /// <summary>
    /// When the process started (as a FILETIME), or 0 if Windows doesn't say. Tells a window handle that Windows reused
    /// for another program apart from the window it used to be.
    /// </summary>
    public static long GetProcessStartTime(int pid) => StartTime(pid);

    /// <summary>True if the process runs as admin (or we can't even look at it, which means the same for us).</summary>
    public static bool IsProcessElevated(int pid)
    {
        IntPtr hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hp == IntPtr.Zero) return true;
        try { return IsTokenElevated(hp) ?? true; }
        finally { Native.CloseHandle(hp); }
    }

    public static bool IsCurrentProcessElevated() => IsTokenElevated(Native.GetCurrentProcess()) ?? false;

    private static bool? IsTokenElevated(IntPtr processHandle)
    {
        if (!Native.OpenProcessToken(processHandle, Native.TOKEN_QUERY, out IntPtr tok)) return null;
        try
        {
            return Native.GetTokenInformation(tok, Native.TokenElevation, out int elevated, sizeof(int), out _)
                ? elevated != 0 : null;
        }
        finally { Native.CloseHandle(tok); }
    }

    public static List<IntPtr> EnumTopLevel()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}

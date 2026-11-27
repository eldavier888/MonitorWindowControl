using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowHome;

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

    public static Kind Classify(IntPtr h, int ownPid)
    {
        if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) return Kind.Hidden;
        if (GetPid(h) == ownPid) return Kind.Never;
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

        if (!DescriptionCache.TryGetValue(path, out var desc))
        {
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
            catch { desc = ""; }
            if (DescriptionCache.Count > 500) DescriptionCache.Clear();
            DescriptionCache[path] = desc;
        }
        return new AppIdentity(exe, string.IsNullOrWhiteSpace(desc) ? exe : desc, false);
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

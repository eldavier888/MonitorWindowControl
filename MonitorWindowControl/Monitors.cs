using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace MonitorWindowControl;

/// <summary>One connected display, identified by its device name (e.g. \\.\DISPLAY2).</summary>
public sealed class MonitorInfo
{
    public string DeviceName { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public bool IsPrimary { get; init; }
    public Native.RECT Bounds { get; init; }
    public Native.RECT Work { get; init; }

    /// <summary>Name shown to the user, e.g. "Odyssey G8 (main)".</summary>
    public string Label => FriendlyName + (IsPrimary ? " (main)" : "");

    public string Details => $"{Bounds.Width} × {Bounds.Height}   ·   {DeviceName.Replace(@"\\.\", "")}";

    public override string ToString() => Label;
}

public static class MonitorService
{
    /// <summary>Lists connected monitors (primary first) with friendly model names.</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var friendly = GetFriendlyNames();
        var raw = new List<Native.MONITORINFOEX>();

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(hMon, ref mi)) raw.Add(mi);
            return true;
        }, IntPtr.Zero);

        var list = new List<MonitorInfo>();
        foreach (var mi in raw)
        {
            friendly.TryGetValue(mi.szDevice, out var name);
            if (string.IsNullOrWhiteSpace(name))
                name = "Display " + mi.szDevice.Replace(@"\\.\DISPLAY", "");
            list.Add(new MonitorInfo
            {
                DeviceName = mi.szDevice,
                FriendlyName = name!,
                IsPrimary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                Bounds = mi.rcMonitor,
                Work = mi.rcWork,
            });
        }

        // Two identical models: add the display number so they can be told apart.
        foreach (var group in list.GroupBy(m => m.FriendlyName).Where(g => g.Count() > 1).ToList())
            foreach (var m in group.ToList())
            {
                int i = list.IndexOf(m);
                list[i] = new MonitorInfo
                {
                    DeviceName = m.DeviceName, IsPrimary = m.IsPrimary, Bounds = m.Bounds, Work = m.Work,
                    FriendlyName = $"{m.FriendlyName} ({m.DeviceName.Replace(@"\\.\", "")})",
                };
            }

        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
    }

    /// <summary>Gets the monitor that Windows says a window is on (never computed from coordinates).</summary>
    public static string? DeviceOfWindow(IntPtr hwnd)
    {
        IntPtr hMon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return null;
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        return Native.GetMonitorInfo(hMon, ref mi) ? mi.szDevice : null;
    }

    /// <summary>Maps GDI device names (\\.\DISPLAYn) to monitor model names via QueryDisplayConfig.</summary>
    private static Dictionary<string, string> GetFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Native.DISPLAYCONFIG_PATH_INFO[] paths;
            uint pathCount, modeCount;
            int err;
            int tries = 0;
            do
            {
                err = Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                if (err != 0) return result;
                paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
                err = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            } while (err == Native.ERROR_INSUFFICIENT_BUFFER && ++tries < 5);
            if (err != 0) return result;

            for (int i = 0; i < pathCount; i++)
            {
                var p = paths[i];
                var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = p.sourceInfo.adapterId,
                        id = p.sourceInfo.id,
                    }
                };
                if (Native.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var tgt = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = p.targetInfo.adapterId,
                        id = p.targetInfo.id,
                    }
                };
                if (Native.DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

                string name = tgt.monitorFriendlyDeviceName?.Trim() ?? "";
                if (name.Length == 0 && tgt.outputTechnology == 5) name = "HDMI display";
                if (name.Length > 0 && !result.ContainsKey(src.viewGdiDeviceName))
                    result[src.viewGdiDeviceName] = name;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not read monitor names: " + ex.Message);
        }
        return result;
    }
}

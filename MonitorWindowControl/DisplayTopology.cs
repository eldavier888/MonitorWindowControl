using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace MonitorWindowControl;

/// <summary>
/// "Disconnect from Windows" and "Connect" for one monitor, like Settings → Display → "Disconnect this display": the
/// monitor stays plugged in, but Windows stops using it. Uses QueryDisplayConfig and SetDisplayConfig; every change is
/// checked with SDC_VALIDATE first, saved in Windows' own display database, and can be put back exactly with
/// <see cref="Restore"/> (the 15 second "Keep these display settings?" question). The main monitor and the last
/// monitor in use are never disconnected. Only the settings app uses this; it needs no administrator rights.
/// </summary>
internal static class DisplayTopology
{
    private const uint QDC_ALL_PATHS = 0x00000001;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_VALIDATE = 0x00000040;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    private const uint ModeIdxInvalid = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, [In] Native.DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements, [In] Native.DISPLAYCONFIG_MODE_INFO[]? modeInfoArray, uint flags);

    /// <summary>The display configuration at one moment (the active paths and their modes), to put back exactly.</summary>
    public sealed record Snapshot(Native.DISPLAYCONFIG_PATH_INFO[] Paths, Native.DISPLAYCONFIG_MODE_INFO[] Modes);

    /// <summary>A monitor that's plugged in but that Windows doesn't use (disconnected here or in Settings → Display).</summary>
    public sealed record Disconnected(string Name, Native.LUID Adapter, uint TargetId)
    {
        public override string ToString() => Name;
    }

    /// <summary>Why a monitor can't be disconnected from Windows, or null if it can.</summary>
    public static string? WhyNotDisconnect(MonitorInfo m, IReadOnlyList<MonitorInfo> all)
    {
        if (m.IsPrimary) return "The main monitor can't be disconnected. Make another monitor the main one first.";
        if (all.Count < 2) return "This is the only monitor Windows uses, so it can't be disconnected.";
        if (m.IsDuplicated) return "Monitors that show the same picture can't be disconnected one at a time.";
        return null;
    }

    /// <summary>The configuration in use now (to go back to), or null if Windows doesn't say.</summary>
    public static Snapshot? Current() => Query(Native.QDC_ONLY_ACTIVE_PATHS);

    /// <summary>
    /// Stops Windows using one monitor (its display paths are switched off). <paramref name="before"/> is the configuration
    /// to put back with <see cref="Restore"/>. Returns an error message, or null.
    /// </summary>
    public static string? Disconnect(MonitorInfo m, IReadOnlyList<MonitorInfo> all, out Snapshot? before)
    {
        before = null;
        if (WhyNotDisconnect(m, all) is string why) return why;
        before = Current();
        if (before == null) return "Windows didn't say how the monitors are set up, so nothing was changed.";
        var keep = before.Paths.Where(p => !string.Equals(SourceName(p), m.DeviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (keep.Length == before.Paths.Length) return "Windows doesn't list this monitor right now, so nothing was changed.";
        if (keep.Length == 0) return "This is the only monitor Windows uses, so it can't be disconnected.";
        var (paths, modes) = Compact(keep, before.Modes);
        string? error = Apply(paths, modes, allowChanges: false);
        if (error == null) Log.Write($"Disconnected {m.Name} from Windows (you asked for it).");
        return error;
    }

    /// <summary>Lets Windows use a plugged-in monitor again (Windows picks its mode and position). Returns an error message, or null.</summary>
    public static string? Connect(Disconnected d, out Snapshot? before)
    {
        before = Current();
        var all = Query(QDC_ALL_PATHS);
        if (before == null || all == null) return "Windows didn't say how the monitors are set up, so nothing was changed.";
        var active = all.Paths.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0).ToList();
        // A path from a free output of the same graphics adapter to this monitor.
        var used = active.Where(p => SameLuid(p.sourceInfo.adapterId, d.Adapter)).Select(p => p.sourceInfo.id).ToHashSet();
        var path = all.Paths.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0 && p.targetInfo.targetAvailable != 0
                                        && SameLuid(p.targetInfo.adapterId, d.Adapter) && p.targetInfo.id == d.TargetId
                                        && !used.Contains(p.sourceInfo.id))
                            .Cast<Native.DISPLAYCONFIG_PATH_INFO?>().FirstOrDefault();
        if (path is not Native.DISPLAYCONFIG_PATH_INFO add) return "Windows has no free output for this monitor right now.";
        add.flags |= DISPLAYCONFIG_PATH_ACTIVE;
        add.sourceInfo.modeInfoIdx = ModeIdxInvalid;
        add.targetInfo.modeInfoIdx = ModeIdxInvalid;
        var (paths, modes) = Compact(active.Append(add).ToArray(), all.Modes);
        string? error = Apply(paths, modes, allowChanges: true);
        if (error == null) Log.Write($"Connected {d.Name} to Windows again (you asked for it).");
        return error;
    }

    /// <summary>Puts a configuration back exactly as it was ("Revert"). Returns an error message, or null.</summary>
    public static string? Restore(Snapshot s)
    {
        string? error = Apply(s.Paths, s.Modes, allowChanges: false);
        if (error != null) error = Apply(s.Paths, s.Modes, allowChanges: true); // a driver may need small adjustments
        Log.Write(error == null ? "Put the previous display settings back." : "Could not put the previous display settings back: " + error);
        return error;
    }

    /// <summary>Monitors that are plugged in but not used by Windows.</summary>
    public static List<Disconnected> ListDisconnected()
    {
        var result = new List<Disconnected>();
        var all = Query(QDC_ALL_PATHS);
        if (all == null) return result;
        var activeTargets = all.Paths.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0)
                               .Select(p => (Luid(p.targetInfo.adapterId), p.targetInfo.id)).ToHashSet();
        var seen = new HashSet<(long, uint)>();
        foreach (var p in all.Paths)
        {
            var key = (Luid(p.targetInfo.adapterId), p.targetInfo.id);
            if (p.targetInfo.targetAvailable == 0 || activeTargets.Contains(key) || !seen.Add(key)) continue;
            if (TargetName(p.targetInfo.adapterId, p.targetInfo.id) is not string name) continue; // (an empty connector)
            result.Add(new Disconnected(name, p.targetInfo.adapterId, p.targetInfo.id));
        }
        return result;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Checks the configuration with SDC_VALIDATE, then applies it and saves it in Windows' display database.</summary>
    private static string? Apply(Native.DISPLAYCONFIG_PATH_INFO[] paths, Native.DISPLAYCONFIG_MODE_INFO[] modes, bool allowChanges)
    {
        uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | (allowChanges ? SDC_ALLOW_CHANGES : 0);
        int check = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags | SDC_VALIDATE);
        if (check != 0) return $"Windows doesn't accept this change (error {check}), so nothing was changed.";
        int result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags | SDC_APPLY | SDC_SAVE_TO_DATABASE);
        return result == 0 ? null : $"Windows couldn't change the display settings (error {result}).";
    }

    /// <summary>The paths with only the modes they use, renumbered (SetDisplayConfig refuses modes no path uses).</summary>
    private static (Native.DISPLAYCONFIG_PATH_INFO[], Native.DISPLAYCONFIG_MODE_INFO[]) Compact(
        Native.DISPLAYCONFIG_PATH_INFO[] paths, Native.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var newModes = new List<Native.DISPLAYCONFIG_MODE_INFO>();
        var map = new Dictionary<uint, uint>();
        uint Remap(uint idx)
        {
            if (idx == ModeIdxInvalid || idx >= modes.Length) return ModeIdxInvalid;
            if (!map.TryGetValue(idx, out uint n)) { n = (uint)newModes.Count; newModes.Add(modes[idx]); map[idx] = n; }
            return n;
        }
        var result = paths.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            result[i].sourceInfo.modeInfoIdx = Remap(result[i].sourceInfo.modeInfoIdx);
            result[i].targetInfo.modeInfoIdx = Remap(result[i].targetInfo.modeInfoIdx);
        }
        return (result, newModes.ToArray());
    }

    private static Snapshot? Query(uint flags)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Native.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount) != 0) return null;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
            int err = Native.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err == Native.ERROR_INSUFFICIENT_BUFFER) continue; // monitors changed in between
            if (err != 0) return null;
            return new Snapshot(paths.Take((int)pathCount).ToArray(), modes.Take((int)modeCount).ToArray());
        }
        return null;
    }

    /// <summary>The GDI name (\\.\DISPLAYn) of a path's source, or null.</summary>
    private static string? SourceName(Native.DISPLAYCONFIG_PATH_INFO p)
    {
        var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id,
            },
        };
        return Native.DisplayConfigGetDeviceInfo(ref src) == 0 ? src.viewGdiDeviceName : null;
    }

    /// <summary>A monitor's model name, or null if Windows has none (nothing plugged in there).</summary>
    private static string? TargetName(Native.LUID adapter, uint id)
    {
        var tgt = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapter, id = id,
            },
        };
        if (Native.DisplayConfigGetDeviceInfo(ref tgt) != 0) return null;
        string name = tgt.monitorFriendlyDeviceName?.Trim() ?? "";
        return name.Length > 0 ? name : string.IsNullOrEmpty(tgt.monitorDevicePath) ? null : "Monitor";
    }

    private static long Luid(Native.LUID l) => ((long)l.HighPart << 32) | l.LowPart;
    private static bool SameLuid(Native.LUID a, Native.LUID b) => a.LowPart == b.LowPart && a.HighPart == b.HighPart;
}

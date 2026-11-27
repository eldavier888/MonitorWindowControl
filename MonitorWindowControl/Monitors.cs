using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MonitorWindowControl;

/// <summary>
/// One physical monitor (a display "target" in Windows). A <see cref="MonitorInfo"/> usually has one; it has two or
/// more when Windows duplicates (mirrors) the same picture on several monitors.
/// </summary>
public sealed class MonitorTarget
{
    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values that matter here.
    public const uint OutputOther = 0xFFFFFFFF, OutputMiracast = 15, OutputIndirectWired = 16, OutputIndirectVirtual = 17;

    /// <summary>The model name (with " (DISPLAYn)" added when two connected monitors share it).</summary>
    public string FriendlyName { get; set; } = "";
    /// <summary>Windows' device path for this monitor on its port, or null.</summary>
    public string? DevicePath { get; init; }
    /// <summary>EDID manufacturer and product code ("4C2D-0F2E"), or null.</summary>
    public string? Model { get; init; }
    /// <summary>EDID serial number, or null when the monitor doesn't report one.</summary>
    public string? Serial { get; init; }
    /// <summary>How it's connected (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY).</summary>
    public uint OutputTechnology { get; init; } = OutputOther;

    /// <summary>Your own name for it, or null (set by the engine).</summary>
    public string? Nickname { get; set; }
    /// <summary>Your own number for it, or null (set by the engine).</summary>
    public int? Number { get; set; }

    /// <summary>Your name for it, else its model name.</summary>
    public string ShownName => string.IsNullOrWhiteSpace(Nickname) ? FriendlyName : Nickname!;

    public MonitorHardware Hardware => new() { DevicePath = DevicePath, Model = Model, Serial = Serial };

    /// <summary>"HDMI", "DisplayPort"… ("" when Windows doesn't say).</summary>
    public string Connection => MonitorService.ConnectionName(OutputTechnology);

    /// <summary>Wireless (Miracast) and virtual monitors come and go: by default they're temporary.</summary>
    public bool IsTemporaryByDefault => OutputTechnology is OutputMiracast or OutputIndirectVirtual;

    /// <summary>Kinds of monitor that can be marked temporary (wireless, virtual, and USB display adapters such as DisplayLink).</summary>
    public bool MayBeTemporary => OutputTechnology is OutputMiracast or OutputIndirectWired or OutputIndirectVirtual;
}

/// <summary>One connected display, identified by its device name (\\.\DISPLAY2, for example).</summary>
public sealed class MonitorInfo
{
    public string DeviceName { get; init; } = "";
    public bool IsPrimary { get; init; }
    public Native.RECT Bounds { get; init; }
    public Native.RECT Work { get; init; }

    /// <summary>The physical monitor(s) showing this display: one, or several when duplicated. Never empty.</summary>
    public IReadOnlyList<MonitorTarget> Targets { get; init; } = new[] { new MonitorTarget() };

    /// <summary>True when Windows shows this display on more than one monitor (Win+P "Duplicate").</summary>
    public bool IsDuplicated => Targets.Count > 1;

    /// <summary>The model name of its (first) monitor: what settings save to find it again.</summary>
    public string FriendlyName => Targets[0].FriendlyName;

    /// <summary>The hardware identity of its (first) monitor: what settings save to find it again.</summary>
    public MonitorHardware Hardware => Targets[0].Hardware;

    /// <summary>Your own name for it (set with "Name &amp; number"), or null.</summary>
    public string? Nickname
    {
        get => Targets[0].Nickname;
        set => Targets[0].Nickname = value;
    }

    /// <summary>Your own number for it (the first one, if duplicated monitors both have one), or null.</summary>
    public int? Number => Targets.Select(t => t.Number).FirstOrDefault(n => n != null);

    /// <summary>Temporary monitor: apps aren't learned here and it's never used instead of a monitor that's off (set by the engine).</summary>
    public bool IsTemporary { get; set; }

    /// <summary>How it's told apart from another monitor of the same model, or null (set by the engine).</summary>
    public string? IdentityNote { get; set; }

    /// <summary>"Treat as switched off" is on: new windows open elsewhere and nothing is learned here (set by the engine).</summary>
    public bool IsSwitchedOff { get; set; }

    /// <summary>When "switched off" ends by itself, or null for "until I turn it back on" (set by the engine).</summary>
    public DateTime? SwitchedOffUntilUtc { get; set; }

    /// <summary>"What can open here: only apps I choose" (set by the engine).</summary>
    public bool OnlyChosenApps { get; set; }

    /// <summary>Another monitor is connected too, so choices that send windows elsewhere make sense (set by the engine).</summary>
    public bool HasOtherMonitors { get; set; }

    /// <summary>Show "Treat as switched off" and "What can open here" for this monitor.</summary>
    public bool ShowMonitorChoices => HasOtherMonitors && !IsDuplicated;

    /// <summary>The "Treat as switched off" choice: 0 = no, 1 = until I turn it back on, 2 = for a while.</summary>
    public int SwitchedOffIndex => !IsSwitchedOff ? 0 : SwitchedOffUntilUtc == null ? 1 : 2;

    /// <summary>The "What can open here" choice: 0 = any app, 1 = only apps I choose.</summary>
    public int OpenHereIndex => OnlyChosenApps ? 1 : 0;

    /// <summary>The description under "Treat as switched off".</summary>
    public string SwitchedOffNote => !IsSwitchedOff
        ? "For a TV that stays connected when you turn it off with its remote. New windows open on another monitor instead, and move back here when you choose No."
        : SwitchedOffUntilUtc is DateTime until
            ? $"Switched off (set by you) until {until.ToLocalTime():t}. New windows open on another monitor, and nothing is learned here."
            : "Switched off (set by you) until you turn it back on. New windows open on another monitor, and nothing is learned here.";

    /// <summary>The description under "What can open here".</summary>
    public string OpenHereNote => OnlyChosenApps
        ? "Only apps you set to open here on the Apps page open here. Other apps aren't learned here, and their new windows move to their own monitor or the main monitor."
        : "Choose \"Only apps I choose\" to keep this monitor for a few apps, like streaming apps on a TV.";

    /// <summary>The name to show: "2 · TV" style when numbered, your name for it, else the model name.</summary>
    public string Name => (Number is int n ? $"{n} · " : "") +
        (IsDuplicated ? string.Join(" + ", Targets.Select(t => t.ShownName)) + " (duplicated)" : Targets[0].ShownName);

    /// <summary>Name shown to the user, for example "1 · Odyssey G8 (main)".</summary>
    public string Label => Name + (IsPrimary ? " (main)" : "");

    /// <summary>The card's description: model, size, connection, Windows' number, and notes.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!IsDuplicated && !string.IsNullOrWhiteSpace(Nickname)) parts.Add(FriendlyName);
            parts.Add($"{Bounds.Width} × {Bounds.Height}");
            string connection = string.Join(" + ", Targets.Select(t => t.Connection).Where(c => c.Length > 0));
            if (connection.Length > 0) parts.Add(connection);
            parts.Add($"Windows display {DeviceName.Replace(@"\\.\DISPLAY", "")}");
            string text = string.Join("   ·   ", parts);
            if (IsDuplicated)
                text += "\nThese monitors show the same picture (Duplicate in Windows). Apps aren't learned while monitors are duplicated.";
            if (IsTemporary) text += "\nTemporary monitor: apps aren't learned here.";
            if (IsSwitchedOff) text += "\nSwitched off (set by you): new windows open on another monitor.";
            if (OnlyChosenApps) text += "\nOnly apps you choose open here.";
            if (IdentityNote != null) text += "\n" + IdentityNote;
            return text;
        }
    }

    /// <summary>
    /// Show the "Apps can open here" switch: wireless, virtual and USB monitors, but not the main one (unless it's
    /// temporary already, so you can still change that).
    /// </summary>
    public bool ShowTemporaryOption => !IsDuplicated && Targets[0].MayBeTemporary && (!IsPrimary || IsTemporary);

    /// <summary>The opposite of <see cref="IsTemporary"/>, for the switch.</summary>
    public bool AppsCanOpenHere => !IsTemporary;

    /// <summary>Duplicated monitors can't get their own name and number until they're extended again.</summary>
    public bool CanRename => !IsDuplicated;

    public override string ToString() => Label;
}

public static class MonitorService
{
    // DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS: the EDID manufacturer and product code are valid.
    private const uint EdidIdsValid = 0x2;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, string?> SerialCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists connected monitors (primary first) with friendly model names and hardware identity.</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var targets = GetTargets();
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
            string generic = "Display " + mi.szDevice.Replace(@"\\.\DISPLAY", "");
            if (!targets.TryGetValue(mi.szDevice, out var found) || found.Count == 0)
                found = new List<MonitorTarget> { new() { FriendlyName = generic } };
            foreach (var t in found.Where(t => string.IsNullOrWhiteSpace(t.FriendlyName))) t.FriendlyName = generic;
            list.Add(new MonitorInfo
            {
                DeviceName = mi.szDevice,
                IsPrimary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                Bounds = mi.rcMonitor,
                Work = mi.rcWork,
                Targets = found,
            });
        }

        // Two identical models on different displays: add the display number so they can be told apart.
        var all = list.SelectMany(m => m.Targets.Select(t => (m, t))).ToList();
        foreach (var group in all.GroupBy(x => x.t.FriendlyName).Where(g => g.Select(x => x.m.DeviceName).Distinct().Count() > 1).ToList())
            foreach (var (m, t) in group)
                t.FriendlyName = $"{t.FriendlyName} ({m.DeviceName.Replace(@"\\.\", "")})";

        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
    }

    /// <summary>
    /// What decides where windows can go: which displays exist, where, which is main, their work areas, and which
    /// physical monitors show them. Names and connection types don't count, and neither do refresh rate, HDR or
    /// colour changes, which change none of this.
    /// </summary>
    public static string LayoutSignature(IEnumerable<MonitorInfo> list) =>
        string.Join("|", list.OrderBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase).Select(m =>
            $"{m.DeviceName}{(m.IsPrimary ? "*" : "")}@{m.Bounds}/{m.Work}#" +
            string.Join("+", m.Targets.Select(t => t.DevicePath ?? t.Model ?? ""))));

    /// <summary>Forgets the EDID serial numbers read so far (call when monitors change: another monitor may be on that port now).</summary>
    public static void ClearIdentityCache()
    {
        lock (CacheGate) SerialCache.Clear();
    }

    /// <summary>A plain name for how a monitor is connected ("" when Windows doesn't say or it's unusual).</summary>
    public static string ConnectionName(uint outputTechnology) => outputTechnology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        10 => "DisplayPort",
        6 or 11 or 13 or 0x80000000 => "Built-in",
        MonitorTarget.OutputMiracast => "Wireless",
        MonitorTarget.OutputIndirectWired => "USB",
        MonitorTarget.OutputIndirectVirtual => "Virtual",
        18 => "USB-C",
        _ => "",
    };

    /// <summary>Gets the monitor that Windows says a window is on (never computed from coordinates).</summary>
    public static string? DeviceOfWindow(IntPtr hwnd)
    {
        IntPtr hMon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return null;
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        return Native.GetMonitorInfo(hMon, ref mi) ? mi.szDevice : null;
    }

    /// <summary>
    /// Reads every active display path with QueryDisplayConfig and groups the monitors by the GDI display
    /// (\\.\DISPLAYn) they show. A display shown on two monitors (duplicated) gets both.
    /// </summary>
    private static Dictionary<string, List<MonitorTarget>> GetTargets()
    {
        var result = new Dictionary<string, List<MonitorTarget>>(StringComparer.OrdinalIgnoreCase);
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
                string? path = string.IsNullOrWhiteSpace(tgt.monitorDevicePath) ? null : tgt.monitorDevicePath.Trim();
                if (!result.TryGetValue(src.viewGdiDeviceName, out var group))
                    result[src.viewGdiDeviceName] = group = new List<MonitorTarget>();
                group.Add(new MonitorTarget
                {
                    FriendlyName = name,
                    DevicePath = path,
                    Model = (tgt.flags & EdidIdsValid) != 0 ? $"{tgt.edidManufactureId:X4}-{tgt.edidProductCodeId:X4}" : null,
                    Serial = path == null ? null : SerialOf(path),
                    OutputTechnology = tgt.outputTechnology,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not read monitor names: " + ex.Message);
        }
        return result;
    }

    /// <summary>The EDID serial number of the monitor with this device path (read once, then cached).</summary>
    private static string? SerialOf(string devicePath)
    {
        lock (CacheGate)
        {
            if (SerialCache.TryGetValue(devicePath, out var cached)) return cached;
        }
        string? serial = null;
        try
        {
            // \\?\DISPLAY#SAM0F2E#5&2a5f1b6c&0&UID4352#{e6f07b5f-...} → Enum\DISPLAY\SAM0F2E\5&2a5f1b6c&0&UID4352
            var parts = devicePath.Split('#');
            if (parts.Length >= 3 && parts[1].Length > 0 && parts[2].Length > 0
                && parts[1].IndexOf('\\') < 0 && parts[2].IndexOf('\\') < 0)
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
                if (key?.GetValue("EDID") is byte[] edid) serial = ParseEdidSerial(edid);
            }
        }
        catch { /* not readable: the other identity fields still work */ }
        lock (CacheGate) SerialCache[devicePath] = serial;
        return serial;
    }

    /// <summary>
    /// The serial number in an EDID block: the text serial (descriptor 0xFF) if there is one, else the 32-bit number
    /// at bytes 12–15. Null when neither is set (many TVs and cheap monitors report 0).
    /// </summary>
    public static string? ParseEdidSerial(byte[] edid)
    {
        if (edid.Length < 128) return null;
        for (int d = 54; d + 18 <= 126; d += 18)
        {
            if (edid[d] != 0 || edid[d + 1] != 0 || edid[d + 3] != 0xFF) continue;
            var chars = new List<char>();
            for (int i = d + 5; i < d + 18 && edid[i] != 0x0A && edid[i] != 0; i++)
                if (edid[i] >= 0x20 && edid[i] < 0x7F) chars.Add((char)edid[i]);
            string text = new string(chars.ToArray()).Trim();
            if (text.Length > 0 && text.Any(c => c != '0' && c != ' ')) return text;
        }
        uint number = BitConverter.ToUInt32(edid, 12);
        return number == 0 || number == 0xFFFFFFFF ? null : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Finds a saved monitor among the connected ones. In order: the port it's plugged into (device path), then its model
/// and serial number, then the model name and Windows' display number (what older versions saved). Pure logic, so it
/// can be tested without real monitors.
/// </summary>
public static class MonitorMatcher
{
    /// <summary>The connected display showing the saved monitor, or null if it isn't connected.</summary>
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> monitors, string? name, string? device, MonitorHardware? hw) =>
        FindTarget(monitors, name, device, hw)?.Monitor;

    /// <summary>The connected physical monitor matching the saved one (and the display it shows), or null.</summary>
    public static (MonitorInfo Monitor, MonitorTarget Target)? FindTarget(IReadOnlyList<MonitorInfo> monitors, string? name,
                                                                         string? device, MonitorHardware? hw)
    {
        var all = monitors.SelectMany(m => m.Targets.Select(t => (m, t))).ToList();
        hw = hw == null || hw.IsEmpty ? null : hw;

        // 1. The same port, unless the serial number shows it's another monitor of that model plugged in there now.
        if (!string.IsNullOrEmpty(hw?.DevicePath))
            foreach (var x in all)
                if (Eq(x.t.DevicePath, hw!.DevicePath) && !Contradicts(hw, x.t)) return x;

        // 2. The same model and serial number (the cable moved to another port).
        if (hw != null && !string.IsNullOrEmpty(hw.Model) && !string.IsNullOrEmpty(hw.Serial))
        {
            var hits = all.Where(x => Eq(x.t.Model, hw.Model) && x.t.Serial == hw.Serial).ToList();
            if (hits.Count == 1) return hits[0];
        }

        // 3. Model name and display number, never picking a monitor that is known to be a different one.
        return ByNameAndDevice(all.Where(x => !Contradicts(hw, x.t)).ToList(), name, device);
    }

    /// <summary>True when the saved identity proves this is a different monitor (another model, or another serial number).</summary>
    private static bool Contradicts(MonitorHardware? hw, MonitorTarget t)
    {
        if (hw == null || string.IsNullOrEmpty(hw.Model) || string.IsNullOrEmpty(t.Model)) return false;
        if (!Eq(hw.Model, t.Model)) return true;
        return !string.IsNullOrEmpty(hw.Serial) && !string.IsNullOrEmpty(t.Serial) && hw.Serial != t.Serial;
    }

    /// <summary>
    /// What older versions did: the model name wins, as it survives Windows renumbering displays. The saved device
    /// ("DISPLAY3") is only trusted when no name was saved, when Windows couldn't read a model name, or for two
    /// identical models; otherwise a monitor that isn't connected stays "not connected".
    /// </summary>
    private static (MonitorInfo Monitor, MonitorTarget Target)? ByNameAndDevice(List<(MonitorInfo m, MonitorTarget t)> all,
                                                                              string? name, string? device)
    {
        static bool Generic(string n) => n.StartsWith("Display ", StringComparison.Ordinal);
        static string Model(string n) { int i = n.LastIndexOf(" (DISPLAY", StringComparison.Ordinal); return i > 0 ? n[..i] : n; }

        var byDevice = all.Where(x => Eq(x.m.DeviceName, device)).ToList();
        if (string.IsNullOrEmpty(name)) return byDevice.Count > 0 ? byDevice[0] : null;
        var byName = all.Where(x => x.t.FriendlyName == name).ToList();
        if (byName.Count > 0 && byName.All(x => ReferenceEquals(x.m, byName[0].m))) return byName[0];
        if (byDevice.Count == 0) return null;
        if (byName.Count > 1)                                                          // identical models: trust the number
            return byName.Where(x => ReferenceEquals(x.m, byDevice[0].m)).Select(x => ((MonitorInfo, MonitorTarget)?)x)
                         .FirstOrDefault() ?? byDevice[0];
        if (Generic(name)) return byDevice[0];                                         // no model name to go by
        foreach (var x in byDevice) if (Generic(x.t.FriendlyName)) return x;
        foreach (var x in byDevice) if (Model(x.t.FriendlyName) == Model(name)) return x; // same model, renumbered
        return null;
    }

    private static bool Eq(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace MonitorWindowControl;

/// <summary>
/// "Import settings": what an exported file contains, which of this PC's monitors each of its monitors is (by hardware
/// identity, then model name, then main to main), and how Replace and Merge combine it with your current settings.
/// The file itself was already read and repaired by <see cref="SettingsStore.ReadUntrusted"/>.
/// </summary>
public static class SettingsTransfer
{
    /// <summary>A monitor the imported file refers to, and the monitor of this PC it is (null = not connected now).</summary>
    public sealed class OldMonitor
    {
        public MonitorRef Ref { get; init; } = new();
        /// <summary>How it's shown: "TV" (its name on the other PC) or its model name.</summary>
        public string Label { get; init; } = "";
        public MonitorInfo? Match { get; set; }
        /// <summary>True when it was found by itself (no need to ask).</summary>
        public bool Found { get; init; }
    }

    /// <summary>"12 apps (4 set by you), 3 monitor names".</summary>
    public static string Summary(AppSettings s)
    {
        int apps = s.Apps.Count, chosen = s.Apps.Count(a => a.Pinned || a.NeverMove);
        int names = s.MonitorNicknames.Count(n => !string.IsNullOrWhiteSpace(n.Nickname) || n.Number != null);
        string text = apps == 1 ? "1 app" : $"{apps} apps";
        if (chosen > 0) text += $" ({chosen} set by you)";
        return text + (names == 1 ? ", 1 monitor name" : $", {names} monitor names");
    }

    /// <summary>Every monitor reference in the settings (not the app rules' own monitor, see <see cref="RuleRef"/>).</summary>
    private static IEnumerable<MonitorRef> Refs(AppSettings s) =>
        s.Apps.SelectMany(a => a.AllowedMonitors)
            .Concat(s.Apps.Select(a => a.FallbackMonitor).OfType<MonitorRef>())
            .Concat(s.Apps.SelectMany(a => a.Positions).Select(p => p.Monitor))
            .Concat(s.MonitorNicknames.Select(n => n.Monitor))
            .Concat(s.MonitorOptions.Select(o => o.Monitor))
            .Concat(s.Apps.SelectMany(a => a.WindowRules).Select(w => w.Monitor).OfType<MonitorRef>())
            .Concat(s.Groups.SelectMany(g => g.Apps).Select(a => a.Monitor).OfType<MonitorRef>())
            .Concat(s.Setups.SelectMany(x => x.Homes).Select(h => h.Monitor))
            .Concat(new[] { s.FallbackMonitor, s.ExportedMainMonitor }.OfType<MonitorRef>());

    /// <summary>An app rule's monitor as a reference (null if it has none).</summary>
    private static MonitorRef? RuleRef(AppRule r) =>
        r.MonitorDevice == null && r.MonitorName == null && r.MonitorHardware == null ? null
            : new MonitorRef { Device = r.MonitorDevice ?? "", Name = r.MonitorName, Hardware = r.MonitorHardware };

    /// <summary>What tells one saved monitor from another: its serial number, else its port, else its model name and number.</summary>
    private static string Key(MonitorRef r)
    {
        var h = r.Hardware;
        if (!string.IsNullOrEmpty(h?.Model) && !string.IsNullOrEmpty(h!.Serial)) return $"s:{h.Model}|{h.Serial}".ToUpperInvariant();
        if (!string.IsNullOrEmpty(h?.DevicePath)) return "p:" + h!.DevicePath!.ToUpperInvariant();
        return $"n:{r.Name}|{r.Device}".ToUpperInvariant();
    }

    /// <summary>
    /// The monitors the imported file refers to, each matched to one of this PC's monitors if possible: the same
    /// monitor (serial number or port), else the only monitor with that model name, else main to main.
    /// </summary>
    public static List<OldMonitor> FindMonitors(AppSettings imported, IReadOnlyList<MonitorInfo> monitors)
    {
        var refs = Refs(imported).Concat(imported.Apps.Select(RuleRef).OfType<MonitorRef>()).ToList();
        string? mainKey = imported.ExportedMainMonitor == null ? null : Key(imported.ExportedMainMonitor);
        var result = new List<OldMonitor>();
        foreach (var group in refs.GroupBy(Key))
        {
            // The most complete reference of this monitor (with a name, with hardware).
            var r = group.OrderByDescending(x => (x.Hardware == null ? 0 : 2) + (x.Name == null ? 0 : 1)).First();
            var match = Match(r, group.Key == mainKey, monitors);
            string? nickname = imported.MonitorNicknames.FirstOrDefault(n => Key(n.Monitor) == group.Key && !string.IsNullOrWhiteSpace(n.Nickname))?.Nickname;
            string model = r.Name ?? r.Device;
            result.Add(new OldMonitor
            {
                Ref = r, Match = match, Found = match != null,
                Label = nickname == null ? model : $"\"{nickname}\" ({model})",
            });
        }
        return result;
    }

    private static MonitorInfo? Match(MonitorRef r, bool wasMain, IReadOnlyList<MonitorInfo> monitors)
    {
        if (r.Hardware != null && MonitorMatcher.Find(monitors, null, null, r.Hardware) is MonitorInfo same) return same;
        if (!string.IsNullOrEmpty(r.Name) && !r.Name!.StartsWith("Display ", StringComparison.Ordinal))
        {
            var byName = monitors.Where(m => m.Targets.Any(t => t.FriendlyName == r.Name)).ToList();
            if (byName.Count == 1) return byName[0];
        }
        return wasMain ? monitors.FirstOrDefault(m => m.IsPrimary) : null;
    }

    /// <summary>Points every monitor reference in <paramref name="s"/> at the monitor of this PC it was matched to.</summary>
    public static void Remap(AppSettings s, IReadOnlyList<OldMonitor> map)
    {
        var byKey = map.Where(m => m.Match != null).ToDictionary(m => Key(m.Ref), m => m.Match!);
        MonitorRef? Mapped(MonitorRef r) =>
            byKey.TryGetValue(Key(r), out var m)
                ? new MonitorRef { Device = m.DeviceName, Name = m.FriendlyName, Hardware = m.IsDuplicated ? null : MonitorHardware.Clean(m.Hardware) }
                : null;
        foreach (var r in Refs(s))
        {
            if (Mapped(r) is not MonitorRef to) continue;
            r.Device = to.Device; r.Name = to.Name; r.Hardware = to.Hardware;
        }
        foreach (var a in s.Apps)
        {
            if (RuleRef(a) is not MonitorRef old || Mapped(old) is not MonitorRef to) continue;
            a.MonitorDevice = to.Device; a.MonitorName = to.Name; a.MonitorHardware = to.Hardware;
        }
    }

    /// <summary>
    /// "Replace": the imported settings, keeping the launch shortcuts you have here. Returns the apps whose launch
    /// shortcuts should be deleted (apps that aren't in the imported settings; an app group's shortcut that isn't is
    /// returned as an entry that only has its file name).
    /// </summary>
    public static AppSettings Replace(AppSettings current, AppSettings imported, out List<AppRule> orphanedShortcuts)
    {
        orphanedShortcuts = new List<AppRule>();
        foreach (var a in imported.Apps) { a.ShortcutFile = null; a.MoreShortcuts.Clear(); } // made on the other PC: they don't exist here
        foreach (var mine in current.Apps.Where(a => a.ShortcutFile != null || a.MoreShortcuts.Count > 0))
        {
            if (imported.Find(mine.Key) is AppRule same) { same.ShortcutFile = mine.ShortcutFile; same.MoreShortcuts = mine.MoreShortcuts.ToList(); }
            else orphanedShortcuts.Add(mine);
        }
        foreach (var g in imported.Groups) g.ShortcutFile = null;
        foreach (var mine in current.Groups.Where(g => g.ShortcutFile != null))
        {
            if (imported.Groups.FirstOrDefault(g => g.Id == mine.Id) is AppGroup same) same.ShortcutFile = mine.ShortcutFile;
            else orphanedShortcuts.Add(new AppRule { Key = mine.Id, ShortcutFile = mine.ShortcutFile });
        }
        return imported;
    }

    /// <summary>
    /// "Merge": your settings, plus the imported apps you don't have yet and names and choices for monitors you haven't
    /// set. Nothing you already have is changed.
    /// </summary>
    public static AppSettings Merge(AppSettings current, AppSettings imported)
    {
        foreach (var a in imported.Apps.Where(a => current.Find(a.Key) == null))
        {
            a.ShortcutFile = null;
            a.MoreShortcuts.Clear();
            current.Apps.Add(a);
        }
        foreach (var g in imported.Groups.Where(g => !current.Groups.Any(c => c.Id == g.Id)))
        {
            g.ShortcutFile = null;
            current.Groups.Add(g);
        }
        var named = current.MonitorNicknames.Select(n => Key(n.Monitor)).ToHashSet();
        current.MonitorNicknames.AddRange(imported.MonitorNicknames.Where(n => !named.Contains(Key(n.Monitor))));
        var options = current.MonitorOptions.Select(o => Key(o.Monitor)).ToHashSet();
        current.MonitorOptions.AddRange(imported.MonitorOptions.Where(o => !options.Contains(Key(o.Monitor))));
        return current;
    }
}

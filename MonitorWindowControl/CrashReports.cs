using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MonitorWindowControl;

/// <summary>
/// Local crash reports: when the app or the helper stops after an unexpected error, a short text file is written to
/// %LocalAppData%\MonitorWindowControl\Crashes (the version, Windows build, how it runs, the error, the monitors by model
/// name and the last lines of the activity log, without window titles). Nothing is ever sent anywhere; the settings
/// window offers to open or copy the newest one.
/// </summary>
public static class CrashReports
{
    private const int Keep = 10;
    private const int LogLines = 200;

    public static string Dir => Path.Combine(Paths.HelperDir, "Crashes");
    private static string SeenFile => Path.Combine(Dir, "seen.txt");

    /// <summary>
    /// Writes a report. <paramref name="fatal"/>: the program stopped (otherwise it kept running). Returns the file, or
    /// null if it couldn't be written. Never throws: it runs while something already went wrong.
    /// </summary>
    public static string? Write(string who, bool fatal, string error, AppSettings? settings, IReadOnlyList<MonitorInfo>? monitors)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var now = DateTime.Now;
            string file = Path.Combine(Dir, $"crash-{now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Monitor Window Control crash report");
            sb.AppendLine($"Time: {now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Program: {who}, version {Version()}");
            sb.AppendLine($"Stopped: {(fatal ? "yes" : "no, it kept running")}");
            sb.AppendLine($"Windows: {Environment.OSVersion.Version}, {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            if (settings != null) sb.AppendLine($"How it runs: {settings.RunMode}; move new windows: {settings.Enabled}; apps saved: {settings.Apps.Count}");
            sb.AppendLine();
            sb.AppendLine("Error:");
            sb.AppendLine(error);
            sb.AppendLine();
            sb.AppendLine("Monitors:");
            foreach (var line in MonitorLines(monitors)) sb.AppendLine("  " + line);
            sb.AppendLine();
            sb.AppendLine($"Last {LogLines} lines of the activity log (window titles removed):");
            foreach (var line in LastLogLines(LogLines)) sb.AppendLine(WithoutTitles(line));
            File.WriteAllText(file, sb.ToString());
            Log.Write($"{(fatal ? "Stopped after an unexpected error" : "Unexpected error")}; a crash report was saved: {file}");
            Prune();
            return file;
        }
        catch { return null; }
    }

    /// <summary>The monitors in plain words, without serial numbers or port paths.</summary>
    public static IEnumerable<string> MonitorLines(IReadOnlyList<MonitorInfo>? monitors)
    {
        if (monitors == null || monitors.Count == 0) { yield return "(not known)"; yield break; }
        foreach (var m in monitors)
        {
            var b = m.Bounds;
            string names = string.Join(" + ", m.Targets.Select(t => $"{t.FriendlyName} ({t.Connection})"));
            yield return $"{m.DeviceName}: {names}, {b.Right - b.Left}×{b.Bottom - b.Top} at {b.Left},{b.Top}" +
                         (m.IsPrimary ? ", main" : "") + (m.IsSwitchedOff ? ", treated as switched off" : "");
        }
    }

    private static string Version()
    {
        var v = typeof(CrashReports).Assembly.GetName().Version;
        return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>The last lines of the activity log (empty if it can't be read).</summary>
    public static List<string> LastLogLines(int count)
    {
        try
        {
            using var f = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            var lines = new Queue<string>();
            while (r.ReadLine() is string line)
            {
                lines.Enqueue(line);
                if (lines.Count > count) lines.Dequeue();
            }
            return lines.ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// A log line without window titles: the log writes them in quotes (chrome.exe "My bank - Chrome"), so whatever is
    /// between two quotes is replaced by "…".
    /// </summary>
    public static string WithoutTitles(string line)
    {
        if (line.IndexOf('"') < 0) return line;
        var sb = new StringBuilder(line.Length);
        bool inside = false;
        foreach (char c in line)
        {
            if (c != '"') { if (!inside) sb.Append(c); continue; }
            sb.Append(inside ? "…\"" : "\"");
            inside = !inside;
        }
        if (inside) sb.Append('…'); // an unmatched quote: the rest of the line is left out
        return sb.ToString();
    }

    /// <summary>The reports, newest first.</summary>
    public static List<FileInfo> List()
    {
        try
        {
            if (!Directory.Exists(Dir)) return new List<FileInfo>();
            return new DirectoryInfo(Dir).GetFiles("crash-*.txt").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    /// <summary>The newest report that the settings window hasn't shown yet, or null.</summary>
    public static FileInfo? NewestUnseen()
    {
        var newest = List().FirstOrDefault();
        if (newest == null) return null;
        string seen = "";
        try { if (File.Exists(SeenFile)) seen = File.ReadAllText(SeenFile).Trim(); } catch { }
        return string.CompareOrdinal(newest.Name, seen) > 0 ? newest : null;
    }

    /// <summary>The settings window showed this report: don't show it again.</summary>
    public static void MarkSeen(FileInfo report)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(SeenFile, report.Name); } catch { }
    }

    /// <summary>True if the report says the program stopped (not just that it had a problem and kept running).</summary>
    public static bool WasFatal(FileInfo report)
    {
        try { return File.ReadLines(report.FullName).Take(6).Any(l => l == "Stopped: yes"); }
        catch { return true; }
    }

    /// <summary>When the report was written, from its file name.</summary>
    public static DateTime TimeOf(FileInfo report) =>
        DateTime.TryParseExact(report.Name.Length >= 21 ? report.Name.Substring(6, 15) : "", "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : report.LastWriteTime;

    /// <summary>Keeps the newest <see cref="Keep"/> reports.</summary>
    private static void Prune()
    {
        foreach (var old in List().Skip(Keep))
            try { old.Delete(); } catch { }
    }
}

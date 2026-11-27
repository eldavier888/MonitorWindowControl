using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace MonitorWindowControl;

/// <summary>
/// About → Save diagnostics: one .zip with what helps to understand a problem, saved where you choose. Nothing is sent:
/// you decide whether to share it. Window titles can be left out, and so can the app list.
/// </summary>
public static class Diagnostics
{
    /// <summary>What goes in the .zip, in words, for the dialog that asks first.</summary>
    public static List<string> Contents(int crashReports) => new()
    {
        "The activity log (what was moved where, and when)",
        "Your settings (the app list only if you include it)",
        "Your monitors: model names, sizes, positions and how they're connected",
        crashReports switch { 0 => "Crash reports (there are none)", 1 => "1 crash report", _ => $"{crashReports} crash reports" },
        "The version of Monitor Window Control and of Windows",
    };

    /// <summary>Writes the .zip. Returns an error message, or null.</summary>
    public static string? Save(string zipFile, Engine engine, bool includeApps, bool removeTitles)
    {
        try
        {
            if (File.Exists(zipFile)) File.Delete(zipFile);
            using var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create);
            Add(zip, "about.txt", About(engine));
            var log = CrashReports.LastLogLines(int.MaxValue);
            Add(zip, "monitorwindowcontrol.log", string.Join(Environment.NewLine, removeTitles ? log.Select(CrashReports.WithoutTitles) : log));
            var settings = SettingsStore.Copy(engine.Settings);
            if (!includeApps) settings.Apps.Clear();
            Add(zip, "settings.json", System.Text.Json.JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
            Add(zip, "monitors.txt", MonitorsText(engine.Monitors));
            foreach (var report in CrashReports.List())
            {
                var lines = File.ReadAllLines(report.FullName);
                Add(zip, "Crashes/" + report.Name, string.Join(Environment.NewLine, removeTitles ? lines.Select(CrashReports.WithoutTitles) : lines));
            }
            Log.Write($"Saved diagnostics to {zipFile} (app list {(includeApps ? "included" : "left out")}, window titles {(removeTitles ? "removed" : "kept")}).");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string About(Engine engine)
    {
        var s = engine.Settings;
        var sb = new StringBuilder();
        sb.AppendLine($"Monitor Window Control {MainWindow.AppVersion}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
        sb.AppendLine($"How it runs: {s.RunMode}; helper running: {HelperManager.IsRunning()}; start with Windows: {s.StartWithWindows}");
        sb.AppendLine($"Move new windows: {s.Enabled}; learn: {s.LearnAutomatically}; apps saved: {s.Apps.Count}");
        var p = engine.Policy;
        sb.AppendLine(p.HasAny
            ? $"Managed by the organization: enabled={p.Enabled}, learn={p.LearnAutomatically}, unknown apps={p.UnknownApps}, " +
              $"run mode={p.RunMode}, make main allowed={p.AllowMakeMain}, app rules={p.AppRules.Count}"
            : "Managed by the organization: nothing");
        return sb.ToString();
    }

    /// <summary>The monitors as Windows reports them (without serial numbers).</summary>
    private static string MonitorsText(IReadOnlyList<MonitorInfo> monitors)
    {
        var sb = new StringBuilder();
        foreach (var line in CrashReports.MonitorLines(monitors)) sb.AppendLine(line);
        sb.AppendLine();
        foreach (var m in monitors)
        {
            sb.AppendLine($"{m.DeviceName}: work area {m.Work.Left},{m.Work.Top} to {m.Work.Right},{m.Work.Bottom}; " +
                          $"temporary={m.IsTemporary}; only chosen apps={m.OnlyChosenApps}; {m.IdentityNote}");
            foreach (var t in m.Targets)
                sb.AppendLine($"  {t.FriendlyName}: model code {t.Model ?? "?"}, connection {t.Connection} ({t.OutputTechnology}), " +
                              $"serial number {(string.IsNullOrEmpty(t.Serial) ? "none" : "reported")}");
        }
        return sb.ToString();
    }
}

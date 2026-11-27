using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MonitorWindowControl;

/// <summary>
/// Settings → Apps → Installed apps: the app adds itself there (for this user, no admin needed) and keeps the entry
/// pointing at wherever the exe is now. "Remove from this PC" (About page, or Uninstall in Windows Settings, which
/// starts the exe with --uninstall) stops everything, removes what it added to Windows, and deletes the exe once it has
/// exited. Your settings are kept unless you choose otherwise.
/// </summary>
public static class Uninstaller
{
    public const string Argument = "--uninstall";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorWindowControl";
    private const string ExeName = "MonitorWindowControl.exe";

    /// <summary>Adds or updates the Installed apps entry for the exe at <paramref name="exe"/> (only what changed is written).</summary>
    public static void Register(string exe, string version)
    {
        if (!string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase)) return; // (dotnet.exe while debugging)
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            void Set(string name, object value, RegistryValueKind kind = RegistryValueKind.String)
            {
                if (!Equals(k.GetValue(name), value)) k.SetValue(name, value, kind);
            }
            Set("DisplayName", "Monitor Window Control");
            Set("DisplayIcon", $"\"{exe}\",0");
            Set("DisplayVersion", version);
            Set("Publisher", "Monitor Window Control project");
            Set("InstallLocation", Path.GetDirectoryName(exe) ?? "");
            Set("UninstallString", $"\"{exe}\" {Argument}");
            Set("URLInfoAbout", "https://github.com/eldavier888/MonitorWindowControl");
            Set("NoModify", 1, RegistryValueKind.DWord);
            Set("NoRepair", 1, RegistryValueKind.DWord);
            Set("EstimatedSize", (int)Math.Max(1, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Write("Could not update the Installed apps entry: " + ex.Message); }
    }

    /// <summary>Asks first. Returns null if you cancelled, else whether to keep your settings and app list.</summary>
    public static async Task<bool?> AskAsync()
    {
        string exe = Environment.ProcessPath ?? ExeName;
        var keep = new CheckBox
        {
            Content = "Keep my settings and app list", IsChecked = true, Margin = new Thickness(0, 12, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(keep, "Keep my settings and app list");
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Monitor Window Control stops, and is removed from Start with Windows and Installed apps. Its launch shortcuts " +
                   $"and its helper are deleted, and so is the program itself ({exe}). Windows stay where they are.",
        });
        content.Children.Add(keep);
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 2, 0, 0), FontSize = 12 * ThemeSupport.TextScale, Opacity = 0.8,
            Text = "If you use it again later, your apps and monitor names are still there. Clear this to delete them too.",
        });
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Remove Monitor Window Control from this PC?", Content = content,
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return null;
        return keep.IsChecked == true;
    }

    /// <summary>
    /// Removes everything Monitor Window Control added to Windows: the helper (stopped first), Start with Windows, launch
    /// shortcuts and the Installed apps entry. The program file, the helper's folder and (unless
    /// <paramref name="keepSettings"/>) the settings folder are deleted by a short-lived process once this one has exited,
    /// so the caller must exit right after this.
    /// </summary>
    public static void Remove(bool keepSettings, IEnumerable<AppRule> apps, IEnumerable<AppGroup> groups)
    {
        Log.Write($"Removing Monitor Window Control from this PC ({(keepSettings ? "keeping" : "deleting")} the settings).");
        HelperManager.Stop();
        StartupManager.Apply(RunModes.Tray, false, "", "");
        foreach (var rule in apps) ShortcutManager.Delete(rule);
        foreach (var group in groups) ShortcutManager.Delete(group);
        Native.SetRestartAfterUpdate(null);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
        catch (Exception ex) { Log.Write("Could not remove the Installed apps entry: " + ex.Message); }
        DeleteAfterExit(keepSettings);
    }

    /// <summary>Starts a hidden PowerShell that waits for this process to exit, then deletes the files.</summary>
    private static void DeleteAfterExit(bool keepSettings)
    {
        static string Q(string path) => "'" + path.Replace("'", "''") + "'";
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"Wait-Process -Id {Environment.ProcessId} -Timeout 60",
            "Start-Sleep -Milliseconds 500",
        };
        string? exe = Environment.ProcessPath;
        if (exe != null && string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Remove-Item -LiteralPath {Q(exe)} -Force");
            // Its own folder (such as %LocalAppData%\Programs\MonitorWindowControl) goes too, but only if it's empty now.
            string? dir = Path.GetDirectoryName(exe);
            if (dir != null && string.Equals(Path.GetFileName(dir), "MonitorWindowControl", StringComparison.OrdinalIgnoreCase))
                lines.Add($"if (-not (Get-ChildItem -LiteralPath {Q(dir)} -Force)) {{ Remove-Item -LiteralPath {Q(dir)} -Force }}");
        }
        if (Path.GetFileName(Paths.HelperDir) == "MonitorWindowControl")
            lines.Add($"Remove-Item -LiteralPath {Q(Paths.HelperDir)} -Recurse -Force");
        if (!keepSettings && Path.GetFileName(Paths.Dir) == "MonitorWindowControl")
            lines.Add($"Remove-Item -LiteralPath {Q(Paths.Dir)} -Recurse -Force");
        string script = string.Join("; ", lines);
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script }) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Write("Could not schedule deleting the program file: " + ex.Message); }
    }

    /// <summary>
    /// Started with --uninstall (Windows Settings → Installed apps → Uninstall): asks first, stops a running copy of the
    /// app, removes everything and exits.
    /// </summary>
    public static async Task RunFromCommandLineAsync(Func<bool> stopRunningCopy)
    {
        bool? keep = await AskAsync();
        if (keep == null) return;
        if (!stopRunningCopy())
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Couldn't remove Monitor Window Control",
                Content = "Monitor Window Control is still running and didn't close when asked. Close it (Exit in its window or tray menu), then try again.",
                CloseButtonText = "OK",
            }.ShowDialogAsync();
            return;
        }
        var settings = SettingsStore.Load(out _);
        Remove(keep.Value, settings.Apps, settings.Groups);
        await new Wpf.Ui.Controls.MessageBox
        {
            Title = "Monitor Window Control was removed",
            Content = keep.Value ? "Your settings were kept, in case you use it again." : "Your settings were deleted too.",
            CloseButtonText = "OK",
        }.ShowDialogAsync();
    }
}

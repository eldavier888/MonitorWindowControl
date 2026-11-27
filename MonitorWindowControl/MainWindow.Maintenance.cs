using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MonitorWindowControl;

/// <summary>
/// The window's upkeep parts: organization policies, the helper watchdog and crash reports (General page), and
/// Backup, Save diagnostics and Remove from this PC (About page).
/// </summary>
public partial class MainWindow
{
    private FileInfo? _crashReport; // the report the crash bar is about

    // ------------------------------------------------------------------ organization policies

    /// <summary>"Some settings are managed by your organization" on each page that has locked settings.</summary>
    private void UpdatePolicyBars()
    {
        var p = _engine.Policy;
        var locked = new List<string>();
        if (p.RunMode != null) locked.Add("how it runs");
        if (p.Enabled != null) locked.Add("move new windows");
        if (p.LearnAutomatically != null) locked.Add("learn automatically");
        if (p.UnknownApps != null) locked.Add("where apps it hasn't learned yet open");
        PolicyInfoBar.IsOpen = p.HasAny;
        PolicyInfoBar.Message = locked.Count > 0
            ? $"Your organization sets {Sentence(locked)}, so you can't change them here."
            : "Your organization sets some choices on the Monitors and Apps pages.";
        PolicyMonitorsBar.IsOpen = !p.MakeMainAllowed;
        PolicyAppsBar.IsOpen = p.AppRules.Count > 0;
    }

    /// <summary>"a, b and c".</summary>
    private static string Sentence(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    // ------------------------------------------------------------------ helper watchdog and crash reports

    /// <summary>
    /// Helper mode: says when the helper should be running but isn't (it crashed, or something stopped it), with
    /// Start it. Checked when the window opens and with the window's regular refresh (a cheap check, no polling otherwise).
    /// </summary>
    private void UpdateHelperBar()
    {
        bool down = _engine.Settings.RunMode == RunModes.Helper && !HelperManager.IsRunning();
        var visibility = down ? Visibility.Visible : Visibility.Collapsed;
        if (HelperDownPanel.Visibility == visibility) return;
        HelperDownPanel.Visibility = visibility;
        if (!down) return;
        _crashReport = CrashReports.List().FirstOrDefault();
        HelperReportButton.Visibility = _crashReport == null ? Visibility.Collapsed : Visibility.Visible;
        if (_app.HelperProblem != null) HelperDownBar.Message = _app.HelperProblem;
        Announcer.Announce(this, HelperDownBar.Title);
    }

    /// <summary>When the window opens: say once that the app or the helper stopped unexpectedly since last time.</summary>
    private void ShowCrashIfAny()
    {
        var report = CrashReports.NewestUnseen();
        if (report == null) return;
        _crashReport = report;
        bool fatal = CrashReports.WasFatal(report);
        CrashBar.Title = $"Monitor Window Control {(fatal ? "stopped unexpectedly" : "ran into a problem")} on {CrashReports.TimeOf(report):f}";
        System.Windows.Automation.AutomationProperties.SetName(CrashBar, CrashBar.Title);
        CrashPanel.Visibility = Visibility.Visible;
    }

    private async void StartHelper_Click(object sender, RoutedEventArgs e)
    {
        string? problem = _app.StartHelper();
        BackgroundStatusText.Text = _app.BackgroundStatus();
        UpdateHelperBar();
        if (problem != null)
            await new Wpf.Ui.Controls.MessageBox { Title = "Couldn't start the helper", Content = problem, CloseButtonText = "OK" }.ShowDialogAsync();
    }

    private void OpenCrashReport_Click(object sender, RoutedEventArgs e)
    {
        var report = _crashReport ?? CrashReports.List().FirstOrDefault();
        if (report == null) return;
        CrashReports.MarkSeen(report);
        OpenUri(report.FullName);
    }

    private async void CopyCrashReport_Click(object sender, RoutedEventArgs e)
    {
        if (_crashReport == null) return;
        try
        {
            Clipboard.SetText(File.ReadAllText(_crashReport.FullName));
            CrashReports.MarkSeen(_crashReport);
        }
        catch (Exception ex)
        {
            await new Wpf.Ui.Controls.MessageBox { Title = "Couldn't copy the report", Content = ex.Message, CloseButtonText = "OK" }.ShowDialogAsync();
        }
    }

    private void DismissCrash_Click(object sender, RoutedEventArgs e)
    {
        if (_crashReport != null) CrashReports.MarkSeen(_crashReport);
        CrashPanel.Visibility = Visibility.Collapsed;
        StartupToggle.Focus();
    }

    // ------------------------------------------------------------------ Backup

    /// <summary>Fills "Restore a backup" with the daily copies.</summary>
    private void FillBackups()
    {
        var backups = SettingsStore.ListBackups();
        BackupCombo.ItemsSource = backups.Select(b => new BackupItem(b.Day, b.File)).ToList();
        BackupCombo.SelectedIndex = backups.Count > 0 ? 0 : -1;
        BackupCombo.IsEnabled = RestoreButton.IsEnabled = backups.Count > 0;
        BackupHint.Text = backups.Count == 0
            ? "No backups yet. A copy of your settings is kept each day you use Monitor Window Control (the last 5 days), and before Forget all."
            : "A copy of your settings is kept each day you use Monitor Window Control (the last 5 days), and before Forget all and imports.";
    }

    /// <summary>One daily copy in the "Restore a backup" list: "Today", "Yesterday" or the date.</summary>
    private sealed record BackupItem(DateTime Day, string File)
    {
        public override string ToString() =>
            Day == DateTime.Today ? "Today" : Day == DateTime.Today.AddDays(-1) ? "Yesterday" : Day.ToString("D");
    }

    private async void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export settings",
            FileName = $"Monitor Window Control settings {DateTime.Today:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            Filter = "Settings (*.json)|*.json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        string? error = _engine.ExportSettings(dialog.FileName);
        await new Wpf.Ui.Controls.MessageBox
        {
            Title = error == null ? "Settings exported" : "Couldn't export settings",
            Content = error ?? $"{SettingsTransfer.Summary(_engine.Settings)} were saved to {dialog.FileName}. " +
                               "To use them on another PC, choose Import settings there.",
            CloseButtonText = "OK",
        }.ShowDialogAsync();
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import settings", Filter = "Settings (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var imported = SettingsStore.ReadUntrusted(dialog.FileName, out string? error);
        if (imported == null)
        {
            await new Wpf.Ui.Controls.MessageBox { Title = "Couldn't import settings", Content = error, CloseButtonText = "OK" }.ShowDialogAsync();
            return;
        }
        var map = SettingsTransfer.FindMonitors(imported, _engine.Monitors);
        bool? replace = await AskImportAsync(imported, map, Path.GetFileName(dialog.FileName));
        if (replace == null) return;
        foreach (var old in map.Where(m => !m.Found)) old.Match = await AskWhichMonitorAsync(old);
        SettingsTransfer.Remap(imported, map);
        string summary = SettingsTransfer.Summary(imported);
        var orphans = new List<AppRule>();
        _engine.ImportSettings(current => replace == true
                ? SettingsTransfer.Replace(current, imported, out orphans)
                : SettingsTransfer.Merge(current, imported),
            replace == true ? $"Imported settings ({summary}), replacing yours." : $"Added imported settings ({summary}) to yours.");
        foreach (var rule in orphans) ShortcutManager.Delete(rule); // their apps aren't in the imported settings
        FillBackups();
    }

    /// <summary>What's in the file, and Replace / Merge. Null = cancelled; true = replace.</summary>
    private async Task<bool?> AskImportAsync(AppSettings imported, IReadOnlyList<SettingsTransfer.OldMonitor> map, string file)
    {
        var content = new StackPanel();
        void Line(string text, double top = 0, bool secondary = false)
        {
            var block = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            if (secondary) { block.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"); block.FontSize = 12 * ThemeSupport.TextScale; }
            content.Children.Add(block);
        }
        Line($"{file} has {SettingsTransfer.Summary(imported)}.");
        if (map.Count > 0)
        {
            Line("Its monitors:", 10);
            foreach (var m in map)
                Line(m.Match != null ? $"{m.Label} → {m.Match.Name}" : $"{m.Label} → not found yet (you choose next)", 2, secondary: true);
        }
        Line("Replace: use its settings instead of yours. Merge: add only the apps and monitor names you don't have yet; " +
             "everything you have stays as it is.", 10);
        Line("How it runs, Start with Windows and Keep my main monitor stay as they are on this PC. A copy of your current settings " +
             "is kept first (Restore a backup).", 6, secondary: true);
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Import settings", Content = content,
            PrimaryButtonText = "Replace", SecondaryButtonText = "Merge", CloseButtonText = "Cancel",
        };
        return await box.ShowDialogAsync() switch
        {
            Wpf.Ui.Controls.MessageBoxResult.Primary => true,
            Wpf.Ui.Controls.MessageBoxResult.Secondary => false,
            _ => null,
        };
    }

    /// <summary>"Your old "TV": which monitor is it now?" Null = not connected now (it's kept as it is).</summary>
    private async Task<MonitorInfo?> AskWhichMonitorAsync(SettingsTransfer.OldMonitor old)
    {
        const string NotHere = "It isn't connected now";
        var items = new List<object>(_engine.Monitors) { NotHere };
        var combo = new ComboBox { ItemsSource = items, SelectedItem = NotHere, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(combo, $"Which monitor is your old {old.Label} now");
        var content = new StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Its apps and settings go to the monitor you choose. If it isn't connected now, they're kept and used when it is.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        content.Children.Add(combo);
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Your old {old.Label}: which monitor is it now?", Content = content, PrimaryButtonText = "OK", CloseButtonText = "Skip",
        };
        box.Loaded += (_, _) => combo.Focus();
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary ? combo.SelectedItem as MonitorInfo : null;
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupCombo.SelectedItem is not BackupItem item) return;
        var backup = SettingsStore.ReadUntrusted(item.File, out string? error);
        if (backup == null)
        {
            await new Wpf.Ui.Controls.MessageBox { Title = "Couldn't restore the backup", Content = error, CloseButtonText = "OK" }.ShowDialogAsync();
            return;
        }
        string when = item.ToString();
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Restore the backup from {(item.Day >= DateTime.Today.AddDays(-1) ? when.ToLowerInvariant() : when)}?",
            Content = $"Your settings are replaced by the ones from then: {SettingsTransfer.Summary(backup)}. " +
                      "A copy of your current settings is kept first, so you can go back.",
            PrimaryButtonText = "Restore", CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        var orphans = new List<AppRule>();
        _engine.ImportSettings(current => SettingsTransfer.Replace(current, backup, out orphans), $"Restored the settings from {when.ToLowerInvariant()}.");
        foreach (var rule in orphans) ShortcutManager.Delete(rule);
        FillBackups();
    }

    // ------------------------------------------------------------------ Save diagnostics, Remove from this PC

    private async void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = "The .zip file contains:", TextWrapping = TextWrapping.Wrap });
        foreach (var line in Diagnostics.Contents(CrashReports.List().Count))
            content.Children.Add(new System.Windows.Controls.TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 0, 0) });
        var titles = new CheckBox { Content = "Remove window titles", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        var apps = new CheckBox { Content = "Include my app list (the apps and where they open)", IsChecked = false, Margin = new Thickness(0, 6, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(titles, "Remove window titles");
        System.Windows.Automation.AutomationProperties.SetName(apps, "Include my app list");
        content.Children.Add(titles);
        content.Children.Add(apps);
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Nothing is sent: you choose where to save it, and who to give it to.", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0), FontSize = 12 * ThemeSupport.TextScale, Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        });
        var box = new Wpf.Ui.Controls.MessageBox { Title = "Save diagnostics", Content = content, PrimaryButtonText = "Save…", CloseButtonText = "Cancel" };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save diagnostics", FileName = $"Monitor Window Control diagnostics {DateTime.Now:yyyy-MM-dd HHmm}.zip",
            DefaultExt = ".zip", Filter = "Zip file (*.zip)|*.zip", AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        string? error = Diagnostics.Save(dialog.FileName, _engine, apps.IsChecked == true, titles.IsChecked == true);
        await new Wpf.Ui.Controls.MessageBox
        {
            Title = error == null ? "Diagnostics saved" : "Couldn't save diagnostics",
            Content = error ?? $"Saved to {dialog.FileName}.",
            CloseButtonText = "OK",
        }.ShowDialogAsync();
    }

    private void RemoveFromPc_Click(object sender, RoutedEventArgs e) => _app.RemoveFromThisPc();
}

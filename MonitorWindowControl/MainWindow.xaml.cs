using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MonitorWindowControl;

public sealed record AppRow(string Key, string Name, string Detail, string OpensOn, string Source);

public partial class MainWindow : FluentWindow
{
    private const string NeverMoveLabel = "Never move this app";
    private readonly Engine _engine;
    private bool _updating;

    public MainWindow(Engine engine)
    {
        _engine = engine;
        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        _engine.Changed += Refresh;
        Refresh();
    }

    /// <summary>Stop listening to the engine (called on exit).</summary>
    public void Detach() => _engine.Changed -= Refresh;

    // Closing the window really closes it (frees its memory); the app keeps running in the tray.
    protected override void OnClosed(EventArgs e)
    {
        Detach();
        base.OnClosed(e);
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            var s = _engine.Settings;
            EnabledToggle.IsChecked = s.Enabled;
            LearnToggle.IsChecked = s.LearnAutomatically;
            StartupToggle.IsChecked = StartupManager.IsEnabled();
            UnknownCombo.SelectedIndex = s.MoveUnknownToMain ? 0 : 1;
            LastActionText.Text = _engine.LastAction;
            LastActionText.Visibility = string.IsNullOrEmpty(_engine.LastAction) ? Visibility.Collapsed : Visibility.Visible;

            MonitorsList.ItemsSource = _engine.Monitors.ToList();

            string? selectedKey = (AppsList.SelectedItem as AppRow)?.Key;
            var rows = s.Apps
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(ToRow).ToList();
            AppsList.ItemsSource = rows;
            AppsList.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey);
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updating = false; }
        UpdateSelectionPanel();
    }

    private AppRow ToRow(AppRule r)
    {
        string opensOn;
        if (r.NeverMove) opensOn = "Never moved";
        else
        {
            var m = _engine.Resolve(r);
            opensOn = m != null ? m.Label : $"{r.MonitorName ?? r.MonitorDevice} (not connected)";
        }
        string name = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Key : r.DisplayName;
        string detail = string.Equals(name, r.Key, StringComparison.OrdinalIgnoreCase) ? "" : r.Key;
        return new AppRow(r.Key, name, detail, opensOn, r.Pinned || r.NeverMove ? "set by you" : "learned");
    }

    private void UpdateSelectionPanel()
    {
        var row = AppsList.SelectedItem as AppRow;
        var rule = row == null ? null : _engine.Settings.Find(row.Key);
        SelectionPanel.IsEnabled = rule != null;

        var items = new List<object>(_engine.Monitors) { NeverMoveLabel };
        TargetCombo.ItemsSource = items;
        if (rule == null) { TargetCombo.SelectedIndex = -1; return; }
        TargetCombo.SelectedItem = rule.NeverMove
            ? NeverMoveLabel
            : (object?)_engine.Resolve(rule) ?? _engine.MainMonitor;
    }

    /// <summary>The list grows with its content; let the mouse wheel scroll the whole page instead.</summary>
    private void AppsList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset - e.Delta);
    }

    /// <summary>Clicking a row shouldn't make the page jump.</summary>
    private void AppsList_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) UpdateSelectionPanel();
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e) => _engine.SetEnabled(EnabledToggle.IsChecked == true);

    private void LearnToggle_Click(object sender, RoutedEventArgs e) => _engine.SetLearn(LearnToggle.IsChecked == true);

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = StartupToggle.IsChecked == true;
        try
        {
            StartupManager.Set(on);
            Log.Write("Start with Windows: " + (on ? "on" : "off"));
        }
        catch (Exception ex)
        {
            Log.Write("Could not change Start with Windows: " + ex.Message);
        }
        StartupToggle.IsChecked = StartupManager.IsEnabled();
    }

    private void UnknownCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || UnknownCombo.SelectedIndex < 0) return;
        _engine.SetMoveUnknownToMain(UnknownCombo.SelectedIndex == 0);
    }

    private void ChangeMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow row) return;
        switch (TargetCombo.SelectedItem)
        {
            case MonitorInfo m: _engine.Pin(row.Key, m); break;
            case string s when s == NeverMoveLabel: _engine.Pin(row.Key, null); break;
        }
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is AppRow row) _engine.Forget(row.Key);
    }

    private async void ForgetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.Settings.Apps.Count == 0) return;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Forget all apps?",
            Content = $"Monitor Window Control will forget where all {_engine.Settings.Apps.Count} apps open, including the ones you set yourself. It will start learning again from scratch.",
            PrimaryButtonText = "Forget all",
            CloseButtonText = "Cancel",
        };
        var result = await box.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary) _engine.ForgetAll();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(Paths.LogFile)) Log.Write("Log opened.");
            Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Could not open log: " + ex.Message);
        }
    }
}

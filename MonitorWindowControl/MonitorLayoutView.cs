using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MonitorWindowControl;

/// <summary>
/// A scaled picture of how the monitors are arranged, like Settings → Display: one tile per monitor with your number
/// and name and "(main)". In <see cref="MultiSelect"/> mode the tiles are check boxes (several can be on), otherwise
/// radio buttons (one is chosen). Each tile can be reached with Tab and is named for Narrator.
/// </summary>
public sealed class MonitorLayoutView : Canvas
{
    private const double Gap = 4; // space between tiles that touch, so each reads as its own monitor
    private readonly List<(MonitorInfo Monitor, ToggleButton Tile)> _tiles = new();
    private string _sig = "";

    /// <summary>Tiles are check boxes (several can be on) instead of radio buttons.</summary>
    public bool MultiSelect { get; set; }

    /// <summary>The tallest the picture gets.</summary>
    public double MaxDiagramHeight { get; set; } = 180;

    /// <summary>A tile was selected with the mouse or keyboard: the monitor, and whether its tile is now on.</summary>
    public event Action<MonitorInfo, bool>? TileClicked;

    public MonitorLayoutView()
    {
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
        AutomationProperties.SetName(this, "Monitor layout");
        SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5) ArrangeTiles(); };
    }

    /// <summary>Shows these monitors (rebuilt only when they or their names changed).</summary>
    public void SetMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        string sig = string.Join("|", monitors.Select(m => $"{m.DeviceName}={m.Label}/{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Right},{m.Bounds.Bottom}"));
        if (sig == _sig && _tiles.Count == monitors.Count)
        {
            // Same monitors: keep the tiles (and focus), only take the new objects.
            for (int i = 0; i < monitors.Count; i++) _tiles[i] = (monitors[i], _tiles[i].Tile);
            return;
        }
        _sig = sig;
        var selected = _tiles.Where(t => t.Tile.IsChecked == true).Select(t => t.Monitor.DeviceName).ToHashSet();
        Children.Clear();
        _tiles.Clear();
        string group = "MonitorLayout" + GetHashCode();
        foreach (var m in monitors)
        {
            ToggleButton tile = MultiSelect ? new CheckBox() : new RadioButton { GroupName = group };
            tile.SetResourceReference(StyleProperty, "MonitorTile");
            tile.IsChecked = selected.Contains(m.DeviceName);
            var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            if (m.IsPrimary) text.Children.Add(new TextBlock { Text = "(main)", TextAlignment = TextAlignment.Center, FontSize = 12 });
            tile.Content = new Viewbox { Child = text, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(6) };
            AutomationProperties.SetName(tile, m.Label);
            tile.ToolTip = m.Label;
            var monitor = m;
            tile.Click += (_, _) => TileClicked?.Invoke(Current(tile) ?? monitor, tile.IsChecked == true);
            _tiles.Add((m, tile));
            Children.Add(tile);
        }
        ArrangeTiles();
    }

    private MonitorInfo? Current(ToggleButton tile) => _tiles.FirstOrDefault(t => ReferenceEquals(t.Tile, tile)).Monitor;

    /// <summary>Marks these monitors as chosen (and the others as not), without raising <see cref="TileClicked"/>.</summary>
    public void Select(IEnumerable<MonitorInfo?> monitors)
    {
        var names = monitors.Where(m => m != null).Select(m => m!.DeviceName).ToHashSet();
        foreach (var (m, tile) in _tiles) tile.IsChecked = names.Contains(m.DeviceName);
    }

    /// <summary>Gives keyboard focus to the chosen tile (or the first).</summary>
    public void FocusTile()
    {
        var tile = _tiles.FirstOrDefault(t => t.Tile.IsChecked == true).Tile ?? _tiles.FirstOrDefault().Tile;
        tile?.Focus();
    }

    /// <summary>Scales the monitors' real positions to fit the width, centred, with no more than the maximum height.</summary>
    private void ArrangeTiles()
    {
        if (_tiles.Count == 0) { Height = 0; return; }
        int left = _tiles.Min(t => t.Monitor.Bounds.Left), top = _tiles.Min(t => t.Monitor.Bounds.Top);
        int right = _tiles.Max(t => t.Monitor.Bounds.Right), bottom = _tiles.Max(t => t.Monitor.Bounds.Bottom);
        double totalW = Math.Max(1, right - left), totalH = Math.Max(1, bottom - top);
        double width = ActualWidth > 0 ? ActualWidth : 480;
        double scale = Math.Min(width / totalW, MaxDiagramHeight / totalH);
        double offsetX = (width - totalW * scale) / 2;
        Height = totalH * scale;
        foreach (var (m, tile) in _tiles)
        {
            double w = Math.Max(24, (m.Bounds.Right - m.Bounds.Left) * scale - Gap);
            double h = Math.Max(24, (m.Bounds.Bottom - m.Bounds.Top) * scale - Gap);
            tile.Width = w; tile.Height = h;
            SetLeft(tile, offsetX + (m.Bounds.Left - left) * scale + Gap / 2);
            SetTop(tile, (m.Bounds.Top - top) * scale + Gap / 2);
        }
    }
}

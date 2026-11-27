using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MonitorWindowControl;

/// <summary>
/// "Identify": for 3 seconds, a large see-through label on every monitor with your number and name ("3 · TV"), its
/// model and whether it's the main one. Also the outline drawn around a monitor while you point at its card. These
/// windows can't be clicked or activated, have no taskbar button and belong to this app, so the engine never moves
/// or learns them (it skips its own programs and tool windows).
/// </summary>
public static class IdentifyOverlay
{
    private static readonly List<Window> Labels = new();
    private static Window? _outline;
    private static DispatcherTimer? _timer;

    /// <summary>Shows the label on every monitor for 3 seconds (pressing Identify again starts the 3 seconds again).</summary>
    public static void ShowLabels(IReadOnlyList<MonitorInfo> monitors)
    {
        CloseLabels();
        foreach (var m in monitors)
        {
            try { Labels.Add(Show(LabelContent(m), m, sizeToBounds: false)); }
            catch (Exception ex) { Log.Write($"Could not show the label on {m.Name}: {ex.Message}"); }
        }
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick -= OnTimer;
        _timer.Tick += OnTimer;
        _timer.Stop();
        _timer.Start();
    }

    private static void OnTimer(object? sender, EventArgs e) { _timer?.Stop(); CloseLabels(); }

    /// <summary>Draws an outline around this monitor (until <see cref="HideOutline"/>).</summary>
    public static void ShowOutline(MonitorInfo m)
    {
        HideOutline();
        try
        {
            var border = new Border
            {
                BorderThickness = new Thickness(8),
                Background = Brushes.Transparent,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            if (border.BorderBrush == null) border.BorderBrush = SystemColors.HighlightBrush;
            _outline = Show(border, m, sizeToBounds: true);
        }
        catch (Exception ex) { Log.Write($"Could not outline {m.Name}: {ex.Message}"); }
    }

    public static void HideOutline()
    {
        var w = _outline;
        _outline = null;
        w?.Close();
    }

    /// <summary>Closes every label and the outline (when the settings window closes).</summary>
    public static void CloseAll() { _timer?.Stop(); CloseLabels(); HideOutline(); }

    private static void CloseLabels()
    {
        foreach (var w in Labels.ToList()) w.Close();
        Labels.Clear();
    }

    private static UIElement LabelContent(MonitorInfo m)
    {
        var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = m.Number is int n ? n.ToString() : m.Name, FontSize = 120, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (m.Number != null)
            text.Children.Add(new TextBlock { Text = m.Name, FontSize = 36, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        if (!string.Equals(m.FriendlyName, m.Name, StringComparison.CurrentCultureIgnoreCase) && !m.Name.EndsWith(m.FriendlyName, StringComparison.CurrentCulture))
            text.Children.Add(new TextBlock { Text = m.FriendlyName, FontSize = 22, Foreground = Brushes.Gainsboro, HorizontalAlignment = HorizontalAlignment.Center });
        if (m.IsPrimary)
            text.Children.Add(new TextBlock { Text = "Main monitor", FontSize = 22, Foreground = Brushes.Gainsboro, HorizontalAlignment = HorizontalAlignment.Center });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24),
            Child = new Viewbox { Child = text, Stretch = Stretch.Uniform },
        };
    }

    /// <summary>
    /// A click-through, never-activated, always-on-top window on this monitor: the whole monitor, or a box in its centre.
    /// It's placed in physical pixels after it's created, so it lands right whatever each monitor's scale is.
    /// </summary>
    private static Window Show(UIElement content, MonitorInfo m, bool sizeToBounds)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Focusable = false,
            Content = content, Opacity = 0, Title = "Identify monitor", Left = -32000, Top = -32000, Width = 10, Height = 10,
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
        w.Show();
        var b = m.Bounds;
        int bw = b.Right - b.Left, bh = b.Bottom - b.Top;
        int width = sizeToBounds ? bw : Math.Min(bw, Math.Max(320, bw * 2 / 5));
        int height = sizeToBounds ? bh : Math.Min(bh, Math.Max(240, bh * 2 / 5));
        int x = b.Left + (bw - width) / 2, y = b.Top + (bh - height) / 2;
        var hwnd = new WindowInteropHelper(w).Handle;
        // Twice: the first move can change the window's scale (another monitor's DPI), which resizes it.
        for (int i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, HwndTopmost, x, y, width, height, Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        w.Opacity = 1;
        return w;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MonitorWindowControl;

/// <summary>
/// The tray icon, drawn to suit the taskbar like Windows' own tray icons: a white monitor-with-a-house on a dark
/// taskbar, a black one on a light taskbar. In a contrast theme the full-colour app icon is used instead.
/// </summary>
internal static class TrayIconArt
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>A new icon (the caller disposes it) at <paramref name="size"/> pixels for a light or dark taskbar.</summary>
    public static Icon Create(bool lightTaskbar, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float k = size / 16f;
            var ink = lightTaskbar ? Color.FromArgb(255, 28, 28, 28) : Color.White;
            using var pen = new Pen(ink, Math.Max(1f, 1.3f * k));
            using var brush = new SolidBrush(ink);
            g.DrawRectangle(pen, 1.5f * k, 2f * k, 13f * k, 9f * k);                 // the monitor
            g.DrawLine(pen, 5f * k, 14f * k, 11f * k, 14f * k);                      // its stand
            g.DrawLine(pen, 8f * k, 11f * k, 8f * k, 14f * k);
            g.FillPolygon(brush, new[] { new PointF(5f * k, 7f * k), new PointF(8f * k, 4.2f * k), new PointF(11f * k, 7f * k) }); // the house
            g.FillRectangle(brush, 6f * k, 6.8f * k, 4f * k, 2.7f * k);
        }
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone(); // (owns its own copy of the handle)
        }
        finally { DestroyIcon(handle); }
    }
}

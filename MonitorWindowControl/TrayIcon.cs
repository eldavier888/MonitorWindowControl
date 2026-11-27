using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MonitorWindowControl;

/// <summary>
/// Notification-area icon: double-click opens; menu has Open, Move new windows (pause), Monitors ("treat as switched
/// off", two clicks from the couch), Exit.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int MaxTipLength = 127; // NotifyIcon.Text limit
    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _monitorsItem;
    private readonly Icon? _iconImage;
    private readonly Font _boldFont;

    public TrayIcon(Engine engine, Action open, Action exit)
    {
        _engine = engine;

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            _iconImage = s != null ? new Icon(s, SystemInformation.SmallIconSize) : null;

        _menu = new ContextMenuStrip();
        using (var menuFont = SystemFonts.MenuFont ?? new Font("Segoe UI", 9f)) _boldFont = new Font(menuFont, FontStyle.Bold);
        var openItem = new ToolStripMenuItem("Open", null, (_, _) => open()) { Font = _boldFont };
        _enabledItem = new ToolStripMenuItem("Move new windows", null, (_, _) => _engine.SetEnabled(!_engine.Settings.Enabled));
        _monitorsItem = new ToolStripMenuItem("Monitors");
        _monitorsItem.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false }); // filled when the menu opens
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => exit());
        _menu.Items.AddRange(new ToolStripItem[] { openItem, _enabledItem, _monitorsItem, new ToolStripSeparator(), exitItem });
        _menu.Opening += (_, _) => FillMonitorsMenu();

        _icon = new NotifyIcon
        {
            Icon = _iconImage ?? SystemIcons.Application,
            Text = "Monitor Window Control",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };

        _engine.Changed += Update;
        _engine.Notice += ShowBalloon;
        Update();
    }

    private void Update()
    {
        bool on = _engine.Settings.Enabled;
        _enabledItem.Checked = on;
        string tip = !on ? "Monitor Window Control (paused)"
            : _engine.IsPausedForRemoteDesktop ? "Monitor Window Control (paused: Remote Desktop)" : "Monitor Window Control";
        var off = _engine.Monitors.Where(m => m.IsSwitchedOff).Select(m => $"{m.Name}: off").ToList();
        if (off.Count > 0) tip += $" ({string.Join(", ", off)})";
        _icon.Text = tip.Length <= MaxTipLength ? tip : tip[..(MaxTipLength - 1)] + "…";
    }

    /// <summary>
    /// Monitors ▸ "Treat as switched off": one checkable entry per monitor (until you turn it back on), plus
    /// "For 1 hour ▸". Built each time the menu opens, so it always shows the monitors connected now.
    /// </summary>
    private void FillMonitorsMenu()
    {
        foreach (ToolStripItem old in _monitorsItem.DropDownItems.Cast<ToolStripItem>().ToList()) old.Dispose();
        _monitorsItem.DropDownItems.Clear();
        var monitors = _engine.Monitors.Where(m => !m.IsDuplicated).ToList();
        _monitorsItem.Visible = monitors.Count > 1;
        if (monitors.Count < 2) return;

        _monitorsItem.DropDownItems.Add(new ToolStripMenuItem("Treat as switched off:") { Enabled = false });
        foreach (var m in monitors)
        {
            string text = Escape(m.Name) + (m.SwitchedOffUntilUtc is DateTime until ? $" (until {until.ToLocalTime():t})" : "");
            var item = new ToolStripMenuItem(text) { Checked = m.IsSwitchedOff };
            item.Click += (_, _) => _engine.SetMonitorSwitchedOff(m, !m.IsSwitchedOff);
            _monitorsItem.DropDownItems.Add(item);
        }

        var forAnHour = new ToolStripMenuItem("For 1 hour");
        foreach (var m in monitors.Where(m => !m.IsSwitchedOff))
            forAnHour.DropDownItems.Add(new ToolStripMenuItem(Escape(m.Name), null,
                (_, _) => _engine.SetMonitorSwitchedOff(m, true, Engine.OffForAnHour)));
        forAnHour.Enabled = forAnHour.DropDownItems.Count > 0;
        _monitorsItem.DropDownItems.Add(new ToolStripSeparator());
        _monitorsItem.DropDownItems.Add(forAnHour);
    }

    /// <summary>A monitor name as menu text ("&amp;" would otherwise underline the next letter).</summary>
    private static string Escape(string text) => text.Replace("&", "&&");

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _engine.Changed -= Update;
        _engine.Notice -= ShowBalloon;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage?.Dispose();
        _boldFont.Dispose();
    }
}

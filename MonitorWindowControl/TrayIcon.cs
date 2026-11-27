using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace MonitorWindowControl;

/// <summary>Notification-area icon: double-click opens; menu has Open, Move new windows (pause), Exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
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
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => exit());
        _menu.Items.AddRange(new ToolStripItem[] { openItem, _enabledItem, new ToolStripSeparator(), exitItem });

        _icon = new NotifyIcon
        {
            Icon = _iconImage ?? SystemIcons.Application,
            Text = "Monitor Window Control",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };

        _engine.Changed += Update;
        Update();
    }

    private void Update()
    {
        bool on = _engine.Settings.Enabled;
        _enabledItem.Checked = on;
        _icon.Text = !on ? "Monitor Window Control (paused)"
            : _engine.IsPausedForRemoteDesktop ? "Monitor Window Control (paused: Remote Desktop)" : "Monitor Window Control";
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _engine.Changed -= Update;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage?.Dispose();
        _boldFont.Dispose();
    }
}

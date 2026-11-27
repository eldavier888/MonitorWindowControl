using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace MonitorWindowControl;

/// <summary>Notification-area icon: double-click opens; menu has Open, Enabled, Exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly Icon? _iconImage;

    public TrayIcon(Engine engine, Action open, Action exit)
    {
        _engine = engine;

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            _iconImage = s != null ? new Icon(s, SystemInformation.SmallIconSize) : null;

        _menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open", null, (_, _) => open()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => _engine.SetEnabled(!_engine.Settings.Enabled));
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
        _icon.Text = on ? "Monitor Window Control" : "Monitor Window Control (paused)";
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _engine.Changed -= Update;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage?.Dispose();
    }
}

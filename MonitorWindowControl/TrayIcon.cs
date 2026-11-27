using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MonitorWindowControl;

/// <summary>
/// Notification-area icon: double-click opens. The menu has, for the last app you used, "Always open on…"; then Open,
/// Move new windows (pause), Move open windows to their monitors, Bring all windows to…, Open a group (app groups),
/// Monitors (Identify, "treat as switched off", two clicks from the couch, and Disconnect from Windows / Connect), Exit. Notifications from the engine show as balloons; selecting one undoes it.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int MaxTipLength = 127; // NotifyIcon.Text limit
    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _appItem;
    private readonly ToolStripSeparator _appSeparator;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _tidyItem;
    private readonly ToolStripMenuItem _gatherItem;
    private readonly ToolStripMenuItem _monitorsItem;
    private readonly ToolStripMenuItem _groupsItem;
    private readonly Icon? _iconImage;
    private readonly Font _boldFont;
    private readonly Action<string, string> _openApp;
    private readonly Action<Engine.UndoOutcome> _undone;
    private readonly Action _identify;
    private readonly Action<MonitorInfo> _disconnect;
    private readonly Action<DisplayTopology.Disconnected> _connect;
    private readonly Action<AppGroup> _openGroup;
    private Image? _appImage;
    private int? _balloonActivity; // the Recent activity event the balloon on screen is about (selecting it undoes it)

    /// <param name="openApp">Opens the window on the Apps page with this app (key, name) selected.</param>
    /// <param name="undone">Called after a balloon was selected to undo something.</param>
    /// <param name="identify">Shows each monitor's number and name on it for a few seconds.</param>
    /// <param name="disconnect">Disconnect from Windows (asks first, then offers to keep it).</param>
    /// <param name="connect">Connects a monitor that's disconnected from Windows (then offers to keep it).</param>
    /// <param name="openGroup">Opens an app group's apps, each on its monitor.</param>
    public TrayIcon(Engine engine, Action open, Action exit, Action<string, string> openApp, Action<Engine.UndoOutcome> undone, Action identify,
        Action<MonitorInfo> disconnect, Action<DisplayTopology.Disconnected> connect, Action<AppGroup> openGroup)
    {
        _engine = engine;
        _openApp = openApp;
        _undone = undone;
        _identify = identify;
        _disconnect = disconnect;
        _connect = connect;
        _openGroup = openGroup;

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            _iconImage = s != null ? new Icon(s, SystemInformation.SmallIconSize) : null;

        _menu = new ContextMenuStrip();
        using (var menuFont = SystemFonts.MenuFont ?? new Font("Segoe UI", 9f)) _boldFont = new Font(menuFont, FontStyle.Bold);
        _appItem = new ToolStripMenuItem("") { Visible = false };
        _appSeparator = new ToolStripSeparator { Visible = false };
        var openItem = new ToolStripMenuItem("Open", null, (_, _) => open()) { Font = _boldFont };
        _enabledItem = new ToolStripMenuItem("Move new windows", null, (_, _) => _engine.SetEnabled(!_engine.Settings.Enabled));
        _tidyItem = new ToolStripMenuItem("Move open windows to their monitors", null, (_, _) => _engine.TidyUp(includeMoved: false));
        _gatherItem = new ToolStripMenuItem("Bring all windows to");
        _groupsItem = new ToolStripMenuItem("Open a group") { Visible = false };
        _monitorsItem = new ToolStripMenuItem("Monitors");
        _monitorsItem.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false }); // filled when the menu opens
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => exit());
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _appItem, _appSeparator, openItem, _enabledItem, _tidyItem, _gatherItem, _groupsItem, _monitorsItem, new ToolStripSeparator(), exitItem,
        });
        _menu.Opening += (_, _) =>
        {
            FillAppMenu();
            FillTidyItems();
            FillGroupsMenu();
            FillMonitorsMenu();
        };

        _icon = new NotifyIcon
        {
            Icon = _iconImage ?? SystemIcons.Application,
            Text = "Monitor Window Control",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => UndoFromBalloon();
        _icon.BalloonTipClosed += (_, _) => _balloonActivity = null;

        _engine.Changed += Update;
        _engine.Notice += ShowBalloon;
        _engine.ActivityNotice += ShowActivity;
        Update();
    }

    private void Update()
    {
        bool on = _engine.Settings.Enabled;
        _enabledItem.Checked = on;
        _enabledItem.Enabled = _engine.Policy.Enabled == null; // (set by your organization)
        string tip = !on ? "Monitor Window Control (paused)"
            : _engine.IsPausedForRemoteDesktop ? "Monitor Window Control (paused: Remote Desktop)"
            : _engine.IsPausedForGame ? "Monitor Window Control (paused: game running)" : "Monitor Window Control";
        var off = _engine.Monitors.Where(m => m.IsSwitchedOff).Select(m => $"{m.Name}: off").ToList();
        if (off.Count > 0) tip += $" ({string.Join(", ", off)})";
        _icon.Text = tip.Length <= MaxTipLength ? tip : tip[..(MaxTipLength - 1)] + "…";
    }

    /// <summary>
    /// The top of the menu: the last app you used ("Chrome · on 1 · Main") with "Always open on" ▸ each monitor, Where I
    /// used it last, Never move this app, and More choices… (the Apps page). Choosing a monitor also moves its open windows.
    /// </summary>
    private void FillAppMenu()
    {
        DisposeItems(_appItem);
        _appItem.Image = null;
        _appImage?.Dispose(); _appImage = null;
        var fg = _engine.LastForegroundApp();
        bool show = fg != null && _engine.Monitors.Count > 0;
        _appItem.Visible = _appSeparator.Visible = show;
        if (!show) return;

        string key = fg!.App.Key, name = fg.App.DisplayName;
        var rule = _engine.Settings.Find(key);
        _appItem.Text = Escape(fg.Monitor != null ? $"{name} · on {fg.Monitor.Name}" : name);
        _appImage = IconOf(fg.ExePath);
        _appItem.Image = _appImage;

        var items = _appItem.DropDownItems;
        items.Add(new ToolStripMenuItem($"Always open {Escape(name)} on:") { Enabled = false });
        bool setMonitor = rule is { Pinned: true, NeverMove: false, FollowLastUsed: false, FollowMouse: false };
        foreach (var m in _engine.Monitors)
        {
            var item = new ToolStripMenuItem(Escape(m.Label))
            {
                Checked = setMonitor && _engine.Resolve(rule!)?.DeviceName == m.DeviceName,
            };
            item.Click += (_, _) => _engine.Pin(key, m, name);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        var lastUsed = new ToolStripMenuItem("Where I used it last") { Checked = rule?.FollowLastUsed == true };
        lastUsed.Click += (_, _) =>
        {
            // Every monitor, except monitors kept for chosen apps (the same as Save with nothing ticked on the Apps page).
            var allowed = _engine.Monitors.Where(m => !m.OnlyChosenApps).ToList();
            if (allowed.Count == 0) allowed = _engine.Monitors.ToList();
            _engine.SetFollowLastUsed(key, allowed, fg.Monitor, name);
        };
        items.Add(lastUsed);
        var never = new ToolStripMenuItem("Never move this app") { Checked = rule?.NeverMove == true };
        never.Click += (_, _) => _engine.Pin(key, null, name);
        items.Add(never);
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("More choices…", null, (_, _) => _openApp(key, name)));
    }

    /// <summary>The app's icon for the menu (from its program file), or null.</summary>
    private static Image? IconOf(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            return icon?.ToBitmap();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// "Move 3 windows to their monitors" (with a preview count, worked out when the menu opens) and "Bring all windows
    /// to" ▸ each monitor, the one with the mouse pointer first.
    /// </summary>
    private void FillTidyItems()
    {
        int n = _engine.TidyCount(includeMoved: false);
        _tidyItem.Text = n > 0 ? $"Move {n} open window{(n == 1 ? "" : "s")} to their monitors" : "Open windows are on their monitors";
        _tidyItem.Enabled = n > 0;

        DisposeItems(_gatherItem);
        var monitors = _engine.Monitors.ToList();
        _gatherItem.Visible = monitors.Count > 1;
        if (monitors.Count < 2) return;
        string here = Screen.FromPoint(Cursor.Position).DeviceName;
        foreach (var m in monitors.OrderByDescending(m => string.Equals(m.DeviceName, here, StringComparison.OrdinalIgnoreCase)))
        {
            bool isHere = string.Equals(m.DeviceName, here, StringComparison.OrdinalIgnoreCase);
            _gatherItem.DropDownItems.Add(new ToolStripMenuItem(Escape(m.Label) + (isHere ? " (this monitor)" : ""), null,
                (_, _) => _engine.MoveWindowsTo(m, null)));
        }
    }

    /// <summary>
    /// Monitors ▸ "Treat as switched off": one checkable entry per monitor (until you turn it back on), plus
    /// "For 1 hour ▸". Built each time the menu opens, so it always shows the monitors connected now.
    /// </summary>
    private void FillMonitorsMenu()
    {
        DisposeItems(_monitorsItem);
        var monitors = _engine.Monitors.Where(m => !m.IsDuplicated).ToList();
        _monitorsItem.Visible = _engine.Monitors.Count > 0;
        _monitorsItem.DropDownItems.Add(new ToolStripMenuItem("Identify", null, (_, _) => _identify()));
        if (monitors.Count < 2) { FillConnectItems(monitors); return; } // (Connect matters most with one monitor left)
        _monitorsItem.DropDownItems.Add(new ToolStripSeparator());

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
        FillConnectItems(monitors);
    }

    /// <summary>
    /// Monitors ▸ "Disconnect from Windows" ▸ each monitor but the main one, and "Connect" for monitors that are plugged in
    /// but disconnected from Windows. Both open the settings window, which asks first and offers to undo it.
    /// </summary>
    private void FillConnectItems(System.Collections.Generic.List<MonitorInfo> monitors)
    {
        var disconnect = new ToolStripMenuItem("Disconnect from Windows");
        foreach (var m in monitors.Where(m => m.CanDisconnect))
            disconnect.DropDownItems.Add(new ToolStripMenuItem(Escape(m.Name) + "…", null, (_, _) => _disconnect(m)));
        disconnect.Enabled = disconnect.DropDownItems.Count > 0;
        _monitorsItem.DropDownItems.Add(new ToolStripSeparator());
        _monitorsItem.DropDownItems.Add(disconnect);
        foreach (var d in DisplayTopology.ListDisconnected())
            _monitorsItem.DropDownItems.Add(new ToolStripMenuItem($"Connect {Escape(d.Name)}", null, (_, _) => _connect(d)));
    }

    /// <summary>"Open a group" ▸ each app group (hidden while there are none).</summary>
    private void FillGroupsMenu()
    {
        DisposeItems(_groupsItem);
        var groups = _engine.Settings.Groups.ToList();
        _groupsItem.Visible = groups.Count > 0;
        foreach (var g in groups)
            _groupsItem.DropDownItems.Add(new ToolStripMenuItem(Escape(g.Name), null, (_, _) => _openGroup(g)));
    }

    private static void DisposeItems(ToolStripMenuItem parent)
    {
        foreach (ToolStripItem old in parent.DropDownItems.Cast<ToolStripItem>().ToList()) old.Dispose();
        parent.DropDownItems.Clear();
    }

    /// <summary>A monitor or app name as menu text ("&amp;" would otherwise underline the next letter).</summary>
    private static string Escape(string text) => text.Replace("&", "&&");

    public void ShowBalloon(string title, string text)
    {
        _balloonActivity = null;
        _icon.ShowBalloonTip(4000, title, text.Length > 0 ? text : "Monitor Window Control", ToolTipIcon.None);
    }

    /// <summary>A Recent activity event as a notification. If it can be undone, selecting the notification undoes it.</summary>
    private void ShowActivity(ActivityEntry entry)
    {
        string text = entry.Reason.Length > 0 ? entry.Reason : "Monitor Window Control";
        if (entry.CanUndo) text += "\nSelect this notification to undo it.";
        _icon.ShowBalloonTip(5000, entry.Text, text, ToolTipIcon.None);
        _balloonActivity = entry.CanUndo ? entry.Id : null;
    }

    private void UndoFromBalloon()
    {
        if (_balloonActivity is not int id) return;
        _balloonActivity = null;
        _undone(_engine.Undo(id));
    }

    public void Dispose()
    {
        _engine.Changed -= Update;
        _engine.Notice -= ShowBalloon;
        _engine.ActivityNotice -= ShowActivity;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage?.Dispose();
        _appImage?.Dispose();
        _boldFont.Dispose();
    }
}

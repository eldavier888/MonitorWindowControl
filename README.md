# Monitor Window Control

Monitor Window Control opens each app's windows on the monitor you actually use that app on.
It sits in the system tray, watches for new windows, and moves each one to its app's "home" monitor.
It learns homes by itself: when you use an app on a monitor for a few seconds, that monitor becomes the app's home.

<p align="center">
  <img src="docs/screenshot.png" alt="Monitor Window Control window: toggles, the three connected monitors by model name, and a list of apps with the monitor each one opens on" width="560">
</p>

**[⬇ Download MonitorWindowControl.exe](../../releases/latest)** (Windows 10/11, single file, no install)

## Why

With several displays, Windows often opens new windows on the wrong one. Think of a TV behind you that you
only use for streaming. Unplugging it isn't an answer, because you *do* want your streaming apps there.
Windows today either restores a window's last position or falls back to the primary monitor, and it has no
idea that an app "belongs" on a particular screen.

Monitor Window Control is a working prototype of how this could work in Windows itself:

- **A home monitor per app.** New windows open on the app's home, or on the main monitor if that one isn't connected.
- **Learned, not configured.** The monitor where you actively use an app (the focused window, for a few seconds) becomes its home.
  Background windows never count, so a window forgotten on the TV can't teach it the wrong thing.
- **Easy overrides.** You can pin an app to a monitor, or mark it "never move".
- **Never fights you.** Each new window is placed once. If you drag it somewhere else, it stays there.

Existing features solve different problems. "Remember window locations based on monitor connection" restores
windows when a monitor reconnects. Snap Layouts, FancyZones and Workspaces arrange windows. None of them say
which monitor an app's new windows should open on.

## Run it

1. Download `MonitorWindowControl.exe` from the [latest release](../../releases/latest) and put it anywhere you like
   (for example `C:\Tools\MonitorWindowControl\`).
   It is one self-contained file: nothing else needs to be installed, and it doesn't need admin rights.
2. Double-click it. The first time, its window opens. After that it starts quietly in the tray
   (look for the blue monitor-with-a-house icon; it may be under the **^** arrow next to the clock).
3. To have it start when you sign in, turn on **Start with Windows** in its window.

Coming from the earlier AutoHotkey version (it was called WindowHome)? Put its `WindowHome.ini` in the **same folder as MonitorWindowControl.exe**
before the very first run, and Monitor Window Control imports it (homes, pinned apps, "never move" apps, and settings).
Stop the AutoHotkey script so the two don't both move windows.

Requirements: Windows 10 or 11, 64-bit. The exe isn't code-signed, so Windows SmartScreen may warn the first
time. Choose *More info → Run anyway*.

## Using it

- **Tray icon**: double-click to open the window. Right-click for *Open*, *Enabled* (pause/resume), *Exit*.
  Closing the window just puts it back in the tray.
- **Enabled**: moves new windows to their app's monitor.
- **Learn automatically**: only the window you're actually using counts. If the same app stays focused on the
  same monitor for two checks in a row (about 4 seconds), that monitor becomes its home. Windows sitting in the
  background never teach it anything.
- **Apps it hasn't learned yet**: open them on the main monitor, or leave them where Windows puts them.
- **Your monitors**: shows each monitor by its model name; "(main)" is the one set as main display in Windows.
- **Apps and where they open**: select an app, pick a monitor (or *Never move this app*) and press
  **Change monitor**. That's "set by you": learning will never change it. **Forget** removes one app;
  **Forget all…** removes everything (asks first).
- **Open log**: every move, learn, pin and forget is written to a log with the time.

How it decides, for each new window (about 0.4 s after it appears, because apps often reposition themselves
right after opening):

| The app is... | Monitor Window Control... |
|---|---|
| learned or set by you | moves it to that monitor. If that monitor is off/disconnected, to the main monitor |
| set to "Never move" | leaves it alone |
| not known yet | moves it to the main monitor (or leaves it, per your setting) |

Each window is handled once. If you drag it somewhere else afterwards, Monitor Window Control won't move it back.
Minimized and fullscreen windows are skipped. Store apps (Netflix, Disney+, Calculator…) all run inside one
Windows process, so they're told apart by window title and shown as "(Store app)".
Windows that run as administrator can't be moved by a normal app; Monitor Window Control skips them and notes it in the log.

## Where things are stored

`%AppData%\MonitorWindowControl\` (paste that into the Explorer address bar):

- `settings.json`: settings and the list of apps and their monitors.
- `monitorwindowcontrol.log`: the activity log (kept to about 1 MB).

To start completely fresh: Exit Monitor Window Control from the tray, delete that folder, start it again.

## Build it yourself (optional)

You only need this if you change the code.

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer).
2. Open PowerShell in the `MonitorWindowControl` folder (the one with `MonitorWindowControl.csproj`) and run:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ..\publish
```

The result is `publish\MonitorWindowControl.exe` (about 145 MB, because the whole .NET runtime is inside it).

Project layout:

- `MonitorWindowControl\Engine.cs`: detects new windows, decides and moves them, and does the learning
- `MonitorWindowControl\Monitors.cs`: lists monitors and their model names
- `MonitorWindowControl\WindowInfo.cs`: decides which windows are real app windows, and which app they belong to
- `MonitorWindowControl\Storage.cs`: settings file, log, Start with Windows, import of the old `.ini`
- `MonitorWindowControl\MainWindow.xaml`, `TrayIcon.cs`, `App.xaml.cs`: the window, tray icon and app startup
- `MonitorWindowControl\tools\make-icon.ps1`: regenerates `app.ico`; `tools\rebuild-and-run.ps1`: developer helper

Tech: C# / .NET 8, WPF with the WPF-UI library (Windows 11 Fluent look, Mica, follows light/dark mode),
PerMonitorV2 DPI awareness.

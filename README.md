# Monitor Window Control

Monitor Window Control opens each app's windows on the monitor you actually use that app on.
It sits in the system tray, watches for new windows, and moves each one to its app's "home" monitor.
It learns homes by itself: when you use an app on a monitor for a few seconds, that monitor becomes the app's home.

<p align="center">
  <img src="docs/screenshot.png" alt="Monitor Window Control window: toggles, the three connected monitors by model name, and a list of apps with the monitor each one opens on" width="560">
</p>

**[⬇ Download MonitorWindowControl.exe](../../releases/latest/download/MonitorWindowControl.exe)** (8 MB, Windows 10/11, no install)

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
   (for example `C:\Tools\MonitorWindowControl\`). It's a single file that needs no installing and no admin rights.
   It uses the free [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 8 or newer, which
   many PCs already have. If yours doesn't, Windows offers to download it the first time you start the app.
2. Double-click it. The first time, its window opens. After that it starts quietly in the tray
   (look for the blue monitor-with-a-house icon; it may be under the **^** arrow next to the clock).
3. To have it start when you sign in, turn on **Start with Windows** in its window.

Coming from the earlier AutoHotkey version (it was called WindowHome)? Put its `WindowHome.ini` in the **same folder as MonitorWindowControl.exe**
before the very first run, and Monitor Window Control imports it (homes, pinned apps, "never move" apps, and settings).
Stop the AutoHotkey script so the two don't both move windows.

Requirements: Windows 10 or 11, 64-bit. The exe isn't code-signed, so Windows SmartScreen may warn the first
time. Choose *More info → Run anyway*.

## How it runs: your choice

Something has to notice a new window to move it, because Windows has no "open this app on that monitor"
setting of its own. So you choose how much runs in the background (**How it runs**, at the top of the window):

| Mode | What runs in the background | What gets placed |
|---|---|---|
| **In the tray** (default) | The app, in the tray (about 0% CPU, ~30 MB) | Every new window. Learns where you use apps. |
| **Tiny invisible helper** | Only a small helper: no window, no tray icon (about 0% CPU, ~5 MB). The settings window runs only while it's open. | Every new window. Learns where you use apps. |
| **Nothing in the background** | Nothing at all | Only apps you open from their **launch shortcuts** (Start menu → Monitor Window Control). A helper starts with the app, places its window, and exits after a few seconds. Windows opened any other way (links, apps opening extra windows, autostart) aren't moved, and nothing is learned automatically. |

To make a launch shortcut: select an app in the list, save where it opens, then press **Create launch shortcut**.
It appears in Start menu → Monitor Window Control (right-click it → *Pin to taskbar* if you like); you can also
put one on the desktop. Your existing shortcuts are never changed. Launch shortcuts work in every mode.

## Using it

- **Tray icon** (tray mode): double-click to open the window. Right-click for *Open*, *Enabled* (pause/resume), *Exit*.
  Closing the window just puts it back in the tray. In the other modes there's no tray icon: start
  `MonitorWindowControl.exe` again to change settings, and closing its window closes it completely.
- **Enabled**: moves new windows to their app's monitor.
- **Learn automatically**: only the window you're actually using counts. If the same app stays focused on the
  same monitor for two checks in a row (about 4 seconds), that monitor becomes its home. Windows sitting in the
  background never teach it anything.
- **Apps it hasn't learned yet**: open them on the main monitor, or leave them where Windows puts them.
- **Your monitors**: shows each monitor by its model name; "(main)" is the one set as main display in Windows.
  - **Name & number**: give a monitor your own name ("TV") and number ("3"). It then shows as "3 · TV"
    everywhere in the app, and your numbers set the order. (Windows keeps its own display numbers; apps can't change those.)
  - **Make main**: makes that monitor the main display in Windows itself, the same as Settings → Display →
    "Make this my main display". The taskbar clock and Start menu move there, and apps it hasn't learned yet open there.
- **Apps and where they open**: lists your saved apps **and every app that's open right now** ("open now").
  Select one, choose where it opens and press **Save**:
  - **a monitor**: it always opens there, and its open windows move there right away;
  - **Where I used it last…**: tick the monitors it may use (for example the side monitor and the TV). It opens on
    whichever of those you used it on most recently;
  - **Never move this app**: leave it alone.

  Your choice is "set by you": learning never overrides it. **Forget** removes one app;
  **Forget all…** removes everything (asks first).
- **Open log**: every move, learn, pin and forget is written to a log with the time.

How it decides, for each new window (about 0.4 s after it appears, because apps often reposition themselves
right after opening):

| The app is... | Monitor Window Control... |
|---|---|
| learned or set by you | moves it to that monitor. If that monitor is off/disconnected, to the main monitor |
| set to "Where I used it last" | moves it to the monitor (of the ones you ticked) where you last used it |
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

The tiny helper is unpacked to `%LocalAppData%\MonitorWindowControl\MonitorWindowControlHelper.exe` when a mode
or shortcut needs it.

To start completely fresh: Exit Monitor Window Control (tray → Exit, or set "How it runs" to tray first),
delete those folders and Start menu → Monitor Window Control, then start it again.

## Build it yourself (optional)

You only need this if you change the code.

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) 10 (or newer), and Visual Studio's
   **Desktop development with C++** tools (the helper is compiled to native code and needs the C++ linker).
2. Open PowerShell in this folder and run:

```powershell
.\build.ps1
```

The result is `publish\MonitorWindowControl.exe` (about 8 MB, needs the .NET Desktop Runtime 8 or newer;
the 2.4 MB native helper is packed inside it).

Project layout:

- `MonitorWindowControl\Engine.cs`: detects new windows, decides and moves them, and does the learning
  (shared by the app and the helper; runs on whatever `IEngineHost` the program provides, see `Host.cs`)
- `MonitorWindowControl\Monitors.cs`: lists monitors and their model names
- `MonitorWindowControl\WindowInfo.cs`: decides which windows are real app windows, which app they belong to,
  and how to start that app again
- `MonitorWindowControl\Storage.cs`: settings file (shared safely by the app and the helper), log, Start with Windows,
  import of the old `.ini`
- `MonitorWindowControl\MainWindow.xaml`, `TrayIcon.cs`, `App.xaml.cs`: the window, tray icon, app startup and run modes
- `MonitorWindowControl\HelperManager.cs`, `ShortcutManager.cs`: unpacking/starting the helper; launch shortcuts
- `MonitorWindowControlHelper\`: the tiny native helper (`Program.cs`: background and `--launch` modes;
  `NativeHost.cs`: its Windows message loop)
- `MonitorWindowControl\tools\make-icon.ps1`: regenerates `app.ico`

Tech: C# / .NET 8, WPF with the WPF-UI library (Windows 11 Fluent look, Mica, follows light/dark mode),
PerMonitorV2 DPI awareness. The helper: C# compiled with .NET 10 NativeAOT (no .NET needed to run it).

## Privacy

Monitor Window Control collects nothing and never connects to the internet; everything stays on your PC. See [PRIVACY.md](PRIVACY.md).


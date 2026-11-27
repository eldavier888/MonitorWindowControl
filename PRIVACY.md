# Privacy statement — Monitor Window Control

_Last updated: 27 November 2026_

Monitor Window Control does **not collect, send or share any personal data**. It has no accounts, no analytics,
no telemetry, no ads, and it never connects to the internet.

## What it uses, and where it stays

To put each app's windows on the right monitor, the app looks at information on your own PC only:

- **Windows and apps:** the title of new windows and the name and location of the program that opened them
  (for example `chrome.exe`), to recognise which app a window belongs to.
- **Monitors:** the model names, positions and sizes of your connected displays.

It saves only what it needs to work, **on your PC**:

- `%AppData%\MonitorWindowControl\settings.json`: your settings, the apps you've set or it has learned, and
  the monitor each one opens on. This includes the app name and, for launch shortcuts, the app's program path.
- `%AppData%\MonitorWindowControl\monitorwindowcontrol.log`: a local activity log (which app was moved where,
  with window titles), kept to about 1 MB.
- If you turn them on: a "start with Windows" entry in your user registry
  (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), and launch shortcuts in
  Start menu → Monitor Window Control (and optionally on your desktop).
- The small helper program is unpacked to `%LocalAppData%\MonitorWindowControl`.

None of this ever leaves your computer. You can read or delete these files at any time. To remove everything the
app stored: press **Forget all…** (it also deletes your launch shortcuts, including any on the desktop), turn off
"Start with Windows", exit the app, then delete the two folders above and Start menu → Monitor Window Control.

## Changing or removing your data

Use **Forget** / **Forget all** in the app, or delete the files listed above.

## Contact

Questions about privacy: open an issue at <https://github.com/eldavier888/MonitorWindowControl/issues>.

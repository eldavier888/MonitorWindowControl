# Privacy statement — Monitor Window Control

_Last updated: 27 November 2026_

Monitor Window Control does **not collect, send or share any personal data**. It has no accounts, no analytics,
no telemetry, no ads, and it never connects to the internet.

## What it uses, and where it stays

To put each app's windows on the right monitor, the app looks at information on your own PC only:

- **Windows and apps:** the title of new windows and the name and location of the program that opened them
  (for example `chrome.exe`), to recognise which app a window belongs to.
- **Monitors:** the model names, positions and sizes of your connected displays.
- **Policies:** settings your organization may set in `HKLM` / `HKCU\SOFTWARE\Policies\MonitorWindowControl` (read only).

It saves only what it needs to work, **on your PC**:

- `%AppData%\MonitorWindowControl\settings.json`: your settings, the apps you've set or it has learned, and
  the monitor each one opens on. This includes the app name and, for launch shortcuts, the app's program path.
- `%AppData%\MonitorWindowControl\monitorwindowcontrol.log`: a local activity log (which app was moved where,
  with window titles), kept to about 1 MB.
- If you turn them on: a "start with Windows" entry in your user registry
  (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), and launch shortcuts in
  Start menu → Monitor Window Control (and optionally on your desktop).
- The small helper program is unpacked to `%LocalAppData%\MonitorWindowControl`.
- `%AppData%\MonitorWindowControl\settings.YYYYMMDD.json`: daily backups of your settings (the last 5), so you can
  restore them.
- `%LocalAppData%\MonitorWindowControl\Crashes`: if the app or its helper ever stops after an unexpected error, a short
  crash report (the version, the Windows build, how it runs, the error, your monitors' model names and sizes, and the
  last lines of the activity log with window titles removed). The last 10 are kept.
- An entry in Settings → Apps → Installed apps (`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall`), so you
  can remove it from there.

Files you make yourself are saved only where you choose: **Export settings** (your settings and app list) and **Save
diagnostics** (a .zip with the log, settings, monitor setup and crash reports; the dialog lists them, removes window
titles unless you clear that, and leaves out the app list unless you include it). Nothing is sent: you decide whether
to share them.

None of this ever leaves your computer. You can read or delete these files at any time. To remove everything the
app stored: About → **Remove from this PC** with **Keep my settings and app list** cleared (or Settings → Apps →
Installed apps → Monitor Window Control → Uninstall).

## Changing or removing your data

Use **Forget** / **Forget all** in the app, **Remove from this PC**, or delete the files listed above.

## Contact

Questions about privacy: open an issue at <https://github.com/eldavier888/MonitorWindowControl/issues>.

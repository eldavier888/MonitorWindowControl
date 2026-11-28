# Roadmap: what's missing in Windows and in Monitor Window Control

A list of everything Windows 11 still lacks for multi-monitor window placement, and what Monitor Window Control
adds (or will add) to fill each gap. Researched from Windows' own features, Feedback Hub complaints, existing tools
(DisplayFusion, PowerToys, Actual Window Manager and others) and everyday use with a TV, a dock and several monitors.

Priority: **must** (you'd be surprised it's missing), **should**, **could**, **later** (needs code signing, the
Store or other infrastructure). Effort: S / M / L / XL.

| # | Item | Priority | Effort | Windows today |
|---|---|---|---|---|
| 0 | Send windows back when their monitor comes back (TV on, wake, redock) | must | M | Partly |
| 1 | Stable monitor identity from EDID and connector path, not \\.\DISPLAYn | must | M | Only internally: Windows keys its display configuration database (GraphicsDrivers\Configuration) on EDID identity, but it exposes only renumberable display numbers to users and apps |
| 2 | Hold placements while monitors are changing (topology generation counter) | must | S | No |
| 3 | 'Monitor is off' switch: keep new windows off a dark but connected TV | must | M | No |
| 4 | Bring windows home, gather them here, and rescue off-screen windows | must | M | No |
| 5 | Place windows of apps that opened before Monitor Window Control started | must | S | No |
| 6 | Late-uncloaking windows and virtual desktop awareness | must | S | No |
| 7 | Ordered fallback: 'If that monitor is off, use…' per app plus a global default | should | S | No |
| 8 | Remember where on the monitor: position, size and maximized per app | should | M | No |
| 9 | Open dialogs and pop-ups on the same monitor as their app | should | M | Partly |
| 10 | Undo, 'why it went there' and optional notices | should | M | No |
| 11 | Identify monitors and show a clickable monitor layout | should | M | Partly |
| 12 | Keyboard shortcuts: send this window to monitor N, and make it the app's home | should | M | Partly |
| 13 | Quick 'Always open on…' for the window in front, from the tray | should | S | No |
| 14 | Keep my main monitor (stop Windows promoting the TV) | should | S | No |
| 15 | 'Where my mouse is' as a placement choice | should | S | No |
| 16 | Pause while a game or presentation runs | should | S | Partly |
| 17 | 'What can open here' per monitor (reserve the TV for chosen apps) | should | S | No |
| 18 | Restore window positions after sleep and hibernate | should | L | No explicit snapshot; it relies on the same 'remember window locations' mechanism, which users report failing after hibernate |
| 19 | Correct size and DPI for moves between differently scaled monitors | should | M | Partly |
| 20 | Handle Remote Desktop and temporary displays (Miracast, virtual, DisplayLink) | should | M | No |
| 21 | First-run welcome: your monitors, your apps, how it runs | should | M | No |
| 22 | Export, import and automatic backups of settings | should | M | No |
| 23 | Restart the background helper if it stops, and say when it isn't running | should | M | Task Scheduler supports restart on failure; the app doesn't use it yet |
| 24 | Local crash reports and a 'Save diagnostics' bundle (no telemetry) | should | M | WER collects crashes only for registered apps, and LocalDumps needs admin configuration |
| 25 | Installed Apps entry and one-click 'Remove from this PC' | should | M | Windows has the Installed apps uninstall flow; this makes the app take part in it |
| 26 | Accessibility and theme conformance (high contrast, text size, announcements) | should | M | Windows provides the tooling (Accessibility Insights, Narrator); conformance is up to the app |
| 27 | Engine unit tests and a GitHub Actions build | should | L | N/A |
| 28 | Rules for some windows of an app (title or browser profile) | could | L | No |
| 29 | Different homes for different monitor setups (TV on vs off, docked vs undocked) | could | L | No |
| 30 | Disconnect or reconnect a monitor in one click ('Turn TV off for Windows') | could | M | Partly |
| 31 | Handle duplicated (mirrored) displays | could | S | Windows supports cloning but gives apps no notion that a home monitor is currently mirrored |
| 32 | Launch shortcuts for a specific monitor, with arguments | could | S | No |
| 33 | App groups: open a set of apps, each on its monitor | could | M | No (only PowerToys Workspaces, a separate install, with absolute positions) |
| 34 | Per-app timing for stubborn apps (Advanced) | could | S | No |
| 35 | Organisation policies (preset or lock settings through the registry) | could | M | No |
| 36 | Translated interface | could | L | N/A |
| 42 | Split a monitor into areas that work like separate monitors | could | M | No (Snap layouts and PowerToys FancyZones arrange windows, but apps can't have a home area, and nothing is learned per area) |
| 37 | Code-sign both executables (Azure Artifact Signing) | later | M | N/A |
| 38 | winget package | later | S | N/A |
| 39 | Microsoft Store (MSIX) with packaged startup tasks | later | L | N/A |
| 40 | Move administrator apps too (signed UIAccess helper, per-machine install) | later | L | No |
| 41 | Opt-in 'Check for updates' that keeps the no-network promise | later | M | The Store and winget update apps; portable apps get nothing |

## Must have

### 0. Send windows back when their monitor comes back (TV on, wake, redock)
**Status:** partly built: 'Ask me' is left out (a tray balloon can't hold Move and Leave buttons, and the helper has no tray icon), and the fake-monitor-source Engine tests wait for the test project (item 27).

*Monitor changes · effort M*

**Problem.** The TV gets switched off, Netflix opens on (or is pushed to) the main monitor, and when the TV comes back the window stays on the main monitor. Only new windows go to the TV again. Windows' own 'Remember window locations' only restores windows that were on the TV when it went away, it is unreliable after sleep and hibernate, and it does nothing for windows opened while the monitor was off.

**Windows today.** Partly. Settings > System > Display > Multiple displays > 'Remember window locations based on monitor connection' handles windows that were on the monitor at disconnect, inconsistently (failures after sleep or hibernate are widely reported). It does nothing for windows opened elsewhere while the monitor was missing.

**Design.** General > Placing windows gets a new card, 'When a monitor comes back', with a ComboBox: 'Move its apps' windows back' (default) / 'Ask me' / 'Leave them'. The engine already records _fallbackPlaced, the windows put on main only because their home wasn't connected. Extend it to a Dictionary<HWND, Displaced{AppKey, HomeRef, DroppedRect, ProcessStartTime}>. Also record, just before each display change, the windows that were on the monitor that disappeared (EnumWindows + MonitorFromWindow + GetWindowPlacement). After the monitor reappears, wait for RefreshMonitors to stop changing plus about 3 s. Then re-check each window and skip it if Windows already restored it, if the user moved it (rect != DroppedRect), if the HWND was reused (different process start time), or if it is minimized, fullscreen or elevated. Move the rest home with WindowMover.MoveTo, restoring the saved placement. Write one log line ('TV is back: moved Netflix, Plex to 3 · TV'). With 'Ask me', a tray balloon offers [Move] [Leave].

**Risks.** Fighting Windows' own restore: always re-check the position after the delay and skip windows already home. A TV that stays 'connected' while its panel is off never triggers this, which is why the 'Monitor is off' item exists. Needs Engine tests with a fake monitor source.

### 1. Stable monitor identity from EDID and connector path, not \\.\DISPLAYn
**Status:** partly built: everything in the design is built; the matching-order unit tests wait for the test project (item 27).

*Monitors · effort M*

**Problem.** FindMonitor matches by model name and falls back to the GDI device (\\.\DISPLAY2) only when two connected monitors share a model. The owner's two 21:9 monitors may well be the same model. Windows renumbers DISPLAYn when the TV toggles, after driver updates and after GPU resets, so homes, nicknames and numbers can silently swap between the main and side monitors.

**Windows today.** Only internally: Windows keys its display configuration database (GraphicsDrivers\Configuration) on EDID identity, but it exposes only renumberable display numbers to users and apps.

**Design.** Add MonitorRef.HardwareId, made from DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath (Monitors.cs already fetches this struct for the friendly name) plus edidManufactureId and edidProductCodeId. Add MonitorRef.Serial, the EDID serial read without admin from HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<hw>\<inst>\Device Parameters\EDID. Matching order in FindMonitor and Matches: device path, then manufacturer+product+serial (when the serial isn't 0), then today's name and device logic. Fill the new fields in silently on existing MonitorRefs the first time each monitor is seen, keeping the old fields so a downgrade still works. On the Monitors page, each card's secondary line adds the connection (HDMI, DisplayPort, USB-C, from outputTechnology). When two monitors share a model it adds 'Same model as 1 · Main: told apart by its serial number' or '…by the port it's plugged into'.

**Risks.** Cheap monitors and some TVs report serial 0 or identical EDIDs, and the device path changes when a cable moves to another port. Keep the name fallback, and never drop old fields during migration. Unit-test the matching order. Fast enough to run on every refresh because it uses no DDC.

### 2. Hold placements while monitors are changing (topology generation counter)
**Status:** partly built: everything in the design is built; the fake-monitor-source test waits for the test project (item 27).

*Stability · effort S*

**Problem.** Learning pauses for 30 s after a display change, but placement doesn't. When a TV switches on over HDMI-CEC, the PC wakes, or a dock reconnects, the monitor set changes over 5 to 20 s. A window placed in that window can be centred on a stale work area, land on a monitor that is going away, or get 'corrected' in Verify onto a renumbered DISPLAYn (Pending stores only a device string).

**Windows today.** No. Windows' own post-change rearranging is exactly what races with a placement made at that moment.

**Design.** No UI. Add an int _topologyGeneration in Engine, bumped in OnDisplaySettingsChanged and whenever RefreshMonitors sees a new signature, and store it in Pending. Before each SetWindowPos step and in Verify, if the generation changed, abort and requeue the window. From WM_DISPLAYCHANGE until about 2 s after the last change, keep new windows in _pending, cap the hold at about 3 s, and extend GiveUp for held windows. After MoveTo, check that the window rect intersects the target's work area (MonitorFromRect with MONITOR_DEFAULTTONULL) and pull it fully onto the screen if it doesn't. Log 'Held Spotify during a display change'.

**Risks.** Launching an app during wake feels up to 3 s slower. Keep the cap small. Test with a fake monitor source that changes between the two SetWindowPos calls.

### 3. 'Monitor is off' switch: keep new windows off a dark but connected TV
**Status:** partly built: the manual switch is built (Monitors page card and tray Monitors menu, until turned back on or for 1 hour); the optional DDC/CI 'Detect automatically' is not built, because it needs a background thread with slow calls and many TVs don't support it. The flag lives in the existing MonitorOptions entry instead of a renamed MonitorSettings.

*Monitor changes · effort M*

**Problem.** Many HDMI TVs, and Windows' Rapid Hot Plug Detection, keep a TV that was switched off with its remote 'connected'. Windows extends the desktop onto a black screen, the app still treats the TV as available, and Netflix (or the cursor, or a stray dialog) opens where nobody can see it. The same need appears when presenting: 'keep new windows off the projector for now'.

**Windows today.** No. Windows knows only connected or disconnected, and RHPD deliberately keeps a powered-off TV connected.

**Design.** Monitors page: each card's '…' menu gets 'Treat as switched off'. The tray menu gets a 'Monitors' submenu with a checkable 'TV is off' entry per monitor, so it takes two clicks from the couch. While a monitor is flagged off, ResolveTarget and the unknown-app path treat it as not connected (the normal fallback applies, and the window counts as displaced, so the 'comes back' feature returns it when the flag clears), and nothing is learned there. The card shows 'Switched off (set by you)' and the tray tooltip says 'TV: off'. Options in the flag menu: 'Until I turn it back on' / 'For 1 hour'. Optional, Advanced: 'Detect automatically when possible', polling DDC/CI VCP 0xD6 power mode (GetPhysicalMonitorsFromHMONITOR + GetVCPFeatureAndVCPFeatureReply) at most every 30 s, and only for monitors where a test read succeeded. The flag lives in a MonitorSettings entry (MonitorNickname renamed) so the helper sees it too.

**Risks.** DDC/CI is slow (50 to 200 ms) and many TVs don't implement it over HDMI, so the manual switch is the dependable path and DDC must never run on the placement path. Settings writes must go through the existing cross-process lock.

### 4. Bring windows home, gather them here, and rescue off-screen windows
**Status:** built in v1.6.0 (the Monitors page has a "Move its windows" button with a menu of the other monitors, as its cards have no '…' menu; the Apps page's "Move its windows there now" is in the selected app's options; "Bring all windows to" lists every monitor, the one with the mouse pointer first; apps it hasn't learned are only tidied off a monitor that can't be used; the off-screen rescue leaves "Never move" apps alone; in helper mode the helper does the moving, so it knows which windows you moved yourself).

*Window rescue · effort M*

**Problem.** Windows still end up in the wrong place: dumped on the main monitor when the TV went off, stranded on a dark TV, left at coordinates outside every monitor after a resolution change, or opened before the app was running. 'Never fights you' means nothing ever gathers them back. Today the only remedy is dragging each one, or using the Shift+right-click taskbar > Move > arrow keys trick.

**Windows today.** No. Win+Shift+Left/Right moves one window to the adjacent monitor, and Windows only pulls windows back on a real disconnect.

**Design.** Three commands built on the existing EnumWindows + WindowInfo.Classify + MoveTo. (1) 'Move open windows to their monitors': a General > 'Tidy up' card with a button that shows a preview count ('Moves 7 windows'), plus a tray item. It applies each app's rule once and skips Never move, minimized, fullscreen, elevated and cloaked windows, and windows the user moved by hand this session unless 'Include windows I moved' is ticked. (2) 'Bring all windows to this monitor' (the monitor under the cursor), in the tray menu. Each Monitors page card's '…' menu also gets 'Move its windows to [monitor ▾]'. Neither changes any app's home. (3) Automatic, after each display settle: any visible app window whose rect intersects no monitor (MonitorFromRect MONITOR_DEFAULTTONULL) goes to its app's home or the main monitor, with a log line. Apps page 'open now' rows get 'Move its windows here'.

**Risks.** Surprising the user with mass moves, so command 1 is manual with a preview, and only the off-screen rescue is automatic. Skip DWMWA_CLOAKED windows so other virtual desktops aren't disturbed. Minimized windows: adjust rcNormalPosition with SetWindowPlacement rather than restoring them.

### 5. Place windows of apps that opened before Monitor Window Control started
**Status:** built in v1.6.0 (the card is under Startup, next to Start with Windows, and works only with it on; 'foreground for more than about 3 s' is checked as 'the window in front, and you've used the keyboard or mouse more than 3 s after its app started', since Windows keeps no foreground history).

*Placement · effort S*

**Problem.** Startup apps (Discord, Steam, Teams, Spotify) and apps Windows reopens after an update restart often show their windows before the tray app or helper is running. Engine.Activate marks every visible window as already handled, so they stay on the primary monitor for the whole session.

**Windows today.** No. Startup apps restore their own saved positions, if they have any.

**Design.** General > Startup gets a checkbox under 'Start with Windows': 'Also place apps that opened while signing in' (on by default). In Activate, for visible app windows whose process start time (GetProcessTimes) is within about 2 minutes of now and within about 3 minutes of sign-in (session logon time from WTSQuerySessionInformation, or the system uptime), run the normal TryPlace once instead of adding them to _handled. Skip any window that has already been foreground for more than about 3 s, and windows already on their target. Log 'Placed 3 apps that opened at sign-in'.

**Risks.** Moving a window the user already started working in: the foreground-time check and the process-age limit keep this narrow. It must not apply when the user merely restarts the tray app mid-session (use the session logon time).

### 6. Late-uncloaking windows and virtual desktop awareness
**Status:** built in v1.6.0 (the tidy-up commands it mentions wait for item 4; the Apps list, learning, 'Where I last had it' and moving an app's open windows ignore other desktops).

*Stability · effort S*

**Problem.** Classify returns NotYet for cloaked windows, and placement gives up after GiveUpMs (4 s). Store apps under sign-in load, Electron splash-then-main windows, and windows created on another virtual desktop stay cloaked longer, become visible unplaced, and are never placed because they are already in _handled. Learning and the open-apps list can also pick up windows from other desktops.

**Windows today.** No. There is no placement rule at all, per desktop or otherwise.

**Design.** No UI. Add a second WinEvent hook for EVENT_OBJECT_UNCLOAKED (0x8018, outside the current DESTROY..SHOW range). Read DWMWA_CLOAKED and treat DWM_CLOAKED_SHELL (another desktop) as 'not yet' with no time limit, keyed on the window's first-seen time. A window counts as new when it uncloaks within about 30 s of first being seen. A desktop switch that uncloaks old windows never counts, because they are older. Use the documented IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop so learning, the tidy-up commands and GetOpenApps ignore windows on other desktops.

**Risks.** A desktop switch storms uncloak events, so the first-seen timestamp is essential. Never use the undocumented IVirtualDesktopManagerInternal, whose GUIDs change between builds. The IVirtualDesktopManager COM call must also work in the NativeAOT helper (use ComWrappers or a raw vtable call).

## Should have

### 7. Ordered fallback: 'If that monitor is off, use…' per app plus a global default
**Status:** built in v1.6.0

*Placement · effort S*

**Problem.** When an app's home isn't available, it always goes to the main monitor. The owner wants streaming apps on the side 21:9 when the TV is off, not in the middle of work. 'Where I used it last' has a set of allowed monitors but no order.

**Windows today.** No. Windows falls back to the primary monitor or the app's own saved position.

**Design.** Apps page detail pane, under 'Open it on': 'If it's off, open on' with a ComboBox: 'Main monitor (default)' / each other monitor / 'Leave where Windows puts it'. With 'Where I used it last', the allowed-monitor checklist gets Move up / Move down buttons (keyboard accessible, with Narrator names) so the order becomes the fallback order. General > Placing windows gets a matching default card, 'When an app's monitor is off'. Schema: AppRule.Fallback (MonitorRef?, or a 'leave' sentinel) and AllowedMonitors treated as ordered. ResolveTarget walks home, then fallback, then the global default. Existing settings keep today's behaviour.

**Risks.** Low. The fallback target must also be checked against the 'Monitor is off' flag. Windows placed on a non-main fallback must still count as displaced for the 'comes back' feature (today _fallbackPlaced only looks for 'isn't connected' plus main).

### 8. Remember where on the monitor: position, size and maximized per app
**Status:** built in v1.6.0 ('Where I last had it' and 'Maximized' also apply when the window already opened on the right monitor; windows that can't be resized keep their size).

*Placement · effort M*

**Problem.** MoveTo always centres the window and clamps its size. On a 21:9 screen a centred window is almost never what you want: Spotify lives on the right third, Outlook on the left half. Streaming apps sent to the 65-inch TV open as a small centred window that needs a mouse trip across a dark room to maximize.

**Windows today.** No. Some apps save their own absolute position, which breaks when monitors change. FancyZones' 'last known zone' requires zones.

**Design.** Apps page detail pane, under 'Open it on': 'Place it' ComboBox: 'Centered (default)' / 'Where I last had it on that monitor' / 'Maximized'. 'Reset' hyperlink next to it. For 'Where I last had it', the learning tick (it already samples the focused window every 2 s) stores GetWindowPlacement.rcNormalPosition and the show state as fractions of that monitor's work area (AppRule.Placement {X, Y, W, H, Maximized} per monitor HardwareId), using DWMWA_EXTENDED_FRAME_BOUNDS to correct for the invisible borders. On placement, MoveTo does its two-step DPI move, then applies the scaled rect with SetWindowPlacement, or ShowWindow(SW_MAXIMIZE) for 'Maximized'. Applied once, never enforced. Real Snap state can't be set, so this places the rect only. Borderless fullscreen and F11 injection are deliberately left out.

**Risks.** Apps that resize themselves right after opening (the existing verify covers jumping back, not resizing, so verify the size once as well). Enforce minimum sizes by re-reading the rect. Recording the rect every 2 s must not write settings.json every tick: save only when it changes by more than a few percent.

### 9. Open dialogs and pop-ups on the same monitor as their app
**Status:** built in v1.6.0 (only owned windows with a title bar count as dialogs; a dialog is also left alone when it runs as administrator or doesn't respond).

*Placement · effort M*

**Problem.** Save As, Print, Settings and other pop-ups often open on the primary monitor while their app is on the side monitor or the TV. It is one of the longest-running Windows multi-monitor complaints. Today WindowInfo.Classify returns Never for any owned window (GW_OWNER), so the app never fixes it.

**Windows today.** Partly. Windows centres some common dialogs on their owner, but many apps compute positions against the primary monitor, and Windows doesn't correct it.

**Design.** General > Placing windows gets a toggle card: 'Open dialogs on the same monitor as their app' (on by default). In the hook, an owned, visible top-level window (GW_OWNER != 0; not WS_EX_TOOLWINDOW or WS_EX_NOACTIVATE; not classes #32768, tooltips_class32 or IME; smaller than 60% of the work area) whose rect lies entirely on a different monitor from its root owner (GetAncestor GA_ROOTOWNER) is centred over the owner on the owner's monitor. It is never resized, uses the same 0.4 s delay and place-once rule, and is skipped if the owner's app is 'Never move' or the dialog overlaps the owner's monitor at all. Add a new Kind.OwnedDialog so learning still ignores these windows.

**Risks.** Deliberate cross-monitor windows (presenter views, OBS projectors) are mostly large or unowned, so the size limit and 'overlaps the owner's monitor' rule protect them. Modal dialogs that reposition themselves are handled by reusing Verify.

### 10. Undo, 'why it went there' and optional notices
**Status:** built in v1.6.0 (all four notifications are off by default; a tray balloon can't hold an Undo button, so selecting the balloon undoes it; in helper mode the helper keeps its last 10 events in memory and the settings window shows them in Recent activity; Recent activity also lists other events, such as settings you changed, without Undo).

*Trust · effort M*

**Problem.** An automatic window mover that guesses wrong feels broken, and users can't tell whether Windows, Snap or this app moved a window. Today Recent activity shows log text only, there is no way to undo a move or a learned home, and nothing tells you that an app's home just changed.

**Windows today.** No.

**Design.** General > Recent activity becomes a list of the last 10 events. Each row has an icon, a plain reason ('Learned: you used Spotify on 2 · Side', 'Set by you', 'TV is off, so main monitor for now', 'Skipped: runs as administrator') and an 'Undo' link. Undoing a move sends the window back to its Pending.FromDevice and rect, after checking that the HWND still exists and belongs to the same process. Undoing a learn restores the previous home. Undoing the same app twice asks 'Stop moving Spotify?', which sets Never move. General gets a 'Notifications' card with checkboxes: 'When it learns a new monitor for an app' (off), 'When an app opens on another monitor because its own is off' (off), 'When a monitor comes back and windows move back' (on), 'When it can't move an admin app' (once per app). In tray mode these show as tray balloons with Undo, throttled to 1 per app per day. In helper mode they are queued and shown in Recent activity next time the window opens.

**Risks.** Undo data must stay in memory (no window titles persisted beyond the existing log). Real Windows toasts for an unpackaged app need an AUMID on the Start menu shortcut, so start with tray balloons.

### 11. Identify monitors and show a clickable monitor layout
**Status:** built in v1.6.0

*Monitors · effort M*

**Problem.** '3 · TV' only helps if you remember which physical screen it is, and two identical 21:9s are hard to tell apart. Windows Settings shows a layout diagram and an Identify button, but with Windows' own numbers, not this app's names and numbers.

**Windows today.** Partly. Settings > Display > Identify shows Windows' own renumberable numbers only, and has no app placement view.

**Design.** Monitors page header: a scaled diagram (WPF Canvas from MonitorInfo.Bounds, normalised) showing each monitor as a tile with the user's number and name, '(main)', and a highlight on the selected one, like Settings > Display. 'Identify' button next to it, also in the tray menu: for 3 s, shows a large translucent overlay on every monitor with '3 · TV', the model name and '(main)'. Hovering or focusing a monitor card outlines that physical screen. Apps page detail pane: the same mini diagram as an alternative to the 'Open it on' ComboBox, where clicking a tile chooses the monitor, and in 'Where I used it last' the tiles toggle.

**Risks.** Overlay windows must be per-monitor DPI aware, topmost, WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE, and ignored by the engine (it already skips its own PID). Each tile must be a focusable radio button with a Narrator name.

### 12. Keyboard shortcuts: send this window to monitor N, and make it the app's home
**Status:** built in v1.6.0 (keys are chosen with check boxes for Ctrl, Alt and the Windows key plus a list of keys, in a dialog under 'Change the keys', instead of a key-capture button: Windows itself and the shortcuts already registered catch many of these key combinations before the settings window could see them; Shift is always the 'also save' version. A send-to-monitor shortcut is registered for each connected monitor only, and none with a single monitor).

*Keyboard · effort M*

**Problem.** Win+Shift+Left/Right only cycles to the adjacent monitor in Windows' order, so with three monitors you press it up to twice and watch where it lands, and nothing is remembered. There is no one-keystroke 'always open here'.

**Windows today.** Partly. Win+Shift+Arrow moves to the adjacent monitor only, in Windows' order, and remembers nothing.

**Design.** General gets a new 'Keyboard shortcuts' section (off by default), with each shortcut as a card holding a key-capture button and 'Shortcut in use by another app' when RegisterHotKey fails. Defaults: Ctrl+Win+Alt+1/2/3… sends the foreground window to the monitor with the user's number 1/2/3; Ctrl+Win+Alt+Right/Left goes to the next/previous monitor in the user's own order; adding Shift also saves that monitor as the app's home ('Set by you'), with a small balloon 'Spotify → 2 · Side (saved)'. Ctrl+Win+Alt+Home runs 'Move open windows to their monitors'. Moves use MoveTo, so they are DPI-safe and keep the maximized state (and use the remembered relative rect when that feature exists).

**Risks.** Only the process holding the Active mutex may register hotkeys, and the helper needs WM_HOTKEY in its NativeHost message window. Hide the section in launch-only mode. Elevated foreground windows can't be moved: say so in a balloon.

### 13. Quick 'Always open on…' for the window in front, from the tray
**Status:** built in v1.6.0 (the last app window in front is noted when you switch windows, with a window event, rather than on the learning tick, so it's right even when you open the menu within 2 seconds; 'Where I used it last' allows every monitor except ones kept for chosen apps, and 'More choices…' opens the Apps page with the app selected).

*Tray · effort S*

**Problem.** Setting an app's monitor means opening the window, going to Apps, finding the app, choosing and pressing Save. Windows has no per-window 'always open here' action.

**Windows today.** No.

**Design.** The tray right-click menu gets a top section for the last foreground app window that isn't one of ours (tracked in LearnTick): '[icon] Chrome · on 1 · Main', with a submenu 'Always open on ▸ 1 · Main / 2 · Side / 3 · TV / Where I used it last… / Never move this app'. Choosing a monitor calls the existing Pin logic, so its open windows move too. In helper mode (no tray icon) the same choice is reachable through the Shift variant of the send-to-monitor shortcuts.

**Risks.** Low. Don't inject into other apps' system menus (that needs a global hook DLL that antivirus tools flag).

### 14. Keep my main monitor (stop Windows promoting the TV)
**Status:** built in v1.6.0 (a toggle that keeps the monitor that was main when it was turned on, instead of a monitor drop-down, so turning it on never changes the main monitor by surprise; Make main changes which one is kept).

*Monitors · effort S*

**Problem.** After reconnects, driver updates or the TV waking over HDMI, Windows sometimes makes another display primary. The taskbar clock, Start, notifications and every unlearned app then move to the TV, and the app's fallback ('main monitor') follows.

**Windows today.** No. You can set the primary display but not lock it.

**Design.** Monitors page gets a toggle card under the list: 'Keep [1 · Main ▾] as the main monitor'. After each display change has settled, if the chosen monitor (matched by HardwareId) is connected and isn't primary, call the existing Engine.SetMainMonitor. Log it and show a one-time balloon: 'Windows made 3 · TV the main monitor; changed it back'. Pressing 'Make main' on another monitor updates the choice.

**Risks.** A driver that keeps reasserting its own primary could cause a loop: cap at 2 attempts per 5 minutes, then stop and log. Never act during the hold window, or while only one monitor is active.

### 15. 'Where my mouse is' as a placement choice
**Status:** built in v1.6.0 (keyboard fallback: when the pointer hasn't moved for 2 minutes, the monitor of the window you were using counts).

*Placement · effort S*

**Problem.** For some apps (Explorer, Notepad, Calculator, a quick launcher, a new browser window) the right monitor is wherever you're working right now, not a fixed home.

**Windows today.** No. Windows sometimes uses the monitor of the last input for new processes, but it is undocumented and inconsistent.

**Design.** Apps page 'Open it on' ComboBox gets 'The monitor I'm using (mouse pointer)'. It can be combined with the allowed-monitor checklist ('…but only on these monitors') so the TV is never chosen by accident. General > 'Apps it hasn't learned yet' gets a third option: 'Open on the monitor with the mouse pointer'. The cursor position (GetCursorPos + MonitorFromPoint) is recorded in the hook callback when the window is created or first shown, not 0.4 s later. AppRule.FollowMouse flag; learning never overrides it.

**Risks.** Low. Keyboard-only users may prefer 'the monitor of the focused window'; offer it as the same option's fallback when the cursor hasn't moved for a long time.

### 16. Pause while a game or presentation runs
**Status:** partly built: the pause, the held windows and the status in the General page, tray tooltip and log are built; the optional GameConfigStore 'Game' badge (games defaulting to Never move) is not. Held windows that waited more than 15 minutes are left where they are.

*Stability · effort S*

**Problem.** Moving a game's splash or launcher window can crash it or send it to the wrong output, and a game changing resolution shuffles monitors. Focus checks during play can also learn the wrong home. Only true fullscreen windows are skipped today.

**Windows today.** Partly. Do Not Disturb turns on automatically for games, but nothing touches placement.

**Design.** General > Placing windows gets a toggle card: 'Pause while a game or presentation is running' (on by default). While SHQueryUserNotificationState returns QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE, or the foreground window is borderless and covers its whole monitor, the engine neither moves nor learns, and a held window is placed when the pause ends only if the user hasn't focused it. The General page header, the tray tooltip and the log show 'Paused: game running'. Optional: apps listed under HKCU\System\GameConfigStore\Children default to Never move, with a 'Game' badge on the Apps list that can be overridden.

**Risks.** Over-pausing during borderless video playback; the per-app override and 'presentation' semantics limit it. SHQueryUserNotificationState is cheap and can be checked on each placement and learn tick.

### 17. 'What can open here' per monitor (reserve the TV for chosen apps)
**Status:** built in v1.6.0

*Monitors · effort S*

**Problem.** Nothing should open on the TV unless it is a streaming app. A browser dragged there once to show someone can still teach the TV as its home, and Windows' own placement can drop stray windows there.

**Windows today.** No.

**Design.** Monitors page card: ComboBox 'What can open here': 'Any app (default)' / 'Only apps I choose'. With 'Only apps I choose', learning never makes this monitor a home (only Save on the Apps page or the allowed list can), and a new window of any other app that opens there, by Windows' choice, is moved to that app's home or the main monitor. Stored on MonitorSettings, checked in Learn() and in the placement path.

**Risks.** Low. Make sure the 'Where I used it last' allowed list still works when it includes a reserved monitor.

### 18. Restore window positions after sleep and hibernate
**Status:** partly built: the snapshot, wake-up restore and 'skip windows touched after waking' are built; the tray and Tidy-up 'Put windows back where they were' command with Undo waits for items 4 and 10. On PCs with Modern Standby, Windows may not send the sleep notification, so the restore may not run there.

*Monitor changes · effort L*

**Problem.** Pile-ups mostly happen across sleep and hibernate: Windows treats sleeping monitors as unplugged and briefly uses a fallback resolution, squeezing and stacking every window. With two 21:9s and a TV, users rearrange by hand every morning.

**Windows today.** No explicit snapshot; it relies on the same 'remember window locations' mechanism, which users report failing after hibernate.

**Design.** General > Placing windows: an option under 'When a monitor comes back': 'Also after sleep: put windows back where they were' (on). Subscribe to suspend and resume (RegisterSuspendResumeNotification, or WM_POWERBROADCAST PBT_APMSUSPEND / PBT_APMRESUMEAUTOMATIC) and to session lock and unlock (WTSRegisterSessionNotification). At suspend, and every 5 minutes, take an in-memory snapshot of each app window: HWND, process start time, monitor HardwareId, normal rect and show state. After resume, once the monitor set matches the snapshot and placements are no longer held, restore windows whose monitor or rect changed and that the user hasn't touched since unlock. The tray menu and the General 'Tidy up' card get 'Put windows back where they were', with Undo.

**Risks.** Restoring a window the user moved on purpose right after waking: skip windows touched after unlock. Keep the snapshot in memory only (privacy, and no disk writes). The helper needs a message window for the power notifications, which NativeHost already has. Shares its code with the 'comes back' item.

### 19. Correct size and DPI for moves between differently scaled monitors
**Status:** partly built: unaware and system-aware windows are measured after Windows rescales them and re-checked at the existing 0.8 s check, instead of predicting the scaled size and adding a 500 ms check; the regression test waits for the test project (item 27).

*Stability · effort M*

**Problem.** The TV likely runs at 200 to 300% scaling and the 21:9s at 100 to 125%. MoveTo's two steps don't wait for the app to handle WM_DPICHANGED, GetWindowRect includes the invisible resize borders (about 7 px drift), and DPI-unaware or system-aware apps end up oversized, off-centre or blurry.

**Windows today.** Partly. Windows rescales windows on DPI change, but doesn't centre or clamp a moved window for the target.

**Design.** No UI. In MoveTo: after step 1, wait until GetDpiForWindow matches GetDpiForMonitor of the target (poll, up to 200 ms, only when the DPIs differ) before clamping and centring. Use DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) for the visible rect. For windows whose GetWindowDpiAwarenessContext is UNAWARE or SYSTEM_AWARE, scale the clamp by the DPI ratio and check the size again after 500 ms. Make sure HDR, advanced-colour or refresh-rate-only changes don't reset _lastMonitorChangeUtc (add a regression test).

**Risks.** Double-scaling per-monitor-aware apps: correct only unaware and system-aware windows. Polling adds latency only in the mixed-DPI case.

### 20. Handle Remote Desktop and temporary displays (Miracast, virtual, DisplayLink)
**Status:** built in v1.6.0 (temporary by default: Miracast and virtual monitors only; USB/DisplayLink monitors can be marked temporary with the switch).

*Stability · effort M*

**Problem.** In a Remote Desktop session the client's monitors replace the real ones, so every home looks disconnected, everything falls back to the RDP display, and after 30 s learning starts on the remote layout. Miracast casts and virtual displays appear briefly and can become a learned home or the unknown-app target.

**Windows today.** No. Windows only restores positions when the console reconnects.

**Design.** General > Placing windows: 'While connected with Remote Desktop: Pause (default) / Keep working'. Detect it with GetSystemMetrics(SM_REMOTESESSION) and WM_WTSSESSION_CHANGE (WTS_REMOTE_CONNECT / WTS_CONSOLE_CONNECT). Show an InfoBar at the top of the General page: 'Paused: Remote Desktop session'. Monitors whose outputTechnology is MIRACAST (15) or INDIRECT_WIRED/INDIRECT_VIRTUAL default to 'Temporary': never learned as a home and never the fallback target. A per-monitor 'Apps can open here' override on the Monitors page lets users with a permanent DisplayLink screen turn that off.

**Risks.** Some permanent USB-C monitors are DisplayLink (INDIRECT_WIRED), hence the override, which could default to 'Temporary' only for Miracast and virtual displays. Monitors.cs already reads outputTechnology.

### 21. First-run welcome: your monitors, your apps, how it runs
**Status:** partly built: step 2 lists the apps that are open, not apps that are installed but closed (finding those would mean reading the Start menu and Store app lists); the rest is built in v1.6.0

*Onboarding · effort M*

**Problem.** A new user sees an empty list and three toggles. They don't know that learning takes a few seconds per app, that the TV can be reserved for streaming, or which run mode to pick. The value only shows after days.

**Windows today.** No.

**Design.** On first start (no settings.json), show a 3-step flow inside the window (a WPF-UI ContentDialog-style overlay), with 'Skip' on every step. Step 1, 'These are your monitors': the layout diagram with name and number boxes prefilled from the model name, plus 'Identify'. Step 2, 'Anything that belongs on a specific monitor?': open apps and known installed apps (Netflix, Spotify, Teams…) with a monitor dropdown each, plus a checkbox per monitor, 'Only apps I choose' (for the TV). Step 3, 'How should it run?': the three modes as selectable cards with their memory cost, and 'Start with Windows'. Finish opens the General page.

**Risks.** Low. It reuses the diagram, Identify and reserve-monitor items, so build those first. It must keep working when the old WindowHome.ini import runs on first start.

### 22. Export, import and automatic backups of settings

**Status:** built in v1.6.0

*Data · effort M*

**Problem.** A new PC, a Windows reinstall, or 'Forget all' clicked by accident means re-learning everything. Settings live only in %AppData%\MonitorWindowControl\settings.json, and reset is all or nothing.

**Windows today.** No.

**Design.** About page: a new 'Backup' section with three cards. 'Export settings…' (SaveFileDialog, .json). 'Import settings…', showing a preview dialog ('12 apps, 3 monitor names: Replace / Merge'). 'Restore a backup', listing the last 5 daily copies (settings.YYYYMMDD.json; 'Forget all' always writes one first). On import, monitors are matched by HardwareId, then model name, then role (main to main). Unmatched ones get a mapping dialog: 'Your old "TV": which monitor is it now?'.

**Risks.** An imported file is untrusted: parse it into the typed model with the existing repair logic, cap its size, and reject unknown run modes. Rotation happens in SettingsStore under the existing cross-process lock.

### 23. Restart the background helper if it stops, and say when it isn't running

**Status:** partly built: the Task Scheduler task (restart on failure) isn't made; the Run key stays, with RegisterApplicationRestart in both programs and the General page's Start it

*Stability · effort M*

**Problem.** In helper mode, if the helper crashes or is killed (antivirus, a driver reset), nothing places windows until the next sign-in, and with no tray icon nobody notices.

**Windows today.** Task Scheduler supports restart on failure; the app doesn't use it yet.

**Design.** When the settings window opens with RunMode=Helper and HelperManager.IsRunning() is false, show an InfoBar at the top of General: 'The background helper isn't running' [Start it] [View report]. Prefer a per-user Task Scheduler task (logon trigger, restart on failure 3 times at 1 minute, no elevation, created through the Task Scheduler COM API) over the Run key, falling back to the Run key if policy blocks it. Call RegisterApplicationRestart in both processes so they come back after Windows Update restarts. Exit and mode changes end the task cleanly (exit code 0 means no restart).

**Risks.** Task Scheduler entries are visible and some corporate policies block user tasks, hence the fallback. The Active mutex must keep preventing two engines from running. StartupManager.Apply must clean up whichever mechanism isn't in use.

### 24. Local crash reports and a 'Save diagnostics' bundle (no telemetry)

**Status:** partly built: StackTraceSupport stays off in the helper (to keep it small), so helper reports have the error but no stack trace; the monitor dump is the app's own monitor list, not a raw QueryDisplayConfig dump

*Supportability · effort M*

**Problem.** Crashes produce only a 'FATAL:' line in a 1 MB rolling log that may already have rolled over. The NativeAOT helper is built with StackTraceSupport=false, so its crashes have no stack trace.

**Windows today.** WER collects crashes only for registered apps, and LocalDumps needs admin configuration.

**Design.** In App.OnUnhandled, the AppDomain handler and a helper-side handler, write %LocalAppData%\MonitorWindowControl\Crashes\crash-<time>.txt containing the version, OS build, run mode, exception, sanitised monitor list and last 200 log lines. On the next start of the settings window, show an InfoBar: 'Monitor Window Control stopped unexpectedly on <date>' [Open report] [Copy]. About page gets a 'Save diagnostics…' card that zips the log, settings.json (app list optional), a QueryDisplayConfig dump and crash files, after a dialog lists what's inside and offers 'Remove window titles'. Turn StackTraceSupport on in the helper (a few hundred KB).

**Risks.** No network, so PRIVACY.md stays true; update it to mention the crash folder. Check that the helper size increase is acceptable for the '5 MB' promise.

### 25. Installed Apps entry and one-click 'Remove from this PC'

**Status:** partly built: the optional 'Install for this user' button isn't made

*Install · effort M*

**Problem.** Removing the app today takes six manual steps, it never appears in Settings > Apps > Installed apps, and deleting the exe leaves the Run key and a running extracted helper behind.

**Windows today.** Windows has the Installed apps uninstall flow; this makes the app take part in it.

**Design.** About page gets a 'Remove from this PC…' card, and an optional 'Install for this user' button that copies the exe to %LocalAppData%\Programs\MonitorWindowControl. Register HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorWindowControl (DisplayName, DisplayIcon, DisplayVersion, Publisher, EstimatedSize, UninstallString "…exe" --uninstall) so it appears in Installed apps without admin. --uninstall stops the helper (HelperExit event), removes the Run value, scheduled task and launch shortcuts, asks 'Keep my app list' (default) or delete both data folders, then deletes the exe through a short-lived process that waits for the PID to exit.

**Risks.** Deleting a running exe needs the wait-then-delete child. Never delete settings unless asked. Re-point the Run value and shortcuts at the current exe path at every start so a moved exe doesn't break autostart.

### 26. Accessibility and theme conformance (high contrast, text size, announcements)

**Status:** partly built: the automated Axe.Windows check isn't added (WPF UI tests need a desktop session the hosted build doesn't have); contrast themes, Narrator and 225 % text still need checking by hand.

*Polish · effort M*

**Problem.** The README claims keyboard and Narrator support, but nothing verifies it. A Settings-style app must render correctly in contrast themes, at 225% text size, and with transparency effects off, and must announce changes. Light and dark mode is already live (SystemThemeWatcher), but the tray icon and high contrast aren't handled.

**Windows today.** Windows provides the tooling (Accessibility Insights, Narrator); conformance is up to the app.

**Design.** Under SystemParameters.HighContrast, drop Mica and custom brushes and use SystemColors so Aquatic, Desert, Dusk and Night sky render correctly, switching live. Use a solid background when transparency effects are off. Ship light and dark tray icon variants (HKCU\…\Personalize\SystemUsesLightTheme, refreshed on WM_SETTINGCHANGE 'ImmersiveColorSet'). Raise UIA notifications (AutomationPeer.RaiseNotificationEvent) for 'Saved', 'Forgot <app>', 'Paused' and the InfoBars, and have the app list announce 'n apps' after a search. Add visible focus rectangles on cards, and check 225% text scaling for clipping. Run Axe.Windows in a test.

**Risks.** WPF-UI controls only partly honour contrast themes, so some styles may need overrides. Test manually with Narrator and each contrast theme.

### 27. Engine unit tests and a GitHub Actions build

**Status:** partly built: the integration test that creates a real window and checks Classify isn't added, and app.manifest's version is checked against Directory.Build.props rather than generated from it.

*Quality · effort L*

**Problem.** There are no tests and no CI. The rules that decide where windows go (FindMonitor fallbacks, ResolveTarget with allowed monitors, the learning streak, the 30 s settle, the one-monitor rule, settings repair) are subtle and change by hand every release, and the version is duplicated in two csproj files and app.manifest.

**Windows today.** N/A.

**Design.** Add an IMonitorSource seam (Engine calls the static MonitorService.Enumerate today; IEngineHost already abstracts timers). Write xUnit tests for: renumbered DISPLAYn with identical models; TV off and on; generic 'Display 3' names; fallback order; no learning within 30 s of a change; topology changing mid-move; corrupt or partial settings.json; concurrent saves under the lock. Add an integration test that creates a real window and checks Classify (normal, tool, owned). A workflow on windows-latest builds both projects (the NativeAOT helper needs the C++ tools, which the image has), runs the tests and uploads the exe with its SHA256. Generate the version once from a Directory.Build.props.

**Risks.** Hosted runners have one virtual display, so multi-monitor behaviour is covered by the fake source. Signing is a separate, later item.

## Could have

### 28. Rules for some windows of an app (title or browser profile)
**Status:** built in v1.6.0 (the dialog is a message box like the app's other dialogs; the title of a new window of an app with title rules is checked again every 0.25 s for up to 1.5 s instead of listening to EVENT_OBJECT_NAMECHANGE; browser profiles are offered from the profiles' open windows; windows a rule applies to teach nothing; a window placed by a rule while its monitor is off goes back to the rule's monitor).

*Placement · effort L*

**Problem.** One app, several jobs: two browser profiles (work on main, personal on the side), the Teams meeting window versus the chat, OBS's projector on the TV, Discord's popped-out call, one VS Code window per project. Per-app rules can't tell them apart. Web apps are already separated internally, but ordinary windows aren't.

**Windows today.** No.

**Design.** Apps page detail pane: 'Add a rule for some of its windows…' opens a ContentDialog. 'Window title [contains / starts with / is] [text]', prefilled from a dropdown of the app's open window titles. For Chrome and Edge, 'Browser profile' is matched from the window's AppUserModelID (SHGetPropertyStoreForWindow PKEY_AppUserModel_ID, the same mechanism the web-app detection uses). Then 'Opens on [monitor]' or 'Never move'. Child rows appear indented under the app ('Chrome: title contains "Work" → 1 · Main'). Stored as AppRule.WindowRules and evaluated before the app rule. Matching is case-insensitive substring only, with no regular expressions.

**Risks.** Titles change right after creation (blank, then 'New Tab'), so re-read the title at the 0.4 s placement and listen to EVENT_OBJECT_NAMECHANGE for about 1.5 s. Keep the UI from growing into a trigger editor. Learning should apply to the app rule only.

### 29. Different homes for different monitor setups (TV on vs off, docked vs undocked)
**Status:** partly built: the optional mapping of 'main' and 'side' at a new setup isn't built; a setup is named by its monitors' EDID model ids (temporary monitors don't count) and the first one seen is the usual setup; new setups are noted by the program that places windows once monitors have settled for 30 s (with Nothing in the background, only the first one is); a monitor learned for a setup can't be undone from Recent activity (choosing one by hand can).

*Placement · effort L*

**Problem.** The right home can depend on which screens are connected: with the TV on, the media player belongs there; with only the two 21:9s, on the side monitor. Laptops have home and office setups. Homes today are keyed to specific monitors, so a new setup means every app falls back to main.

**Windows today.** No. Windows stores per-topology display configuration but not app placement.

**Design.** Implicit, with no profile editor. The engine names the current setup by its sorted monitor HardwareIds. A setup gets its own overrides only when the user changes an app's monitor (or learning happens) while that setup is active and it differs from the default. The Apps list then shows a chip on that row, 'With 2 monitors'. Monitors page gets a 'Your setups' section, shown only after a second setup has been seen, listing each with a rename button ('Home: 3 monitors', 'Laptop only') and 'Forget'. Optional: at a new setup, map 'main' to the new main monitor and 'side' to the largest other monitor. Storage: Dictionary<setupId, overrides> next to the existing rules, which stay the default. For the TV-off case alone, the ordered fallback item usually suffices.

**Risks.** UI complexity is the main risk: show at most a chip and a small list. Identical monitors at home and at work produce the same ids, which is usually what users want.

### 30. Disconnect or reconnect a monitor in one click ('Turn TV off for Windows')
**Status:** partly built: the optional keyboard shortcut isn't built; the Monitors card has a Disconnect button (the cards have no '…' menu) and disconnected monitors get their own card with Connect; Connect lets Windows choose the mode and position from its display database (only the 15 s revert restores an exact saved configuration); every change is checked with SDC_VALIDATE before it's applied; not yet tried on real hardware.

*Monitors · effort M*

**Problem.** Stopping Windows from using the TV without unplugging it means digging into Settings > Display > 'Disconnect this display'. Win+P only knows two-screen modes, which are useless with three monitors. There is no tray or hotkey access.

**Windows today.** Partly. Settings > Display has 'Disconnect this display' per monitor, but no tray, quick settings or hotkey access.

**Design.** Monitors page card '…' menu and the tray 'Monitors' submenu get 'Disconnect from Windows' and 'Connect'. Disconnect saves the current QueryDisplayConfig(QDC_ALL_PATHS) topology, deactivates that monitor's path and applies it with SetDisplayConfig(SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE). Connect restores the saved path with its position, mode and refresh rate. A 15 s 'Keep these display settings?' dialog reverts automatically, like Windows' resolution dialog. The engine treats it as a planned change, so the windows of that monitor are displaced and return on Connect (via the 'comes back' item). Optional shortcut in the Keyboard shortcuts section.

**Risks.** Driver quirks (HDR or VRR state not restored), the primary changing, a black screen. Never allow disconnecting the primary or the last active monitor, and always offer the timed revert. Doesn't need admin.

### 31. Handle duplicated (mirrored) displays
**Status:** built in v1.6.0 (Name & number is hidden on a duplicated card until the monitors are extended again).

*Stability · effort S*

**Problem.** With Win+P 'Duplicate', two targets share one source. GetFriendlyNames keeps only the first target name per DISPLAYn, so the UI shows one monitor with whichever name came first, apps homed on the TV resolve as 'not connected' or as main, and learning during duplication records a home that is really two screens.

**Windows today.** Windows supports cloning but gives apps no notion that a home monitor is currently mirrored.

**Design.** In Monitors.cs, group paths by sourceInfo.id and keep every target. A clone group shows as one Monitors card, 'Main + TV (duplicated)', with both identities. An app homed on either member resolves to the group. Pause learning while any clone group exists ('Duplicated displays: learning paused' in the log and on the General page).

**Risks.** Low: local to Monitors.cs and FindMonitor. Test with Win+P on the owner's setup.

### 32. Launch shortcuts for a specific monitor, with arguments
**Status:** built in v1.6.0 (the dialog is a message box like the app's other dialogs; the monitor is passed as its model name and hardware identity, not one 'HardwareId'; 'Open with' accepts one http(s) address or full path, passed as one quoted argument, and only for programs, since Store apps can't be given one; when the tray app or helper is running, the shortcut tells it the monitor through launch.json and an event).

*Launch · effort S*

**Problem.** A launch shortcut always opens the app on its saved home. Users sometimes want 'Edge on TV' and 'Edge on main', or a shortcut that opens a specific URL or file on a chosen screen.

**Windows today.** No.

**Design.** The 'Create launch shortcut' button opens a small ContentDialog: 'Open on: its usual monitor (default) / [monitor ▾]' and 'Open with (optional): [URL or file]'. The name is prefilled ('Netflix on 3 · TV'). ShortcutManager writes the .lnk to Start menu > Monitor Window Control with /monitor:<HardwareId> and the arguments, and the helper's --launch mode uses that monitor for this launch only, without changing the rule.

**Risks.** Low. Uses the stable HardwareId so shortcuts survive renumbering. Quote and validate arguments carefully, because they come from the user.

### 33. App groups: open a set of apps, each on its monitor
**Status:** built in v1.6.0 (apps that are running already are left as they are, because starting a browser again opens another window; the tray entry is in tray mode only).

*Launch · effort M*

**Problem.** Users open the same group every time (Netflix on the TV plus Discord and a browser on the side monitor). PowerToys Workspaces uses absolute positions, which break when monitors change.

**Windows today.** No (only PowerToys Workspaces, a separate install, with absolute positions).

**Design.** Apps page gets a 'Groups' section under the list: 'New group…' takes a name and a checklist of saved apps with launch targets. Each app opens on its home, with an optional per-group override. 'Create launch shortcut' for the group puts 'Start Streaming' in the Start menu, and groups also appear in the tray menu. Closing apps is deliberately left out. Reuses LaunchTarget (exe or AUMID) and the helper's --launch placement for each app's first window.

**Risks.** Matching each launched app's first window when several start at once; the helper needs a list mode. Overlaps PowerToys Workspaces, so position it as the monitor-aware, setup-tolerant version and keep it small.

### 34. Per-app timing for stubborn apps (Advanced)
**Status:** built in v1.6.0 ('Keep correcting for' moves a window back at most 3 times; the tip shows on the Apps row until you change the app's timing).

*Placement · effort S*

**Problem.** Electron, Steam, Adobe and Java apps show a splash first or reposition themselves after 1 to 2 s, so the fixed 0.4 s delay moves the splash and the 0.8 s verify loses to the app. Users can't fix one stubborn app.

**Windows today.** No.

**Design.** Apps page detail pane gets an 'Advanced' CardExpander (collapsed): 'Wait before moving: Automatic / 1 s / 3 s' and 'Keep correcting for: 1 s (default) / 5 s'. When an app jumps back twice in a row, the log says 'Spotify moved itself back twice: try a longer wait', and its Apps row shows a small info icon with that tip.

**Risks.** Low. Uses the existing Pending timings per rule. Keep 'Keep correcting' bounded so the 'never fights you' promise holds.

### 35. Organisation policies (preset or lock settings through the registry)

**Status:** built in v1.6.0

*Enterprise · effort M*

**Problem.** For IT to deploy it (or to justify building it into Windows), admins need to preset or lock settings: run mode, learning, a line-of-business app pinned to main, or turning it off on shared PCs.

**Windows today.** No. There is no policy for which monitor an app opens on.

**Design.** Read HKLM\SOFTWARE\Policies\MonitorWindowControl (and HKCU): Enabled, LearnAutomatically, UnknownApps, RunMode, AllowMakeMain, and an AppRules list (app key = 'main' | 'never' | monitor model name). Merge them over settings at load in both the app and the helper. Controls set by policy are disabled, with the Windows Settings pattern: an InfoBar 'Some settings are managed by your organization' at the top of the page. Ship ADMX and ADML templates in the repo.

**Risks.** Reading policy keys needs no admin, but testing does (to write HKLM); HKCU policies can be tested without it. Every locked state must be visible in the UI.

### 36. Translated interface

*Polish · effort L*

**Problem.** Every string is hard-coded English in XAML and code, which blocks non-English users and anything Windows-grade.

**Windows today.** N/A.

**Design.** Move UI strings to Strings.resx plus per-language files, bound through a markup extension. Follow the Windows display language (CultureInfo.CurrentUICulture), with an optional About > 'Language' ComboBox. Keep the log and the helper's log in English so support logs stay readable. Start with French (Canada), then accept community translations. Check German-length strings and right-to-left layout (FlowDirection).

**Risks.** Touches every string, and every translation needs a reviewer. Do it after the UI settles, or strings will churn.

### 42. Split a monitor into areas that work like separate monitors
**Status:** built (2 equal, 60/40, 70/30, 3 equal, 4 equal or 2 stacked; the shares come from the preset and aren't draggable; Windows still sees one monitor, so the taskbar and Start menu don't change; "Treat as switched off", "What can open here" and "Apps can open here" apply to the whole monitor; not yet tried on real hardware).

*Monitors · effort M*

**Problem.** With one monitor there's nothing to learn, and on a big ultrawide "the monitor" is too coarse: you keep the editor on the left and chat on the right, but every app opens in the middle at whatever size it likes.

**Windows today.** No. Snap layouts and FancyZones arrange windows by hand, but an app can't have a home area that its new windows open in.

**Design.** Monitors page, each monitor card: 'Split into areas…' opens a dialog with the presets, a live drawing of the areas and their names (Left / Right, Left / Middle / Right, Top / Bottom). Each area is an extra MonitorInfo ("\\.\DISPLAYn#k", the monitor's hardware identity plus an area number, bounds = a share of the work area), so it gets its own card, number and name and appears in 'Open it on', fallbacks, 'Where I used it last', tidy up, shortcuts, Identify and the layout picture. Every mapping of a window to a monitor resolves a split monitor by the window's centre (its restored position when maximized). New windows are placed in their area, sized to fill it, in the normal state, kept inside it as far as a minimum size allows. Learning counts areas, so one split monitor learns. Make main, Disconnect, 'switched off' and DPI use the whole monitor. 'Maximize fills the area' (on by default) restores a window maximized on a split monitor to its area; the EVENT_OBJECT_LOCATIONCHANGE hook exists only while a monitor is split and the option is on, so there's no polling and nothing runs otherwise. Saved in settings.json as MonitorSplits (monitor identity, preset, shares, names) plus MaximizeFillsArea; homes on an area save its area number in the hardware identity. The General page suggests it when only one monitor is connected.

**Risks.** Apps that maximize themselves again are left maximized after three tries in ten seconds. A window larger than its area (minimum size) sticks out of it. Older versions see a home on an area as the whole monitor.
## Later (needs infrastructure)

### 37. Code-sign both executables (Azure Artifact Signing)

*Distribution · effort M*

**Problem.** The README tells users to click 'More info, Run anyway' past SmartScreen. An unsigned app that extracts an unsigned exe to %LocalAppData% and runs it at sign-in matches malware heuristics, and being unsigned blocks the Store, winget trust and UIAccess.

**Windows today.** N/A.

**Design.** Sign with Azure Artifact Signing (formerly Trusted Signing) from build.ps1 or CI: sign the helper after the NativeAOT publish and before it is embedded, then sign MonitorWindowControl.exe, timestamped with RFC 3161. HelperManager.EnsureExtracted checks the extracted helper's Authenticode signature (WinVerifyTrust) and that its signer matches the parent's before running it, re-extracting if not. Publish SHA256 hashes in release notes.

**Risks.** Needs a paid subscription and identity validation. The order 'sign the helper, then embed it' must be kept in build.ps1.

### 38. winget package

*Distribution · effort S*

**Problem.** Users find the release by hand, and PCs without .NET 8 Desktop get a runtime prompt on first start. There is no one-command install or upgrade.

**Windows today.** N/A.

**Design.** Submit to microsoft/winget-pkgs as a portable package with a PackageDependencies entry for Microsoft.DotNet.DesktopRuntime.8. A release job runs wingetcreate update on each tag. Add a --version switch. Re-point the Run value and shortcuts at the current exe at every start so upgrades into a new folder don't break autostart.

**Risks.** Best done after signing (unsigned packages can trip validation). Portable uninstall removes only the exe, so pair it with the Remove-from-this-PC cleanup.

### 39. Microsoft Store (MSIX) with packaged startup tasks

*Distribution · effort L*

**Problem.** 'Something Microsoft would ship' means installable and auto-updated from the Store, cleanly uninstalled and trusted without SmartScreen.

**Windows today.** N/A.

**Design.** An MSIX with runFullTrust. Both exes ship inside the package (no extracting the helper). Run modes use windows.startupTask extensions (StartupTask.RequestEnableAsync, shown in Settings > Apps > Startup) instead of the Run key. Launch shortcuts become an AppExecutionAlias with a monitor argument. Detect packaged mode with GetCurrentPackageFullName and switch StartupManager, HelperManager and ShortcutManager.

**Risks.** AppData and HKCU writes are virtualised (Start menu .lnk files would be invisible). The Store needs signing, a privacy URL and an age rating, and framework-dependent .NET needs a declared dependency or a self-contained build.

### 40. Move administrator apps too (signed UIAccess helper, per-machine install)

*Placement · effort L*

**Problem.** Elevated windows (admin PowerShell, regedit, installers, Task Manager, some game launchers) open wherever Windows puts them, because a normal process can't move them (UIPI). Today they are skipped and logged.

**Windows today.** No.

**Design.** An optional per-machine installer (one UAC prompt) puts a signed helper with uiAccess="true" in C:\Program Files\Monitor Window Control. In that layout HelperManager runs it from Program Files instead of extracting it, so no user-writable exe is ever auto-started. A General toggle 'Also move apps that run as administrator' appears only when that helper is installed, and the existing 'skipped: runs as administrator' log line gains 'turn on X to move these'. Never point an elevated scheduled task at the %LocalAppData% copy.

**Risks.** UIAccess requires a valid signature and a secure folder, and it is a privileged attack surface (settings must be treated strictly as data). Some anti-cheat software dislikes UIAccess processes, so the game pause and Never move must apply.

### 41. Opt-in 'Check for updates' that keeps the no-network promise

*Distribution · effort M*

**Problem.** Users on an old portable exe miss fixes, but the privacy statement promises no network access, and a silent updater would break that trust.

**Windows today.** The Store and winget update apps; portable apps get nothing.

**Design.** Make the Store or winget the normal update channel. For portable copies: an About-page button 'Check for updates' and an optional, off-by-default 'Check weekly' toggle. It makes a single request to the GitHub releases API with no identifiers and shows the release notes. Download only after the user clicks Update, verify that the Authenticode signer matches the running exe (WinVerifyTrust), swap with rename-aside and restart in the same mode. Hide it when the app was installed by the Store, MSIX or winget, and update PRIVACY.md to describe exactly this request.

**Risks.** Meaningless without code signing (nothing trustworthy to verify), and it changes the privacy statement. Never auto-update without a click.

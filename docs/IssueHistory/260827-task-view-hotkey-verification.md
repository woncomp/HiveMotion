# Task View Hotkey Verification

## Date

2026-08-27

## Status

Implementation is complete and the Release build succeeds with zero warnings and zero errors. Desktop interaction verification is still required in an environment where Computer Use is explicitly authorized.

## Background

HiveMotion previously treated any foreground `XamlExplorerHostIslandWindow` as the native Windows Task View. Windows Explorer also uses that class for other shell surfaces, so a recognized `Win+Tab` could be passed to Windows before HiveMotion requested the overlay to open.

The original diagnostic signature was:

```text
0827 17:41:13 I [ACTIVATION] Recognized hotkey Win+Tab; overlayOpen=False; foreground=handle=0x232CAA, class=XamlExplorerHostIslandWindow, pid=18612.
0827 17:41:13 I [ACTIVATION] Foreground native UI matched (window class); passing key through unchanged.
```

The hotkey behavior was changed as follows:

- While the overlay is open, pressing the configured hotkey always closes HiveMotion, swallows the complete key press, and restores the previous foreground window. It never invokes the system action.
- While the overlay is hidden, `Win+Tab` is passed to Windows only when the foreground window is strictly confirmed as Task View.
- All uncertain Task View detections fall back to swallowing `Win+Tab` and opening HiveMotion.
- The second-press passthrough setting, management-center control, localization resources, and persisted model property were removed.

## Current Task View Criteria

All of the following must be true before `Win+Tab` is passed to Windows:

1. The configured hotkey is exactly `Win+Tab` with no additional modifiers.
2. The foreground window is visible.
3. Its class is `XamlExplorerHostIslandWindow` or `MultitaskingViewFrame`.
4. Its title is exactly `Task View` or `任务视图`.
5. Its PID matches the Explorer PID obtained from `GetShellWindow()`.
6. `DwmGetWindowAttribute(DWMWA_CLOAKED)` succeeds and reports that the window is not cloaked.
7. The foreground HWND is unchanged at the end of the inspection.

The strict title assumption is intentionally conservative. In particular, `Task Switching` and `任务切换` are not accepted because those titles may identify Alt+Tab rather than Task View.

## Unverified Assumptions

- Current English and Simplified Chinese Windows 11 builds expose Task View through one of the accepted classes and titles.
- The Task View PID equals the shell PID returned through `GetShellWindow()`.
- The Task View host is visible, not cloaked, and remains foreground throughout the bounded inspection.
- Passing `Win+Tab` after a strict match closes Task View without leaving a stuck modifier or opening HiveMotion.
- Repeating the configured hotkey while HiveMotion is open closes it, restores the previous foreground window, and never opens Task View, Game Bar, or another native action.
- Alt+Tab, taskbar previews, snap surfaces, and transient Explorer XAML windows do not satisfy the complete Task View criteria.
- Importing an older backup containing `SecondPressPassthrough`, `NativeClassNames`, or `NativeProcessNames` ignores those properties while preserving the remaining settings.
- The revised input path remains responsive at 60 Hz and high refresh rates and across mixed-DPI displays.

## Computer Use Verification Procedure

### Preparation

1. Record the Windows version/build, display language, monitor DPI scaling, and refresh rates.
2. Build with `dotnet build HiveMotion.sln -c Release` and confirm zero warnings and zero errors.
3. Exit any previously running HiveMotion instance and start the Release build from `HiveMotion/bin/Release/net8.0-windows/HiveMotion.exe`.
4. Open the live log viewer or the current log under `%LOCALAPPDATA%\HiveMotion\Logs`.
5. Keep activation log lines and screenshots for every failure.

### Normal Overlay Open

1. Focus a normal application and press `Win+Tab`.
2. Repeat from File Explorer, the desktop, and any available Explorer-hosted shell surface.
3. Confirm that HiveMotion opens every time.
4. Confirm that the log contains `Foreground Task View not confirmed (...)` followed by `Overlay hidden; notifying overlay-open listeners.`
5. Confirm that there is no `Foreground Task View confirmed (...)` entry.

### Repeated-Hotkey Close

1. Open HiveMotion with the configured hotkey.
2. Press the same hotkey again from the home layer and from a child layer if available.
3. Confirm that HiveMotion closes, the previous application regains foreground, and no native Windows action appears.
4. Repeat with a custom non-`Win+Tab` hotkey.
5. Confirm the sequence includes:
   - `Overlay already open; notifying close listeners and swallowing the hotkey.`
   - `Swallowed key-down`
   - `Executing overlay close after repeated hotkey.`
   - `Closing overlay; restoreFocus=True`
6. Confirm that neither `Foreground Task View confirmed` nor a native Task View/Game Bar appears.

### Real Task View Close

1. Open Task View by clicking the taskbar Task View button, not by pressing HiveMotion's hotkey.
2. While Task View visibly owns the foreground, press `Win+Tab` once.
3. Confirm that Task View closes and HiveMotion does not open.
4. Confirm that the recognition log reports the observed foreground class, exact title, and PID.
5. Confirm that the next entry is `Foreground Task View confirmed (...)` and that the reported PID matches the shell PID in the reason string.
6. Repeat at least ten times, including immediately after opening Task View and after leaving it open for several seconds.

### False-Positive Regression

1. Exercise Alt+Tab normally, including quick switches and cancelling with `Esc`.
2. Hover taskbar application icons to show thumbnail previews, then press `Win+Tab` from the resulting foreground application.
3. Exercise Snap Layouts/Snap Assist if available, then press `Win+Tab`.
4. Repeat the original workflow that produced a foreground `XamlExplorerHostIslandWindow` without visible Task View.
5. In every case where Task View is not visibly open, confirm that HiveMotion opens and the log reports why Task View was not confirmed.

### Settings and Backup Compatibility

1. Confirm that the Hotkeys page no longer contains the passthrough Behavior card.
2. Import an older configuration backup containing `SecondPressPassthrough` and hotkey native-UI arrays.
3. Confirm that motions, priorities, hotkey, language, and history import normally.
4. Confirm that the removed behavior does not return and that a later export omits the removed properties.

### Display and Timing Coverage

1. Repeat overlay open and repeated-hotkey close at 60 Hz and at the highest available refresh rate.
2. Repeat on each available monitor and across different DPI scaling values.
3. Confirm that the hotkey transition remains immediate, no native UI flashes, and focus restoration targets the correct window.

## Acceptance Criteria

- No non-Task-View shell window causes `Win+Tab` to bypass HiveMotion.
- A strictly matched real Task View closes through one passed `Win+Tab`.
- A repeated configured hotkey always closes HiveMotion without invoking the system action and restores the previous foreground window.
- Old backups import without errors or reintroducing removed settings.
- Release build remains warning-free and the tested input transitions show no visible latency regression.

## Follow-Up Rules

- If real Task View is rejected because its title differs, capture its exact class, title, PID, shell PID, visibility, cloak result, Windows build, and display language before changing the allowlist.
- Do not add `Task Switching` or `任务切换` merely to make the test pass. First prove that the same title is not used by Alt+Tab on that Windows build.
- If Task View and Alt+Tab share all cheap Win32 properties, keep the conservative fallback and investigate an asynchronously prepared discriminator. Do not add UI Automation or variable-duration process inspection directly to the low-level keyboard callback.
- If DWM inspection fails, preserve the safe fallback to opening HiveMotion and record the HRESULT before deciding whether a narrowly scoped fallback is justified.

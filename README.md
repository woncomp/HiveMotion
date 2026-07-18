# HiveMotion

A modal window manager for Windows. Press `Win+Tab` to bring up an overlay, then press a single letter key to launch or switch to an app. For example: `Win+Tab` → `N` opens or focuses Notepad.

## Why HiveMotion?

- **Faster than `Alt+Tab`** — jump directly to the app you want.
- **Fewer keystrokes than a launcher** — no typing or searching.
- **More precise than the Start menu** — one key press, one result.

## What It Does

- Intercepts `Win+Tab` and shows a centered overlay instead of Windows Task View.
- If the application is not running, it launches a new instance.
- If the application is already running, it switches to the existing window.
- When multiple application windows are open, a sub-menu appears so you can pick one by number.
- Lets you cancel the action with `Esc` or trigger the native Task View by pressing `Win+Tab` again.
- Runs from the system tray and can start automatically on login.

## End-to-End User Flow

### Scenario A: Notepad is not running

1. Press `Win+Tab`.
2. The system Task View does **not** open. Instead, the HiveMotion overlay appears in the center of the screen showing a Notepad icon labeled **N**.
3. Press `N`.
4. The overlay closes and a new Notepad window opens and receives focus.

### Scenario B: Notepad is running with one window

1. Press `Win+Tab` to show the overlay.
2. Press `N`.
3. The overlay closes and the existing Notepad window is brought to the foreground.

### Scenario C: Notepad is running with multiple windows

1. Press `Win+Tab` to show the overlay.
2. Press `N`.
3. The overlay switches to a sub-menu listing all open Notepad windows with numbers `1`, `2`, `3`, etc.
4. Press the number matching the window you want.
5. The overlay closes and the chosen window receives focus.

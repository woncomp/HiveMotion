# Zombie Overlay After Foreground Loss

## Symptoms

After opening HiveMotion with `Win+Tab`, pressing the standalone Windows key could open Start while leaving the HiveMotion overlay visible. The overlay no longer received keyboard input, and later clicks or focus changes did not dismiss it. The issue reproduced on both same-monitor and multi-monitor layouts.

## Root Cause

The overlay relied on WPF's `Deactivated` event to dismiss itself after losing focus. In the failing path, native foreground ownership was confirmed, but WPF did not deliver a usable deactivation transition when Start took input. The overlay therefore became an inactive, topmost "zombie" window. Later foreground changes could not generate a second deactivation edge for the already inactive overlay.

`WindowSnapshotService` already subscribed to `EVENT_SYSTEM_FOREGROUND`, but it used that event only to request a background window scan and did not notify the application.

## Resolution

Commit `0dbaa47` adds an internal foreground-change event to `WindowSnapshotService`. `App` dispatches the event at input priority and dismisses the overlay only when the current presentation generation is still open, native foreground ownership was previously confirmed, and `GetForegroundWindow()` is a nonzero HWND other than the overlay.

The UI-thread recheck rejects delayed or transient native events, and dismissal does not restore the prior application so Start or the newly selected application retains focus. WPF `Deactivated` remains the fast-path fallback for normal click-away behavior.

## Regression Checks

- Repeatedly open with `Win+Tab`, then press the standalone Windows key; Start remains interactive and the overlay hides.
- Test primary and secondary displays, including mixed-DPI layouts and taskbars on different monitors.
- Verify external click-away dismissal, second-hotkey behavior, `Esc`, search, cell selection, folder navigation, and application activation.
- Confirm transient foreground events during native overlay activation do not close the newly opening overlay.

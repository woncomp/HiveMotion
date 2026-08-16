# Overlay Activation Race

## Date

2026-08-19

## Symptom

Pressing the configured hotkey could occasionally leave HiveMotion invisible immediately after opening. The diagnostic log showed that the overlay had been populated and shown, but it was closed before the user could interact with it.

## Diagnostic Signature

The failing sequence was:

1. The overlay was shown and WPF `Focus()` reported success.
2. `WindowManager.ActivateWindowOnce` attached the previous foreground thread input queue.
3. The overlay received `Deactivated` and the application hid it while it was still in `TaskGrid` state.
4. `SetForegroundWindow` then succeeded, but the overlay HWND had already been hidden.

This was not a cell selection: the target handle was the HiveMotion overlay HWND, and the normal cell-activation log entry was absent. The signature occurred with multiple foreground applications, including Qt, Chromium, WPF, and terminal windows, so it was an activation timing race rather than an application-specific compatibility problem.

## Root Cause

`OverlayWindow.ShowTaskGrid` called WPF `Focus()` before native foreground ownership was confirmed. During the subsequent attached-input activation handoff, WPF could raise `Deactivated`. `App` treated every such event as a click-away dismissal and hid the overlay.

## Resolution

Commit `95262f5` introduced a per-presentation Win32 foreground-confirmation gate.

- The overlay defers WPF `Focus()` until `GetForegroundWindow()` confirms its HWND.
- Pre-confirmation `Deactivated` events are logged and ignored; normal click-away dismissal resumes after confirmation.
- `ActivateWindowOnce` now returns verified foreground status and records the foreground HWND before and after the native activation attempt.
- Background activation retries preserve the original correlation ID. If retries are exhausted, the visible overlay remains usable by mouse or hotkey and a warning is logged.

## Verification

- Build with `dotnet build HiveMotion.sln -c Release`.
- Repeatedly open the overlay from Qt, Chromium, WPF, Windows Terminal, desktop, and shell foreground windows.
- Confirm that no `Closing overlay; restoreFocus=False; state=TaskGrid` entry occurs before foreground ownership is confirmed.
- Verify click-away dismissal, `Esc`, second-hotkey close, cell selection, focus restoration, multi-monitor behavior, and high-refresh-rate operation.

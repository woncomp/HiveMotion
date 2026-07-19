# Pinned Cells — Manage Center UI

Feature documentation for the pin management interface (the **固定 / Pins** page of the
manage center) and how it relates to the in-overlay `Ctrl+P` flow.

## 1. Overview

A *pinned cell* reserves one keyboard letter (A–Z) for a **launch identity**: an
executable path plus its argument set. Once pinned:

- the cell only ever matches windows of the same program launched with the same
  arguments — other windows can never occupy it;
- if no matching window is running, the cell stays visible as a launcher: clicking it
  relaunches the program with the exact pinned arguments and working directory;
- pins persist across restarts in `%AppData%/HiveMotion/pins.json`.

There are two ways to manage pins, and they share one store and one set of rules:

| Path | Interaction |
|---|---|
| Hive overlay | `Ctrl+P` while hovering (or search-highlighting) a cell: pin, remove, or move |
| Manage center | Tray icon → 管理中心… → 固定 page: full editing, drag & drop letter assignment, launch-history picker |

Changes made in either place write through to `pins.json` immediately; the overlay
re-reads the store every time it opens, so no synchronization mechanism is needed.

## 2. Entry

Tray icon → right-click menu → **管理中心…**, then the **固 定** navigation item.
The manage center is a normal singleton window (visible in Alt+Tab and the taskbar);
reopening the menu item brings the existing window forward.

## 3. Page layout

```
┌────────────────────────────────────────────────────────────┐
│ 字 母 分 配        拖拽格子调整字母 · 点击格子编辑   [清空全部固定] │
│  ┌────┐┌────┐┌────┐ … QWERTYUIOP                            │
│  │icon││ W  ││icon│   ASDFGHJKL                             │
│  └────┘└────┘└────┘   ZXCVBNM                               │
├────────────────────────────────────────────────────────────┤
│ [icon] [N] 显示名称                            状态          │
│        程序路径 [_____________________] [浏览…]              │
│        启动参数 [_____________________]                      │
│        工作目录 [_____________________] [浏览…]              │
│        完整命令行 (preview)                                  │
│        [立即启动] [删除固定]                                  │
└────────────────────────────────────────────────────────────┘
```

## 4. Letter overview (top card)

All 26 letters in keyboard rows. Tiles are built from `KeyGrid.Rows`, so the layout
mirrors the hive overlay.

- **Pinned tile**: honey-gold border, app icon, letter badge (top-left), status dot
  (bottom-right): green = a running window currently matches the identity, gray = not
  running. Status refreshes every 5 seconds via a background scan.
- **Empty tile**: dim letter; click opens the history picker (§6).
- **Click a pinned tile**: opens it in the editor (§5).

### 4.1 Drag & drop — the only way to change a letter

The letter is deliberately **not** editable in the editor (no dropdown); reassignment
happens by dragging tiles:

| Gesture | Result |
|---|---|
| Drop on an empty letter | Move the pin to that letter |
| Drop on an occupied letter | The two pins **swap** letters — no confirmation (cheap to undo by dragging back) |
| Drop outside the grid | Cancels; the pin stays put |

While dragging, the hovered target tile gets a bright honey border as the landing
preview. Both stores save immediately on drop, and the editor follows the dragged pin
(its letter badge updates in place).

## 5. Pin editor (bottom card)

Appears when a pinned tile is selected. All edits write through on `LostFocus`
(即改即存); destructive actions ask for confirmation via the in-window modal.

| Field | Rules |
|---|---|
| Letter badge | Read-only; reflects the current letter (drag to change) |
| 显示名称 | Free text; if cleared, falls back to the executable's file name on save |
| 程序路径 | Required for a meaningful pin. Missing file → red border + ⚠ warning, but saving is **not** blocked (removable drives, network paths) |
| 启动参数 | Free text, original quoting preserved; whitespace-normalized for matching |
| 工作目录 | Optional; folder picker available; non-existent folder only warns implicitly on launch (launch skips a missing directory) |
| 完整命令行 | Read-only live preview: `path + arguments`, updated on every keystroke |
| 状态 | `● 运行中(有窗口匹配该命令行)` / `○ 未运行`, using the same matching rule as the overlay assigner — the quickest way to learn what "same program, same arguments" means |
| 立即启动 | Launches `path` with `arguments` in `工作目录` (commits pending edits first) |
| 删除固定 | Confirms, removes the pin, closes the editor |

The header also shows the resolved 48px icon via `IconHelper.ForExecutable`.

## 6. History picker

Clicking an **empty** tile opens the picker modal for that letter.

- **Data source**: `history.json`. Every hive scan feeds the store; an identity absent
  from the previous scan counts as a fresh launch, so **LaunchCount** approximates
  "how often this exact command line was started".
- **Ordering**: existing executables first, then LaunchCount descending, then most
  recent — frequently launched programs surface at the top.
- **Row contents**: icon, display name, full command line, `启动 N 次 · 相对时间`, and
  a `运行中` badge when the identity is currently running.
- **Missing files**: entries whose exe no longer exists are dimmed to 50%, tagged
  `(文件缺失)`, and sink to the bottom — kept, not deleted (may be a detached drive).
- **Search**: filters display name / path / arguments.
- **手动填写…**: file-open dialog for programs that never appeared in a scan; creates
  the pin with the exe's file name as display name.
- Selecting a row pins that identity to the letter immediately and opens the editor.

`Esc` or clicking the dimmed backdrop closes the picker.

## 7. Matching rules (shared by overlay and manage center)

A running window matches a pin when:

1. its process image path equals the pin's `ExecutablePath` (case-insensitive), and
2. its argument tail equals the pin's `Arguments` after whitespace normalization
   (case-insensitive). A pin with **empty** arguments matches any arguments — this is
   also the fallback when a process's command line cannot be read (elevated/protected
   processes).

When several windows match one pin (multi-window single process), the topmost window
in z-order wins the cell.

## 8. Overlay counterparts (for reference)

| Overlay UI | Behavior |
|---|---|
| `Ctrl+P` on an unpinned running cell | Captures exe + arguments + working directory from the process (PEB) and pins it to that letter |
| `Ctrl+P` on a pinned cell (running or not) | In-overlay confirm: remove the pin |
| `Ctrl+P` on a cell whose identity is pinned on another letter | Confirm: move the pin to the hovered letter |
| Pinned-not-running cell | Amber name + `点击启动` hint; hover shows the full command line in the preview area instead of a DWM thumbnail; click relaunches |
| Pinned running cell | Behaves like any running cell (title, thumbnail, activation) |
| Search list extras | `Ctrl+R` reveals the exe in Explorer, `Ctrl+S` copies the command line |

Rejection notices in the overlay: UWP windows (`ApplicationFrameHost`, identity cannot
be relaunched) and processes whose image path cannot be queried.

## 9. Data model & persistence

- `PinnedApp { Key, ProcessName, ExecutablePath, Arguments, WorkingDirectory, DisplayName }`
- `pins.json` — one array, written on every mutation (`Set`/`Remove`/`ReplaceAll`)
- `history.json` — `HistoryEntry` records with `LaunchCount`, `FirstSeen`, `LastSeen`;
  capped at 200 entries, least-recently-seen evicted first
- Export/import (常规 page) bundles pins, history and settings into one JSON file

## 10. Known limitations

- Pins are **process identities**, not window identities: two windows of one process
  are indistinguishable, and pinning a non-main window pins its process. Relaunch can
  only reproduce the command line, not a specific document window.
- Elevated processes: command line unreadable → exe-only pin matching any arguments.
- UWP apps cannot be pinned (no relaunchable exe identity).
- `explorer.exe` pins relaunch to the default folder, not the previously open folder.
- Letter reassignment has no keyboard-only path yet (drag & drop only).

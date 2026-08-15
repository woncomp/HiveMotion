# Cells & Motions — Model, Overlay Behavior, and Manage Center UI

Feature documentation for the cell/motion model: the **固定 / Pins** page of the
manage center, the in-overlay `Ctrl+P` flow, and folder cells.

## 1. Concept model

The hive grid is a set of 26 letter-key **cells** (containers). A **Motion** is
anything that can occupy a cell:

- **ApplicationMotion** — a *launch identity*: an executable path plus its argument
  set. This is the semantic successor of the old "pinned app". Once placed:
  - the cell only ever matches windows of the same program launched with the same
    arguments — other windows can never occupy it;
  - if no matching window is running, the cell stays visible as a launcher: clicking
    it relaunches the program with the exact pinned arguments and working directory.
- **FolderMotion** — a named group with a custom icon. Activating a folder cell swaps
  the whole grid to the folder's own items. Folder layers are **never backfilled**
  with scanned windows: only the configured items occupy cells, empty letters stay
  empty. Folders cannot nest (enforced by store validation and the manage center,
  not by the item type, so future motion kinds stay placeable inside folders).

New motion kinds extend the abstract `Motion` type and become placeable on the home
layer and inside folders without model changes.

Motions persist across restarts in `%AppData%/HiveMotion/motions.json` as a
polymorphic JSON array (`"$type": "application" | "folder"`).

There are two ways to manage motions, and they share one store and one set of rules:

| Path | Interaction |
|---|---|
| Hive overlay | `Ctrl+P` while hovering (or search-highlighting) an application cell: pin, remove, or move |
| Manage center | Tray icon → 管理中心… → 固定 page: full editing, drag & drop letter assignment, folder editing, launch-history picker |

Changes made in either place write through to `motions.json` immediately; the
overlay re-reads the store every time it opens, so no synchronization mechanism is
needed.

### Migration from pins.json

On startup, if `motions.json` does not exist but a legacy `pins.json` does, every
legacy pin is converted to an application motion and saved as `motions.json`. The
legacy file is left on disk as a rollback backup. Backup import (常规 page) likewise
accepts v1 bundles whose `Pins` payload has no `$type` discriminator.

## 2. Entry

Tray icon → right-click menu → **管理中心…**, then the **固 定** navigation item.
The manage center is a normal singleton window (visible in Alt+Tab and the taskbar);
reopening the menu item brings the existing window forward.

## 3. Page layout

```
┌────────────────────────────────────────────────────────────┐
│ 字 母 分 配        拖拽格子调整字母 · 点击格子编辑   [清空全部固定] │
│  ┌────┐┌────┐┌────┐ … QWERTYUIOP                            │
│  │icon││ W  ││ 📁│   ASDFGHJKL                              │
│  └────┘└────┘└────┘   ZXCVBNM                               │
├────────────────────────────────────────────────────────────┤
│ Application editor  ← shown for application cells           │
│  [icon] [N] 显示名称                            状态          │
│        程序路径 [_____________________] [浏览…]              │
│        启动参数 [_____________________]                      │
│        工作目录 [_____________________] [浏览…]              │
│        完整命令行 (preview)                                  │
│        [立即启动] [删除固定]                                  │
├────────────────────────────────────────────────────────────┤
│ Folder editor  ← shown for folder cells                     │
│  [icon] [N] 文件夹名称                          N 个项目      │
│        图标     [_____________________] [浏览…] [清除]       │
│        内容 · 拖拽调整字母，点击编辑                          │
│        ┌──┐┌──┐┌──┐ … (26 letter child grid)                │
│        [删除文件夹]                                          │
└────────────────────────────────────────────────────────────┘
```

## 4. Letter overview (top card)

All 26 letters in keyboard rows. Tiles are built from `KeyGrid.Rows`, so the layout
mirrors the hive overlay.

- **Application tile**: honey-gold border, app icon, letter badge (top-left), status
  dot (bottom-right): green = a running window currently matches the identity,
  gray = not running. Status refreshes every 5 seconds via a background scan.
- **Folder tile**: custom icon or folder glyph, letter badge, small folder badge in
  place of the status dot (folders have no running state).
- **Empty tile**: dim letter; click opens the picker (§6) for that letter.
- **Click an occupied tile**: opens it in the matching editor (§5 / §7).

### 4.1 Drag & drop — the only way to change a letter

The letter is deliberately **not** editable in the editors (no dropdown);
reassignment happens by dragging tiles:

| Gesture | Result |
|---|---|
| Drop on an empty letter | Move the motion to that letter |
| Drop on an occupied letter | The two motions **swap** letters — no confirmation (cheap to undo by dragging back) |
| Drop outside the grid | Cancels; the motion stays put |

While dragging, the hovered target tile gets a bright honey border as the landing
preview. The store saves immediately on drop, and the editor follows the dragged
motion (its letter badge updates in place). Dropping a motion *onto* a folder tile
does **not** insert it into the folder (folder contents are edited inside the
folder editor).

## 5. Application editor (bottom card)

Appears when an application tile is selected, on the home layer or inside a folder.
All edits write through on `LostFocus` (即改即存); destructive actions ask for
confirmation via the in-window modal.

| Field | Rules |
|---|---|
| Letter badge | Read-only; reflects the current letter (drag to change) |
| 显示名称 | Free text; if cleared, falls back to the executable's file name on save |
| 程序路径 | Required for a meaningful application cell. Missing file → red border + ⚠ warning, but saving is **not** blocked (removable drives, network paths) |
| 启动参数 | Free text, original quoting preserved; whitespace-normalized for matching |
| 工作目录 | Optional; folder picker available; non-existent folder only warns implicitly on launch (launch skips a missing directory) |
| 完整命令行 | Read-only live preview: `path + arguments`, updated on every keystroke |
| 状态 | `● 运行中(有窗口匹配该命令行)` / `○ 未运行`, using the same matching rule as the overlay assigner |
| 立即启动 | Launches `path` with `arguments` in `工作目录` (commits pending edits first) |
| 删除固定 | Confirms, removes the application cell, closes the editor |

The header also shows the resolved 48px icon via `IconHelper.ForExecutable`.
When the editor edits a **folder child**, a `← 返回文件夹` button appears in the
header and deletion removes the child from its folder instead of the home layer.

## 6. History picker

Clicking an **empty** tile — on the home layer or inside the folder editor's child
grid — opens the picker modal for that letter.

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
  the application cell with the exe's file name as display name.
- **新建文件夹** (home layer only): creates an empty folder on that letter and opens
  the folder editor. Hidden when picking a folder child, because folders cannot nest.
- Selecting a row places that identity on the letter immediately and opens the editor.

`Esc` or clicking the dimmed backdrop closes the picker.

## 7. Folder editor (bottom card)

Appears when a folder tile is selected.

| Field | Rules |
|---|---|
| 文件夹名称 | Free text; if cleared, falls back to the localized default ("新建文件夹") |
| 图标 | Optional custom icon image (png/ico/jpg/bmp/exe). Decoded at a bounded size, frozen, and cached by path + last-write time (`IconHelper.ForImageFile`). Cleared → folder glyph fallback |
| 内容 (child grid) | 26 letters mirroring the home overview. Empty letter → picker adds a child application; child tile → application editor (§5) with a back-to-folder button; drag & drop moves/swaps child letters |
| 删除文件夹 | Confirms; the folder and its items are removed together (items are independent copies, home-layer cells are unaffected) |

Folder children are application motions with the same fields and matching rules as
home-layer application cells. The running-status dots inside the child grid reuse
the same 5-second scan.

## 8. Overlay behavior

| Overlay UI | Behavior |
|---|---|
| Letter key / click on an application cell (running) | Activates the matched window |
| Letter key / click on an application cell (not running) | Relaunches with the exact pinned arguments and working directory |
| Letter key / click on a folder cell | **Enters the folder**: the 26 cells swap to the folder's items in place; the overlay stays open |
| `Esc` | Pops one layer: folder → home, home → close |
| `Backspace` | Same layer pop as `Esc` (no-op on the home layer) |
| `Space` (search) | Searches the **current layer's** cells only |
| `Ctrl+P` on an unpinned running cell (home layer) | Captures exe + arguments + working directory from the process (PEB) and pins it to that letter |
| `Ctrl+P` on a pinned application cell (running or not) | In-overlay confirm: remove the pin |
| `Ctrl+P` on a cell whose identity is pinned on another letter | Confirm: move the pin to the hovered letter |
| `Ctrl+P` inside a folder layer | No-op; folder contents are edited in the manage center |
| Pinned-not-running cell | Amber name + `点击启动` hint; hover shows the full command line in the preview area instead of a DWM thumbnail; click relaunches |
| Folder cell | Folder badge + `点击进入` hint; hover shows the folder name and item count; snapshot refreshes never backfill folder layers |
| Search list extras | `Ctrl+R` reveals the exe in Explorer, `Ctrl+S` copies the command line (home layer only) |

Rejection notices in the overlay: UWP windows (`ApplicationFrameHost`, identity
cannot be relaunched) and processes whose image path cannot be queried.

### 8.1 Hover preview is motion-defined

Each motion kind declares what the hover preview area shows via
`Motion.DescribeHover(cell)`, which returns a lightweight `MotionHoverPreview`
descriptor (`None` / `WindowThumbnail` / `Info { Title, Detail }`). The overlay
(`TaskGridView.ShowPreview`) only renders the descriptor — DWM thumbnail interop
stays in `TaskGridView` / `DwmThumbnailPreview`. Today: application motions provide
the live DWM thumbnail when running and the launch identity when not; folders
provide an info panel (name + item count); plain scanned windows default to the
thumbnail.

## 9. Matching rules (shared by overlay and manage center)

A running window matches an application motion when:

1. its process image path equals the motion's `ExecutablePath` (case-insensitive), and
2. its argument tail equals the motion's `Arguments` after whitespace normalization
   (case-insensitive). A motion with **empty** arguments matches any arguments — this
   is also the fallback when a process's command line cannot be read
   (elevated/protected processes).

When several windows match one motion (multi-window single process), the topmost
window in z-order wins the cell.

## 10. Data model & persistence

- `Motion` (abstract) — `Key`, `DisplayName`, `DescribeHover(cell)`
- `ApplicationMotion` — `ProcessName`, `ExecutablePath`, `Arguments`,
  `WorkingDirectory`, matching helpers
- `FolderMotion` — `IconPath`, `Items` (`List<Motion>`; nesting rejected at load/edit)
- `motions.json` — one polymorphic array (`$type` discriminator), written on every
  mutation (`MotionStore.Set`/`Remove`/`ReplaceAll`/`Save`). Load sanitizes: A-Z
  letters only, unique per layer, applications require an executable path, nested
  folders are dropped with a log line.
- `pins.json` — legacy format; migrated once into `motions.json` (see §1), then ignored
- `history.json` — `HistoryEntry` records with `LaunchCount`, `FirstSeen`, `LastSeen`;
  capped at 200 entries, least-recently-seen evicted first
- Export/import (常规 page) bundles motions, history and settings into one JSON file;
  v1 bundles with a flat `Pins` payload import as application motions

## 11. Known limitations

- Application motions are **process identities**, not window identities: two windows
  of one process are indistinguishable, and pinning a non-main window pins its
  process. Relaunch can only reproduce the command line, not a specific document window.
- Elevated processes: command line unreadable → exe-only matching any arguments.
- UWP apps cannot be pinned (no relaunchable exe identity).
- `explorer.exe` pins relaunch to the default folder, not the previously open folder.
- Letter reassignment has no keyboard-only path yet (drag & drop only).
- Folder contents cannot be edited from the overlay (manage center only).
- Dragging a tile onto a folder tile swaps letters instead of inserting into the folder.

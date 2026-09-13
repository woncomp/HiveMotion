# Phase 1 — UI-Thread Contention Fixes

Status: Complete (2026-09-12; verified with the release build via Computer Use, committed)

## Outcome

- **Evidence 1 (log viewer) was already resolved before this phase started.** Commit `270b2d0` removed the built-in log viewer entirely (log viewing moved to external tools per `docs/logging.md`), so no viewer work remained; per-entry dispatcher callbacks and full-text rebuilds no longer exist.
- **History persistence is now asynchronous** (`HistoryStore`): mutating calls deep-snapshot entries on the UI thread and hand the snapshot to a single background writer that coalesces repeated saves; a bounded `Flush()` drains pending writes at shutdown (`App.OnExit`). `SortedForPicker()` reads a cached per-path existence flag (optimistic for unknown paths) refreshed on a background thread triggered by the picker open; no `File.Exists` runs on the UI thread.
- **Hook logging is lazy** (`GlobalKeyboardHook`): `DescribeForeground()` now runs only when `Logger.IsVerboseEnabled` is true; the remaining hook-path interpolations are cheap value formatting. The audited benign-chord log needed no change.
- New checks: `tests/HiveMotion.HistoryChecks` (4 checks: write coalescing, snapshot isolation during in-flight saves, failed-save memory integrity, existence-cache picker ordering). 14 icon + 17 handoff + 4 history checks all pass; Release build is warning-free.

## Original Analysis (kept for the record)

## Evidence

1. **Log viewer rebuilds everything per entry.** `LogWindow.xaml.cs:29-37` queues a dispatcher callback per log entry, and `RefreshOutput()` (`LogWindow.xaml.cs:72-78`) rebuilds the entire displayed text with `string.Join` over all entries. With verbose logging enabled during activations, every entry triggers a full rebuild on the UI thread.
2. **History persistence writes synchronously on the UI thread.** `App.xaml.cs:395-408` dispatches `HistoryStore.RecordScan` at `ApplicationIdle` priority, but `RecordScan` calls `Save()` (`HistoryStore.cs:87-88`), which serializes JSON and calls `File.WriteAllText` inline (`HistoryStore.cs:132-144`). Once started, it can delay a subsequent hotkey. Separately, `SortedForPicker()` (`HistoryStore.cs:92-97`) calls `File.Exists` per entry on the UI thread.
3. **The keyboard hook formats log arguments eagerly.** `GlobalKeyboardHook.cs:157` interpolates `DescribeForeground()` (four P/Invoke calls plus string building) into the message before `Logger.ActivationInfo` checks `IsVerboseEnabled` inside (`Logger.cs:88-93`). The work runs on the low-level hook callback even when verbose logging is off.

## Scope and Non-Goals

- In scope: the three items above, including an audit of other interpolated log calls on the hook hot path.
- Out of scope: changing log file format, log retention, history.json schema, or hook rule behavior.

## Steps

### 1. Incremental log-viewer output

- Add an append fast path: when a new entry passes the current filters, append `Environment.NewLine + entry.DisplayText` instead of rebuilding; update the entry count; scroll only when the follow toggle is on.
- Coalesce bursts with a short `DispatcherTimer` (background priority, ~100 ms) so N queued entries produce one append batch.
- Keep the existing full `RefreshOutput()` for filter, search, clear, and culture changes only.
- Preserve current filter/clear/copy/follow semantics.

### 2. Background history persistence

- Keep `RecordScan`'s in-memory diff on the UI thread (it is cheap); move `Save()`'s serialization and disk write to a single background writer task with a dirty flag, coalescing repeated saves (the same single-writer pattern used by `AsyncResourceCache`).
- Serialize from a snapshot copy of `Entries` so UI-thread reads during a save are safe. `Entries` ownership stays on the UI thread; no background mutation.
- Replace the per-call `File.Exists` in `SortedForPicker()` with a cached existence flag per path, refreshed on a background thread when the picker opens; fall back to the cached value. Keep the existing ordering semantics (existing files first, then most-launched, then most-recent).
- `Clear()` and `ReplaceAll()` keep their current synchronous semantics for callers, then mark the writer dirty.

### 3. Lazy hook logging

- At `GlobalKeyboardHook.cs:157`, evaluate `DescribeForeground()` only when `Logger.IsVerboseEnabled` is true (guard at the call site, or add a lazy-message overload to `Logger`).
- Audit the hook callback path for other eager interpolations (e.g., the benign-chord log at `GlobalKeyboardHook.cs:196-197`) and make them equally cheap when logging is disabled.
- The hook callback must do no P/Invoke description work and no string allocation when verbose logging is off.

## Automated Verification

- Follow the `tests/HiveMotion.IconChecks` console-check pattern to add checks for background history persistence: repeated `RecordScan` calls coalesce into bounded writes; mutations made during an in-flight save do not corrupt the written JSON; save failures leave in-memory history intact.
- `dotnet build HiveMotion.sln -c Release` with zero warnings and zero errors.
- All existing checks pass (14 icon checks, 17 handoff checks).

## Manual Validation

- Open the log window with verbose logging enabled, trigger several activations, and confirm filters, search, copy, clear, and follow still behave correctly.
- Launch and close applications, then inspect `history.json` for correct counts and timestamps.
- With verbose logging disabled, confirm (by code review plus a verbose-enabled comparison run) that the hook performs no foreground-description work.

## Acceptance

- No per-entry full-text rebuild in the log viewer.
- No synchronous file write or per-entry `File.Exists` from history code on the UI thread.
- Hook callback performs no eager log-argument work when verbose logging is disabled.
- Shared acceptance baseline from [activation-performance.md](activation-performance.md) holds.

## Dependencies

None. Independent of all other phases.

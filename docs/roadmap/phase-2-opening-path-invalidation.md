# Phase 2 — Opening-Path UI Invalidation Reduction

Status: Complete (2026-09-12; verified with the release build via Computer Use, committed)

## Outcome

- **Diff-based application.** `ApplyCells` keeps a per-letter baseline (`_appliedCells`) of what each pool view currently shows and skips `SetCell` when the identity-relevant content (motion reference and its IsConfigured/DisplayName/IconPath, icon, icon request, window handle, process identity, title, names, command line) is unchanged; `Visibility` is written only on actual occupancy flips. The baseline survives `ResetForOverlayClose`, so reopening the overlay with unchanged windows performs no per-cell updates at all.
- **No-op reset fast path.** The reset block (preview/toast animation clears plus `ExitSearchImmediate`) runs only when transient state is live — search transitions, visible preview, confirm dialog, toasts, or running animations. A pristine overview re-entry skips it entirely; `IsTransientStateClean()` gates the skip conservatively.
- **Search rows off the opening path.** `ApplyCells` no longer calls `RebuildResults()`; it invalidates the result version and schedules a build at `ContextIdle` bound to the cells generation (stale builds are dropped; hidden/closed overlays skip). `EnterSearch` forces a synchronous build, so the first Space press always sees rows.
- **Allocation audit.** Caption brushes (`HiveCellView`), the highlight-row brush, and the space-bar border colors are frozen statics; no per-update `ColorConverter` or brush allocation remains on per-cell paths.
- New checks: `tests/HiveMotion.OpeningChecks` (5 checks: same-cells no-rerender, partial update re-renders only the changed letter, handle change re-renders, clean reopen touches no search state, first search entry builds rows). 14 icon + 17 handoff + 4 history + 5 opening checks all pass; Release build is warning-free.

> Phase 6 measurement: `grid-apply-start -> grid-apply-pool-complete` was 12 ms on the cold open and 0.1-1 ms on warm reopens; the search rebuild never appeared on the opening path.

## Original Analysis (kept for the record)

## Evidence

`TaskGridView.xaml.cs:260-303` (`ApplyCells`) currently, on every open:

- Allocates a dictionary over all cells (`:273`).
- Calls `SetCell` and sets `Visibility` on every occupied pool view (`:274-286`), even when the cell content is unchanged since the last open.
- Resets preview state and calls `ExitSearchImmediate()` whenever `resetSearch` is true (`:288-298`), including opens that were never searching.
- Calls `RebuildResults()` unconditionally (`:302`) to pre-build the search-result tree before Space is pressed.

The 355 ms sample from the analysis spent ~268.7 ms between the backdrop checkpoint and `Show()` returning, which covers this path. The existing coarse checkpoints (`cell-assignment-complete` → `overlay-shown`) provide the before/after regression signal for this phase; finer attribution arrives with [Phase 5](phase-5-activation-instrumentation.md) and must not gate this work.

## Scope and Non-Goals

- In scope: diff-based cell updates, skipping no-op resets, moving the search-tree rebuild off the opening path, brush/allocation audit in per-cell update code (`HiveCellView.xaml.cs`, badge/tooltip updates in `TaskGridView.xaml.cs`).
- Out of scope: changing cell layout, grid geometry, search behavior or visuals; introducing a hidden pre-measure pass except as the explicitly conditional option below.
- Constraint from AGENTS.md: never clear and recreate the visual tree inside a keyboard transition; pre-created pools keep their current role.

## Steps

1. **Diff-based application.** Track the last applied cell per pool letter (`_displayedCells` already exists). Compare identity-relevant content (motion reference, window handle, title, running state, name) and skip `SetCell` entirely for unchanged cells. Set `Visibility` only when occupancy actually changes.
2. **Skip no-op resets.** When opening to the overview and the previous state was not searching and had no visible preview/confirm/toast, make the reset block a verified no-op fast path instead of unconditional animation clears and visibility writes.
3. **Move `RebuildResults()` off the opening path.** Build the result tree at `ContextIdle` priority after `Show()` returns, or lazily before the first Space press — preserving the existing intent that the tree exists before search is entered. Bind the deferred build to the transition/presentation generation so a close/reopen cannot apply a stale build, and so the rebuild never runs inside a keyboard transition.
4. **Allocation audit.** Reuse frozen brushes (the `FrozenBrush` helper already exists) and cached strings in per-cell updates; no per-open color parsing or repeated badge allocations.
5. **Conditional option — hidden pre-measure.** Only if the before/after `overlay-shown` comparison still attributes a large residual to first layout of the populated view, pre-measure/arrange the populated grid while hidden (`Opacity=0`, hit-testing disabled), per the AGENTS.md transition-architecture rules. Do not implement this speculatively.

## Automated Verification

- `dotnet build HiveMotion.sln -c Release` with zero warnings and zero errors; all existing checks pass.
- Add hidden-control checks in the `tests/HiveMotion.IconChecks` style where practical: applying the same cell list twice must not re-run per-cell content updates; opening from a non-searching state must not touch search transforms.

## Manual Validation

- Exercise open/close, letter selection, multi-window numeric selection, folder enter/exit, search enter/exit (Space/Esc), and Ctrl+P pinning.
- Confirm search results appear correctly on the first Space press after each open (no missing or stale rows).
- Compare the `cell-assignment-complete` → `overlay-shown` interval distribution before and after using existing verbose activation logs.

## Acceptance

- Unchanged cells and unchanged visibility produce no visual-tree mutations on reopen.
- No search rebuild work occurs between hotkey receipt and `Show()` returning.
- No behavioral regression in the manual flows above.
- Shared acceptance baseline from [activation-performance.md](activation-performance.md) holds.

## Dependencies

None blocking. [Phase 3](phase-3-post-show-coalescing.md) modifies the same opening-path code and must be implemented after this phase.

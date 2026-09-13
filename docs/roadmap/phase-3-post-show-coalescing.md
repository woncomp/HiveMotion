# Phase 3 — Post-Show Update Coalescing and Dispatcher Priorities

Status: Complete (2026-09-12; verified with the release build via Computer Use, committed)

## Outcome

- **Opening gate.** New `OpeningUpdateGate` opens when an activation generation starts and closes on keyboard readiness (`OverlayWindow` now signals a once-per-activation `KeyboardReady` event, also fired when readiness retries are exhausted) or when the overlay closes. While open, snapshot-driven updates — Window View projection batch applications and `RefreshTaskGrid` — retain only the newest snapshot and apply nothing; the release rebuilds the projection batch and applies it once at `Background` priority, bound to the existing generation checks. The refresh requested at the end of `OpenTaskGrid` now lands through this gate instead of inline.
- **Deprioritized.** Snapshot publish and projection-batch callbacks dispatch at `DispatcherPriority.Background` (below `Render`), down from `Normal`.
- **Verification.** The verbose log confirms the ordering on a churned opening: `keyboard-ready-timeout +609ms` immediately followed by `Opening gate released; applying retained snapshot …` (exactly one coalesced application), while clean openings show `keyboard-ready` with no gate activity. A console check covers the gate contract (retain newest only, release once, reopen discards residue). 14 icon + 17 handoff + 4 history + 6 opening checks all pass; Release build is warning-free.

> Phase 6 measurement: churned opens applied exactly one retained snapshot after readiness; no opening-frame interruption attributable to snapshot application in any sample.

## Original Analysis (kept for the record)

## Evidence

- `App.xaml.cs:356` dispatches `ApplyWindowViewProjectionBatch` at the default `Normal` dispatcher priority, which runs above `Render`.
- `App.xaml.cs:240` calls `_windowSnapshots.RequestRefresh()` immediately after `ShowTaskGrid` returns, so a fresh scan can publish while the opening transition is still rendering.
- The 1,234 ms sample from the analysis returned from `Show()` at 31.4 ms yet rendered ~1.2 s later without using any attached-input fallback; snapshot/bounds callbacks at `Normal` priority and uncoalesced updates are among the remaining suspects. Bounds reapplication was already made conditional on an actual rect mismatch (`OverlayWindow.xaml.cs:187-196`).
- The post-render activation path already demonstrates the required pattern: `QueuePostRenderActivation` (`OverlayWindow.xaml.cs:171-204`) waits for `Show()` plus the first render, then works at `Background` priority under a generation guard.

## Scope and Non-Goals

- In scope: coalescing and deprioritizing snapshot-driven updates (Window View projection batches, `RefreshTaskGrid`/`UpdateCells`) while the overlay is opening; the timing of the refresh requested at the end of `OpenTaskGrid`.
- Out of scope: changing scanner behavior, snapshot contents, or the activation/foreground logic; re-measuring beyond the existing coarse checkpoints (that is [Phase 5](phase-5-activation-instrumentation.md)).

## Steps

1. **Opening gate.** While an activation generation is between `Show()` and keyboard-ready, snapshot-driven cell refreshes and Window View projection batches are not applied immediately; only the newest batch is retained.
2. **Deprioritize.** Dispatch the retained application at `Background` priority (the same pattern as `QueuePostRenderActivation`), bound to the activation generation so stale work cannot modify a newer state.
3. **Apply once.** After keyboard readiness (or immediately when the overlay is already open and stable), apply the newest retained batch; discard superseded batches.
4. **Refresh timing.** Keep the `RequestRefresh()` at the end of `OpenTaskGrid` (`App.xaml.cs:240`) but ensure its publish lands through the gate above, never inline in the opening path; document the resulting ordering.
5. **Preserve the search guarantee.** Per AGENTS.md, pressing Space during a scanner refresh must not cancel, restart, or visibly alter a running transition; the gate must hold during search entry/exit as well (icon updates already defer this way via `IconPresentationState`).

## Automated Verification

- Add a console check (IconChecks style): a snapshot published mid-opening is applied exactly once, after readiness, with the newest content; a superseded batch is never applied.
- `dotnet build HiveMotion.sln -c Release` with zero warnings and zero errors; all existing checks pass.

## Manual Validation

- Open the overlay while windows are churning (e.g., while an application launches); the opening transition must not stutter from snapshot application.
- Rapidly close and reopen the overlay; no stale or duplicate cell content.
- Press Space during a scanner refresh; the transition must be unaffected.

## Acceptance

- No snapshot-driven UI update executes between `Show()` and keyboard readiness.
- At most one coalesced application runs afterward, carrying the newest state.
- No visible regression in window-content freshness while the overlay is open.
- Shared acceptance baseline from [activation-performance.md](activation-performance.md) holds.

## Dependencies

Must be implemented after [Phase 2](phase-2-opening-path-invalidation.md); both modify the opening path, and this phase builds on its diff-based `ApplyCells`.

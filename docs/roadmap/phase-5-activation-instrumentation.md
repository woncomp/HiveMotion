# Phase 5 — Activation Instrumentation and Log Analysis

Status: Complete (2026-09-12; verified with the release build via Computer Use, committed)

## Outcome

- **Finer checkpoints** (all verbose-gated; `ActivationTiming.Checkpoint` now guards `IsVerboseEnabled` internally so the disabled path allocates nothing):
  - `grid-apply-start`, `grid-apply-pool-complete`, `grid-search-rebuild-complete` — `TaskGridView.ApplyCells` entry, after the diffed cell-pool loop, and after the deferred ContextIdle search rebuild (`TaskGridView.ActivationTiming` is fed by `ShowTaskGrid`).
  - `native-show-start`, `native-show-complete` — around the native `Show()` call in `ShowTaskGrid`.
  - `screen-bounds-corrected` (existing) now has a paired verbose `bounds-check obtained=… mismatch=… rect=… expected=…` line for both outcomes.
  - `projection-batch retained …` / `projection-batch applied … duration=…ms` — verbose-only duration lines for the Phase 3 gate paths (not `activation`-shaped; grep-able alongside).
- **Log-analysis tool** `tools/analyze-activation-logs.ps1`: parses `activation <name> +<ms>ms` lines (including the `first-wpf-render (not hardware presentation)` parenthetical) into per-activation groups; reports per-stage cumulative and per-consecutive-interval distributions (count/min/median/p95/max) plus timelines for the slowest N activations; supports `-From`/`-To`/`-Filter`/`-TopN`; `-SelfTest` validates against `tools/sample-activation.log.txt`.
- **Effect comparison** (today's retained logs, 8 activations, all from the verification session — small sample under automation load; Phase 6 collects real-usage data):
  - Assignment and opening-path stages are now uniformly small: `snapshot-ready → cell-assignment-complete` max 13.1 ms (documented pre-roadmap sample: 130.3 ms); `grid-apply-start → grid-apply-pool-complete` ≤ 1 ms on reopen diffs; post-open refresh applies cost ~1 ms.
  - **Dominant remaining interval (flagged for Phase 6):** foreground confirmation. Two activations show `keyboard-ready-timeout` (~600 ms of denied `SetForegroundWindow`) followed by `foreground-confirmed` 13.8 s / 18.1 s later — the Issue-1 multi-second stall, now precisely attributable: the opening path itself completes in <50 ms; the stall is entirely in foreground acquisition. Retry cadence `foreground-request-returned → foreground-request-start` is 85–121 ms (the 80 ms retry timer + scheduling).

## Checkpoint Name Reference

Stable, lowercase-hyphenated activation checkpoint names: `hotkey-received-ui`, `snapshot-ready`, `cell-assignment-complete`, `overlay-ui-start`, `backdrop-cache-hit`, `backdrop-cache-miss`, `grid-apply-start`, `grid-apply-pool-complete`, `grid-search-rebuild-complete`, `native-show-start`, `native-show-complete`, `overlay-shown`, `first-wpf-render` (with parenthetical), `screen-bounds-corrected`, `foreground-request-start`, `foreground-request-returned`, `foreground-confirmed`, `keyboard-ready`, `keyboard-ready-timeout`. Verbose-only duration lines (non-activation shape): `bounds-check …`, `projection-batch retained|applied …`.

## Original Analysis (kept for the record)

## Evidence

Current checkpoints (`HiveMotion/ActivationTiming.cs`) cover `hotkey-received-ui`, `snapshot-ready`, `cell-assignment-complete`, backdrop cache hit/miss, `overlay-shown`, `first-wpf-render`, foreground request/confirm, and `keyboard-ready`. They cannot separate `SetCells` sub-stages, native `Show()` cost, snapshot-batch application, or bounds correction. During the original analysis, per-activation timelines were reconstructed with one-off PowerShell snippets against `%LOCALAPPDATA%\HiveMotion\Logs`; that parsing is not preserved in the repository.

## Scope and Non-Goals

- In scope: additional verbose-gated checkpoints; a log-analysis script under `tools/`; effect comparison of Phases 1–4 from real logs.
- Out of scope: any behavior change to the activation path; new always-on logging; changing the log file format (checkpoints must keep the existing `activation <name> +<ms>ms` line shape so old logs stay parseable).

## Steps

1. **Finer checkpoints** (all verbose-gated, thread IDs already included by `ActivationTiming`):
   - Around `SetCells`/`ApplyCells` (`TaskGridView.xaml.cs:260-303`): entry, cell-pool update complete, search rebuild complete.
   - Around the native `Show()` call in `ShowTaskGrid` (`OverlayWindow.xaml.cs:130-166`): immediately before and after.
   - Snapshot/projection batch application start and complete (`App.xaml.cs`, `ApplyWindowViewProjectionBatch`).
   - Screen-bounds correction decision (`OverlayWindow.xaml.cs:187-196`), including the not-corrected outcome.
   - Checkpoint names must be stable, lowercase-hyphenated, and documented in this file when implemented.
2. **Zero-cost when disabled.** Every new checkpoint and log argument must be behind `Logger.IsVerboseEnabled` guards at the call site so the disabled path performs no formatting or allocation (consistent with the Phase 1 hook fix).
3. **Log-analysis script** `tools/analyze-activation-logs.ps1`:
   - Parse `activation <name> +<ms>ms` records grouped by correlation/activation across a log directory.
   - Emit a per-stage distribution table (count, min, median, p95, max) and a per-activation timeline for the slowest N activations.
   - Accept a date range / file filter so before/after comparisons are one command each.
4. **Effect comparison.** Using real logs from normal usage, compare stage distributions from before and after Phases 1–4; record the result in each phase document's Status section and flag any remaining dominant interval for Phase 6.

## Automated Verification

- `dotnet build HiveMotion.sln -c Release` with zero warnings and zero errors; all existing checks pass.
- Run the script against existing historical logs to prove it parses the current format; optionally keep a small sample log fixture under `tools/` for a self-test mode.

## Manual Validation

- None beyond collecting logs during normal use with verbose logging enabled.

## Acceptance

- New checkpoints add no measurable cost when verbose logging is disabled.
- The script reproduces the stage distributions from the retained historical logs without modification.
- Shared acceptance baseline from [activation-performance.md](activation-performance.md) holds.

## Dependencies

Best executed after Phases 1–4 so the comparison covers them, but adds no code dependency on them; may be implemented earlier if a debugging need arises.

# Phase 5 — Activation Instrumentation and Log Analysis

Status: Planned

## Objective

Provide the fine-grained timing evidence that Phases 1–4 cannot produce on their own, and formalize ad-hoc log parsing into a reusable repository tool. This phase is deliberately placed after the implementation phases: it adds measurement only, changes no behavior, and its output validates the earlier phases and drives [Phase 6](phase-6-foreground-validation.md).

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

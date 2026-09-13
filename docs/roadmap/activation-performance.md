# Activation Performance Roadmap

Date: 2026-09-12

## Background

An analysis of the latency between pressing Win+Tab and the overlay becoming visible identified five issues: occasional multi-second foreground-activation stalls, synchronous icon work inside cell assignment, redundant UI invalidation before `Show()`, an independent delay after `Show()`, and secondary UI-thread contention (log viewer, history persistence, hook logging). A September 7 sample recorded 130.3 ms between `snapshot-ready` and `cell-assignment-complete`; older samples recorded first WPF renders 1.2–6.0 s after hotkey receipt.

The icon work is complete ([Phase 0](phase-0-async-icon-loading.md)). The remaining work is decomposed into the phases below so each can be implemented independently in a separate session.

## Phases

| Phase | Goal | Status |
|---|---|---|
| [0 — Async icon loading](phase-0-async-icon-loading.md) | Assignment never loads icons; missing icons refill progressively | Complete (automated checks; interactive validation in Phase 6) |
| [1 — UI-thread contention](phase-1-ui-thread-contention.md) | Move log-viewer rebuilds, history writes, and hook log formatting off the UI hot path | Complete (viewer removed in `270b2d0`; history + hook fixed; HistoryChecks added) |
| [2 — Opening-path invalidation](phase-2-opening-path-invalidation.md) | `ApplyCells` updates only what changed; search rebuild leaves the opening path | Complete (diff-based cells, reset fast path, deferred search build, OpeningChecks) |
| [3 — Post-Show coalescing](phase-3-post-show-coalescing.md) | Snapshot/projection updates coalesced and deprioritized during opening | Complete (OpeningUpdateGate, Background priority, KeyboardReady signal) |
| [4 — Icon cache leftovers](phase-4-icon-cache-leftovers.md) | Argument-normalization caching; DPI-tiered icon cache | Complete (cached normalization, tier {32,48,96} cache with DPI-aware sizes) |
| [5 — Activation instrumentation](phase-5-activation-instrumentation.md) | Finer checkpoints plus a log-analysis script; measures Phases 1–4 | Planned |
| [6 — Foreground validation](phase-6-foreground-validation.md) | Confirm multi-second activation stalls are gone; holistic interactive sign-off | Planned |

## Ordering Principle

Implementation phases come first and must not gate on measurement or manual validation. Instrumentation and validation are deliberately last: the existing coarse checkpoints (`snapshot-ready` → `cell-assignment-complete` → `overlay-shown` → `first-wpf-render` → `keyboard-ready`) already provide before/after regression comparisons, and Phase 5 adds the fine-grained evidence that Phase 6 needs.

Phases 2 and 3 touch the same opening-path code and must run in that order. Phases 1 and 4 are independent of each other and of 2/3.

## Shared Acceptance Baseline

Every implementation phase must satisfy:

- Release build (`dotnet build HiveMotion.sln -c Release`) with zero warnings and zero errors.
- All existing checks pass: `tests/HiveMotion.IconChecks` and `tests/HiveMotion.HandoffChecks`.
- No persistence-format changes (`motions.json`, `history.json`, `settings.json`).
- Code, comments, and documentation in English.
- No commits and no version bumps unless the user explicitly asks.

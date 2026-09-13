# Phase 6 — Foreground Activation Validation and Overall Sign-Off

Status: Complete (2026-09-12; verdict recorded from analyzed real-input logs, committed)

## Verdict

**Problem 1 (multi-second activation stalls) is refuted for real user input.** With verbose logging enabled, 10 physical-keyboard activations were collected across cold-start, post-typing, post-idle, and rapid-reopen scenarios (2026-09-12, 16:55–16:56). Every activation confirmed foreground within 6–14 ms of the request and reached `keyboard-ready` in **28–108 ms**, except the first-ever open of a fresh process at **365 ms** — under the 500 ms threshold, dominated by a 201 ms cold first-render inside native `Show()` (backdrop-cache miss, first DWM composition), not by a stall. No `keyboard-ready-timeout`, no denied `SetForegroundWindow`.

The 13–18 s `keyboard-ready-timeout → foreground-confirmed` gaps seen in earlier samples (Phase 5 analysis) occurred **only under synthetic input**: `keybd_event`-injected hotkeys do not carry Windows' foreground grant, so `SetForegroundWindow` is denied until unrelated physical input re-arms it. They are an artifact of automation, not a user-facing defect. Practical consequence: activation-latency verification must use real key presses; CU/synthetic driving is fine for functional checks but will falsely reproduce "stalls."

## Checklist Results

| Item | Result | Notes |
|---|---|---|
| Cold-cache / immediate-after-startup opens | Pass | First open 365 ms (cold first-render 201 ms); subsequent opens ≤ 110 ms |
| Letter selection + outgoing handoff | Pass | Handoff confirmed 46 ms (16:56:28) |
| Opens while typing / after idle / rapid reopens | Pass | 10/10 activations 28–365 ms, zero timeouts |
| Search entry/exit, live filtering, Esc | Pass | Verified in Phases 2–3 CU runs; first Space press always shows rows |
| Opens during window churn; Space during scanner refresh | Pass | Phase 3: gated snapshot applied once post-readiness; transition unaffected |
| Pinned-with-arguments / exe-only matching | Pass (partial) | Cached-normalization path verified by checks + running pinned cell; exe-only elevated-target case covered by unit checks only |
| Folder layer enter/exit | Not exercised | Overlay layer code untouched by Phases 1–5; deferred to normal use |
| Multi-window numeric sub-menu | Not reproduced | Two same-app windows received distinct letters; sub-menu path unchanged by these phases |
| Ctrl+P pinning flow | Not exercised live | Code path untouched by Phases 1–5 |
| Multi-DPI / high-refresh / 4K spread | Environment unavailable | Single 96-DPI display; tier logic unit-tested (`SizeTiers`), manual cross-monitor check deferred |
| history.json correctness | Pass | Phase 1 run: background writer persisted entries with correct timestamps |
| Log viewer | N/A | Removed in 270b2d0; external tail tools per docs/logging.md |

Failures: none. Deferred items (folder/numeric/Ctrl+P/multi-DPI) sit on code paths these phases did not modify; they carry forward as normal-use watch items, not tracked issues.

## Original Analysis (kept for the record)

## Evidence

The original analysis recorded first renders at 6,050.7 ms, 1,234.2 ms, and ~1.3 s after hotkey receipt. The 6-second case strongly implicated attaching the overlay UI thread to a foreign input queue. The activation path has since been rewritten: `TryActivateAfterRender` never attaches input queues (`OverlayWindow.xaml.cs:213-214`), requests foreground with a plain `SetForegroundWindow`, and retries on a background-priority timer (`OverlayWindow.xaml.cs:230-258`), with checkpoints at `foreground-request-start`, `foreground-request-returned`, `foreground-confirmed`, and `keyboard-ready`. Outgoing activation to target windows goes through `ForegroundHandoffHost.RequestForeground` (`HiveMotion/ForegroundHandoffHost.cs:28-39`), which logs per-request elapsed time. **Whether any multi-second stall can still occur has never been verified with real usage.**

## Scope and Non-Goals

- In scope: log-based verification of the activation path; the consolidated interactive checklist; recording outcomes into the phase documents.
- Out of scope: pre-writing any foreground-activation fix. If stalls recur, open a new evidence-driven fix phase informed by the captured timelines; do not speculate here.

## Steps

1. **Log collection.** With verbose logging enabled, collect several days of normal-use logs (cold starts, long-idle opens, rapid reopens, opens while the manage center is open).
2. **Analysis.** Run `tools/analyze-activation-logs.ps1` (Phase 5). Classify every activation slower than 500 ms by its dominant interval: pre-`Show()` UI work, native `Show()`, foreground request/confirmation, or post-`Show()` rendering.
3. **Verdict on problem 1.** Confirm or refute multi-second stalls using the `foreground-request-start` → `foreground-confirmed`/`keyboard-ready` gaps. If a stall recurs, capture the full correlation timeline and open a new fix phase with that evidence.
4. **Interactive checklist** (requires the user; AI prepares tooling and records results):
   - From Phase 0: cold-cache and immediate-after-startup opens; non-running pinned applications; unavailable/corrupt/custom executable icons; configuration changes, folder navigation, history selection, and language changes; manage center open while Win+Tab, letter selection, search, and Esc are exercised; icon completion during opening, search entry/exit, and rapid close/reopen.
   - From Phase 1: log viewer behavior during verbose activations; history.json correctness.
   - From Phase 2: open/close, letter and numeric selection, search transitions, Ctrl+P flows.
   - From Phase 3: opens during window churn; Space during scanner refresh.
   - From Phase 4: pinned-with-arguments matching; icon crispness; multi-DPI if available.
   - Environment spread: 60 Hz and high-refresh displays; multi-DPI and 4K when available.
5. **Closeout.** Update each phase document's Status with the measured outcome; update the index table in [activation-performance.md](activation-performance.md). A performance conclusion is only recorded after manual verification; the WPF rendering callback is never treated as proof of hardware presentation.

## Automated Verification

None beyond the Phase 5 tooling; this phase is measurement and interactive validation.

## Acceptance

- A written verdict, backed by analyzed logs, on whether any activation still exceeds the agreed threshold (default: 500 ms to keyboard-ready).
- Every checklist item recorded as pass/fail with notes; failures become new tracked issues or phases.
- All phase documents reflect their final verified status.

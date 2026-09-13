# Phase 4 — Icon Cache Leftovers

Status: Complete (2026-09-12; verified with the release build via Computer Use, committed)

## Outcome

- **Argument-normalization caching.** `ApplicationMotion` recomputes its normalized argument form only when `Arguments` is written (internal `NormalizedArguments`); `RunningWindow` carries a scanner-computed `NormalizedArguments` (lazy fallback for windows built elsewhere); `Matches`/`SameIdentityAs` consume the cached forms. `HistoryEntry` gains `KeyFromNormalized` and `HistoryStore.RecordScan` builds keys from the scanner-cached form, keeping identity construction symmetric across `ProcessIdentity`/`Matches`/`HistoryStore` (normalization function unchanged; idempotent).
- **DPI-tiered icon cache.** Cache keys gain a quantized tier suffix (`{32, 48, 96}`); `TryGetCached`/`Request` take a pixel size resolved at the call site (overlay cells 48 DIP, search rows 24, manage tiles/editors from their rendered widths) × `VisualTreeHelper.GetDpi`, gated on `IsLoaded`; scanner/startup callers use `SystemFallbackPixels` (system DPI via `GetDpiForSystem`). The worker extracts shell icons and decodes images at the tier size; `Invalidate` evicts every tier; glyph cache stays size-independent. `IconBinding` re-requests on `DpiChanged`. Phase 0 guarantees hold: no I/O on the synchronous path, fallbacks immediate, progressive fill.
- New checks: HistoryChecks normalization check (cache invalidation, cross-product parity with uncached matching, exe-only fallback, key symmetry) and IconChecks `SizeTiers`/`TierInvalidation`/`GlyphTiers`. 17 icon + 17 handoff + 5 history + 6 opening checks all pass; Release build is warning-free. On the 96-DPI test machine the overlay uses tier 48 (identical cost to before); cross-monitor crispness on a real multi-DPI setup remains a manual follow-up.

> Phase 6 measurement: worker extraction at the tier size confirmed in verbose logs (size=48 at 96 DPI); icons rendered crisply with no fallback regressions in any verification run.

## Original Analysis (kept for the record)

## Evidence

1. **Argument normalization repeats `Split`/`Join` per comparison.** `ApplicationMotion.NormalizeArguments` (`HiveMotion/Motions/ApplicationMotion.cs:48-51`) normalizes on every call, and `Matches` (`ApplicationMotion.cs:35-45`) calls it for both sides. `CellAssigner` (`HiveMotion/CellAssigner.cs:151`) calls `Matches` for every unplaced window against every configured application on every open, so the same strings are re-split and re-joined repeatedly.
2. **Icon cache ignores display size and DPI.** `IconHelper.LoadFile` extracts executable icons at a fixed 256 pixels, and decoded images are bounded to 96 pixels on the long side, regardless of the actual rendered size (28 px tiles in the manage center, larger cells in the overlay) or the monitor's DPI scale. Cache keys carry no size dimension, so a future second size cannot coexist with the first.

## Scope and Non-Goals

- In scope: normalization caching in `ApplicationMotion` and `RunningWindow`; a size/DPI dimension in the icon cache key; bounded tier selection in the worker.
- Out of scope: filesystem watchers (deliberately excluded; the 30-second throttled revalidation stays); changing matching semantics, ordering, or the persistence format; redesigning `HistoryStore` key construction beyond keeping it symmetric.

## Steps

### 1. Argument-normalization caching

- Cache the normalized form on `ApplicationMotion` (recompute when the `Arguments` setter changes; expose it as an internal property).
- Normalize each `RunningWindow`'s command-line arguments once on the scanner's background thread when the snapshot is built, and carry the normalized form on the snapshot object.
- Make `Matches` consume both pre-normalized forms without allocating.
- **Hard constraint (AGENTS.md):** process-identity, command-line, and working-directory handling must remain symmetric across `ProcessIdentity`, `ApplicationMotion.Matches`, and `HistoryStore`. Any normalization change must be applied identically in all three places (including `HistoryEntry.Key` construction).

### 2. DPI-tiered icon cache

- Extend the resource key in `AsyncResourceCache`/`IconService` with a pixel-size dimension.
- Derive requested size from the actual rendered size times the monitor's DPI scale at the call site (overlay cells, manage-center tiles, editors); keep the set of tiers small (e.g., at most 2–3 per resource) to bound memory.
- The worker picks the closest suitable source size: exe/dll extraction requests the tier's pixel size instead of always 256; image decoding bounds the long side to the tier size, preserving aspect ratio.
- `TryGetCached` selection gains the size dimension; the exact tier for the requesting view must be resolvable synchronously without I/O, preserving the Phase 0 guarantee.
- Glyphs are vector-based and stay size-independent; do not tier glyph cache entries.

## Automated Verification

- Extend `tests/HiveMotion.IconChecks`: normalization cache invalidates when `Arguments` changes; matching results are identical to the uncached implementation across representative argument strings (including exe-only fallback and unreadable-arguments cases); size-tier keys deduplicate per size; the same file at two tiers does not start duplicate loads for the same tier.
- `dotnet build HiveMotion.sln -c Release` with zero warnings and zero errors; all existing checks pass.

## Manual Validation

- Pinned apps with arguments still match their running windows; exe-only matching still works when arguments are unreadable (elevated targets).
- Icons remain crisp on the overlay and in the manage center; if a multi-DPI setup is available, move windows across monitors and reopen.
- Cold-cache open still shows fallbacks immediately, with icons arriving progressively (Phase 0 behavior must not regress).

## Acceptance

- `Assign` performs no repeated `Split`/`Join` normalization work for unchanged inputs.
- Cache entries for different sizes coexist; memory stays bounded by the tier cap.
- Matching symmetry across `ProcessIdentity`/`Matches`/`HistoryStore` is preserved and covered by checks.
- Shared acceptance baseline from [activation-performance.md](activation-performance.md) holds.

## Dependencies

None blocking. Independent of Phases 1–3; must preserve the Phase 0 in-memory-only guarantee for `TryGetCached`.

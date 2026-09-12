# Asynchronous Icons and Cell Assignment

Date: 2026-09-07

Status: Implementation complete with automated verification. Interactive performance validation remains pending.

## Objective and Evidence

Cell assignment determines the position, identity, and action of each cell. Icon availability must not gate overlay presentation or keyboard operation.

The September 7 activation log recorded approximately **130.3 ms between snapshot-ready and cell-assignment-complete**, with six running windows. This is an interval for all assignment work, not an isolated icon measurement. No measured end-to-end latency improvement is claimed by this change. Foreground activation and WPF/DWM presentation latency remain separate concerns.

Before this change, `CellAssigner` called `IconHelper.ForMotion`. A cache miss could extract executable resources through Shell COM, decode a custom image, or build glyph geometry on the UI thread. Even a custom-image cache hit first called `File.Exists` and `File.GetLastWriteTime`. Startup prewarming covered only some glyphs and custom images, leaving default executable icons and early activations exposed.

## Implemented Architecture

### Assignment and Immutable Requests

`IconRequest` copies the custom image path, default executable path, and built-in glyph identity from configuration. Constructing the request does not inspect files or processes. `CellAssigner` creates these requests while matching existing window snapshots and reserving letters; it no longer invokes icon loaders.

`HiveCell.Icon` remains an optional, already-prepared runtime image. Icon arrival never mutates a cell model, window snapshot, or background Window View projection. Each view resolves the image for its current content independently.

Existing placement, executable/argument matching, ordering, and overlay running-state behavior remain unchanged. Argument normalization and assignment allocations were not optimized in this change.

### Shared Resource Service

`IconService` provides:

- `TryGetCached`: in-memory selection only, with custom image, runtime image, executable image, and built-in glyph precedence.
- `Request`: nonblocking submission, with visible content prioritized over pending prewarming.
- `Invalidate`: configuration-driven version invalidation.
- `Changed`: resource-key notification after a usable result is published.

`AsyncResourceCache` deduplicates case-insensitive resource keys and runs at most two loading workers. A resource undergoing invalidation cannot start another overlapping extraction. Its obsolete result is discarded, and the latest requested version is processed afterward. Locks protect cache state only; no file access or extraction runs under a cache lock.

Path keys are normalized syntactically. Workers inspect modification time and length, and reuse unchanged images. Custom exe/dll icons and default executable icons use the same cache, eliminating the old nested-cache invalidation mismatch.

A failed load keeps the previous usable image, if any. Missing resources use the view's fallback. Ordinary validation and retries are throttled for 30 seconds from completion; new view requests initiate eligible checks, and configuration changes invalidate immediately. There is no filesystem watcher or continuous polling timer.

Home icons are prewarmed before folder children. Prewarming is best-effort and is not awaited before enabling the hotkey. All built-in glyphs are prepared and frozen on the UI thread before consumers start; resource workers never construct glyphs.

### Worker-Only Extraction

`IconHelper.LoadFile` is the synchronous worker backend and rejects execution on the application's UI dispatcher. It performs:

1. File-type selection.
2. For exe/dll resources, Shell image extraction and associated-icon fallback, with existing GDI/COM cleanup.
3. For supported image files, dimension inspection and decoding with the longer dimension bounded to 96 pixels while preserving aspect ratio.
4. Frozen-image publication after the resource version is checked again.

The executable extraction size remains 256 pixels. DPI-dependent cache sizes were not introduced. In-flight native calls are not forcibly terminated; shutdown stops accepting work and does not wait on the UI thread. If both workers are blocked by external resources, remaining icons keep their fallbacks until workers become available.

The existing snapshot scanner continues to enumerate windows and obtain native window-icon fallbacks on its background thread. Executable requests now use the shared asynchronous cache, and process-lifetime metadata no longer retains a separate executable image. Management no longer owns or calls a scanner.

Microsoft documents that Shell icon extraction can be time-consuming and should not execute on the UI thread: [IShellItemImageFactory.GetImage](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage).

### Progressive Overlay Presentation

The overlay initially uses cached or runtime images and existing fallback content. It never awaits icon tasks. Resource notifications are coalesced into background-priority dispatcher batches.

Updates become eligible only after the current `Show()` has returned and its first WPF rendering callback has occurred, using the existing post-render activation path. The rendering callback is not a guarantee of hardware presentation.

The grid refreshes only the image and fallback elements of current cell controls and search rows. It does not rerun assignment, call the full `SetCells` path, rebuild search controls, or change the query, highlight, scroll position, or focus. No completion callback carries a historical cell or letter target: callbacks resolve the latest displayed content and immutable request against the versioned cache.

Closing the overlay disables updates and unsubscribes. A later open starts a new presentation. During search entry/exit, updates are deferred; at transition completion, pending cells are applied first and current icon content is then refreshed.

### Management Center

All management image consumers use asynchronous bindings: application/folder/system-action/Window View tiles, editors, and application history. Image bindings store the current immutable request, unsubscribe on unload, and reread only current content when a queued callback executes. Rebuilding a list or changing the selected editor cannot let an obsolete result overwrite the current icon.

The following functionality was removed:

- Application running-state dots on home and folder tiles.
- Running/not-running text in the application editor.
- Running markers in application history.
- The initial synchronous scan, five-second status timer, window cache, and scanner constructor dependency.
- Three unused running-status localization entries in both resource files.

The editor still shows the unconfigured hint and collapses that hint for configured applications. Folder counts, type badges, file-missing hints, launch history, configuration validation, editing, and dragging remain. File-existence checks for history/configuration validation are separate from icon loading and were not redesigned here.

## Automated Verification

Commands:

```powershell
dotnet build HiveMotion.sln -c Release
dotnet run --project tests/HiveMotion.IconChecks -c Release
dotnet run --project tests/HiveMotion.HandoffChecks -c Release
```

The icon checks use controlled loaders, hidden WPF control instances, dispatcher processing, and temporary image files. They do not show interactive windows or change user configuration.

Verified scenarios:

- Assignment and initial visuals complete while extraction remains blocked on a worker.
- Cache reads do not start loaders; duplicate requests share work and concurrency stays bounded.
- Visible requests precede queued prewarming.
- Invalidation suppresses obsolete results without overlapping a resource load.
- Initially missing resources retry after backoff; transient failures retain a usable image and recover.
- Disposal does not wait for a blocked load or publish to disposed consumers.
- Presentation requires both Show completion and rendering for its current lifetime.
- Reopening with a different cell at the same letter rejects old content.
- Search transitions defer updates while preserving the query and result controls.
- File replacement reloads content, and portrait/landscape dimensions remain bounded.
- Custom and default executable requests share loading, case-insensitive identity, and invalidation.
- Runtime images remain usable while custom images load.
- Editor selection changes and unloaded controls reject stale completions; reloading consumes cached results.

Results: **14 icon checks passed; 17 handoff checks passed.** Release build: **zero warnings and zero errors**.

## Diagnostics and Remaining Manual Validation

Activation checkpoints now include managed thread IDs. Verbose icon diagnostics record cache hit/miss totals at shutdown, worker metadata and extraction/decoding duration, Shell/fallback duration, startup glyph generation, and UI icon-update batches. No synchronous file logging was added to assignment.

Interactive validation is still required before considering the performance work fully verified:

- Cold-cache and immediate-after-startup Win+Tab activation.
- Non-running pinned applications and unavailable/corrupt/custom executable icons.
- Configuration changes, folder navigation, history selection, and language changes.
- Management remaining open while Win+Tab, letter selection, search, and Esc are exercised.
- Icon completion during opening, search entry/exit, and rapid close/reopen.
- 60 Hz and high-refresh displays, plus multi-DPI and 4K configurations when available.

Record cold/warm assignment distributions, maximum frame intervals, first visibility, and keyboard readiness. Do not infer that the original 130.3 ms was entirely icon work or that the measured end-to-end delay has been eliminated solely from passing automated checks.

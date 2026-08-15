# General Agent Guidelines

## ⚠️ HIGH PRIORITY RULES

- **Do not commit unless the user explicitly asks.** Even when the user explicitly requests a commit, that request applies only to the current conversation turn. Never treat commit as a default action after making changes.
- **All code and documentation must be written in English.** This includes code comments, commit messages, doc files, and any text the agent produces.
- **Never leave "Merge branch" commits in main** Follow Branch Merging Guidelines, use **Rebase + Fast-Forward** approach whenever not requesting a squash merge.

## Versioning

This project uses a mix of semver and C# Assembly Version Number. There are 3 or 4 components in the version string:
- **major**: first component (e.g., `X.y.z.w` )
- **minor**: second component (e.g., `x.Y.z.w` )
- **patch**: third component (e.g., `x.y.Z.w` )
- **build**: fourth component (e.g., `x.y.Z.w` )

### Version Editing

Files to touch when modifying version:
- `HiveMotion/HiveMotion.csproj` (`Version`, `AssemblyVersion`, and `FileVersion`)
- `HiveMotion/app.manifest` (`assemblyIdentity` version attribute)
- `Installer/HiveMotion.Bootstrapper/Bundle.wxs` (`Bundle` `Version` attribute)
- `Installer/HiveMotion.Setup/Package.wxs` (`Package` `Version` attribute)
- `Installer/HiveMotion.Setup/HiveMotion.Setup.wixproj` (`ProductVersion`)

Keep version in different files in sync.

### Version advancing during daily development
When the user explictly asks a version bump, increase the build version by 1. If the build version doesn't exist at the moment of this operation, append one and start from version 1.
- Format in the package version: `x.y.z-build-(W+1)`.
  - The package version is the `Version` field in `HiveMotion/HiveMotion.csproj`.
- Format in the assembly version: `x.y.z.(W+1)`.

### Release workflow

When the user requests a release, reset the build version to 0 and bump the version based on which version component the user requested. If the user did not mention the component, bump the **patch** version.
For package version, just remove the build version suffix.

Steps publish a new release:
  1. Stage the changed version files.
  2. Ask the user to review before committing.
  3. After the user approves, commit it, push to `origin`, create a tag for this new version (e.g., `vX.Y.Z`), and push the tag to `origin` to trigger the release workflow.
  4. The user must ask explicitly to start this process, don't run this release flow in regular commit requests.

## Branch Merging
When merging changes from an agent-created branch or worktree back into `main`, adhere to these workflows:
  - By default, use **Rebase + Fast-Forward** merging to maintain a clean, linear commit history on `main` without creating merge commits. First, rebase the branch onto `main` (`git rebase main`), then checkout `main` and fast-forward merge the branch (`git merge <branch>`).
  - If the user explicitly requests a squash, use **Squash Merge** (`git merge --squash <branch>`). In this case, the agent must compose a clean, descriptive summary of the changes to be used as the commit message.

## Using Worktrees

When creating Git worktrees, create them under the `<repo>/worktrees/` directory. This keeps all worktrees inside the repository root and makes them easy to find and clean up. The `worktrees/` folder is ignored by `.gitignore` and must never be committed.

## IssueHistory

This is a folder to keep critical design changes or difficult issues that are easy to break again. They are there to remind developers and agents to be careful of some unobvious details.
When the user asks, the agent may summarize the key details while implementing the last request, and create a document in the `IssueHistory` folder, the file name pattern is `{YYMMDD}-{brief-title}.md`.

## High-Performance UI and Animation

HiveMotion is a latency-sensitive desktop UI. Treat every keyboard-triggered transition as a real-time rendering path, even when it contains only a few controls. WPF cost is often dominated by layout, cache invalidation, text/effect rasterization, dispatcher contention, and allocations rather than the number of visible objects.

### Performance targets

- Design for 144 Hz on a normal hardware-rendered desktop. A 144 Hz frame has a 6.94 ms total budget; leave headroom for DWM composition and other applications.
- The input handler that starts a transition must do only bounded state changes and animation starts. Window enumeration, icon extraction, backdrop capture, filtering, visual-tree construction, logging, and other variable-duration work must happen before or after the transition.
- Optimize frame-time consistency, not only average FPS. One 30-50 ms frame is visible even when the remaining frames are fast.
- Do not assume a small visual tree is cheap. Measure invalidation and render cost before deciding that a path is lightweight.

### WPF rendering rules

- Animate `RenderTransform` and `Opacity`. Do not animate width, height, margin, padding, canvas position, visibility, or other properties that trigger measure or arrange.
- Cache the complete expensive subtree with `BitmapCache` when it moves as one unit. Put the animated transform on the cached element or its parent so text, gradients, geometry, icons, blurs, and shadows are not rasterized every frame.
- Size `BitmapCache.RenderAtScale` for the largest temporary scale used by the animation. Avoid excessive values that waste GPU memory.
- Do not change content inside a moving cached subtree. Queue and coalesce model or snapshot updates until the transition reaches a stable state.
- Avoid animated `DropShadowEffect`, blur, opacity masks, clipping geometry, and nested effects. If an effect is required, rasterize it before motion and animate the resulting cached surface.
- Reuse and freeze WPF `Freezable` objects such as brushes, geometries, key splines, and reusable animation templates when possible. Avoid per-frame allocation and minimize per-transition allocation.
- Keep hardware rendering enabled. If `RenderCapability.Tier` shows a non-hardware tier, prefer a deliberate reduced-effects mode rather than allowing expensive software-rendered effects to stutter.

### Transition architecture

- Pre-create fixed visual pools for the 26 hive cells and bounded search-result rows. Update model content on existing controls; never clear and recreate the tree in a keyboard transition.
- Keep frequently opened panels measured and arranged while hidden with `Opacity="0"` and disabled hit testing when memory cost is acceptable. Avoid changing from `Collapsed` to `Visible` on the first transition frame.
- Model multi-stage animations with explicit states such as `Overview`, `Entering`, `Search`, and `Exiting`.
- While entering or exiting, retain only the newest background refresh and apply it after the animation completes. Bind callbacks, timers, and deferred work to a transition generation so stale work cannot modify a newer state.
- Do not post a dispatcher callback that collapses or rebuilds a visual before its animation actually finishes.
- When focus changes are required, allow the first visual frame to render before focusing an input control. Preserve keyboard routing during the delay.
- Prewarm caches and visual pools before the user can trigger the transition. Prewarming must not activate, foreground, or visibly flash the overlay window.

### Data and background work

- Publish immutable window snapshots and consume an already prepared snapshot on the UI thread.
- Run window scanning, process identity resolution, icon loading, history I/O, and filtering away from the animation path. Marshal only the final bounded update to the UI dispatcher.
- Coalesce bursty scanner notifications. The UI must not rebuild cells multiple times for intermediate snapshots that the user will never see.
- Cache decoded icons at the required display size. Do not decode or resize image sources during motion.

### Measurement and verification

- Instrument from input receipt through dispatcher entry, animation start, first `CompositionTarget.Rendering`, transition completion, and queued-update application.
- Record maximum frame time and dropped/late frames in addition to average FPS. Correlate with WPF/ETW or Windows Performance Analyzer traces when a regression is not obvious from code.
- Validate Release builds at 60 Hz and high refresh rates, on multi-DPI and 4K displays, and with enough windows to fill all cells and search rows.
- Confirm that pressing Space during a scanner refresh does not cancel, restart, or visibly alter the transition.
- A performance change is incomplete until the project builds cleanly and the affected transition is manually exercised. Use Computer Use for that manual verification only when the user explicitly requests it.

## Computer Use

You may have access to a Computer Use facility in your development environment.
Don't use it by default, only use it when a user asks. Such as:
* Please implement the plan and verify it using Computer Use.
* Please verify the feature after implementation using Computer Use in the coming tasks of this session.

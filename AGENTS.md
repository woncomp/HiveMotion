# General Agent Guidelines

## ⚠️ HIGH PRIORITY RULES

- **Do not commit unless the user explicitly asks.** Even when the user explicitly requests a commit, that request applies only to the current conversation turn. Never treat commit as a default action after making changes.
- **All code and documentation must be written in English.** This includes code comments, commit messages, doc files, and any text the agent produces.
- **Never leave "Merge branch" commits in main.** Follow Branch Merging Guidelines and use **Rebase + Fast-Forward** whenever the user does not explicitly request a squash merge.
- **Do not assume a small visual tree is cheap.** HiveMotion is a latency-sensitive WPF overlay; every keyboard-triggered transition is a real-time rendering path. Review the High-Performance UI and Animation section before touching any animation, layout, or input path.

## Project Overview

HiveMotion is a Windows desktop modal window manager / app launcher. It intercepts a configured global hotkey (default `Win+Tab`) and shows a centered WPF overlay with a hexagonal keyboard grid instead of the native Windows Task View. Pressing a letter key jumps to or launches the matching app. When multiple windows of the same app exist, the overlay switches to a numbered sub-menu.

Core capabilities:
- Global low-level keyboard hook (`GlobalKeyboardHook`) with configurable hotkey rules (`HotkeyRule`).
- Background window enumeration (`WindowScanner`) and snapshot publishing (`WindowSnapshotService`) driven by `WinEvent` hooks and debouncing.
- Hexagonal keyboard grid (`KeyGrid`, `TaskGridView`, `HiveCellView`) with DWM live thumbnails (`DwmThumbnailPreview`).
- Cell **motions** (`Motion`, `ApplicationMotion`, `FolderMotion`, `SystemActionMotion`, `MotionStore`) that reserve a letter for a launch identity (executable + arguments), a folder of items, or a built-in system action (shell object / protocol URI / `LockWorkStation` — never simulated keys), surviving reboots in `%AppData%\HiveMotion\motions.json` (legacy `pins.json` is migrated once).
- Launch history (`HistoryEntry`, `HistoryStore`) used to pick identities for empty cells in the manage center, persisted in `%AppData%\HiveMotion\history.json`.
- User settings (`AppSettings`, `SettingsStore`) in `%AppData%\HiveMotion\settings.json`.
- System tray icon (`TrayIconManager`) and startup registration (`AutoStartManager`).
- Manage center (`ManageWindow` in `ManageCenter/`) for editing pins, priorities, hotkeys, language, diagnostics, and backup/restore.
- Bilingual UI (`Localization/LocalizationManager`, `LocExtension`, `Strings.resx`, `Strings.en.resx`); default is Chinese (`zh-CN`), with English fallback and system-language auto-detection.
- Async bounded logger (`Logger`) writing to `%LOCALAPPDATA%\HiveMotion\Logs`.

## Technology Stack

- **Runtime target:** .NET 8 (`net8.0-windows`), built with the .NET 8/9 SDK.
- **UI framework:** WPF (`UseWPF=true`) with Windows Forms used only for the system tray icon (`NotifyIcon`).
- **Project language:** C# with `Nullable` enabled and `ImplicitUsings` enabled. WinForms global usings are explicitly removed in `HiveMotion.csproj` to avoid namespace clashes with WPF.
- **Interop:** Heavy use of P/Invoke (`user32.dll`, `kernel32.dll`, `dwmapi.dll`, `ntdll.dll`, `shell32.dll`, `gdi32.dll`).
- **Installer (local):** WiX Toolset v5 (`Installer/HiveMotion.Setup`, `Installer/HiveMotion.Bootstrapper`), framework-dependent publish, bootstrapper downloads the .NET 8 Desktop Runtime if missing.
- **Installer (CI / release):** Inno Setup (`Installer/HiveMotion.iss`), self-contained publish. The CI workflow publishes a single-file self-contained executable and packages it with Inno Setup.
- **CI/CD:** GitHub Actions (`.github/workflows/build-and-release.yml`).

## Solution Layout

The repository is organized into areas: root solution files, the main WPF project, the installer projects, and documentation.

- **Root** — `HiveMotion.sln` (main app) and `HiveMotion.Installer.sln` (WiX installer).
- **`HiveMotion/`** — Main WPF application.
  - **Root files** — application entry point (`App.xaml`), overlay window and hex-grid UI, background window-scanning services, motion/history/settings stores, P/Invoke helpers, and logging.
  - **`Localization/`** — resource manager, XAML markup extension, and bilingual string resources (`Strings.resx`, `Strings.en.resx`).
  - **`ManageCenter/`** — management center UI for editing pins, priorities, hotkeys, language, diagnostics, and backup/restore.
  - **`Motions/`** — motion kind implementations and their catalog/trigger logic (`ApplicationMotion`, `FolderMotion`, `SystemActionMotion`).
  - **`Properties/`** — assembly info and publish profiles (`FrameworkDependent.pubxml`).
  - **`Resources/`** — application icon and embedded assets.
- **`Installer/`** — WiX v5 MSI and bootstrapper projects, plus the Inno Setup script used by CI.
- **`docs/`** — Feature documentation, roadmap notes, and end-user guidance.

## Build and Test Commands

The application can be built and published without any test suite. There are currently no unit or integration test projects; validation is manual.

Build the main application:
```powershell
dotnet restore HiveMotion.sln
dotnet build HiveMotion.sln -c Release
```

Publish for the CI / Inno Setup path (self-contained, single-file, `win-x64`):
```powershell
dotnet publish HiveMotion/HiveMotion.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output installer/publish `
  /p:PublishSingleFile=true `
  /p:DebugType=None `
  /p:DebugSymbols=false `
  /p:Version=0.1.2
```

Build the local WiX installer (requires WiX Toolset v5 and the .NET SDK):
```powershell
.\Installer\build-installer.ps1
```
This script uses the framework-dependent publish profile (`HiveMotion/Properties/PublishProfiles/FrameworkDependent.pubxml`), harvests the published files, builds the WiX MSI and bootstrapper, and copies outputs to `Installer/artifacts/`.

Build the Inno Setup installer locally (requires Inno Setup 6):
```powershell
$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
& $iscc "/DMyAppVersion=0.1.2" 'installer\HiveMotion.iss'
```

Outputs:
- CI: `installer/dist/HiveMotion-Setup-{version}.exe`
- WiX: `Installer/artifacts/HiveMotion-Setup.exe` and `Installer/artifacts/HiveMotion-Setup.msi`

## Versioning

This project uses a mix of semver and C# Assembly Version Number. The version string has up to four components:
- **major**: first component
- **minor**: second component
- **patch**: third component
- **build**: fourth component (assembly only)

### Version Editing

Files to touch when modifying version (keep them in sync):
- `HiveMotion/HiveMotion.csproj` (`Version`, `AssemblyVersion`, and `FileVersion`)
- `HiveMotion/app.manifest` (`assemblyIdentity` version attribute)
- `Installer/HiveMotion.Bootstrapper/Bundle.wxs` (`Bundle` `Version` attribute)
- `Installer/HiveMotion.Setup/Package.wxs` (`Package` `Version` attribute)
- `Installer/HiveMotion.Setup/HiveMotion.Setup.wixproj` (`ProductVersion`)

For CI/Inno builds, the version is passed as `/DMyAppVersion={version}` to `ISCC.exe`; the Inno script itself defaults to `0.0.0` if the define is absent. Do not change the `UpgradeCode` GUIDs in the WiX files unless you are creating a new installer product family.

### Version Advancing During Daily Development

When the user explicitly asks for a version bump, increase the **build** version by 1. If the build version does not exist at the moment, append one and start from version 1.
- Format in the package version (`Version` in `HiveMotion.csproj`): `x.y.z-build-(W+1)`.
- Format in the assembly version (`AssemblyVersion`): `x.y.z.(W+1)`.

### Release Workflow

When the user requests a release, reset the build version to 0 and bump the requested version component. If the user did not mention a component, bump the **patch** version. For the package version, remove the build suffix entirely.

Release steps:
1. Update the version files above and stage them.
2. Ask the user to review before committing.
3. After the user approves, commit, push to `origin`, create a tag `vX.Y.Z`, and push the tag to `origin` to trigger the release workflow.
4. The user must ask explicitly to start this process; do not run the release flow on ordinary commit requests.

## Branch Merging

When merging an agent-created branch or worktree back into `main`:
- **Default:** Rebase the branch onto `main`, then checkout `main` and fast-forward merge (`git merge <branch>`). This keeps a linear history without merge commits.
- **If the user explicitly asks for a squash merge:** Use `git merge --squash <branch>` and compose a clean, descriptive commit message summarizing the changes.

## Using Worktrees

Create Git worktrees under the `<repo>/worktrees/` directory. This folder is ignored by `.gitignore` and must never be committed. It keeps worktrees inside the repository root for easy cleanup.

## IssueHistory

Critical design changes or issues that are easy to regress should be recorded in the `IssueHistory/` folder. When the user asks, summarize the key details while implementing the request and create a document named `{YYMMDD}-{brief-title}.md`.

## Code Style Guidelines

- All comments, docstrings, commit messages, and documentation must be in English.
- Follow the existing C# naming conventions and file organization.
- Keep `ImplicitUsings` enabled; do not add unnecessary global using overrides unless you are resolving a namespace clash with WPF/WinForms (see `HiveMotion.csproj`).
- Keep `Nullable` enabled. Avoid suppressing nullability without a clear reason.
- WPF XAML uses a dark honey-gold accent palette (`#F5B301`, `#FFD97A`, `#10131A`, etc.); match the existing styles when adding new UI.
- Localization strings are added to both `Strings.resx` (Chinese default) and `Strings.en.resx` (English). Use the `l:Loc` markup extension in XAML and `Loc.Get` / `Loc.Format` in code-behind.
- Process-identity, command-line, and working-directory extraction must remain symmetric between `ProcessIdentity`, `ApplicationMotion.Matches`, and `HistoryStore`.

## Testing Instructions

There is no automated test project. Verify changes manually:
- Build the solution in Release (`dotnet build HiveMotion.sln -c Release`) with zero warnings and errors.
- Run `HiveMotion.exe` and test the overlay open/close flow (`Win+Tab` by default).
- Test letter selection, multi-window numeric selection, search (`Space`), and `Esc` cancel.
- Test pinning (`Ctrl+P`), unpinning, and moving pins via the overlay and manage center.
- Test tray icon menu entries (open, manage, log, auto-start, exit).
- Test language switching between system/Chinese/English.
- For any animation or input-path change, exercise the transition at both 60 Hz and high refresh rates and on multi-DPI displays if possible. Use the live log viewer with verbose logging enabled to inspect activation timing checkpoints.
- Do not mark performance-related work complete until the affected transition builds cleanly and has been manually exercised.

## Security Considerations

- The application installs a **global low-level keyboard hook** (`WH_KEYBOARD_LL`) and swallows configured combos while the overlay is hidden.
- It reads memory from other processes via `ReadProcessMemory` on the PEB to obtain command-line arguments and working directories. This can fail or return partial data for elevated/protected processes; the code degrades gracefully to exe-only identity matching.
- It modifies the current-user registry run key (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) for auto-start.
- It does **not** require elevation by default. Running as a standard user limits which processes can be inspected.
- The installer is per-machine and installs to `Program Files`; it does not request elevation inside the app itself.
- Be cautious when changing P/Invoke signatures, thread-attach logic, or foreground-window activation (`WindowManager.ActivateWindow`) — these are security-sensitive and easy to break for elevated or foreground-protected targets.

## High-Performance UI and Animation

HiveMotion is a latency-sensitive desktop UI. Treat every keyboard-triggered transition as a real-time rendering path, even when it contains only a few controls. WPF cost is often dominated by layout, cache invalidation, text/effect rasterization, dispatcher contention, and allocations rather than the number of visible objects.

### Performance Targets

- Design for 144 Hz on normal hardware-rendered desktops. A 144 Hz frame has a ~6.94 ms total budget; leave headroom for DWM composition and other applications.
- The input handler that starts a transition must do only bounded state changes and animation starts. Window enumeration, icon extraction, backdrop capture, filtering, visual-tree construction, logging, and other variable-duration work must happen before or after the transition.
- Optimize frame-time consistency, not only average FPS. One 30–50 ms frame is visible even when the remaining frames are fast.
- Do not assume a small visual tree is cheap. Measure invalidation and render cost before deciding that a path is lightweight.

### WPF Rendering Rules

- Animate `RenderTransform` and `Opacity`. Do not animate `Width`, `Height`, `Margin`, `Padding`, canvas position, `Visibility`, or other properties that trigger measure or arrange.
- Cache the complete expensive subtree with `BitmapCache` when it moves as one unit. Put the animated transform on the cached element or its parent so text, gradients, geometry, icons, blurs, and shadows are not rasterized every frame.
- Size `BitmapCache.RenderAtScale` for the largest temporary scale used by the animation. Avoid excessive values that waste GPU memory.
- Do not change content inside a moving cached subtree. Queue and coalesce model or snapshot updates until the transition reaches a stable state.
- Avoid animated `DropShadowEffect`, blur, opacity masks, clipping geometry, and nested effects. If an effect is required, rasterize it before motion and animate the resulting cached surface.
- Reuse and freeze WPF `Freezable` objects such as brushes, geometries, key splines, and reusable animation templates when possible. Avoid per-frame allocation and minimize per-transition allocation.
- Keep hardware rendering enabled. If `RenderCapability.Tier` shows a non-hardware tier, prefer a deliberate reduced-effects mode rather than allowing expensive software-rendered effects to stutter.

### Transition Architecture

- Pre-create fixed visual pools for the 26 hive cells and bounded search-result rows. Update model content on existing controls; never clear and recreate the tree in a keyboard transition.
- Keep frequently opened panels measured and arranged while hidden with `Opacity="0"` and disabled hit testing when memory cost is acceptable. Avoid changing from `Collapsed` to `Visible` on the first transition frame.
- Model multi-stage animations with explicit states such as `Overview`, `Entering`, `Search`, and `Exiting`.
- While entering or exiting, retain only the newest background refresh and apply it after the animation completes. Bind callbacks, timers, and deferred work to a transition generation so stale work cannot modify a newer state.
- Do not post a dispatcher callback that collapses or rebuilds a visual before its animation actually finishes.
- When focus changes are required, allow the first visual frame to render before focusing an input control. Preserve keyboard routing during the delay.
- Prewarm caches and visual pools before the user can trigger the transition. Prewarming must not activate, foreground, or visibly flash the overlay window.

### Data and Background Work

- Publish immutable window snapshots and consume an already prepared snapshot on the UI thread.
- Run window scanning, process identity resolution, icon loading, history I/O, and filtering away from the animation path. Marshal only the final bounded update to the UI dispatcher.
- Coalesce bursty scanner notifications. The UI must not rebuild cells multiple times for intermediate snapshots that the user will never see.
- Cache decoded icons at the required display size. Do not decode or resize image sources during motion.

### Measurement and Verification

- Instrument from input receipt through dispatcher entry, animation start, first `CompositionTarget.Rendering`, transition completion, and queued-update application.
- Record maximum frame time and dropped/late frames in addition to average FPS. Correlate with WPF/ETW or Windows Performance Analyzer traces when a regression is not obvious from code.
- Validate Release builds at 60 Hz and high refresh rates, on multi-DPI and 4K displays, and with enough windows to fill all cells and search rows.
- Confirm that pressing `Space` during a scanner refresh does not cancel, restart, or visibly alter the transition.
- A performance change is incomplete until the project builds cleanly and the affected transition is manually exercised.

## Computer Use

You may have access to a Computer Use facility in the development environment. Do not use it by default. Use it only when the user explicitly asks, for example:
- "Please implement the plan and verify it using Computer Use."
- "Please verify the feature after implementation using Computer Use in the coming tasks of this session."

## CI/CD Notes

`.github/workflows/build-and-release.yml` runs on every push and on pull requests to `main`:
- Determines the version (`vX.Y.Z` from a tag, otherwise `0.0.0-ci.{run_number}`).
- Restores and publishes the app as a self-contained, single-file `win-x64` binary to `installer/publish`.
- Installs Inno Setup via Chocolatey and builds `Installer/HiveMotion.iss` with the version define.
- Uploads the installer as a GitHub artifact.
- For tags starting with `v`, the release job downloads the artifact and creates a GitHub Release.

Note: The local installer path uses WiX v5, while the CI/release path uses Inno Setup. Both are valid but not interchangeable; update the appropriate files when changing installer behavior, runtime bundling, or output naming.

## Useful References

- `docs/pinned-cells-ui.md` — detailed behavior of the pin-management UI, the `Ctrl+P` overlay flow, matching rules, and data model.
- `docs/roadmap/draft.md` — brief backlog notes.
- `README.md` — end-user feature overview and usage scenarios.
- `Installer/README.md` — local WiX installer instructions and offline-runtime bundling steps.
- Runtime data locations:
  - `%AppData%\HiveMotion\motions.json`
  - `%AppData%\HiveMotion\pins.json` (legacy; migrated into motions.json on first run)
  - `%AppData%\HiveMotion\history.json`
  - `%AppData%\HiveMotion\settings.json`
  - `%LOCALAPPDATA%\HiveMotion\Logs\hivemotion-YYYY-MM-DD.log`

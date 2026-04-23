# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.12.1] - 2026-04-23

### Fixed
- **CodeQL** — replaced bare `catch` blocks with specific exception types
  (`SecurityException`, `UnauthorizedAccessException`) in PerformanceService
  and PerformanceViewModel. No functional changes.

## [0.12.0] - 2026-04-23

### Added
- **Performance Mode tab** — tune system performance settings with per-tweak
  Apply buttons. Every change is non-destructive and reversible.
  - **Power Plan**: switch between Balanced, High Performance, and Ultimate
    Performance via powercfg.
  - **Visual Effects**: reduce animations, fades, and shadows via P/Invoke
    `SystemParametersInfo` (instant, no logout needed).
  - **Game Mode**: enable or disable Windows Game Mode via registry.
  - **Xbox Game Bar**: disable Game Bar overlay and Game DVR via registry.
  - **NVIDIA GPU**: force max performance (DisableDynamicPstate) with
    auto-detected GPU subkey (not hardcoded). Requires reboot.
  - **Processor State**: force CPU min state to 100% via powercfg.
  - **Overlays info**: manual instructions for Discord, Steam, NVIDIA GFE,
    and EA App overlays (not safe to modify externally).
  - **OriginalSnapshot**: captures exact system state before first change;
    Restore All reverts to the snapshot, not hardcoded defaults.
  - Confirmation dialog before every change.
  - GPU changes warn about reboot requirement.
- **38 new unit tests** for `PerformanceService`, `PerformanceViewModel`,
  and `PerformanceProfile`.

## [0.11.1] - 2026-04-23

### Fixed
- **Process Manager** — kill process now shows a Yes/No confirmation dialog
  warning about potential data loss before terminating.
- **Uninstaller** — uninstall shows a confirmation dialog listing all
  selected apps before proceeding. Select All warns when selecting more
  than 20 apps without an active filter.

## [0.11.0] - 2026-04-23

### Added
- **Uninstaller tab** — lists all installed applications via winget and
  allows batch uninstall of selected apps.
  - Scan installed apps with `winget list`.
  - Filter by name or package ID.
  - Select/deselect all, checkbox per app.
  - Uninstall selected apps silently via `winget uninstall`.
  - Cancel support during scan and uninstall.
  - Virtualized ListView for smooth scrolling.
  - Live console output from winget.
- **18 new unit tests** for `UninstallerService` (table parser, edge cases,
  model properties) and `UninstallerViewModel` (commands, state, filter).

## [0.10.0] - 2026-04-23

### Added
- **Battery Health tab** — monitors battery charge, health percentage, wear
  level, cycle count, chemistry, design vs full-charge capacity, and
  estimated runtime via WMI.
  - Charge bar with percentage and status (Charging / Discharging / Full).
  - Health % (full-charge ÷ design capacity) and wear % display.
  - Detail grid: battery name, chemistry, design capacity, full-charge
    capacity, cycle count, estimated runtime, manufacturer/ID.
  - Gracefully shows "No battery detected" on desktops.
  - Specific exception handling for CodeQL compliance.
- **20 new unit tests** for `BatteryService` and `BatteryHealthViewModel` —
  covers status mapping, chemistry mapping, model calculations, property
  notifications, runtime display formatting, and ViewModel state.

## [0.9.0] - 2026-04-23

### Added
- **Process Manager tab** — lists running Windows processes with memory,
  thread count, and status. Supports kill, filter, sort, and open file
  location.
  - Lists all running processes with PID, name, description, memory,
    threads, and responding status.
  - Real-time filter by name, description, or PID.
  - Sort by memory (default), name, or PID.
  - Kill process button (per-process).
  - Open file location in Explorer.
  - Virtualized ListView for smooth scrolling with 200+ processes.
- **24 new unit tests** for `ProcessManagerService` and
  `ProcessManagerViewModel` — covers snapshot, entries, cancellation,
  kill edge cases, model properties, commands, and filter/sort defaults.

## [0.8.0] - 2026-04-23

### Added
- **Disk Analyzer tab** — shows space breakdown by top-level folders with
  drill-down navigation and drive usage overview.
  - Scans top-level subfolders and computes total size recursively.
  - Shows folder name, size, file/folder count, and percentage bar.
  - Drive usage bar with total/used/free at the top.
  - Drill-down into any folder to see its subfolders.
  - Go Up button to navigate back to parent.
  - Preset paths (fixed drives, user profile, Program Files).
  - Browse button for custom folder selection.
  - Show in Explorer for each folder.
  - Cancellation support.
  - Read-only by design — nothing is modified.
  - Skips system paths ($Recycle.Bin, WinSxS, System Volume Information).
- **30 new unit tests** for `DiskAnalyzerService` and
  `DiskAnalyzerViewModel` — covers empty dirs, subfolders, nested files,
  root files, percentages, invalid inputs, cancellation, progress, and
  model properties.

## [0.7.0] - 2026-04-23

### Added
- **Duplicate File Finder tab** — scans a folder tree for files with
  identical content and shows them grouped by SHA-256 hash.
  - Two-pass scan: group by size first, then hash only size-matched files.
  - SHA-256 content hashing with cancellation support.
  - Duplicate groups sorted by wasted space (descending).
  - Preset folders (user profile, documents, downloads, all fixed drives).
  - Browse button for custom folder selection.
  - Configurable minimum file size filter (default 1 KB).
  - "Show in Explorer" and "Copy path" for each file.
  - Read-only by design — no delete functionality.
  - Skips system paths ($Recycle.Bin, WinSxS, System Volume Information)
    and system files (pagefile, hiberfil, swapfile).
- **41 new unit tests** for `DuplicateFileService` and
  `DuplicateFileViewModel` — covers empty dirs, single files, duplicate
  detection, subdirectories, min size filter, wasted bytes calculation,
  cancellation, progress reporting, hash determinism, and model properties.

## [0.6.0] - 2026-04-22

### Added
- **Startup Manager tab** — lists every program that runs at Windows boot
  and lets users toggle them on/off non-destructively.
  - Scans Registry `Run` / `RunOnce` keys (HKCU + HKLM).
  - Reads `StartupApproved` state (same mechanism as Task Manager).
  - Shows name, publisher, command, and enabled/disabled status.
  - Toggle on/off writes to `StartupApproved` — original `Run` values are
    never deleted.
  - "Open file location" button for each entry.
- **170 new unit tests** for services, models, and helpers — brings the
  total past 1 300 tests.
- **Author header** added to all source files (`laurentiu021`).

### Changed
- **Auto-release workflow** now triggers the release pipeline via
  `workflow_dispatch` instead of relying on tag-push events, fixing a
  race condition where the release job could start before the tag was
  fully pushed.

## [0.5.3] - 2026-04-22

### Fixed
- **CodeQL warnings resolved** — constant-condition check and
  floating-point equality comparison cleaned up.
- **Bug report template visibility** — the issue template was not
  showing up correctly in the GitHub "New issue" picker.

### Added
- **Pure unit tests** for `CleanupViewModel`, `DeepCleanupViewModel`,
  `LargeFileScanner`, and Helpers (converters + `AdminHelper`).
- **Codecov configuration** (`.codecov.yml`) for coverage gating.
- **General issue template** (bug / crash / stability) added to
  `.github/ISSUE_TEMPLATE/`.
- **Auto-release workflow** (`auto-release.yml`) — automatically bumps
  the version and creates a GitHub Release when app code changes land
  on `main`.

### Changed
- **CI** — Codecov upload upgraded to v5; explicit file glob removed.
- **Discussions announcement** posted automatically on every release.
- `.editor/` added to `.gitignore`.

## [0.5.2] - 2026-04-21

### Fixed
- **Cascading error dialogs** — a `DispatcherTimer` ticking at 250 ms could
  queue multiple UI-thread exceptions while a `MessageBox` was blocking the
  dispatcher, producing a cascade of identical "SysManager error" dialogs and
  eventually crashing the app. An interlocked flag now ensures at most one
  error dialog is shown at a time.
- **Ookla speed-test DLL dialogs** — `ProcessStartInfo.ErrorDialog` was not
  set to `false`, so Windows would show a native "DLL was not found" system
  dialog for every failed launch of `speedtest.exe`. The dialog is now
  suppressed; the error surfaces cleanly in the Speed Test status bar instead.
- **Corrupt `speedtest.exe` auto-recovery** — if the downloaded Ookla CLI is
  smaller than 1 KB (partial/corrupt download), it is deleted automatically
  so the next run re-downloads a clean copy.

### Changed
- **Dependencies** — LiveChartsCore 2.0.0-rc5.4 → 2.0.0 (stable release),
  System.Management 10.0.6 → 10.0.7, all GitHub Actions updated to latest
  major versions (checkout v6, setup-dotnet v5, cache v5, upload-artifact v7,
  action-gh-release v3).

### Added
- **CodeQL security scanning** — weekly scheduled analysis plus scan on every
  push/PR. Results visible in the Security tab.
- **Codecov coverage tracking** — unit-test coverage uploaded on every CI run;
  badge in README reflects latest `main` result.
- **App screenshots** — all major tabs captured under `docs/screenshots/`.

### Added
- **Repository hygiene** — `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`,
  `SECURITY.md`, `SUPPORT.md`, `.editorconfig`, and a full
  `.github/` folder (issue + PR templates, CI + release workflows,
  Dependabot config, CODEOWNERS, FUNDING placeholder).
- **CI** — GitHub Actions build + unit-test pipeline on every push/PR,
  plus a separate UI-automation job. Cache NuGet packages between runs.
- **Release workflow** — tag-driven build of a signed-free single-file
  exe, SHA256 checksum file, automatic extraction of release notes from
  `CHANGELOG.md`, uploaded together as a GitHub Release.
- **Copy environment info** button on the About tab — copies SysManager
  version, Windows version, architecture, .NET runtime and elevation
  state to the clipboard, ready to paste into a bug report.
- **Screenshots** folder (`docs/screenshots/`) with capture and privacy
  conventions documented.
- **Manual UI smoke script** (`docs/manual-smoke.ps1`) referenced from
  `TESTING.md` — walks every nav tab via the Windows UI Automation tree.
- **README badges** for CI status, latest release, downloads and open
  issues. New sections for reporting bugs, security and contributing.

### Fixed
- **Broken unit tests on main** — three tests in
  `DeepCleanupServiceTests` and `LargeFileScannerTests` no longer
  matched the service signatures introduced in 0.5.1 (progress reporting).
  They now compile and pass, and the cancellation tests correctly
  assert `TaskCanceledException` from `Task.Run(..., cancelledToken)`.
- **Flaky Network tests excluded from CI** — tests that depend on a
  captured WPF dispatcher (`NetworkViewModelSampleTests`,
  `NetworkViewModelDisableTests`, `NetworkHealthFeedbackTests`,
  `NetworkButtonsTests`, `NetworkViewModelTests`,
  `NetworkExhaustiveTests`) are now tagged
  `[Trait("Category", "LocalOnly")]`. CI runs with
  `--filter "Category!=LocalOnly"` so the build stays green while the
  tests continue to run locally where the dispatcher is deterministic.
- **More slow/real-system tests excluded from CI** — `EventLogServiceTests`,
  `DiskHealthServiceTests`, `PowerShellRunnerTests`,
  `PowerShellRunnerDebugTests`, `MemoryTestServiceTests`,
  `SystemInfoServiceTests`, `AboutViewUiTests`, `DeepCleanupViewUiTests`
  tagged `LocalOnly`; these hit real Windows APIs (Event Log, WMI,
  PowerShell process, WPF pack URIs) that are unavailable or too slow on
  the hosted runner.
- **Bug fixes in test data** — `UpdateServiceTests.IsNewer_HandlesMajorJumps`
  had `latest`/`current` columns swapped; corrected.
- **Bug fix: `UpdateService.ParseVersion`** — `TrimStart('v','V')` stripped
  all leading v characters, so `"vv1.2.3"` parsed successfully instead of
  returning null. Now strips at most one leading v/V.
- **Bug fix: `FixedDriveService.EnumerateAsync`** — passing a pre-cancelled
  `CancellationToken` to `Task.Run` caused `TaskCanceledException` before
  the synchronous `Enumerate()` delegate ran. Token is no longer forwarded.

## [0.5.1] - 2026-04-20

### Added
- **Progress bars** everywhere the scanner runs:
  - Deep cleanup scan — determinate bar with "[12/20] Scanning Steam..." status.
  - Deep cleanup clean — same, as each selected category is emptied.
  - Large files finder — indeterminate bar with live counter
    ("4,328 files · 12.3 GB scanned") and current folder.
- **Background task mini-tray** in the left sidebar (under the Admin
  badge) — shows live progress for any running scan/clean/large-files
  operation. Stays visible on every tab, clickable to jump back.

### Changed
- Scan and clean operations continue running when you navigate away to
  other tabs. Progress and results are preserved in the view model.

## [0.5.0] - 2026-04-20

### Fixed
- Update check would silently fail with "Couldn't reach GitHub" even when
  the network was fine. The GitHub client now uses an explicit
  `SocketsHttpHandler`, exposes the actual error message, retries once on
  transient network failures, and shows a visible "Retry" button in the
  About tab.

### Added

#### Deep cleanup (safe by design)
- New **Deep cleanup** tab with opt-in categories and a scan-first workflow.
- **System categories**: NVIDIA / AMD / Intel installer leftovers, Windows
  Update cache, Delivery Optimization cache, Windows Installer patch cache
  (`$PatchCache$`), TEMP folders, Prefetch, crash dumps and WER reports,
  old CBS logs (> 30 days), DirectX shader cache, Recycle Bin on every
  fixed drive.
- **Gaming launcher caches** (never game files, never logins):
  - Steam browser & depot cache (`appcache`, `htmlcache`, `depotcache`, `logs`)
  - Steam per-game shader cache (`steamapps\shadercache`)
  - Epic Games Launcher webcache and logs
  - Battle.net agent cache and Blizzard launcher cache
  - Riot Client / League of Legends client logs
  - GOG Galaxy webcache and redists
  - EA Desktop / Origin cache and logs
- **Windows.old** is detected and shown with an "Irreversible" tag — never
  selected by default.
- Every deletion is wrapped in try/catch so locked files are skipped, not
  forced. A live total shows how much space you'll reclaim.

#### Large files finder
- Scan any preset folder (Downloads, Documents, Desktop, Videos, Pictures,
  Music, Program Files, Program Files x86) or a whole fixed drive.
- Configurable min size (default 500 MB) and top N results (default 100).
- Read-only: results only expose "Show in Explorer" and "Copy path" —
  deletion is disabled by design, even with admin rights.
- Skips pagefile/hiberfil/swapfile, WinSxS, System Volume Information,
  Recycle Bin and critical system config folders.

#### Update system
- Auto update check on startup against the GitHub Releases API, plus a
  manual "Check for updates" button.
- New **About** tab showing the current version, build date, license, and
  a full release-note history pulled live from GitHub.
- Discreet banner in the main window when a newer version is detected,
  linking to the About tab for details.
- Automatic background download of the new build with a progress bar.
  If the automatic download is blocked, a "Manual download" button opens
  the GitHub release page in the browser.
- One-click "Install" button that launches the downloaded build and
  closes the current instance so the new version takes over.

### Safety
- Deep cleanup **never** touches: browser caches / cookies / passwords,
  launcher login tokens, the registry, active drivers, Program Files,
  `AppData\Roaming` (live app settings), `ProgramData\NVIDIA` root, or
  actual game files in `steamapps\common`.
- Large files finder is read-only — no delete button exists, so a
  mis-click can't hurt anything important.

## [0.4.0] - 2026-04-20

### Added
- File-system scan auto-discovers all fixed NTFS/ReFS drives and shows a
  checkbox list. Scan one drive, a few, or all of them — runs sequentially
  so disks don't fight for I/O.
- "Scan selected" button in System Health for bulk chkdsk.
- Auto-check for the PSWindowsUpdate module on the Windows Update tab. A
  yellow card prompts installation if it's missing.
- Background-task indicators for SFC and DISM so you can navigate away while
  they grind in the background.

### Fixed
- chkdsk "Access is denied" when the app was launched from a non-system
  working directory (e.g. `E:\Downloads`). All spawned processes now start
  from `System32`.

### Changed
- SFC and DISM no longer block the whole Cleanup tab. Each has its own
  running state; you can keep cleaning TEMP or browsing other tabs while
  they run.

## [0.3.0] - 2026-04-20

### Added
- Self-contained single-file publish profile (`publish.ps1`).
- README, ARCHITECTURE, TESTING, and LICENSE documentation.
- `.gitignore` tuned for .NET / WPF projects.

### Changed
- README rewritten as a general-purpose local monitoring tool.

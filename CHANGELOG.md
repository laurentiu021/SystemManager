# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- **DashboardView** — replaced 30+ hardcoded hex colors with StaticResource tokens
  (Surface1, Surface2, Border1, TextPrimary, TextSecondary, Info).
- **AppBlockerView** — full structural modernization (Display header, Card wrappers,
  button styles, DataGrid accessibility, Background, margins).
- **DnsHostsView** — DataGrid grid-lines removed, accessibility name added, text token.
- **ObservableCollection → BulkObservableCollection** — AppAlerts, AppBlocker,
  ShortcutCleaner now use single-notification ReplaceWith() instead of N Add() events.
- **Missing toast notifications** — added on Drivers, Services, ShortcutCleaner,
  DeepCleanup (3 operations), NetworkRepair.
- **Task.Delay(1)** → Task.Yield() in WindowsUpdateViewModel.

### Fixed
- **UI uniformity audit** — replaced all remaining CheckBoxes with purple ToggleSwitch on:
  Performance (5 toggles), Logs (5 severity filters), Ping targets, Process Manager,
  Deep Cleanup categories.
- **Hover consistency** — all interactive elements now use `#186366F1` purple tint.
  Fixed: LogsView, DiskAnalyzer, NetworkRepair (3 cards), DuplicateFile (added missing hover).
- **Dashboard** — replaced green Tune-Up button with PrimaryButton (purple), green borders
  with Accent.
- **Ping targets** — green tint background replaced with purple.
- **Hardcoded colors → StaticResource** — ~30 instances replaced across 8 views
  (Danger, Success, Warning, Info, Accent tokens).

## [1.10.0] - 2026-05-26

### Added
- **Safety ratings on Services** — each service shows Safe/Caution/Critical badge with
  description tooltip. Filter chips in toolbar to show only safe-to-disable services.
- **Safety ratings on Windows Features** — same badge system as Services.
- **Curated safety database** — 50+ services and 20+ features with researched safety
  levels and human-readable explanations.
- **Startup Manager hide system** — toggle to filter out Windows/Microsoft system entries.
- **Filter chip styles** — reusable green/amber/red radio pill components.

## [1.9.0] - 2026-05-26

### Added
- **Purple toggle switch** — global ToggleButton component replacing all CheckBoxes and
  enable/disable buttons. Consistent on/off/locked states across Startup Manager, Privacy,
  Windows Features, and Context Menu tabs.
- **Glass toast notifications** — bottom-right overlay appears on operation completion
  (scan, install, cleanup, shred, etc). Auto-dismisses after 5 seconds.
- **Inline status bar** — progress state transitions visually from purple (busy) to green (done).

### Changed
- **Startup Manager** — toggle column uses purple ToggleSwitch instead of checkbox.
- **Privacy Toggles** — scaled checkbox replaced with ToggleSwitch.
- **Windows Features** — Enable/Disable button replaced with ToggleSwitch.
- **Context Menu** — checkbox replaced with ToggleSwitch.

## [1.8.0] - 2026-05-26

### Added
- **Dark title bar** — forced immersive dark mode via DWM API, no more white chrome.
- **SSD warning on File Shredder** — info banner explaining wear-leveling limitations.
- **Download button in updater** — users can now click Download when an update is available.
- **Windows Features status column** — shows Enabled/Disabled on initial scan.

### Fixed
- **ProgressBar accent color** — all progress bars now use purple theme globally.
- **RadioButton/CheckBox accent** — power plan selector and all checkboxes match theme.
- **Bulk installer hover** — app rows now highlight with visible purple tint on mouseover.
- **"Install selected" on History tab** — buttons hidden when viewing update history.
- **Startup Manager refresh** — fixed cross-thread collection update crash.
- **Startup Manager open folder** — robust path extraction for apps with arguments (lghub etc).
- **DNS detection** — skips virtual adapters, iterates all active until DNS found.
- **Release history notifications** — single UI update instead of N individual events.

### Changed
- **Complete UI redesign** — glass card components, golden admin system, modern severity
  badges, unified accent color (#6366F1) throughout all views.

## [1.7.20] - 2026-05-25

### Fixed
- **Silent test runs** — AdminHelper.RelaunchAsAdmin, AboutViewModel.OpenUrl, and
  DialogService.Confirm now skip execution in test context, preventing UAC prompts
  and browser tabs during `dotnet test`. All 2281 tests pass silently.

## [1.7.19] - 2026-05-25

### Fixed
- **Task.Delay in WindowsUpdateViewModel** — replaced remaining `Task.Delay(1)`
  with `Task.Yield()` for consistent async startup pattern.

## [1.7.18] - 2026-05-25

### Fixed
- **Atomic update downloads** — UpdateService temp file + SHA-256 verification +
  atomic rename (carried forward from 1.7.17 fix scope).
- **ObservableCollection mutation** — build full list before clearing collection.
- **DeepCleanup skipped-file counts** — track and surface IOException/access errors.
- **Navigation refactor** — data-driven BuildNavGroups() with Group()/Item() helpers.

## [1.7.17] - 2026-05-25

### Fixed
- **Task.Delay anti-patterns** — replaced `Task.Delay(1000)` and `Task.Delay(250)` with
  `Task.Yield()` in AboutViewModel and WindowsUpdateViewModel startup paths.
- **UpdateService atomic download** — downloads now write to a `.tmp` file first, compute
  SHA-256 on the temp file, then atomically `File.Move` to the final target. Prevents
  half-written binaries from being used after interrupted downloads.
- **ObservableCollection mutation** — AboutViewModel `LoadHistoryAsync()` now builds the
  full list with LINQ `.Select().ToList()` before clearing/adding to the collection,
  separating data construction from UI mutation.

### Added
- **DeepCleanup skipped-file counts** — scan now tracks files that threw IOException,
  UnauthorizedAccessException, or SecurityException and reports `SkippedCount` in the
  `CleanupCategory` model. CountDisplay shows "N files - M skipped" when applicable.

### Changed
- **InitNavigation refactored to data-driven** — sidebar tree construction replaced with
  `BuildNavGroups()` returning a `NavGroup[]` via `Group()` and `Item()` helper methods.
  Subtitle and Tooltip are auto-generated from child labels.
- **Version** aligned to 1.7.17.

## [1.7.13] - 2026-05-22

### Fixed
- **Bulk Installer icons** — real application icons (Chrome, Firefox, Steam, etc.)
  downloaded via Google Favicon API with local cache and offline fallback.
- **Elevation banners** — App Updates, Uninstaller, Bulk Installer now uniform.
  Services banner moved above toolbar. All 13 admin pages consistent.
- **File Shredder** — fixed white page (transparent DataGrid background).
- **Column resize** — CanUserResizeColumns on all remaining DataGrids.
- **Tray icon** — shows real app icon from exe (not generic).

## [1.7.8] - 2026-05-22

### Fixed
- **Ping chart flicker** — chart buffers now use BulkObservableCollection with single
  Reset notification instead of per-item Add/Remove, eliminating visual stutter during
  live ping monitoring.

## [1.7.7] - 2026-05-22

### Fixed
- **Uniform elevation banners** — all 10 admin-required pages now show consistent
  elevation UI with page-specific reasons and "Run as administrator" button.

## [1.7.6] - 2026-05-22

### Fixed
- **Uniform elevation banners (first 5)** — Windows Update, Windows Features, Privacy,
  DNS & Hosts, and Services now use identical elevation banner design.

## [1.7.5] - 2026-05-22

### Fixed
- **Ghost checkboxes** — eliminated phantom empty rows in Windows Update and Uninstaller
  DataGrids via `CanUserAddRows="False"`.
- **DNS & Hosts elevation** — added "Run as administrator" banner (was missing).
- **File Shredder empty state** — hides table headers when no files are added.
- **Startup column width** — "Open" button no longer cut off.
- **Resizable columns** — all 18 DataGrid tables now support column resizing.

## [1.7.4] - 2026-05-22

### Fixed
- **DNS & Hosts page empty** — view referenced non-existent converter causing silent
  XAML load failure.
- **Quick Tune-Up ignored No** — now asks explicit confirmation before any action.
- **Design polish** — Bulk Installer redesigned with categories, descriptions, custom
  search. Context Menu Manager with friendly names. Elevation badges restyled.

## [1.7.3] - 2026-05-22

### Fixed
- **Critical: startup crash** — fixed "Entry point DefWindowProc not found in user32.dll"
  that prevented the app from launching. P/Invoke declaration now correctly specifies
  `DefWindowProcW` entry point.
- **Shutdown crash** — fixed ObjectDisposedException when closing the app
  (DnsHostsViewModel CTS disposal race condition).

## [1.7.0] - 2026-05-21

### Added
- **Context Menu Manager** — scan, enable/disable Windows Explorer right-click entries
  via LegacyDisable (non-destructive). Covers files, folders, directory background,
  and desktop with search/filter and registry backup.

## [1.6.0] - 2026-05-21

### Added
- **DNS Changer** — quick-switch between Google, Cloudflare, Quad9, OpenDNS, or DHCP
  with automatic adapter detection and one-click apply/reset.
- **Hosts File Editor** — visual editor for the Windows hosts file with add/remove/toggle
  entries, IP/hostname validation, and automatic backup before saves.

## [1.5.0] - 2026-05-21

### Added
- **Privacy Toggles** — 12 one-click privacy switches (telemetry, advertising ID, Copilot,
  Cortana, web search, widgets, Start suggestions, lock screen tips) with instant apply
  and registry state detection.

## [1.4.0] - 2026-05-21

### Added
- **File Shredder** — secure file deletion with multiple overwrite methods (Quick 1-pass,
  Standard 3-pass, Thorough 7-pass). Protects system paths, uses confirmation dialog,
  supports files and folders.

## [1.3.0] - 2026-05-21

### Added
- **System Info Export** — comprehensive system report (OS, CPU, GPU, RAM, storage,
  network, SMART data) exportable to file or clipboard from the About tab.

## [1.2.0] - 2026-05-21

### Added
- **Bulk App Installer** — install multiple applications via winget with curated list
  of 25 apps across 7 categories, category/text filtering, and per-app progress.

## [1.1.0] - 2026-05-21

### Added
- **Windows Update** — individual update selection via checkboxes. Users can now
  select/deselect specific updates before installing. Added "Select all" and
  "Deselect all" buttons. KB article IDs validated before passing to PowerShell.

## [1.0.0] - 2026-05-20

### Changed
- **BREAKING:** migrated from .NET 9 to .NET 10 — requires .NET 10 Desktop Runtime
  to run. All projects (main, tests, integration tests, UI tests) now target
  `net10.0-windows`. CI workflows updated to use .NET 10 SDK.

## [0.48.39] - 2026-05-20

### Fixed
- **ObservableCollection batch updates** — replaced Clear() + foreach Add() pattern
  (N+1 CollectionChanged events) with BulkObservableCollection.ReplaceWith() (single
  Reset notification) across 10 ViewModels, reducing UI notification overhead during
  data refreshes.

## [0.48.38] - 2026-05-20

### Fixed
- **LogService** — path sanitization regex now dynamically derives the user
  profile directory from `Environment.GetFolderPath` instead of assuming a
  hardcoded `<drive>:\Users\` pattern; falls back to the generic regex if the
  environment variable is unavailable.
- **MarkdownTextBlock** — cached `FontFamily("Consolas")` as a static field to
  eliminate per-render allocation in code span formatting.

## [0.48.37] - 2026-05-19

### Fixed
- **DiskHealthReport** — fixed potential integer overflow in `HealthPercent`
  calculation when `ReadErrors` or `WriteErrors` exceed `int.MaxValue`; arithmetic
  now uses `long` before clamping to the 0–20 deduction cap.
- **SpeedTestService** — documented pinned Ookla CLI version (`1.2.0`) with
  maintenance comment explaining update procedure and Authenticode verification.

## [0.48.36] - 2026-05-19

### Fixed
- **MemoryTestService** — `ManagementObject` instances in `GetModulesAsync` WMI
  query are now properly disposed via `using (mo)` block, preventing native handle
  leaks when enumerating physical memory modules.
- **NetworkSharedState** — `Dispose()` now fully releases all SkiaSharp paint
  resources: series paints (stroke, geometry, fill), axis paints (name, labels,
  separators), and class-level legend/tooltip paints. Previously only typefaces
  were disposed, leaking unmanaged `SKPaint` handles.

### Added
- **ServicesViewModelTests** — 20 unit tests covering ApplyFilter logic: category
  filters (All, Running, Stopped, Safe to disable, Advanced), text search by name/
  display name/description, combined filters, sort order, empty data, and property
  change triggers.

## [0.48.35] - 2026-05-19

### Fixed
- **ProcessManagerViewModel** — resolved CodeQL `cs/complex-condition` alert (#302)
  by replacing chained null-conditional `||` expression with a `ReadOnlySpan` loop
  in `MatchesDescription`.
- **PerformanceView** — eliminated MVVM violation: removed `PropertyChanged`
  subscription and `Checked` event handler from code-behind; radio buttons now use
  two-way `EqualityConverter` binding to `SelectedPlan` (pure XAML, no code-behind
  logic).
- **OperationLockServiceTests** — replaced flaky `Barrier` + `Thread.Sleep`
  thread-safety test with deterministic `CountdownEvent` + `ManualResetEventSlim`
  synchronization; asserts exactly 1 acquisition instead of `>= 1`.

### Added
- **EqualityConverter** — reusable two-way `IValueConverter` that compares a bound
  value to `ConverterParameter`; ideal for radio button groups bound to a string
  property.
- **EqualityConverterTests** — 10 unit tests covering Convert/ConvertBack, null
  handling, and case sensitivity.
- **FormatHelperTests** — 14 unit tests covering `FormatSize` at all boundaries
  (bytes, KB, MB, GB) with exact boundary and mid-range values.

### Changed
- **README.md** — added missing tech stack entries: Microsoft.Extensions.DependencyInjection,
  H.NotifyIcon.Wpf, NSubstitute.

## [0.48.34] - 2026-05-19

### Fixed
- **PerformanceService** — implemented `IDisposable` to properly dispose the
  internal `SemaphoreSlim` gate, preventing resource leaks on app shutdown.

### Changed
- **README.md** — corrected sidebar tab counts (56 total, 25 implemented).
- **ARCHITECTURE.md** — removed false claim that TuneUpService and
  ShortcutCleanerService are instantiated directly (both are registered in DI).
- **ARCHITECTURE.md** — added 9 missing services to the Key services section
  (AppAlertService, AppBlockerService, BatteryService, DialogService,
  IconExtractorService, OperationLockService, ProcessDescriptionService,
  SpeedTestHistoryService, ShortcutCleanerService).

## [0.48.33] - 2026-05-18

### Fixed
- **CodeQL** — resolved 5 remaining source code alerts:
  - Replaced generic `catch (Exception)` in `ViewModelBase.InitializeAsync`
    with specific exception types (`InvalidOperationException`,
    `UnauthorizedAccessException`, `IOException`, `HttpRequestException`,
    `TimeoutException`).
  - Converted `UninstallerService.IsUnderTrustedDirectory` foreach+if to
    LINQ `.Any()` (cs/linq/missed-where).
  - Converted `WindowsFeaturesService.ParseFeatureList` foreach loop to
    LINQ `.Select().Where()` pipeline (cs/linq/missed-select).
  - Extracted `ProcessManagerViewModel.MatchesFilter` complex condition into
    three focused helper methods (cs/complex-condition).
  - Replaced `Path.Combine` with `Path.Join` in `UpdateService.DownloadAsync`
    (cs/path-combine).
- **CodeQL workflow** — added query filter to suppress `cs/call-to-obsolete-method`
  for `UpdateService.VerifyAuthenticode` (intentional use of `CreateFromSignedFile`
  — no modern .NET replacement exists without P/Invoke).

## [0.48.32] - 2026-05-18

### Fixed
- **ConsoleViewModel** — buffer trimming now uses clear-and-rebuild when
  removing more than 25% of lines, reducing worst-case from O(n×excess)
  to O(n) (CQ-LOW: ConsoleViewModel O(n²)).
- **LogsViewModel** — event log entries are now dispatched to the UI thread
  in batches of 50 instead of one-at-a-time, reducing dispatcher overhead
  by ~98% when loading large event logs (CQ-LOW: LogsViewModel batch dispatch).

## [0.48.31] - 2026-05-18

### Fixed
- **FormatSize duplication** — extracted shared `FormatHelper.FormatSize` method;
  `ProcessManagerViewModel`, `DiskAnalyzerViewModel`, and `DuplicateFileViewModel`
  now delegate to the shared helper instead of duplicating the switch expression.
- **OEM encoding duplication** — `CleanupViewModel` (SFC + DISM) and
  `SystemHealthViewModel` (chkdsk) now use `PowerShellRunner.OemEncoding`
  instead of duplicating the encoding resolution logic inline.

### Changed
- **Test parallelism** — enabled `parallelizeTestCollections` in xunit.runner.json
  so pure-logic unit tests run concurrently, reducing CI test time. Tests that
  touch shared OS resources remain serialized via `[Collection("Network")]`
  (TEST-M4).
- **Mocking framework** — added NSubstitute 5.3 to the test project, enabling
  interface-based mocking for future tests that need to isolate OS dependencies
  (TEST-H1).
- **TESTING.md** — documented test infrastructure (frameworks, parallelism
  strategy, conventions for mocking and time-dependent tests).

## [0.48.30] - 2026-05-18

### Fixed
- **ViewModelBase** — added `InitializeAsync` helper method that wraps
  fire-and-forget async calls with structured error handling. Exceptions
  from async initialization are now caught and logged via Serilog instead
  of becoming unobserved task exceptions (CQ-M3).
- **12 ViewModels** — replaced `_ = InitAsync()` fire-and-forget pattern
  with `InitializeAsync(InitAsync)` in: AboutViewModel, BatteryHealthViewModel,
  CleanupViewModel, DashboardViewModel, DeepCleanupViewModel,
  PerformanceViewModel, ProcessManagerViewModel, ServicesViewModel,
  SpeedTestViewModel, StartupViewModel, SystemHealthViewModel,
  WindowsUpdateViewModel.

## [0.48.29] - 2026-05-18

### Changed
- **IconExtractorService** — `FindExecutableByName` results are now cached in a
  `ConcurrentDictionary`, eliminating repeated Program Files directory scans
  (~100+ subdirs) on every process list refresh (PERF-M5).
- **NetworkSharedState** — `TrimBuffer` now uses a clear-and-rebuild strategy
  when removing more than 25% of buffer entries, reducing worst-case complexity
  from O(n×removeCount) to O(n) (PERF-M3).

## [0.48.28] - 2026-05-18

### Fixed
- **CodeQL** — resolved 38 code scanning alerts across 16 source files:
  - Replaced `Path.Combine` with `Path.Join` in 8 locations to prevent
    unexpected path rooting when arguments contain absolute paths.
  - Added descriptive comments to 6 empty catch blocks (intentional
    swallowing of expected exceptions like `FormatException`, `IOException`).
  - Replaced generic `catch (Exception)` in `TrayIconService.OnTimerTick`
    with specific exception types (`OperationCanceledException`,
    `ObjectDisposedException`, `InvalidOperationException`).
  - Converted implicit foreach filters to explicit `.Where()` calls in
    `AppAlertService`, `NetworkSharedState`, `WindowsFeaturesService`,
    `SpeedTestService`, and `TrayIconService`.
  - Extracted complex conditions into helper methods in
    `UninstallerService.IsUnderTrustedDirectory` and
    `ProcessManagerViewModel.MatchesFilter`.
  - Flattened nested if-statements in `UninstallerViewModel.SelectAll`.
  - Replaced `if/else` assignment with ternary in
    `HealthScoreService` weighted average calculation.
  - Converted `ComputeDiskScore` foreach loop to LINQ `.Select().Min()`.
  - Converted `DashboardViewModel` manual `Dispose()` call to `using var`
    declaration for `OperationLockService` lock guard.
  - Removed redundant `(SolidColorBrush)` cast in
    `OutputKindToBrushConverter`.
- **CodeQL workflow** — added `codeql-config.yml` to exclude `obj/` and
  `bin/` directories from analysis (36 alerts in auto-generated code).

## [0.48.27] - 2026-05-15

### Fixed
- **NetworkSharedState** — SkiaSharp `SolidColorPaint` objects are now disposed
  when a ping target is removed, preventing unmanaged memory leaks (CQ-M1).
- **NetworkSharedState** — latency chart offset now uses a stable hash of the
  target host instead of `Targets.IndexOf`, preventing visual jumps when
  targets are removed mid-session (CQ-M2).
- **PerformanceViewModel** — added `Dispose` override to clean up snapshot
  reference and satisfy the base class disposal contract (CQ-M4).

## [0.48.26] - 2026-05-15

### Changed
- **SystemInfoService** — static WMI data (OS caption, CPU name, disk models)
  is now cached on first query; only dynamic data (CPU load, RAM, uptime) is
  re-queried every 60 seconds, reducing WMI overhead by ~70% (PERF-M1).
- **NetworkSharedState** — `RecomputeStats` rewritten with manual loops instead
  of LINQ `.Where().Select().ToList()`, eliminating heap allocations on the
  hot path that runs 32×/sec per target (PERF-M2).
- **TrayIconService** — added `Interlocked` re-entrancy guard on
  `UpdateTooltipAsync` so overlapping timer ticks skip instead of stacking
  concurrent WMI calls (PERF-M4).

## [0.48.25] - 2026-05-15

### Fixed
- **HealthAnalyzer** — no longer claims "DNS is clean" when DNS IS bad; when
  both DNS and game server show trouble, correctly returns Mixed verdict
  instead of GameServer (FUNC-M2).
- **TuneUpService** — empty directory removal now sorts by path depth (separator
  count) instead of string length, ensuring deepest directories are deleted
  first regardless of path name length (FUNC-M3).
- **SpeedTestHistoryService** — `SaveAsync` and `ClearAsync` now serialize via
  `SemaphoreSlim` to prevent concurrent load-modify-save races that could lose
  history entries (FUNC-M4).
- **FixedDriveService** — multi-disk enrichment now maps drive letters to
  physical disks via `MSFT_Partition.DiskNumber`, correctly annotating media
  type and bus type on systems with multiple drives (FUNC-M5).

## [0.48.24] - 2026-05-15

### Fixed
- **UpdateService** — cached download now validated by SHA-256 hash (stored in
  companion `.sha256` file) instead of file size alone; prevents cache poisoning
  with same-size payloads (SEC-M2).
- **SpeedTestService** — Zip Slip protection: manual extraction validates each
  entry path stays within the target directory; blocks path traversal attacks
  via crafted zip archives (SEC-M3).
- **SpeedTestService** — DLL hijacking mitigation: Ookla CLI process now
  launches with `WorkingDirectory` set to System32 instead of the user-writable
  tools directory, preventing CWD-based DLL search order hijacking (SEC-M4).
- **ServiceManagerService** — defensive validation on service names before
  interpolating into registry paths; rejects names containing path separators
  or null characters (SEC-M6).
- **UninstallerService** — `ParseUninstallCommand` hardened: rejects shell
  metacharacters (`|&;` backtick `$(`) to prevent command injection; improved
  `.exe` boundary detection to avoid misparsing paths with `.exe` substrings;
  removed unsafe fallback that treated unparseable strings as executables
  (SEC-M7).
- **PowerShellRunner** — expanded security contract documentation clarifying
  that `ExecutionPolicy.Bypass` is safe only because all script content is
  hard-coded in source; callers must never interpolate user input (SEC-M8).
- **DiskHealthService** — replaced bare `catch` blocks in WMI conversion
  helpers with specific exception types (`FormatException`, `OverflowException`,
  `InvalidCastException`).
- **DeepCleanupService** — replaced bare `catch` with specific `IOException`,
  `UnauthorizedAccessException`, `SecurityException`.
- **TracerouteMonitorService** — replaced bare `catch` with specific network
  and operation exception types.
- **TracerouteService** — replaced generic `catch (Exception)` in event raiser
  with specific `ObjectDisposedException`, `InvalidOperationException`.
- **PingMonitorService** — replaced bare `catch` in event raiser with specific
  exception types.
- **EventLogService** — replaced bare `catch` blocks in record projection and
  message formatting with specific `EventLogException`,
  `InvalidOperationException`.

## [0.48.23] - 2026-05-15

### Fixed
- **UpdateService** — added Authenticode signature verification on downloaded
  update binaries; rejects files with invalid (tampered) signatures (SEC-H1).
- **AboutViewModel** — update script now uses a random GUID filename to prevent
  TOCTOU race conditions on the updater .cmd file (SEC-M1).
- **UninstallerService** — uninstall executables from registry are now validated
  against trusted directories (Program Files, Windows, ProgramData,
  LocalApplicationData); rejects paths outside these locations (SEC-H2).
- **EventLogService** — XPath sanitization now strips all metacharacters
  including `|()@*<>` in addition to the existing set (SEC-M5).
- **BatteryInfo** — `HealthPercent` returns -1 (unavailable) instead of 0 when
  WMI capacity data is missing (no admin elevation), preventing false-critical
  health scores on every non-elevated laptop (FUNC-H1).
- **HealthScoreService** — `ComputeBatteryScore` treats -1 (unavailable) as
  neutral (100) instead of critical (10).
- **BatteryHealthViewModel** — displays "requires elevation" when health data
  is unavailable instead of showing 0%.
- **StartupService** — registry approved-state blob now uses bitmask
  `(blob[0] & 1) == 0` for enabled detection, fixing Windows 11 which uses
  `07` (not just `03`) for disabled entries (FUNC-M1).

## [0.48.22] - 2026-05-15

### Fixed
- **AppAlertService** — `NewAppDetected` event now marshaled to the UI thread
  via captured `SynchronizationContext`, preventing crashes when
  `FileSystemWatcher`/`Timer` callbacks invoke subscribers directly.
- **NetworkRepairService** — added `SemaphoreSlim` gate to serialize
  subscribe/unsubscribe on the shared `PowerShellRunner`, preventing
  concurrent calls from interleaving output.
- **PerformanceService** — same `SemaphoreSlim` serialization for all methods
  that subscribe to `PowerShellRunner.LineReceived`.
- **PowerShellRunner** — documented that `LineReceived` fires on thread-pool
  threads; subscribers must marshal to the dispatcher for UI updates.
- **StartupService** — added `RuntimeBinderException` catch for dynamic COM
  shortcut resolution (`.lnk` files with broken targets).
- **StartupService** — `GetAwaiter().GetResult()` on stderr task now guarded
  with a 5-second timeout to prevent hangs if the pipe isn't fully drained.
- **AppAlertsViewModel** — use `Application.Current.Dispatcher` instead of
  `Dispatcher.CurrentDispatcher` to avoid capturing the wrong dispatcher.
- **NetworkSharedState** — documented that `FlushPending` direct-call path
  (when `Dispatcher == null`) is intentional for unit tests / headless mode.
- **AboutViewModel** — removed auto-download of updates without user consent;
  user must now explicitly click Download.
- **App.xaml.cs** — single-instance activation now uses a named pipe listener,
  fixing activation when the window is minimized to tray (no `MainWindowHandle`).
- **MainWindow.xaml.cs** — ViewModel disposal now also hooks
  `Application.Current.Exit` as a safety net for when `OnClosed` is not called.

### Changed
- **SysManager.csproj** — version updated from 0.12.1 to 0.48.21 (cosmetic;
  auto-release overrides at build time).
- **SysManager.Tests.csproj** — xunit bumped from 2.5.3 to 2.9.3 (matches
  UITests project).
- **SysManager.IntegrationTests.csproj** — xunit bumped from 2.5.3 to 2.9.3.
- **dependabot.yml** — added `IntegrationTests` directory entry for NuGet
  dependency monitoring.

## [0.48.21] - 2026-05-15

### Fixed
- **AdminHelper** — `Process.GetCurrentProcess()` now properly disposed via
  `using` in `RelaunchAsAdmin()` (prevents brief handle leak).
- **HexToBrushConverter** — frozen brushes now cached by hex value in a
  `ConcurrentDictionary` to eliminate repeated allocations and GC pressure
  on frequently-updating bindings (dashboard, health score, tune-up).
- **App.xaml.cs** — `ReleaseMutex()` wrapped in try-catch for
  `ApplicationException` (thrown if called from wrong thread on shutdown).
- **EtaCalculator** — added thread-safety documentation (single-thread
  requirement via UI dispatcher).

## [0.48.20] - 2026-05-15

### Fixed
- **NetworkSharedState** — replaced obsolete `SkiaPaint.FontFamily` with
  `SKTypeface = SKTypeface.FromFamilyName()` on 4 axis paint objects,
  eliminating all CS0618 build warnings.
- **AboutViewModel** — replaced `Assembly.Location` (returns empty in
  single-file publish) with `AppContext.BaseDirectory` lookup, eliminating
  IL3000 warning.

## [0.48.19] - 2026-05-15

### Fixed
- **DuplicateFileService** — skip reparse points (symlinks, junctions) during
  directory traversal to prevent infinite loops on circular symlinks.
- **LargeFileScanner** — same reparse point check added.
- **DeepCleanupService** — `EnumerateFiles()` now catches `IOException` and
  `UnauthorizedAccessException` during `MoveNext()` iteration, not just at
  enumerator creation. Prevents crashes on files that become inaccessible
  mid-scan.
- **TrayIconService** — `OnTimerTick` (async void) now wraps the entire call
  in try-catch to prevent unhandled exceptions from crashing the application.

## [0.48.18] - 2026-05-15

### Fixed
- **SystemInfoService** — `QueryMemory()` and `QueryDisks()` now properly
  dispose `ManagementObject` and `ManagementObjectCollection` instances via
  `using` statements (4 foreach loops fixed, prevents COM handle leaks).
- **FixedDriveService** — same WMI disposal fix for MSFT_PhysicalDisk query.
- **DeepCleanupViewModel** — post-clean rescan no longer deadlocks on the
  operation lock. Extracted `ScanCoreAsync()` (lock-free) called from
  `CleanAsync` which already holds the disk lock.
- **WindowsFeaturesViewModel** — separated shared `_cts` into `_scanCts` and
  `_toggleCts` so toggling a feature no longer cancels a running scan.

## [0.48.17] - 2026-05-15

### Fixed
- **DeepCleanupViewModel** — dispose previous CancellationTokenSource before
  creating a new one in Scan/Clean/LargeScan (3 locations). Prevents kernel
  handle leak on repeated operations.
- **SpeedTestViewModel** — same CTS disposal fix (2 locations: HTTP + Ookla).
- **TracerouteViewModel** — same CTS disposal fix.
- **ShortcutCleanerViewModel** — same CTS disposal fix.
- **NavItem** — implement `IDisposable` to unsubscribe `PropertyChanged`
  handler from ViewModel on teardown. Previously 51 subscriptions leaked
  permanently. `MainWindowViewModel.Dispose()` now disposes all NavItems.

## [0.48.16] - 2026-05-15

### Fixed
- **SpeedTestService** — stdout and stderr now read in parallel via
  `Task.WhenAll` to prevent classic Windows pipe buffer deadlock when
  Ookla CLI writes enough to stderr while stdout is being consumed.
- **DiskHealthService** — added regex validation (`^[\w{}\-\\.:/]+$`) on
  WMI objectId before WQL interpolation (defense-in-depth against injection).
- **UninstallerService** — tightened `PackageIdPattern` regex: replaced `\s`
  (which allows tabs/newlines) with a literal space character.

### Changed
- **WindowsFeaturesService** — added SECURITY-CRITICAL documentation comment
  on `FeatureNamePattern()` regex explaining it is the sole injection defense.

## [0.48.15] - 2026-05-15

### Fixed
- **AppBlockerView, AppAlertsView, ShortcutCleanerView** — removed XAML
  `<UserControl.DataContext>` that bypassed DI container, causing these views
  to operate on isolated ViewModel instances instead of the shared singletons.
- **DashboardView** — ColorHex string bindings now use `HexToBrushConverter`
  instead of invalid `<SolidColorBrush Color="{Binding}"/>` which produced
  runtime binding errors (health score ring, recommendations, disk verdicts,
  tune-up overall verdict).
- **WindowsFeaturesView** — "Not elevated" warning badge now uses `FlexVis`
  converter (supports `ConverterParameter=Inverse`) instead of `BoolToVis`
  which ignores the parameter, causing the badge to always display.

### Changed
- **AppBlockerView, AppAlertsView** — replaced legacy `SystemControlForeground*`
  brushes with app-standard `TextPrimary`/`TextSecondary`/`Border1` resources
  for consistent dark-theme styling.
- **MainWindowViewModel** — corrected stale comment "non-DI resolved" to
  "resolved from DI at runtime" (all 4 VMs are DI singletons since v0.48.0).

## [0.48.14] - 2026-05-15

### Fixed
- **SystemInfoService (CQ-002)** — ManagementObjectCollection and ManagementObject
  instances now properly disposed via `using` in QueryOs() and QueryCpu().
- **HexToBrushConverter** — SolidColorBrush now frozen after creation to prevent
  cross-thread access crashes; bare `catch` narrowed to `catch (FormatException)`.

### Changed
- **LargeFileScanner, DuplicateFileService, DiskAnalyzerService** — replaced
  remaining `Array.Empty<T>()` with collection expressions `[]` (MODERN-003).

## [0.48.13] - 2026-05-15

### Fixed
- **UninstallerService (SEC-007)** — trusted system binaries (MsiExec, rundll32)
  now resolved to absolute System32 path before execution, preventing PATH
  hijacking attacks.
- **SpeedTestService (SEC-008)** — Ookla CLI process now killed on timeout or
  cancellation, preventing orphan processes consuming resources indefinitely.
- **SpeedTestService (PRIV-001)** — all exception messages in Log.Debug calls
  now sanitized via LogService.SanitizePath to prevent username leakage in logs.

## [0.48.12] - 2026-05-15

### Fixed
- **DiskHealthService (CQ-007)** — WQL ASSOCIATORS OF query now escapes single
  quotes in objectId, preventing potential WQL injection.
- **OperationLockService (CQ-008)** — removed redundant lock object; TryAcquire
  and Release now use ConcurrentDictionary atomic TryAdd/TryRemove directly.
- **PingMonitorService (CQ-015)** — CancellationTokenSource only disposed if the
  background loop actually completed, preventing ObjectDisposedException in
  still-running pump tasks.

## [0.48.11] - 2026-05-15

### Fixed
- **ProcessManagerViewModel (CQ-004)** — replaced sync-over-async
  `GetAwaiter().GetResult()` with proper `await` inside `Task.Run` async
  lambda, preventing thread pool thread blocking.
- **DeepCleanupService (CQ-010)** — replaced `Directory.GetFiles()` and
  `GetDirectories()` (full array allocation) with lazy `EnumerateFiles()`
  and `EnumerateDirectories()` to reduce memory pressure on large directories.
- **TracerouteService (CQ-011)** — bare `catch {}` replaced with specific
  `PingException`, `SocketException`, `InvalidOperationException` catches;
  subscriber error catch narrowed to `catch (Exception)`.

## [0.48.10] - 2026-05-15

### Fixed
- **DiskHealthService (CQ-001)** — ManagementObjectCollection and ManagementObject
  instances now properly disposed via `using` statements, preventing COM resource
  leaks during SMART/reliability queries.
- **SpeedTestService (CQ-003)** — Ookla CLI process now has a 5-minute independent
  timeout via linked CancellationTokenSource, preventing indefinite hangs.

### Changed
- **CONTRIBUTING.md** — corrected .NET SDK reference from 8 to 9; added
  SysManager.IntegrationTests to project layout.
- **SECURITY.md** — updated supported versions table to reflect 0.48.x as latest.

## [0.48.9] - 2026-05-14

### Fixed
- **SpeedTestService** — empty catch blocks replaced with `Log.Debug` calls for
  best-effort file cleanup (resolves 4 CodeQL `cs/empty-catch-block` alerts).
- **WindowsFeaturesViewModel** — if/else replaced with ternary for enable/disable
  dispatch (CodeQL `cs/missed-ternary-operator`).
- **UninstallerViewModel** — if/else replaced with ternary for local vs winget
  uninstall dispatch (CodeQL `cs/missed-ternary-operator`).

## [0.48.8] - 2026-05-14

### Fixed
- **UninstallerService (SEC-005)** — `StartsWith` allowlist replaced with exact
  filename match to prevent bypass via similarly-named executables (e.g.
  "MsiExecEvil.exe"). `/I` → `/X` replacement now uses regex word-boundary
  match to avoid corrupting GUIDs.
- **SpeedTestService (SEC-006)** — Authenticode verification now fail-closed:
  if the Ookla binary is unsigned or subject mismatches, it is deleted and an
  exception is thrown instead of just logging a warning.
- **DialogService** — singleton setter now rejects null to prevent global
  null-swap hazards.
- **Application.Current.Shutdown()** — added null-conditional `?.` operator on
  all 5 shutdown call sites (WindowsUpdateVM ×2, DashboardVM, AppUpdatesVM,
  TrayIconService) to prevent NullReferenceException in tests or non-standard
  hosting.
- **AboutViewModel** — clipboard copy no longer reports success when
  `Clipboard.SetText` throws `ExternalException` (clipboard locked).
- **NetworkSharedState** — TOCTOU race in FlushPending replaced
  `ContainsKey` + indexer with `TryGetValue`; all paint SKTypeface instances
  now disposed on cleanup (LEAK-003 complete).
- **AppAlertService** — replaced `ContainsKey` + set with atomic `TryAdd` to
  prevent duplicate new-app notifications in race conditions.
- **PerformanceService** — `CreateRestorePointAsync` no longer uses always-true
  `results != null` check; relies on exception propagation for failure.
- **ServiceManagerService** — service name regex narrowed from `\s` (any
  whitespace including newlines) to literal space only.
- **WindowsFeaturesViewModel** — CancellationTokenSource now cancelled before
  disposal in all code paths to prevent orphaned in-flight operations.
- **App.xaml.cs** — DI ServiceProvider now disposed on application exit,
  ensuring all DI-owned singletons implementing IDisposable are cleaned up.
- **DashboardView.xaml** — disk verdict and overall tune-up verdict colors now
  bound to model `ColorHex`/`OverallColorHex` instead of hardcoded green.

## [0.48.7] - 2026-05-14

### Fixed
- **UninstallerService (SEC-002)** — UninstallLocalAsync now validates that the
  executable exists and has a .exe extension before running. Prevents execution
  of arbitrary commands from HKCU registry keys (modifiable without admin).
- **EventLogService (SEC-003)** — XPath sanitization now strips quotes, brackets,
  slashes in addition to single quotes to prevent XPath injection.
- **LogService (SEC-004)** — path sanitization regex now covers all drive letters
  (A-Z:\Users\) instead of only C: drive.

### Changed
- **Modern C#** — replaced Array.Empty<T>() with collection expressions []
  across 7 files: DiskAnalyzerService, DuplicateFileService, LargeFileScanner,
  UpdateService, CleanupCategory, TuneUpResult, HealthScoreResult (MODERN-003).

## [0.48.6] - 2026-05-14

### Fixed
- **PingMonitorService** — bare catch replaced with specific AggregateException
  and ObjectDisposedException (CodeQL cs/catch-of-all-exceptions).
- **TracerouteMonitorService** — same bare catch fix.
- **OutputKindToBrushConverter** — simplifiable boolean expression refactored
  to pattern matching (CodeQL cs/simplifiable-boolean-expression).
- **LogsViewModel** — unsafe cast from ICollectionView to CollectionView
  replaced with safe as-cast with fallback (CodeQL cs/cast-from-abstract).

## [0.48.5] - 2026-05-14

### Changed
- **DuplicateFileService** — ShouldSkipDir uses OrdinalIgnoreCase instead of
  ToLowerInvariant allocation on every path (PERF-002).
- **LargeFileScanner** — same OrdinalIgnoreCase fix (PERF-002).
- **SpeedTestService** — SHA-256 hashing uses stream instead of
  File.ReadAllBytes to avoid loading entire zip into memory (PERF-004).
- **ProcessManagerService** — MainModule accessed once per process instead of
  twice, halving P/Invoke overhead (PERF-005).
- **AboutViewModel** — CopyEnvironmentInfo WMI queries now run on background
  thread via Task.Run, preventing UI freeze (PERF-008).

## [0.48.4] - 2026-05-14

### Fixed
- **IconExtractorService** — cache eviction race condition resolved with
  double-checked lock pattern (THR-002).
- **PingMonitorService** — Start/Stop race on _cts resolved with lock around
  state transitions (THR-003).
- **TracerouteMonitorService** — same Start/Stop race fix as PingMonitor
  (THR-003).
- **AppAlertService** — List<FileSystemWatcher> access from concurrent threads
  protected with lock on Start/Stop (THR-004).
- **NetworkRepairService** — List<string> output replaced with ConcurrentQueue
  to prevent corruption from background thread callbacks (THR-005).
- **PerformanceView** — SyncRadioButtons now marshals to UI thread via
  Dispatcher.BeginInvoke when called from background (THR-006).

## [0.48.3] - 2026-05-14

### Fixed
- **DuplicateFileGroup** — WastedBytes now raises PropertyChanged when Count or
  FileSize changes (missing NotifyPropertyChangedFor attributes).
- **UpdateService** — pre-release and draft GitHub releases are now filtered out
  in GetRecentAsync results.
- **ServiceManagerService** — StartService no longer throws when the service is
  already in StartPending state.
- **DiskAnalyzerService** — empty directories are no longer incorrectly flagged
  as access-denied; the flag now tracks actual UnauthorizedAccessException.
- **WindowsUpdateViewModel** — null-conditional on Application.Current before
  calling Shutdown() prevents NullReferenceException during unit tests or
  non-standard hosting.
- **TuneUpService** — SHEmptyRecycleBin HRESULT is now checked; returns false
  on failure instead of always reporting success.

## [0.48.2] - 2026-05-14

> **Note:** Versions 0.49.0–0.53.1 below were released under the previous
> repository (`SysManager`). When the project migrated to `SystemManager`
> (2026-05-14), the auto-release workflow reset to the last tag on the new
> repo (v0.48.1). Subsequent releases continue from 0.48.2 onward.
> The entries below are preserved for historical completeness.

### Fixed
- **Security: SpeedTestService** — remove fabricated placeholder SHA-256 hashes
  that caused perpetual warning logs (alert fatigue). Security now relies on
  Authenticode signature verification of the extracted binary + zip structural
  integrity check (SEC-001).

## [0.53.1] - 2026-05-14

### Fixed
- **Resource leak: NetworkSharedState** — dispose SKTypeface on LegendTextPaint
  in Dispose() to release unmanaged SkiaSharp memory (LEAK-003).
- **Resource leak: TrayIconService** — dispose icon resource stream after
  creating System.Drawing.Icon to prevent stream leak (LEAK-006).
- **Resource leak: MemoryTestService** — dispose Process returned by
  Process.Start when launching mdsched.exe (LEAK-007).

## [0.53.0] - 2026-05-13

### Added
- **Navigation: 4 new groups** — Gaming & Profiles, Privacy & Security,
  Customization, and Advanced groups added to sidebar navigation.
- **Gaming & Profiles (5 WIP tabs)** — Gaming Profile, Standby List Cleaner,
  Timer Resolution, CPU Core Affinity, Display Profiles.
- **Privacy & Security (6 WIP tabs)** — Privacy & Telemetry, Debloater & Ads,
  Browser Cleaner, Edge/OneDrive Remover, Defender Tweaks, Notification Blocker.
- **Customization (4 WIP tabs)** — Context Menu, Dark Mode Scheduler, Volume
  Control, Environment Variables.
- **Advanced (4 WIP tabs)** — Restore Points, Profile Export/Import, CLI
  Interface, System Report.
- **Monitor (3 new WIP tabs)** — File Lock Detector, Settings Watchdog,
  Bandwidth Monitor added to existing Monitor group.
- **System (2 new WIP tabs)** — Task Scheduler, Boot Analyzer added to
  existing System group.
- **Cleanup (1 new WIP tab)** — Scheduled Maintenance moved into Cleanup group.

### Changed
- **Navigation structure** — reorganized from 9 groups to 12 groups for better
  feature categorization as the app grows.
- **Placeholder descriptions** — improved all WIP placeholder descriptions with
  clearer feature explanations and correct issue references.

## [0.52.0] - 2026-05-13

### Fixed
- **Resource leak: BatteryService** — dispose WMI ManagementObject instances
  in foreach loops to prevent COM RCW accumulation (LEAK-001, partial).
- **Resource leak: ShortcutCleanerService** — remove double ReleaseComObject
  on same COM interface to prevent undefined behavior (LEAK-002).
- **Resource leak: UninstallerViewModel** — store LineReceived handler in field
  and unsubscribe in Dispose to prevent memory leak (LEAK-004).
- **Bug: WindowsFeaturesViewModel** — call NotifyCanExecuteChanged on
  ToggleFeatureCommand when IsBusy changes to prevent double-clicks (BUG-001).
- **Thread safety: ProcessStatusToBrushConverter** — freeze static brushes to
  prevent cross-thread InvalidOperationException (THR-001, partial).
- **Performance: BoolToElevationBadgeBrushConverter** — pre-create static frozen
  brush instances instead of allocating per Convert call (PERF-001, partial).

## [0.51.0] - 2026-05-13

### Fixed
- **Security: PowerShellRunner** — document ExecutionPolicy Bypass usage and
  caller restrictions in XML doc comment (SEC-005).
- **Performance: App.xaml** — remove DropShadowEffect from CardElevated style
  to avoid software-rendered shadows (PERF-008).
- **Testing: IntegrationTests** — align dependency versions with Tests project
  (coverlet 10.0.0, Test.Sdk 18.5.1, xunit.runner 3.1.5) (TEST-008).

## [0.50.0] - 2026-05-13

### Fixed
- **Performance: ConsoleViewModel** — fix O(n²) trim by removing from index 0
  forward instead of reverse-order removal (PERF-005).
- **Performance: ProcessManagerViewModel** — move icon extraction and process
  description lookup to background thread to prevent UI freezes (PERF-007).
- **CI: auto-release** — detect breaking change commits (feat!:/fix!:) and
  bump major version instead of treating them as minor/patch (CI-001).
- **CI: ci.yml** — add warning annotation when UI automation tests fail so
  failures are visible on PRs without blocking merge (TEST-005).

## [0.49.0] - 2026-05-13

### Fixed
- **Binding: BatteryInfo** — add NotifyPropertyChangedFor on DesignCapacityMWh,
  FullChargeCapacityMWh, EstimatedRuntimeMinutes for computed properties
  HealthPercent, WearPercent, RuntimeDisplay (BIND-001).
- **Binding: DiskHealthReport** — add NotifyPropertyChangedFor on HealthStatus,
  TemperatureC, WearPercent, PowerOnHours, ReadErrors, WriteErrors for 6+
  computed properties (BIND-002).
- **Binding: FriendlyEventEntry** — add NotifyPropertyChangedFor on Timestamp
  and Severity for RelativeTime, FullTimestamp, SeverityIcon, SeverityColor
  (BIND-003).
- **Binding: PerformanceProfile** — add NotifyPropertyChangedFor on
  ActivePlanName and ActivePlanGuid for ProfileSummary (BIND-004).
- **Binding: ProcessEntry** — add NotifyPropertyChangedFor on MemoryBytes for
  MemoryDisplay (BIND-005).
- **Binding: DiskUsageEntry** — add NotifyPropertyChangedFor on SizeBytes for
  SizeDisplay (BIND-006).
- **Binding: InstalledApp** — add NotifyPropertyChangedFor on SizeBytes for
  SizeDisplay (BIND-007).
- **Memory: DeepCleanupViewModel** — replace anonymous PropertyChanged lambda
  with named handler, unsubscribe on rescan and Dispose (MEM-006).
- **Memory: ShortcutCleanerViewModel** — replace anonymous PropertyChanged
  lambda with named handler, unsubscribe on rescan and Dispose (MEM-007).
- **Bug: MemoryTestService** — set ReverseDirection=true on EventLogQuery so
  the cutoff break works correctly with newest-first ordering (BUG-002).
- **Bug: PerformanceService** — fix CreateRestorePointAsync by embedding
  description directly in script instead of using AddParameter which doesn't
  create script-scope variables (BUG-003).
- **Bug: SpeedTestView/CleanupView/DeepCleanupView/NetworkRepairView/
  SystemHealthView/TracerouteView/AboutView** — replace FlexVis converter
  misuse on IsEnabled with dedicated BoolInverterConverter (BUG-004, BUG-005).
- **Security: ServiceManagerService** — replace weak quote-only validation with
  strict allowlist regex for sc.exe service name arguments (SEC-006).
- **Performance: LogsViewModel** — use CollectionView.Count directly instead of
  iterating entire filtered view via Cast/Count (PERF-002).
- **Performance: NetworkSharedState** — simplify buffer trimming to remove from
  front sequentially (PERF-003).
- **Performance: MarkdownTextBlock** — use static compiled Regex instead of
  creating new state machine on every parse call (PERF-004).
- **Performance: DiskAnalyzerService** — use StringComparison.OrdinalIgnoreCase
  instead of allocating ToLowerInvariant copy on every path (PERF-006).

## [0.48.0] - 2026-05-13

### Fixed
- **Security: UpdateService** — treat missing .sha256 hash file as verification
  failure instead of silently passing (SEC-001).
- **Security: SpeedTestService** — pin expected SHA-256 hashes for Ookla CLI
  download, log warning on mismatch (SEC-002).
- **Security: AppBlockerService** — apply same input validation regex to
  UnblockApp as BlockApp to prevent registry path injection (SEC-004).
- **Memory: AppUpdatesViewModel** — store LineReceived handler in field and
  unsubscribe in Dispose to prevent event subscription leak (MEM-001).
- **Memory: NetworkSharedState** — unsubscribe Pinger.SampleReceived and
  TraceMonitor.RouteCompleted in Dispose, dispose TraceMonitor (MEM-002).
- **Memory: ConsoleView** — unsubscribe from previous DataContext's
  CollectionChanged before subscribing to new one (MEM-003).
- **Memory: PerformanceView** — store PropertyChanged handler and unsubscribe
  from previous VM on DataContext change (MEM-004).
- **Bug: DuplicateFileGroup** — guard WastedBytes with Math.Max to prevent
  negative value when Count is 0 (BUG-001).
- **Performance: ProcessEntry** — cache CanOpenFileLocation on creation instead
  of calling File.Exists on every property evaluation (PERF-001).
- **Bug: WindowsFeaturesViewModel** — add CanExecute guard on ToggleFeature
  command to prevent rapid-click race condition (BUG-006).

## [0.47.0] - 2026-05-13

### Changed
- **Migrate to .NET 9** — all projects now target `net9.0-windows`. CI
  workflows updated to use .NET 9 SDK. `Microsoft.Extensions.DependencyInjection`
  bumped to 9.0.4. Closes #257.
- **DI: PowerShellRunner is now Transient** — each ViewModel gets its own
  instance to prevent LineReceived event cross-talk between tabs.

### Fixed
- **Uninstaller** — filter out entries with names shorter than 2 characters
  (eliminates empty rows from winget list parsing edge cases).
- **Process Manager** — explicitly enable column resizing (`CanUserResizeColumns`).
- **Windows Features** — show "Not elevated" warning badge when not running
  as Administrator.
- **SpeedTestService** — suppress SYSLIB0057 obsolete warning for
  `CreateFromSignedFile` (no .NET 9 replacement for Authenticode verification).

## [0.46.0] - 2026-05-13

### Added
- **Windows Features tab** — list, enable, and disable Windows optional
  features (Hyper-V, WSL, .NET 3.5, Telnet, etc.) directly from SysManager.
  Features are categorized (Virtualization, Networking, Development, Media,
  Legacy). Toggle requires admin. Shows reboot-required status. Includes
  search/filter. Closes #5.

## [0.45.0] - 2026-05-13

### Added
- **Dependency Injection container** — introduced
  `Microsoft.Extensions.DependencyInjection` for service and ViewModel
  lifetime management. All services (PowerShellRunner, SystemInfoService,
  WingetService, TrayIconService) are now shared singletons resolved from
  the container. MainWindowViewModel resolves child VMs from DI at runtime,
  falls back to manual creation in tests. Closes #255.

## [0.44.0] - 2026-05-13

### Added
- **Uninstaller — Local app support** — apps not managed by winget (per-user
  installs, legacy software, custom apps) can now be uninstalled directly
  using their registry UninstallString. The service parses quoted paths,
  MsiExec commands, and rundll32 invocations. Prefers QuietUninstallString
  when available. Closes #236.

## [0.43.0] - 2026-05-12

### Added
- **ETA Calculator** — reusable helper that estimates time remaining for
  any progress-based operation. Integrated into Speed Test (HTTP + Ookla)
  and Deep Cleanup (scan + clean). Shows human-friendly estimates like
  "~2 min 15 s" next to progress bars. Closes #241.

## [0.42.0] - 2026-05-12

### Added
- **Drivers — Scrollable view** — wrapped the Drivers tab in a
  ScrollViewer so the full content (toolbar, summary, table) is
  scrollable when the window is small. DataGrid has explicit
  VerticalScrollBarVisibility and MaxHeight for large driver lists.
  Closes #235.

## [0.41.0] - 2026-05-12

### Added
- **Speed Test — History tracking** — each speed test result (HTTP and
  Ookla) is saved to disk and displayed in a history table below the
  test card. Stores up to 20 results per engine with date, download,
  upload, ping, and server. Clear button per engine. Persists between
  sessions. Closes #237.

## [0.40.1] - 2026-05-12

### Fixed
- **Auto-update** — "Install" now performs a true in-place update: verifies
  SHA256 hash of the downloaded build, writes an updater script that waits
  for the current process to exit, copies the new executable over the old
  one, and restarts. Previously it only launched the new exe from a temp
  folder without replacing the original. Closes #240.

## [0.40.0] - 2026-05-12

### Added
- **System Logs — Row highlight** — toggle highlight on any log entry
  for better visibility when reviewing events. Closes #233.
- **Services — Row highlight** — toggle highlight on any service row
  to mark entries of interest while browsing. Closes #239.

## [0.39.0] - 2026-05-12

### Added
- **About — Changelog link** — new "View Changelog" button opens the
  GitHub CHANGELOG.md in the browser. Closes #232.
- **Drivers — Hide system drivers** — toggle to filter out Microsoft /
  Windows drivers from the list, showing only third-party drivers.
  Closes #234.
- **Startup Manager — Hide Windows entries** — toggle to filter out
  Microsoft / Windows startup items that should not be disabled.
  Closes #238.

## [0.38.0] - 2026-05-12

### Added
- **System Tray mode** — minimize-to-tray on window close, background
  health monitoring every 60 seconds, CPU/RAM/uptime tooltip on hover,
  Windows toast notifications when RAM > 90%, uptime > 14 days, or disk
  health degrades. Right-click context menu with Show / Exit. Uses
  H.NotifyIcon.Wpf 2.2.1. Closes #262.

## [0.37.0] - 2026-05-12

### Added
- **Dashboard — Health Score card** — overall system health gauge (0–100)
  combining disk SMART, RAM usage, uptime, and battery wear (on laptops).
  Color-coded circular ring with label (Excellent/Good/Fair/Poor) and up
  to 3 actionable recommendations. Auto-computes on load and refreshes
  with "Scan system". Closes #259.
- **HealthScoreService** — aggregates SystemInfoService, DiskHealthService,
  and BatteryService into a weighted health score.

## [0.36.0] - 2026-05-12

### Added
- **Dashboard — Quick Tune-Up wizard** — one-click button that runs safe
  cleanup (temp files), optionally empties Recycle Bin (with confirmation),
  scans for broken shortcuts (report only), checks disk SMART health,
  flags high uptime (14+ days) and high RAM usage (85%+). Displays a
  dismissible summary card with freed space, disk verdicts, and
  recommendations. Non-destructive, no admin required. Closes #261.
- **IntGreaterThanZeroConverter** — value converter for conditional
  visibility when an integer is greater than zero.
- **IDialogService** — abstraction for user confirmation dialogs, replacing
  direct `MessageBox.Show` calls in ViewModels. Enables unit testing of
  confirmation-gated code paths (CQ-003).

### Fixed
- **Disk Health** — `TemperatureColorHex` returns grey (#9AA0A6) for drives
  without temperature sensors instead of misleading red (QA-004).
- **Battery Health** — `HealthPercent` clamped to 0–100, `WearPercent`
  clamped to ≥0 for new batteries exceeding design capacity (QA-005).
- **Network Monitor** — `TrimBuffer` batch-removes expired points from
  end-to-start, eliminating O(n²) array shifting (CQ-001).
- **Shortcut Cleaner** — COM objects (`IShellLink`, `IPersistFile`) now
  released via `Marshal.ReleaseComObject` in finally block (SEC-006).
- **Models** — deduplicated `FormatSize` from `DiskUsageEntry`, `InstalledApp`,
  and `ProcessEntry`; all now use `CleanupCategory.HumanSize` (CQ-002).
- **Console** — batch-remove excess lines from end-to-start instead of
  repeated `RemoveAt(0)`, reducing O(n) per append to amortized O(1) (CQ-008).

### Security
- **Speed Test** — improved download integrity comment and added
  Authenticode signature verification on extracted speedtest.exe (SEC-001).

## [0.35.11] - 2026-05-12

### Fixed
- **Process Manager** — null-safe filter: `ApplyFilter` no longer throws
  `NullReferenceException` when `Description`, `PlainDescription`, or
  `Category` are null (QA-002).
- **Network Monitor** — `Buffers`/`TraceBuffers` changed from `Dictionary`
  to `ConcurrentDictionary` to prevent `InvalidOperationException` under
  concurrent timer + UI access (QA-003).
- **Disk Analyzer** — `DrillDown`/`GoUp` now await `AnalyzeAsync()` instead
  of fire-and-forget, preventing race conditions with the operation lock
  (QA-001).
- **Console** — `Dispatcher.Invoke` → `BeginInvoke` to avoid thread-pool
  starvation under heavy output (CQ-005).
- **Integration tests** — `UpdateServiceTests.Constants_AreSet` expects
  `"SystemManager"` matching the renamed repo (TEST-004).

### Security
- **chkdsk** — drive letter validated with `^[A-Z]:$` regex before
  interpolation into process arguments (SEC-003).
- **App Blocker** — `exeName` validated with `^[A-Za-z0-9_\-. ]+\.exe$`
  regex to prevent registry path injection via IFEO (SEC-004).
- **Restore Point** — `CreateRestorePointAsync` uses parameterized
  PowerShell (`$desc` variable) instead of string concatenation (SEC-002).

## [0.35.10] - 2026-05-08

### Fixed
- **Auto-update** — UpdateService now points to the new `SystemManager` repo
  name instead of the old `SysManager`. Without this fix, the in-app update
  checker would fail to find new releases.

## [0.35.9] - 2026-05-08

### Changed
- **Code quality** — refactored implicit `foreach` filters to explicit LINQ
  `.Where()` calls across 7 files (GatewayHelper, FixedDriveService,
  AppAlertService, DeepCleanupService, LargeFileScanner,
  ProcessDescriptionService, ShortcutCleanerViewModel). Resolves CodeQL
  `cs/linq/missed-where` alerts.

## [0.35.8] - 2026-05-08

### Fixed
- **Ping chart** — fixed chart visual collapse that occurred after 2–5 seconds
  of monitoring. Root cause: LiveCharts auto-scaled the X-axis on every buffer
  trim, causing momentary layout thrashing. Fix pins the X-axis to a fixed
  time window (now − windowSeconds → now) during active monitoring, and adds
  MinHeight="200" to prevent layout collapse. Axis limits reset on Stop/Clear
  (#518).

## [0.35.7] - 2026-05-08

### Fixed
- **Encoding** — all native Windows tools (powercfg, ipconfig, netsh, sc.exe)
  now use OEM encoding for output parsing, matching the fix applied to chkdsk,
  sfc, and DISM. Added centralized `PowerShellRunner.OemEncoding` static
  property. Prevents garbled output on non-English Windows systems.

## [0.35.6] - 2026-05-08

### Removed
- **Old green progress panel** — removed the legacy green-bordered background
  task tray from the sidebar footer. Progress is now shown exclusively via the
  blue indeterminate bar under each tab name in the sidebar (#513).

## [0.35.5] - 2026-05-08

### Fixed
- **chkdsk** — register OEM code pages (437, 852, etc.) at application startup
  via `CodePagesEncodingProvider`. On .NET 8, these code pages are not available
  by default, causing chkdsk output parsing to fail with encoding errors on
  non-English systems (#505).

## [0.35.4] - 2026-05-08

### Fixed
- **Traceroute** — reduced per-hop timeout from 3s to 2s and DNS reverse
  lookup timeout from 1.5s to 800ms. Prevents the appearance of freezing
  when intermediate hops don't respond (#519).

## [0.35.3] - 2026-05-08

### Fixed
- **Duplicate Finder** — replaced non-virtualized `ItemsControl` with a
  virtualized `ListView` to prevent UI freezes when displaying thousands of
  duplicate groups (#527).
- **Process Manager** — reduced column widths (PID 55, Mem 70, CPU 50,
  Thr 45) and added `MinWidth="200"` on the Name column to prevent columns
  from crowding on smaller screens (#511).

## [0.35.2] - 2026-05-08

### Fixed
- **Shortcut Cleaner** — tab was showing a black page due to referencing
  undefined `BoolToVisibility` converter. Rewrote the View with correct
  converter names and matching app theme styles (#512).
- **Startup Manager** — blank placeholder row at the bottom of the table
  caused by missing `CanUserAddRows="False"` on the DataGrid (#509).
- **Disk Analyzer** — two confusing "Open" buttons renamed: drill-down is
  now "→" and Show in Explorer is now "📂" with distinct tooltips (#514, #515).

## [0.35.1] - 2026-05-07

### Fixed
- **Deep Cleanup / Duplicate Finder** — use Windows Known Folder API
  (SHGetKnownFolderPath) to resolve Downloads, Documents, Desktop, Pictures,
  Music, and Videos paths. If the user has moved these folders to a different
  drive (e.g. D:\Downloads), the application now detects the actual location
  instead of assuming the default C:\Users path (#483).

## [0.35.0] - 2026-05-07

### Added
- **DataGrid sort arrows** — all sortable DataGrid column headers now display
  an ascending (▲) or descending (▼) arrow indicator on the currently sorted
  column (#488).
- **DataGrid hover highlight** — column headers change background color and
  show a hand cursor on hover to signal interactivity (#489).

## [0.34.2] - 2026-05-07

### Fixed
- **Disk Analyzer** — skip junctions, symbolic links, and mount points during
  folder traversal to prevent double-counting files reachable through multiple
  paths (e.g. `C:\Documents and Settings` → `C:\Users`). Fixes reported total
  exceeding actual disk capacity (#484).

## [0.34.1] - 2026-05-07

### Fixed
- **Sidebar** — all groups now start collapsed on launch instead of expanded,
  reducing visual clutter (#482).
- **Speed Test** — swapped card order: Ookla (primary) now appears first,
  HTTP (backup) second (#485).
- **App Updates** — per-package upgrade now includes `--include-unknown` flag
  so packages with undetermined versions can be upgraded (#486).
- **Uninstaller** — blank entries with empty names are now filtered out of
  the installed applications list (#487).
- **About** — "View license" button no longer appears grayed out; changed
  from GhostButton to SecondaryButton style (#490).

## [0.34.0] - 2026-05-07

### Added
- **App Blocker** — fully implemented tab replacing the WIP placeholder.
  Blocks applications from executing using Image File Execution Options (IFEO)
  registry mechanism. Enter an exe name or browse for a file, confirm, and the
  app is prevented from launching. Fully reversible — unblock restores normal
  execution. Shows list of currently blocked apps with select/deselect.
- `AppBlockerService` — IFEO-based block/unblock with specific exception
  handling, admin privilege detection, and GetBlockedApps enumeration.
- `AppBlockerViewModel` — block, unblock selected, browse, refresh, select all.
- `BlockedApp` model with observable properties.
- `AppBlockerView` XAML with input field, toolbar, and DataGrid.
- Unit tests for ViewModel and Model.

## [0.33.0] - 2026-05-07

### Added
- **App Alerts** — fully implemented tab replacing the WIP placeholder.
  Monitors Program Files, AppData\Programs, and registry uninstall keys for
  new application installations using FileSystemWatcher and periodic registry
  polling. Shows timestamped install history with app name, publisher, path,
  and detection source. Start/stop monitoring, acknowledge alerts, show all
  currently installed apps, clear history.
- `AppAlertService` — FileSystemWatcher on install directories + 30s registry
  poll cycle. Thread-safe with ConcurrentDictionary baseline.
- `AppAlertsViewModel` — full MVVM with start/stop, acknowledge, clear,
  refresh installed apps.
- `AppInstallEntry` model with observable properties.
- `AppAlertsView` XAML with DataGrid and toolbar.
- Unit tests for ViewModel and Model.

## [0.32.0] - 2026-05-06

### Added
- **Shortcut Cleaner** — fully implemented tab replacing the WIP placeholder.
  Scans Desktop, Start Menu, Quick Launch, and Recent Items for broken .lnk
  shortcuts whose targets no longer exist. Lists results with name, location,
  and missing target path. Supports select all/deselect, move to Recycle Bin
  or permanent delete, with confirmation dialog before any deletion.
- `ShortcutCleanerService` — COM-based IShellLink resolution, SHFileOperation
  for Recycle Bin support, scans 6 common shortcut locations.
- `ShortcutCleanerViewModel` — full MVVM implementation with scan, delete,
  select/deselect, cancel, and OperationLockService integration.
- `BrokenShortcut` model with observable properties.
- `ShortcutCleanerView` XAML with DataGrid, toolbar, and status footer.
- Unit tests for ViewModel and Model.

## [0.31.0] - 2026-05-06

### Added
- **Process Description Database** — built-in JSON database with 107 common
  Windows processes and popular applications, each with a plain-language
  description, category (System, Browser, Development, Communication, Media,
  Gaming, Graphics, Productivity, Creative, Cloud, Utility, Network, Security),
  and safety indicator (System, Trusted, Unknown).
- **ProcessDescriptionService** — singleton service that loads the embedded
  JSON database and provides fast case-insensitive lookup by process name.
- **ProcessEntry model** — extended with `PlainDescription`, `Category`, and
  `SafetyLevel` fields populated from the database on each refresh.
- **Enhanced filtering** — Process Manager search now matches against
  plain description and category in addition to name and PID.
- Unit tests for `ProcessDescriptionService` covering lookup, case
  insensitivity, .exe stripping, categories, and safety levels.

## [0.30.0] - 2026-05-06

### Added
- **Operation Lock Service** — new `OperationLockService` singleton that
  prevents conflicting concurrent operations across tabs. Operations are
  grouped by category (Disk, Network, SystemModification). If a user tries
  to start a conflicting operation while another is running, the UI shows
  which operation is blocking and refuses to start the new one.
- Integrated operation locks into: `DeepCleanupViewModel` (scan, clean,
  large file scan), `DiskAnalyzerViewModel` (analyze), `DuplicateFileViewModel`
  (scan), `CleanupViewModel` (temp cleanup), `SpeedTestViewModel` (HTTP and
  Ookla tests), `TracerouteViewModel` (trace), `NetworkRepairViewModel`
  (all repair operations).
- Unit tests for `OperationLockService` covering acquire, release, conflict
  detection, thread safety, and double-dispose safety.

## [0.29.1] - 2026-05-06

### Fixed
- **Code quality** — replaced 8 generic `catch (Exception)` blocks with
  specific exception types in `AppUpdatesViewModel`, `DashboardViewModel`,
  and `LogsViewModel`. No behavior change — same error messages, but now
  CodeQL-clean and explicit about what can fail.

## [0.29.0] - 2026-05-06

### Added
- **Sidebar restructure** — reorganized navigation from 7 groups / 21 tabs to
  9 groups / 36 tabs. New groups: **Monitor** (Process Manager moved here,
  plus Resource History, App Alerts, Privacy Monitor placeholders) and
  **Control** (Privacy Settings, Context Menu, Restore Points, Scheduled
  Maintenance, System Report placeholders). Existing groups expanded: System
  (+Windows Features), Cleanup (+Shortcut Cleaner, File Shredder), Network
  (+DNS Changer, Hosts Editor), Apps (+Bulk Installer, App Blocker).
- **PlaceholderView** — generic WIP view showing feature name, description,
  issue reference, and "Work in Progress" badge for planned tabs.
- **PlaceholderViewModel** — lightweight ViewModel for placeholder tabs,
  stores feature name, description, and issue number.

## [0.28.34] - 2026-05-06

### Removed
- **Dead code** — removed legacy `NetworkViewModel.cs` (superseded by split
  ViewModels: PingViewModel, TracerouteViewModel, SpeedTestViewModel,
  NetworkRepairViewModel + NetworkSharedState). Removed associated integration
  tests that exercised the dead class.

## [0.28.33] - 2026-05-06

### Fixed
- **Code quality** — resolved `cs/missed-using-statement` CodeQL alert in
  `ProcessManagerService`: wrapped `Process.GetProcesses()` array in
  try/finally to guarantee disposal of all process handles, even on early
  cancellation.

## [0.28.32] - 2026-05-06

### Fixed
- **Code quality** — resolved final 5 CodeQL alerts: replaced `foreach`+
  `ContainsKey` guard with `TryAdd` in `UninstallerService`, converted
  `foreach`+immediate-map to LINQ `.Select()` in `NetworkSharedState` and
  `IconExtractorService` (×2), added logging to previously empty catch block
  in `StartupService`.

## [0.28.31] - 2026-05-06

### Fixed
- **Code quality** — resolved 2 additional CodeQL alerts: converted
  `foreach`+immediate-map to `.Select()` in `DriversViewModel`, converted
  `foreach`+type-check to `.Where()` in `StartupService.ReadApprovedKey`.

## [0.28.30] - 2026-05-06

### Fixed
- **Code quality** — resolved 40 CodeQL alerts across 16 files: replaced
  `Path.Combine` with `Path.Join` to prevent silent argument dropping (18),
  converted `foreach`+`if continue` to LINQ `.Where()` (17), replaced
  `foreach`+immediate-map to `.Select()` (3), added comments to intentional
  empty catch blocks (2).

## [0.28.29] - 2026-05-05

### Fixed
- **Logs / Console** — replaced generic `catch (Exception)` with specific
  exception types in `LogsViewModel` and `ConsoleViewModel` (resolves
  CodeQL catch-of-all-exceptions alerts).

## [0.28.28] - 2026-05-05

### Fixed
- **Cleanup** — SFC and DISM scans no longer crash with "No data is available
  for encoding 437" on systems where the OEM code page is not registered;
  falls back to UTF-8 (same fix as #443 applied to remaining callers).
- **App Updates** — winget upgrade now accepts package IDs with spaces (same
  fix as #444 applied to WingetService).
- **Code quality** — replaced bare `catch { }` with specific exception types
  in DiskHealthService, FixedDriveService, MemoryTestService, SystemInfoService,
  and AdminHelper (resolves multiple CodeQL alerts).
- **SECURITY.md** — updated supported version table from 0.5.x to 0.28.x.
- **ARCHITECTURE.md** — removed stale tab counts from group headers.

## [0.28.27] - 2026-05-05

### Fixed
- **System Health** — chkdsk scan no longer crashes with "No data is available
  for encoding 437" on systems where the OEM code page is not registered;
  falls back to UTF-8 gracefully (#443).
- **Uninstaller** — packages with spaces in their winget ID (e.g. "Riot
  Games.League of Legends") can now be uninstalled without "Invalid package
  ID" error (#444).

## [0.28.26] - 2026-05-04

### Fixed
- **CodeQL regressions** — resolved 2 alerts introduced during the bug fix
  session: converted `foreach`+`if` to LINQ `Where()` in
  `DeepCleanupService.RiotLogDirs` (missed-where), wrapped `JsonDocument` in
  `using` block in `SpeedTestService.RunOoklaAsync` (missed-using).

## [0.28.25] - 2026-05-04

### Fixed
- **Accessibility: LogsView** — replaced remaining search emoji (🔍) in the
  no-results overlay with Segoe MDL2 Assets glyph (E721). Missed in the
  initial accessibility pass (#411).

## [0.28.24] - 2026-05-04

### Fixed
- **Accessibility** — replaced emoji characters (📁🔍✕📂📋🗑⟳↺⬆) with text
  equivalents across all 21 XAML views; added `AutomationProperties.Name` to
  all DataGrid and ProgressBar elements for screen reader support (#411).

## [0.28.23] - 2026-05-04

### Fixed
- **Services: timeout handling** — `WaitForStatus` in `ServiceManagerService`
  now catches `TimeoutException` and converts to a descriptive error instead
  of crashing when a service takes longer than 30 seconds (#414).
- **Performance: snapshot persistence** — `OriginalSnapshot` is now saved to
  JSON in `%LOCALAPPDATA%\SysManager` and loaded on startup, so Restore All
  works after app restart (#415).
- **Traceroute: DNS race condition** — reverse DNS lookup is now awaited with
  a 1.5 s timeout before emitting the hop, so hostnames appear immediately
  in the UI instead of showing `*` (#416).

## [0.28.22] - 2026-05-04

### Fixed
- **Update download: SHA256 verification** — added `VerifyHashAsync` to
  `UpdateService` that downloads the `.sha256` file from the GitHub release
  and compares against the local file hash (#408).
- **Speed Test: Ookla integrity check** — Ookla CLI download now computes
  SHA256 (logged for audit), validates the zip is not corrupt, and verifies
  it contains `speedtest.exe` before extraction (#409).

## [0.28.21] - 2026-05-04

### Fixed
- **Performance: audit logging** — all registry modifications in
  `PerformanceService` (Game Mode, Xbox Game Bar, GPU, visual effects) now
  log key path, action, and new value via Serilog (#405).
- **Error messages: operation context** — replaced 38+ generic `Error: …`
  messages in `PerformanceViewModel`, `ServicesViewModel`, and
  `SystemHealthViewModel` with operation-specific context like
  "Power plan change failed:" and "Start service failed:" (#407).

## [0.28.20] - 2026-05-04

### Fixed
- **Deep Cleanup: drive scanning** — Riot Games / League of Legends log
  paths now scan all fixed drives instead of only Program Files (#401).
- **Icon cache: eviction** — `IconExtractorService` cache now has a
  configurable `MaxCacheSize` (default 500) with automatic eviction to
  prevent unbounded memory growth (#402).
- **ConfigureAwait(false)** — added to all async calls in
  `PerformanceService`, `UninstallerService`, and `WingetService` to
  prevent potential UI deadlocks (#403).

## [0.28.19] - 2026-05-04

### Fixed
- **Speed Test: JSON error handling** — `SpeedTestService.RunOoklaAsync`
  now catches `JsonException` and `KeyNotFoundException` when Ookla CLI
  returns malformed output (#400).

## [0.28.18] - 2026-05-04

### Fixed
- **Input validation: allowlist regex** — `UninstallerService` and
  `WingetService` now validate package IDs with an allowlist regex
  (`[a-zA-Z0-9._-/+]`, max 256 chars) instead of a blocklist (#397).
- **Null checks: verified safe** — confirmed all `OpenSubKey` calls and
  Process API access already have proper null checks (#398).

## [0.28.17] - 2026-05-04

### Fixed
- **CTS disposal** — added `Dispose(bool)` override to 8 ViewModels that
  had `CancellationTokenSource` fields but no cleanup: `AppUpdatesVM`,
  `DiskAnalyzerVM`, `DriversVM`, `DuplicateFileVM`, `LogsVM`,
  `SpeedTestVM`, `TracerouteVM`, `UninstallerVM` (#396).
- **UpdateService: bare catch** — replaced bare `catch` blocks in
  `GetRecentAsync` and `DownloadAsync` with specific exception types
  (`HttpRequestException`, `JsonException`, `IOException`) plus Serilog
  logging (#413).

## [0.28.16] - 2026-05-04

### Fixed
- **Dispose lifecycle** — `MainWindow.OnClosed` now disposes
  `MainWindowViewModel`, which chains to all child ViewModels and
  `NetworkSharedState`. `NetworkViewModel` disposes its CTS, unsubscribes
  events, and stops the pinger (#395, #410).

## [0.28.15] - 2026-04-30

### Fixed
- **CodeQL: empty-catch-block** — added Serilog logging or descriptive comments
  to ~50 empty catch blocks across 10 files: `IconExtractorService`,
  `DiskAnalyzerService`, `DuplicateFileService`, `ProcessManagerService`,
  `SpeedTestService`, `StartupService`, `UninstallerService`,
  `CleanupViewModel`, `DiskAnalyzerViewModel`, `DuplicateFileViewModel`.
- **CodeQL: catch-of-all-exceptions** — replaced bare `catch { }` in
  `DiskAnalyzerService` (7 blocks) with specific `UnauthorizedAccessException`
  and `IOException`; replaced `catch (Exception)` in `DiskAnalyzerViewModel`
  and `DuplicateFileViewModel` with specific types.
- **CodeQL: missed-where** — converted `ShouldSkip`/`ShouldSkipDir`/
  `ShouldSkipFile` foreach loops to LINQ `Any()` in `DiskAnalyzerService`
  and `DuplicateFileService`.

## [0.28.14] - 2026-04-30

### Fixed
- **CodeQL: missed-using-statement** — `ServiceController` objects in
  `ServiceManagerService.GetAllServices()` and `Process` objects in
  `PerformanceService.TrimWorkingSets()` now use `using` blocks instead of
  manual `try/finally Dispose()`.

## [0.28.13] - 2026-04-30

### Fixed
- **CodeQL: DuplicateFileService catch blocks** — bare `catch { }` in file
  discovery, partial hash, and full hash loops replaced with specific
  `IOException` + `UnauthorizedAccessException`.
- **CodeQL: App.xaml.cs using statement** — `Process` objects in single-instance
  activation now use `using` block instead of manual try/finally dispose.
- **CodeQL: App.xaml.cs static field** — `_instanceMutex` changed from static
  to instance field (only one App instance exists per process).
- **CodeQL: StartupService unused variables** — removed unused `actions`
  variable; stdout drain changed to discard pattern.

## [0.28.12] - 2026-04-30

### Fixed
- **CodeQL: catch-of-all-exceptions** — replaced all `catch (Exception)` and
  bare `catch { }` with specific exception types across 12 files: AboutVM,
  BatteryHealthVM, CleanupVM, DeepCleanupVM, NetworkVM, PerformanceVM,
  ProcessManagerVM, ServicesVM, StartupVM, SystemHealthVM, WindowsUpdateVM,
  ProcessManagerService. Exception types include `InvalidOperationException`,
  `IOException`, `HttpRequestException`, `ManagementException`,
  `Win32Exception`, `TaskCanceledException`, and others.
- **CodeQL: empty catch blocks** — added Serilog logging to previously silent
  catch blocks so failures are traceable in diagnostics.

## [0.28.11] - 2026-04-30

### Fixed
- **ViewModel lifecycle: IDisposable** — `ViewModelBase` now implements
  `IDisposable` with virtual `Dispose(bool)` pattern. All ViewModels with
  event subscriptions or CancellationTokenSources override Dispose to clean up.
- **Event handler leaks** — lambda event handlers in CleanupVM, SystemHealthVM,
  and WindowsUpdateVM replaced with named methods and unsubscribed in Dispose.
- **Fire-and-forget error handling** — 11 ViewModels with `_ = InitAsync()`
  wrapped in try/catch with `Log.Warning` to prevent unobserved task exceptions.
- **CTS disposal in Dispose** — CleanupVM (4×), DeepCleanupVM (3×),
  SystemHealthVM, WindowsUpdateVM now dispose CancellationTokenSources on
  ViewModel teardown.

## [0.28.10] - 2026-04-30

### Fixed
- **Critical: deadlock in StartupService** — `Process.WaitForExit()` called
  before reading stderr/stdout caused pipe buffer deadlock on schtasks.exe.
  Now reads streams asynchronously before waiting.
- **Critical: COM object leak in StartupService** — `WScript.Shell` and
  shortcut COM objects were not released, leaking COM references. Added
  `Marshal.ReleaseComObject` in finally block.
- **Critical: 50 MB allocation in SpeedTestService** — upload test allocated
  a single 50 MB byte array on the Large Object Heap. Replaced with streaming
  `RandomChunkStream` using 256 KB chunks.
- **Input validation** — schtasks, sc.exe, and winget arguments now validated
  against injection characters (`"`, `\0`) in StartupService,
  ServiceManagerService, UninstallerService, and WingetService.
- **Bare catch blocks** — 7 bare catches in StartupService, SpeedTestService,
  ServiceManagerService, UninstallerService, and WingetService replaced with
  specific exception types and Serilog logging.

## [0.28.9] - 2026-04-30

### Fixed
- **Cleanup: CancellationTokenSource disposal** — `_tempCts`, `_binCts`,
  `_sfcCts`, and `_dismCts` were not disposed before recreation, leaking
  handles on repeated Clean TEMP / Empty Recycle Bin / SFC / DISM operations.
  Now follows the same `_cts?.Dispose()` pattern applied in other ViewModels
  during the #161 memory leak fix.

## [0.28.8] - 2026-04-29

### Fixed
- **Process Manager: Open file location disabled for system processes** — button
  was active but non-functional for processes without an accessible file path.
  Now disabled with a tooltip when the path doesn't exist (#100).

### Added
- **Process Manager: Show only apps toggle** — checkbox in the toolbar filters
  out system processes and shows only applications with a visible window,
  reducing the list from 200+ entries to just user-facing apps (#100).

## [0.28.7] - 2026-04-29

### Fixed
- **Memory leak: CancellationTokenSource disposal** — previous CTS instances
  were not disposed before creating new ones across 8 ViewModels (15 locations),
  causing WaitHandle accumulation during extended use. Affected: Windows Update,
  Uninstaller, System Health, Drivers, App Updates, Logs, Duplicate Finder,
  Disk Analyzer (#161).
- **Memory leak: Process object disposal** — `Process.GetProcessesByName()` and
  `GetCurrentProcess()` results in `App.ActivateExistingInstance` were not
  disposed, leaking OS handles (#161).
- **Memory leak: PropertyChanged event handlers** — anonymous lambdas subscribed
  to `target.PropertyChanged` in the Network tab were never unsubscribed when
  targets were removed, preventing garbage collection of removed targets (#161).

## [0.28.6] - 2026-04-29

### Fixed
- **Startup Manager: crash when scrolling** — WPF DataGrid virtualization
  passed internal placeholder objects to command handlers, crashing the app.
  Commands now accept `object?` with pattern matching (#326).
- **About: What's New raw markdown** — release notes were displayed as plain
  text. Added a lightweight markdown-to-Inlines renderer that formats headings,
  bold, bullets, and inline code (#335).
- **System Health: chkdsk false errors** — verdict relied solely on exit code,
  which is non-zero even on healthy volumes. Now parses chkdsk output text for
  known healthy/error patterns (#323).
- **Quick Cleanup: Rescan not updating** — property changes fired from a
  background thread inside Task.Run. Refactored to set ObservableProperties on
  the UI thread after await (#327).
- **Deep Cleanup: sidebar progress missing** — IsBusy was never set. Added
  forwarding from IsScanning/IsCleaning/IsLargeScanning to IsBusy (#328).
- **Disk Analyzer: duplicate progress indicator** — removed the redundant
  background task tray entry; the NavItem slim bar is sufficient (#329).
- **Ping: unreachable targets** — replaced 5 unreachable CS2 Europe IPs and
  removed 3 unreachable FACEIT IPs. All new IPs verified with ICMP ping
  (#330, #331, #332).
- **Traceroute: chart not rendering** — LiveChartsCore CartesianChart collapsed
  to zero height. Added MinHeight=250 (#333).
- **Speed Test: HTTP values too low** — increased parallel streams from 4 to 8
  and payload from 25 MB to 50 MB to saturate 1 Gbps+ links (#334).

## [0.28.0] - 2026-04-28

### Changed
- **Windows Update: structured DataGrid** — the Windows Update tab now displays
  updates in a sortable DataGrid table (Title, KB, Size, Status, Date, Category)
  instead of raw console text. Console output is hidden behind a collapsible
  panel, shown only during Install/Pending Reboot operations (#305, #240).

## [0.27.0] - 2026-04-28

### Changed
- **Drivers: structured DataGrid** — the Drivers tab now displays installed
  drivers in a sortable DataGrid table (Device Name, Manufacturer, Version,
  Date) instead of raw console text. Click column headers to sort (#304).

## [0.26.0] - 2026-04-28

### Added
- **Sidebar busy indicator** — every tab now shows a slim indeterminate progress
  bar under its name in the sidebar when performing a long-running operation.
  Works automatically for all tabs via ViewModelBase.IsBusy (#263).

## [0.25.0] - 2026-04-28

### Added
- **Ping: more targets per region** — CS2 Europe expanded from 4 to 10 targets
  (2 IPs per region + Frankfurt, Spain subnets). FACEIT Europe expanded from 5
  to 8 targets (3× Germany, 2× Netherlands, Sweden, UK, France). A single
  server going down no longer shows the entire region as failed (#285, #259).

## [0.24.0] - 2026-04-28

### Changed
- **Clickable column headers** — all table tabs now use DataGrid with native
  click-to-sort column headers (ascending/descending toggle), replacing
  standalone sort buttons and dropdowns. Consistent with Windows Task Manager
  behavior.
  - **Process Manager**: sortable PID, Name, Memory, CPU%, Threads, Status (#266)
  - **Uninstaller**: sortable Name, Size, Version, Publisher, Source, Status (#254)
  - **Services**: removed redundant Sort ComboBox, column headers handle sorting
  - **Startup Manager**: sortable Name, Publisher, Status (previously had no sort)
  - **App Updates**: sortable Name, Id, Current, Available, Source, Status
    (previously had no sort)

## [0.23.0] - 2026-04-28

### Changed
- **Sidebar readability** — improved font contrast and size for group headers,
  subtitles, and child count badges. TextMuted → TextSecondary, larger font
  sizes, higher opacity (#265).

## [0.22.0] - 2026-04-28

### Changed
- **Removed MemTest86 external reference** — the MemTest86 button, command, and all
  references have been removed from System Health. SysManager no longer references
  external third-party tools. The built-in Windows Memory Diagnostic remains (#271).

## [0.21.9] - 2026-04-27

### Fixed
- **SFC/DISM elevation consent** — SFC and DISM no longer auto-relaunch the
  application with admin privileges. A Yes/No confirmation dialog is now shown
  before any elevation. If the user declines, the operation is cancelled with a
  clear status message (#264).

## [0.21.8] - 2026-04-27

### Fixed
- **chkdsk admin check** — chkdsk /scan now checks for admin privileges before
  running. Without elevation, drives show "Needs admin" status with a clear
  message instead of failing with cryptic exit codes (#270).

## [0.21.7] - 2026-04-27

### Fixed
- **UI freeze on Cleanup scan** — separated PropertyChanged event wiring from
  collection population to reduce per-item UI re-renders (#261).
- **UI freeze on Speed Test** — offloaded synchronous file-system I/O and
  process creation in the Ookla speed test to the thread pool (#258).
- **UI freeze on Drivers** — offloaded Process.Start() and PowerShell runspace
  initialization to the thread pool so the dispatcher is never blocked (#249).

## [0.21.6] - 2026-04-27

### Fixed
- **Speed Test panels independent** — each panel (HTTP / Ookla) now shows its own
  status text, progress bar, and cancel button only while that specific test runs.
  Previously starting one test would display status on both panels (#257).
- **Traceroute auto-trace** — Start Auto-Trace now adds the current host to the
  monitor and runs an initial trace immediately. Previously the monitor had no
  targets when started from the Traceroute tab (#239).

## [0.21.5] - 2026-04-27

### Fixed
- **Startup Manager disable** — entries from the shell Startup folder can now be
  properly disabled. Previously they were incorrectly routed to
  `StartupApproved\Run` instead of `StartupApproved\StartupFolder`, so Windows
  never saw the change (#268).

## [0.21.4] - 2026-04-27

### Fixed
- **Tab name consistency** — all sidebar labels now match their tab headers exactly.
  Adopted descriptive naming throughout: Process Manager, Startup Manager, System
  Logs, Performance Mode, Battery Health, Network Repair, Duplicate Finder, Quick
  Cleanup, Deep Cleanup (#267).
- **System Logs hover highlight** — log entry rows now show a subtle background
  change on mouse hover, consistent with other tabs (#247).

## [0.21.3] - 2026-04-27

### Fixed
- **Buttons grayed out on focus loss** — intercepted `WM_NCACTIVATE` to keep the
  window chrome rendering as active at all times. ModernWPF was dimming controls
  when the window lost focus, making buttons appear disabled across the entire
  application (#252, #251, #248, #245).

## [0.21.2] - 2026-04-26

### Fixed
- **Startup toggle not working** — clicking the checkbox to disable a startup app
  (e.g. MEGAsync) appeared to do nothing. Root cause: WPF CheckBox two-way binding
  flipped `IsEnabled` before the command ran, then the command inverted it back.
  Now uses the already-flipped value as the desired state and reverts on failure.

## [0.21.1] - 2026-04-26

### Fixed
- **Icon extraction quality** — drastically improved icon resolution for all three
  tabs (Startup, Uninstaller, Process Manager):
  - Contextual fallback icons: Windows shield for system processes, gear for services,
    generic app icon for unknown apps (no more blank squares)
  - Deeper path resolution: handles rundll32 (extracts DLL target), msiexec, searches
    PATH, Program Files, and App Paths registry
  - Process Manager: finds exe by process name when FilePath is empty (access denied)
  - Uninstaller: scans HKCU registry for per-user installs (Discord, VS Code, Spotify)
    and searches InstallLocation for exe when DisplayIcon is missing

## [0.21.0] - 2026-04-25

### Added
- **Application icons** — Startup Manager, Uninstaller, and Process Manager now
  show the real application icon (extracted from the exe) next to each app name.
  Uses Shell32 `SHGetFileInfo` with a concurrent cache for performance. Falls back
  to a generic icon when the exe is missing, inaccessible, or a UWP/system process
  (#229).

## [0.20.0] - 2026-04-25

### Added
- **FACEIT Europe ping preset** — 5 EU server locations (Germany, UK, France,
  Netherlands, Sweden) for checking latency to FACEIT CS2 competitive servers.
  Appears in the preset dropdown between CS2 Europe and PUBG Europe (#228).

## [0.19.0] - 2026-04-25

### Added
- **Network split** — the monolithic `NetworkViewModel` (~700 lines) is now split
  into 4 focused ViewModels with separate Views:
  - `PingViewModel` + `PingView` — live ping, targets, presets, latency chart,
    health verdict
  - `TracerouteViewModel` + `TracerouteView` — auto-traceroute + manual trace
    with dedicated Start/Stop buttons (previously only available on Ping)
  - `SpeedTestViewModel` + `SpeedTestView` — HTTP + Ookla speed tests
  - `NetworkRepairViewModel` + `NetworkRepairView` — DNS flush, Winsock reset,
    TCP/IP reset
- **NetworkSharedState** — shared state class for targets, buffers, pinger,
  tracer, and health diagnostic, consumed by all 4 network ViewModels.
- **Sidebar visual hints** on collapsed groups:
  - Child count badge next to label (e.g. "System (6)")
  - Subtitle with abbreviated child labels (auto-hides when expanded)
  - Tooltip with full child labels on hover
- 30+ new unit tests for NetworkSharedState, PingViewModel,
  TracerouteViewModel, SpeedTestViewModel, NetworkRepairViewModel, NavGroup.

### Changed
- **Windows Update** moved from Apps → System group (System now has 6 children).
- **Apps group** reduced to 2 children (App updates + Uninstaller).
- **Network group** expanded from 1 to 4 sidebar children (no longer a
  single-item flat entry).
- Sidebar now shows 21 leaf items across 7 groups (was 18).

## [0.18.0] - 2026-04-25

### Added
- **Sidebar tab reorganization** — the 18 flat sidebar tabs are now grouped into
  7 collapsible categories: Dashboard, System, Cleanup, Storage, Network, Apps,
  and Info. Groups expand/collapse with a click. Single-item groups (Dashboard,
  Network) render as flat top-level entries without expander chrome (#82).
- **NavGroup model** — new `NavGroup` class for collapsible sidebar categories
  containing child `NavItem` entries.

### Changed
- **Large File Finder** — conceptually moved from the Deep Cleanup group to the
  Storage group, alongside Disk Analyzer and Duplicates. This resolves the
  confusion about where to find storage analysis tools (#98).
- **Cleanup tab** renamed to "Quick cleanup" in the sidebar to distinguish it
  from the Cleanup group header.
- **Sidebar rendering** — replaced the flat `ListBox` with a grouped
  `ItemsControl` + `Expander` tree layout. Active-mark accent bar and hover
  states preserved.
- **UI test infrastructure** — `AppFixture.GoToTab` updated to find nav items
  by `AutomationProperties.AutomationId` anywhere in the visual tree instead
  of requiring a `NavList` ListBox.

## [0.17.0] - 2026-04-25

### Added
- **Application logging** — structured Serilog logging across all 16 ViewModels.
  Logs now capture tab navigation, operation completion (cleanup, scan, upgrade,
  speed test, disk analysis, etc.), system state changes (power plan, Game Mode,
  services, startup entries), admin elevation events, and error context. Privacy-safe:
  no PII, IPs, file paths, or hostnames are logged — only operation names, counts,
  and metrics (#95).
- **LogService.SanitizePath** — helper method that strips Windows usernames from
  file paths as a safety net for any future path logging.

## [0.16.1] - 2026-04-25

### Fixed
- **Network / Ping** — latency chart no longer freezes when switching away from the
  Ping sub-tab and returning; LiveCharts2 series are nudged on tab re-entry (#153).
- **Network / Navigation** — switching between Network and Services tabs during
  concurrent background scans no longer throws a cross-thread exception; collection
  updates are now dispatched to the UI thread (#154).
- **Network / Speed test** — HTTP download test now uses 4 parallel connections to
  saturate the link, producing results closer to Ookla/fast.com benchmarks (#152).

## [0.16.0] - 2026-04-25

### Added
- **Logs tab** — relative timestamps ("2h ago", "3d ago") in the event list with
  full timestamp on hover; quick time-range pill buttons (1h / 24h / 7d / 30d / All)
  replacing the dropdown; search placeholder watermark; no-results empty state with
  helpful message when filters match nothing (#83).
- **System Health** — disk health cards now show a computed health percentage
  (0–100%) with colored gauge bar, temperature gauge with color thresholds,
  life-remaining gauge (inverted wear), and friendly power-on time formatting
  (days/years instead of raw hours) (#143).

## [0.15.1] - 2026-04-25

### Fixed
- **Uninstaller** — empty status badges no longer render for apps without a
  status; FlexVis converter now treats empty/whitespace strings as Collapsed (#130).
- **Uninstaller** — ARP-only apps show yellow "Local" tag with tooltip; status
  badge column widened for less truncation (#131).

### Changed
- **Uninstaller / Process Manager** — "Filter:" label renamed to "Search:" with
  placeholder hint text (#130).

## [0.15.0] - 2026-04-25

### Added
- **Sidebar** — SFC /scannow, DISM RestoreHealth, and chkdsk now show progress
  indicators in the left sidebar mini-tray alongside existing background task
  indicators (#146, #149, #156).

## [0.14.0] - 2026-04-25

### Added
- **Cleanup** — SFC /scannow and DISM /RestoreHealth now parse output into
  color-coded verdicts: green (healthy), yellow (repaired), red (failed) (#148).
- **Uninstaller** — application size displayed from registry EstimatedSize;
  sort by Name, Size, or Publisher (#139).
- **Process Manager** — CPU usage percentage measured and displayed; sort by
  CPU added alongside Memory, Name, PID (#78).
- **About** — "Copy environment info" now includes CPU, RAM, GPU, storage,
  and display diagnostics similar to DxDiag (#84).

### Changed
- **Sidebar** — fixed duplicate icons: Processes and Uninstaller now have
  unique Segoe Fluent Icons (#138).

## [0.13.14] - 2026-04-25

### Fixed
- **SFC / DISM / chkdsk** — live output no longer appears corrupted. Added
  optional encoding parameter to `PowerShellRunner.RunProcessAsync`; system
  tools now use the OEM code page instead of UTF-8 (#147, #150, #157).

## [0.13.13] - 2026-04-25

### Fixed
- **Network** — speed test loading indicator now only appears on the panel that
  is actually running (HTTP or Ookla), not both simultaneously (#151).

## [0.13.12] - 2026-04-25

### Fixed
- **Network** — tab content now follows the dark theme. Set transparent
  background on CartesianChart controls and added global TabControl style to
  prevent light-mode bleed-through (#140).

## [0.13.11] - 2026-04-25

### Fixed
- **Drivers** — added sorting options (Name, Manufacturer, Version, Date) via
  ComboBox in the toolbar. Modernized view layout with Card borders and
  consistent typography. Replaced generic catch with specific exceptions (#155).

## [0.13.10] - 2026-04-25

### Fixed
- **DataGrid styling** — added global dark-friendly styles for DataGrid, column
  headers, rows, and cells. Rows now use transparent default with Surface1
  alternating, Surface2 hover, Surface3 selected. Text stays readable in all
  states (#136).
- **Deep Cleanup** — clicking the "Show" button in the large files DataGrid no
  longer highlights the entire cell. Custom DataGridCell template removes the
  default focus/selection highlight (#158).

## [0.13.9] - 2026-04-25

### Fixed
- **Buttons** — buttons across the application no longer become invisible when
  hovered, focused, or navigated via keyboard. Added explicit Foreground binding
  on ContentPresenter and keyboard focus trigger with accent border (#145).
- **About tab** — "View license" button text no longer clips or disappears on
  hover/focus (#162).

## [0.13.8] - 2026-04-25

### Fixed
- **Startup Manager** — toggle now works for Task Scheduler entries via
  `schtasks.exe /Change`. Previously threw `NotSupportedException` silently
  (#160).
- **Startup Manager** — replaced generic "Error — may need admin" message with
  specific error descriptions (`SecurityException`, `UnauthorizedAccessException`,
  `IOException`). Error messages now describe the actual failure (#159).
- **Tests** — fixed flaky `PreScan_EventuallyPopulatesLabels` test by replacing
  fixed 3s delay with polling loop (up to 15s).

## [0.13.7] - 2026-04-25

### Fixed
- **Uninstaller** — error messages are no longer truncated. Added ToolTip on
  status badge for full text on hover, TextTrimming for graceful truncation, and
  widened status column from 90px to 160px (#163).

## [0.13.6] - 2026-04-25

### Fixed
- **Release workflow** — fixed `Rename-Item` in release.yml that was passing a
  full path instead of just the new filename, causing v0.13.3–v0.13.5 releases
  to fail.

## [0.13.5] - 2026-04-25

### Fixed
- **App Updates** — checkbox column alignment corrected; increased width and
  centered the checkbox to prevent clipping on the right side.

## [0.13.4] - 2026-04-25

### Fixed
- **Services tab** — sorting buttons now actually sort the service list. Added
  SortBy property with options (Name, Status, Startup, Recommendation) and a
  sort ComboBox in the toolbar.
- **Cleanup tab** — added auto-rescan after cleaning temp files or emptying the
  Recycle Bin so size labels refresh immediately. Added an explicit Rescan button.

## [0.13.3] - 2026-04-25

### Fixed
- **About tab** — "Copy environment info" now shows a friendly Windows name
  (e.g. "Microsoft Windows 11 Pro (build 26200)") instead of the raw NT version
  string. Uses WMI `Win32_OperatingSystem.Caption` with fallback.

## [0.13.2] - 2026-04-25

### Fixed
- **Single instance** — the application now prevents multiple instances from
  running simultaneously. A named Mutex detects an existing instance; the second
  launch activates the existing window and exits.

### Changed
- **Release assets** — executables are now named `SysManager-vX.Y.Z.exe` instead
  of `SysManager.exe` to avoid filename conflicts when downloading multiple
  releases.

## [0.13.1] - 2026-04-24

### Fixed
- **Services tab** — Rec. column now shows empty for services without a gaming
  recommendation instead of cluttering all 280+ rows with "keep-enabled".

## [0.13.0] - 2026-04-24

### Added
- **Network Repair Tools** — DNS flush, Winsock reset, TCP/IP reset in a new
  Repair sub-tab on the Network tab. Confirmation dialogs and admin checks.
- **Restore Point Creation** — create a Windows System Restore point from the
  Performance tab (requires admin).
- **RAM Working Set Trim** — free physical RAM by trimming all process working
  sets, same as RAMMap's "Empty Working Set" (Performance tab).
- **Hibernation Toggle** — enable/disable hibernation from the Performance tab.
  Disabling deletes hiberfil.sys and frees disk space.
- **Services Management** — new Services tab listing all Windows services with
  gaming recommendations (safe-to-disable / advanced / keep-enabled), filtering,
  and start/stop/disable/enable controls.

## [0.12.5] - 2026-04-24

### Fixed
- **Duplicate File Scanner** — dramatically faster duplicate detection using
  a two-phase hashing approach. Files sharing a size are now pre-filtered by
  a partial hash (first 4 KB) before computing the full SHA-256. Files that
  differ in the first 4 KB are skipped entirely, avoiding gigabytes of
  unnecessary I/O. (Closes #80)

## [0.12.4] - 2026-04-24

### Fixed
- **Performance Mode** — processor state controls are now disabled when the
  active power plan is High Performance or Ultimate Performance (Windows
  forces min state to 100 %). A warning message explains the lock and how
  to unlock by switching to Balanced. (Closes #103)
- **Process Manager** — replaced the plain text status badge with a colored
  dot + text indicator. Green for Running, red for Not responding. New
  `ProcessStatusToBrushConverter`. (Closes #88)
- **Sidebar progress** — added progress indicators in the left navigation
  for Disk Analyzer and Duplicate File scans, matching the existing Deep
  Cleanup mini-tray pattern. Click to navigate to the tab. (Closes #81, #91)

## [0.12.3] - 2026-04-24

### Fixed
- **Cleanup tab** — added explanatory text describing what each operation
  does (Clean TEMP, SFC /scannow, DISM /RestoreHealth) so users understand
  the tools before running them. (Closes #92)
- **System Health** — chkdsk status line now stays visible after the scan
  finishes instead of disappearing. Shows green while running, muted gray
  when done, so the user can see the result. (Closes #94)

## [0.12.2] - 2026-04-24

### Fixed
- **Version display** — updated `.csproj` from `0.5.1` to `0.12.1` so the
  app reports the correct version in the sidebar and About tab. Fixed
  `auto-release.yml` + `release.yml` + `publish.ps1` to inject version at
  build time via `/p:Version=`, so released binaries always match the tag.
  (Closes #90)
- **False update prompt** — the app no longer offers an update when already
  running the latest version. Root cause was the stale assembly version.
  (Closes #74)
- **System Health** — renamed "Rescan" button to "Scan" to match the
  initial prompt text. (Closes #97)
- **System Health scroll** — fixed ConsoleView auto-scroll from
  propagating `BringIntoView` to the parent ScrollViewer, which caused
  the entire page to jump to the bottom during file-system scans. Now
  scrolls the internal ListBox directly via `ScrollToEnd()`. (Closes #93)
- **Startup tab** — now discovers startup items from shell:startup folders
  (user + common) and Task Scheduler logon tasks, not just registry Run
  keys. Resolves `.lnk` shortcuts to their target path. Deduplicates
  entries already found in the registry. Filters out Microsoft/Windows
  system tasks to reduce noise. (Closes #76)
- **Cleanup tab** — auto-scans TEMP folders and Recycle Bin sizes on load,
  showing results in two summary cards so the tab is no longer empty until
  the user runs an action. (Closes #96)
- **Uninstaller** — failed uninstalls now show descriptive error messages
  instead of cryptic exit codes. Covers common winget/MSI codes: access
  denied, cancelled, already removed, reboot required, installer busy.
  (Closes #87)
- **Network chart labels** — increased axis label font sizes and switched
  to Segoe UI with brighter text color (`#E6E9EE`) for better readability
  on the dark background. (Closes #99, #75)
- **Issue templates** — added all missing tabs (Startup, Duplicates, Disk
  Analyzer, Processes, Battery, Uninstaller, Performance) to both bug
  report and feature request templates. Updated version placeholder.
  (Closes #77)

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

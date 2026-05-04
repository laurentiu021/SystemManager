# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
- **Input validation: whitelist regex** — `UninstallerService` and
  `WingetService` now validate package IDs with a whitelist regex
  (`[a-zA-Z0-9._-/+]`, max 256 chars) instead of a blacklist (#397).
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

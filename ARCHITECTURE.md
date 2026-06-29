# Architecture

SysManager is a tabbed WPF desktop app on .NET 10, written in C# 14. It follows
a standard MVVM layout with a thin service layer that wraps Windows APIs,
PowerShell, and external CLIs (winget, Ookla `speedtest`). It's gamer-focused:
network presets for CS2 / PUBG / Streaming and safe cleanup for Steam / Epic
/ Battle.net / Riot / GOG / EA caches.

Built by [laurentiu021](https://github.com/laurentiu021) · MIT licensed.

## Solution layout

```
SysManager/
├── SysManager/                 # main WPF app
│   ├── Data/                   # static data files (ProcessDescriptions.json)
│   ├── Models/                 # POCOs (snapshots, samples, reports, cleanup categories)
│   ├── Services/               # Windows / PowerShell / CLI wrappers
│   ├── ViewModels/             # one VM per tab + MainWindowViewModel
│   ├── Views/                  # XAML views + code-behind
│   ├── Helpers/                # AdminHelper, converters, collections, parsers
│   ├── Resources/              # icons and assets
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)
│   ├── ServiceRegistration.cs  # DI container configuration
│   └── SysManager.csproj
├── SysManager.Tests/           # xUnit unit tests (CI-safe, no system deps)
├── SysManager.IntegrationTests/# xUnit integration tests (local only)
└── SysManager.UITests/         # FlaUI UI-automation tests
```

## Tabs (view models)

The sidebar organises tabs into 12 collapsible groups via `NavGroup` →
`NavItem` hierarchy built by `BuildNavGroups()` using `Group()` and
`Item()` helper methods. Dashboard renders as a flat top-level entry.
Collapsed groups show a child count badge, subtitle (auto-generated
from child labels joined with " · "), and tooltip.
Planned features use `PlaceholderViewModel` with a WIP view.

| Group | View Models |
|-------|-------------|
| Dashboard | `DashboardViewModel` |
| System | `SystemHealthViewModel` · `WindowsUpdateViewModel` · `PerformanceViewModel` · `ServicesViewModel` · `StartupViewModel` · `WindowsFeaturesViewModel` · `RestorePointsViewModel` · `TaskSchedulerViewModel` · `BootAnalyzerViewModel` · `SystemFixesViewModel` |
| Gaming & Profiles | `TimerResolutionViewModel` · `DisplayProfileViewModel` · `CpuAffinityViewModel` · `StandbyMemoryViewModel` · `PlaceholderViewModel` (Gaming Profile) |
| Monitor | `ProcessManagerViewModel` · `ResourceHistoryViewModel` · `PrivacyMonitorViewModel` · `AppAlertsViewModel` · `FileLockViewModel` · `SettingsWatchdogViewModel` · `PlaceholderViewModel` (Bandwidth Monitor) |
| Cleanup | `CleanupViewModel` · `DeepCleanupViewModel` · `ShortcutCleanerViewModel` · `PlaceholderViewModel` (Scheduled Maintenance) |
| Storage | `DiskAnalyzerViewModel` · `DuplicateFileViewModel` |
| Network | `PingViewModel` · `TracerouteViewModel` · `SpeedTestViewModel` · `NetworkRepairViewModel` (shared: `NetworkSharedState`) · `DnsHostsViewModel` |
| Apps | `AppUpdatesViewModel` · `BulkInstallerViewModel` · `UninstallerViewModel` |
| Privacy & Security | `PrivacyViewModel` · `FileShredderViewModel` · `AppBlockerViewModel` · `DebloaterViewModel` · `BrowserCleanerViewModel` · `DefenderViewModel` · `PlaceholderViewModel` (Edge/OneDrive Remover · Notification Blocker) |
| Customization | `ContextMenuViewModel` · `DarkModeViewModel` · `PlaceholderViewModel` (Volume Control) |
| Info | `DriversViewModel` · `BatteryHealthViewModel` · `LogsViewModel` · `SystemReportViewModel` · `LegacyPanelsViewModel` · `AboutViewModel` |
| Advanced | `ProfileViewModel` · `EnvironmentVariablesViewModel` · `CliInterfaceViewModel` |

- `DashboardViewModel` — real-time system vitals (CPU/RAM/GPU at 300ms polling),
  temperatures (LibreHardwareMonitor + NvAPIWrapper), storage overview, system
  alerts (auto-scan at boot), quick actions with inline progress, health score,
  and recent activity log. IsActive pattern pauses polling when tab not visible.
- `AppUpdatesViewModel` — winget scan and bulk upgrade.
- `WindowsUpdateViewModel` — user-triggered Windows Update scan/install via the WUA COM API.
- `SystemHealthViewModel` — SMART, memory diagnostic, multi-drive chkdsk.
- `CleanupViewModel` — TEMP, Recycle Bin, SFC, DISM (background-aware).
- `DeepCleanupViewModel` — scan-first deep cleanup + large-files finder.
- `StartupViewModel` — startup program management (enable/disable via registry).
- `DuplicateFileViewModel` — duplicate file finder with partial-hash pre-filter.
- `DiskAnalyzerViewModel` — disk space breakdown by folder with drill-down.
- `ProcessManagerViewModel` — running processes with kill, filter, sort.
- `BatteryHealthViewModel` — charge %, health %, wear, cycle count via WMI.
- `UninstallerViewModel` — winget-based app uninstaller with batch support.
- `PerformanceViewModel` — per-tweak performance tuning with snapshot restore.
- `PingViewModel` — live ping monitoring with latency chart and health verdict.
- `TracerouteViewModel` — auto-traceroute + manual trace with own Start/Stop.
- `SpeedTestViewModel` — HTTP (Cloudflare) and Ookla speed tests.
- `NetworkRepairViewModel` — DNS flush, Winsock reset, TCP/IP reset.
- `NetworkSharedState` — shared targets, buffers, pinger, tracer, health for all network VMs.
- `ServicesViewModel` — Windows services management with gaming recommendations.
- `DriversViewModel` — driver inventory via Win32_PnPSignedDriver.
- `LogsViewModel` — friendly Event Log viewer.
- `AboutViewModel` — version info, auto-update, release history.
- `WindowsFeaturesViewModel` — list, enable, disable Windows optional features.
- `AppAlertsViewModel` — monitors new app installations via FileSystemWatcher + registry.
- `ShortcutCleanerViewModel` — scans and removes broken desktop/Start Menu shortcuts.
- `AppBlockerViewModel` — block/unblock apps via IFEO (Image File Execution Options) registry mechanism.
- `FileShredderViewModel` — secure multi-pass file overwrite and deletion.
- `BulkInstallerViewModel` — batch app installation via winget with progress tracking.
- `DnsHostsViewModel` — DNS server configuration and hosts file editor in one tab.
- `PrivacyViewModel` — Windows privacy and telemetry toggles via registry.
- `ContextMenuViewModel` — scan and manage Explorer right-click context menu entries.
- `SystemReportViewModel` — generate a read-only full-system snapshot and export it as text, HTML, or JSON.
- `EnvironmentVariablesViewModel` — view/edit User and System environment variables with a dedicated PATH editor (reorder, dedupe, missing-folder detection); staged edits with a one-time backup.
- `CliInterfaceViewModel` — read-only reference tab listing the headless CLI commands (sourced from `CliRunner.Commands`) with copy-to-clipboard; documents the flags, runs nothing itself.
- `RestorePointsViewModel` — list, create, and restore Windows System Restore points (admin for create/restore; restore reboots, gated by confirmation).
- `LegacyPanelsViewModel` — one-click launcher for the fixed catalog of classic Windows applets (pure launchers, no system modification).
- `SystemFixesViewModel` — consolidated one-click repairs (Windows Update reset, network reset, WinGet reinstall) with per-fix confirmation + live output; opens netplwiz for secure auto-logon.
- `BootAnalyzerViewModel` — read-only boot-time history + slow-component breakdown from the Diagnostics-Performance log, with a trend vs recent average; needs admin to read the log.
- `TimerResolutionViewModel` — request the finest Windows timer resolution (≈0.5 ms) for lower game input latency, or release it; shows the live effective value.
- `FileLockViewModel` — find which processes are holding a file/folder (Restart Manager) and optionally end a selected one after confirmation; critical processes are protected.
- `DisplayProfileViewModel` — list displays and supported resolution/refresh modes and switch between them; applies for the session with a 15-second auto-revert safety net.
- `CpuAffinityViewModel` — pin a running process to specific logical CPUs with P-core/E-core labels on hybrid CPUs; per-process and reverts on process exit.
- `DefenderViewModel` — view Microsoft Defender status, toggle PUA / Controlled Folder Access, and manage scan-exclusion folders; every change is admin-gated, confirmed, and verified by read-back (Tamper Protection can silently reject).
- `TaskSchedulerViewModel` — browse Windows scheduled tasks with a safety classification and enable/disable them (reversible, never deletes); system tasks warn before disabling, changes verified by read-back.
- `DarkModeViewModel` — switch the Windows light/dark theme manually or on a fixed-time schedule (DispatcherTimer poll while the app runs); persists the schedule.
- `StandbyMemoryViewModel` — live memory stats (2s poll) with on-demand and threshold-based auto-purge of the Windows standby list; purge needs admin.
- `ProfileViewModel` — export/import SysManager's own config (theme, speed-test history) as a portable JSON profile with selective sections and version checking.
- `DebloaterViewModel` — list and remove preinstalled Store apps with a curated bloat preset; system-critical packages are denylisted; removal is per-user and reversible via the Store.
- `BrowserCleanerViewModel` — scan per-browser cache/history/cookies/sessions with sizes and clean the selected categories; cookies/sessions default unticked.
- `PrivacyMonitorViewModel` — read-only camera/mic/location access history from the consent store; hands off to Windows settings to change permissions.
- `ConsoleViewModel` — shared, per-tab scrollable console (each tab gets its own
  instance; lines capped at 5000 to bound memory) backing the in-app Console mirror
  used by Cleanup, Windows Update, System Health, App Updates, and Uninstaller.

## Services

Thin wrappers around the underlying platform. Each service is designed to be
unit-testable. The key seam interfaces are `IPowerShellRunner` (PowerShellRunner),
`IAppBlockerService` (AppBlockerService), and `IDialogService` (DialogService) —
substitutable in tests (see `ServiceRegistration.cs`).

Key services:
- `PingMonitorService` / `TracerouteService` / `TracerouteMonitorService` —
  network probes on `System.Net.NetworkInformation.Ping` and `tracert`.
- `SpeedTestService` — HTTP speed test against Cloudflare plus the Ookla CLI,
  auto-downloaded on first use.
- `PowerShellRunner` — wraps `System.Management.Automation` to run scripts
  and stream output line-by-line. Always launches spawned processes from
  `System32` so `Access is denied` never bites on `chkdsk` etc.
- `WingetService` — shells out to `winget` and parses its table output.
- `WindowsUpdateService` — drives Windows Update through the WUA COM API
  (scan, select, install) with progress reporting; backs `WindowsUpdateViewModel`.
- `WindowsUpdatePolicyService` — reads/writes the documented Windows Update
  deferral policy keys (defer feature updates, bounded pause, restore default).
  Injectable registry root for tests; deliberately offers no permanent
  disable-updates option, only a bounded pause.
- `DiskHealthService` — pulls SMART data through WMI.
- `MemoryTestService` — scans WHEA / MemoryDiagnostics events.
- `EventLogService` + `EventExplainer` — read Windows Event Log and attach
  human-readable explanations.
- `HealthAnalyzer` — raw SMART / ping data into verdict pills.
- `SystemInfoService` — OS / CPU / RAM / uptime snapshot.
- `BiosService` — read-only BIOS/firmware + motherboard info (Win32_BIOS,
  Win32_BaseBoard, UEFI/Secure-Boot registry) plus a pure manufacturer
  support-URL resolver for BIOS updates; never flashes firmware. Consumed by
  `SystemHealthViewModel`.
- `TuneUpService` — orchestrates the Quick Tune-Up wizard: temp cleanup,
  Recycle Bin, shortcut scan, disk SMART, uptime/RAM checks. Non-destructive.
- `HealthScoreService` — aggregates disk health, RAM, uptime, and battery
  wear into a single 0–100 score with color-coded verdict and recommendations.
- `TrayIconService` — system tray icon with background monitoring (60s),
  tooltip updates, context menu, and Windows toast notifications.
- `LogService` — Serilog wrapper with rolling file sink.
- `FixedDriveService` — enumerate fixed NTFS/ReFS volumes.
- `DeepCleanupService` — scan-first safe cleanup (vendor caches, gaming
  launcher caches, Windows caches). Per-file try/catch so locked files
  are skipped, not forced.
- `LargeFileScanner` — read-only biggest-files discovery; skips WinSxS,
  pagefile, hiberfil, System Volume Information.
- `UpdateService` — GitHub Releases API client with explicit
  `SocketsHttpHandler`, retry, and surfaced error messages.
- `StartupService` — enumerate and toggle startup programs via registry
  Run / RunOnce keys.
- `DuplicateFileService` — three-pass duplicate finder (size grouping →
  partial hash pre-filter → full SHA-256). Read-only, never deletes.
- `DiskAnalyzerService` — folder-level space breakdown with progress
  reporting and system-path skipping.
- `ProcessManagerService` — enumerate running processes, kill by PID,
  open file location.
- `WindowsFeaturesService` — list, enable, disable Windows optional features
  via `Get-WindowsOptionalFeature` / `Enable-WindowsOptionalFeature` PowerShell.
- `UninstallerService` — winget-based uninstall + registry UninstallString
  fallback for local apps not in winget.
- `PerformanceService` — power plan, visual effects, Game Mode, Xbox
  Game Bar, NVIDIA GPU, processor state, restore point creation, RAM
  working set trim, hibernation toggle. Snapshot-based restore.
- `NetworkRepairService` — DNS flush, Winsock reset, TCP/IP reset via
  system commands with live output capture.
- `ServiceManagerService` — enumerate Windows services, gaming
  recommendations, start/stop/disable with admin checks.
- `AppAlertService` — monitors for new application installations via
  FileSystemWatcher and registry polling.
- `AppBlockerService` — blocks/unblocks app execution via Image File
  Execution Options (IFEO) debugger redirect.
- `BatteryService` — battery health, charge cycles, wear level, and
  power report generation via WMI and `powercfg /batteryreport`.
- `DialogService` — centralized confirmation/message dialogs (replaces
  direct MessageBox calls for testability).
- `IconExtractorService` — extracts application icons from executables
  for display in process/app lists; caches results.
- `OperationLockService` — prevents concurrent conflicting operations by
  category (Disk / Network / SystemModification) via a thread-safe
  `ConcurrentDictionary` try-acquire. Returns a disposable handle, or `null`
  immediately if that category is already locked (non-blocking; no timeout).
- `ProcessDescriptionService` — enriches process entries with friendly
  descriptions from file version info and known-process database.
- `SpeedTestHistoryService` — persists speed test results to JSON for
  historical charting and trend analysis.
- `ShortcutCleanerService` — scans Start Menu and Desktop for broken
  shortcuts (dead targets) and offers safe removal.
- `BulkInstallerService` — installs apps via winget in batch with
  per-item progress and error reporting.
- `FileShredderService` — secure multi-pass file overwrite (DoD 5220.22-M
  style) and deletion.
- `PrivacyService` — reads and writes Windows privacy and telemetry
  registry toggles (activity history, advertising ID, diagnostics, etc.).
- `DnsService` — manages DNS server configuration via PowerShell
  `Set-DnsClientServerAddress` with preset support (plain resolvers plus
  ad/malware/family-blocking variants), IPv4 + IPv6, and reversible snapshots.
- `HostsFileService` — parses and edits the Windows hosts file with
  add/remove/toggle operations; keeps a one-time pristine backup and can
  restore it (`HasBackup` / `RestoreBackup`).
- `ContextMenuService` — scans and toggles Explorer context menu shell
  extensions via registry enumeration.
- `SystemReportService` — gathers a comprehensive system snapshot once
  (OS, CPU, memory, GPU, motherboard, storage health, network) into a
  `SystemReportData` payload, then renders it to plain text, self-contained
  HTML, or JSON so all three exports share a single source of truth.
- `EnvironmentVariableService` — reads/writes User and Machine environment
  variables via `Environment.SetEnvironmentVariable` (which broadcasts
  WM_SETTINGCHANGE), with name validation, pure PATH split/join/dedupe helpers,
  and a one-time JSON backup of the original environment before the first write.
- `RestorePointService` — lists (`Get-ComputerRestorePoint`), creates
  (`Checkpoint-Computer`), and restores (`Restore-Computer`) System Restore points
  through the `IPowerShellRunner` seam; the output parser is a pure, unit-tested
  static method.
- `DebloaterService` — lists (`Get-AppxPackage`) and removes (`Remove-AppxPackage`,
  per-user) Windows Store apps through the `IPowerShellRunner` seam. A hard-coded
  denylist of system-critical package families is enforced in code; the parser and
  denylist check are pure, unit-tested static methods.
- `BrowserCleanerService` — scans + cleans per-browser data (Chromium family +
  Firefox) under injectable LOCALAPPDATA/APPDATA roots. Scan is read-only (sizes);
  Clean deletes only discovered files, skips locked files, and never follows
  reparse points. Cookies/sessions are flagged sensitive.
- `PrivacyMonitorService` — read-only reader of the CapabilityAccessManager consent
  store (camera/microphone/location access history). Injectable registry root;
  friendly-name decoding and FILETIME conversion are pure, unit-tested static methods.
- `BootAnalyzerService` — read-only reader of the Diagnostics-Performance log
  (event 100 boot durations; 101–110 slow-component events). Event-ID→kind mapping
  and event-XML field parsing are pure, unit-tested static methods; reading the log
  needs admin.
- `TimerResolutionService` — thin wrapper over ntdll `NtQueryTimerResolution` /
  `NtSetTimerResolution`. The request is a per-process contribution Windows reverts
  on exit, so it's fully reversible and needs no admin; the 100ns→ms conversion and
  high-resolution detection are pure, unit-tested static methods on the model.
- `FileLockService` — Restart Manager (`rstrtmgr.dll`) wrapper that lists the processes
  using a file/folder and can terminate one. The one place we use classic `[DllImport]`
  (not `[LibraryImport]`): `RM_PROCESS_INFO` has inline `ByValTStr` buffers and
  `RmStartSession` needs a `StringBuilder`, neither supported by the source generator.
- `DisplayProfileService` — `user32` display APIs (`EnumDisplayDevicesW` /
  `EnumDisplaySettingsW` / `ChangeDisplaySettingsExW`) to read and switch resolution +
  refresh rate. Session-only apply (reverts on reboot); validated with `CDS_TEST` first.
  Also classic `[DllImport]` (DEVMODE has non-blittable inline buffers).
- `CpuAffinityService` — gets/sets per-process CPU affinity via `Process.ProcessorAffinity`
  and detects P-core/E-core topology via kernel32 `GetLogicalProcessorInformationEx`
  (variable-length buffer walked by each record's `Size`). The mask helpers
  (build/test/all-cores) are pure, unit-tested static methods.
- `DefenderService` — Microsoft Defender via the Defender PowerShell module
  (`Get-MpPreference` / `Set-MpPreference` / `Add`/`Remove-MpPreference`) through
  `IPowerShellRunner`. Normalizes the inverted `Disable*` booleans; exclusion paths are
  bound parameters (never interpolated); every change is verified by reading the value
  back (Tamper Protection can silently reject). The parse helpers are pure, unit-tested.
- `TaskSchedulerService` — the `ScheduledTasks` PowerShell module (`Get-ScheduledTask`
  / `Get-ScheduledTaskInfo` / `Enable`/`Disable-ScheduledTask`) through `IPowerShellRunner`.
  Disabling is reversible and never unregisters; toggles are verified by read-back. The
  `ClassifyTask` safety heuristic (telemetry/system/third-party) is a pure, unit-tested method.
- `WindowsThemeService` — reads/writes the per-user Windows light/dark theme (HKCU
  `AppsUseLightTheme`/`SystemUsesLightTheme`, no admin) and broadcasts
  `WM_SETTINGCHANGE("ImmersiveColorSet")` for immediate effect. Persists the schedule JSON;
  the overnight-aware `ShouldBeDark` evaluation is a pure, unit-tested method. Distinct from
  `ThemeService` (which themes SysManager's own WPF UI).
- `StandbyMemoryService` — `GlobalMemoryStatusEx` for stats (no admin) and ntdll
  `NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)` to purge,
  after enabling `SeProfileSingleProcessPrivilege` via `AdjustTokenPrivileges` (the
  RAMMap/ISLC mechanism). Purge is non-destructive (standby is clean disk-backed cache).
  All-`[LibraryImport]`; checks `ERROR_NOT_ALL_ASSIGNED` to detect a non-elevated token.
- `LegacyPanelService` — opens classic Windows applets (Control Panel, Sound,
  Device Manager, …) via their `control`/`*.cpl`/`*.msc` commands. The catalog is
  hard-coded and `Launch` re-validates catalog membership, so no input reaches
  `Process.Start`; pure launchers, no system modification.
- `SystemFixService` — one-click repairs (reset Windows Update, reset the network
  stack, reinstall WinGet) via hard-coded PowerShell scripts through the
  `IPowerShellRunner` seam; streams output and returns an honest success/failure
  `SystemFixResult`. Auto-logon is delegated to the built-in netplwiz dialog, never
  a plaintext credential write.
- `ProfileService` — bundles SysManager's own config files (theme, speed-test
  history) into a versioned, portable JSON profile and applies it back; only
  catalog-known sections are written (a tampered profile can't drop arbitrary
  files), and the config directory is injectable for tests.
- `AppIconService` — downloads and caches application favicons for UI display.
- `TemperatureService` — aggregates CPU, GPU, and disk temperatures from
  LibreHardwareMonitor (admin) and NvAPIWrapper (non-admin NVIDIA). Real-time
  polling with 2s interval.
- `ActivityLogService` — persists last 20 user actions to JSON file for
  Dashboard recent activity display.
- `ResourceHistoryService` — always-on background sampler (started at app startup,
  runs while minimized to tray) that records CPU/RAM/GPU usage + CPU/GPU temperatures
  every 10s as append-only NDJSON in `%LocalAppData%\SysManager\resource-history.ndjson`,
  with 7/14/30-day retention (periodic prune). Reuses `SystemInfoService` + NvAPIWrapper +
  `TemperatureService`; serialize/parse/prune/downsample/CSV are pure, unit-tested static
  helpers. Strictly local — no system writes, nothing leaves the machine.
- `SettingsWatchdogService` — snapshots a curated catalog of settings Windows Update
  tends to reset (telemetry, web search, widgets, lock-screen ads, Start suggestions, …)
  as a JSON baseline in `%LocalAppData%\SysManager\settings-baseline.json`, then diffs the
  live registry against it and restores drifted values on request. `DetectChanges` is a
  pure, unit-tested diff; registry access reuses the validated HKCU/HKLM helper pattern
  from `PrivacyService`. Reads/writes only well-known values; nothing leaves the machine.
- `CliRunner` — the headless command-line entry point (dispatched from `App.OnStartup`
  before the single-instance mutex, attaching to the parent console). Exposes only
  read-only/safe verbs (`--health`, `--cleanup`, `--trim-ram`, `--version/--help/--list`)
  with `--json`/`--silent` modifiers and conventional exit codes (0/1/2). `Parse` and
  `ExecuteAsync` are pure/return-value-based, so the whole CLI is unit-tested without
  launching the process; `IsCliInvocation` is strict so the elevation/update-applier args
  never trigger CLI mode.
- `SafetyDatabase` — curated safety ratings for Windows services.
- `ThemeService` — runtime theme switching with 12 presets and persistence.
- `ToastService` — global glass-style toast notifications.

## Helpers

Utility classes that don't fit neatly into Services or ViewModels:

- `AdminHelper` — elevation check (`IsElevated()`) and UAC relaunch.
- `BulkObservableCollection<T>` — `ObservableCollection` subclass that
  suppresses change notifications during bulk add/remove for UI performance
  (in `ObservableCollectionExtensions.cs`).
- `WingetTableParser` — parses the fixed-width table output from `winget`
  CLI commands into structured objects.
- `FormatHelper` — byte-size formatting, duration humanization, and other
  display helpers.
- `GatewayHelper` — default gateway IP lookup for network tabs.
- `EtaCalculator` — estimates time remaining for long-running operations.
- `KnownFolders` — resolves Windows Known Folder paths via shell API.
- `RecycleBinHelper` — empties the Recycle Bin via the shell API; shared by Deep
  Cleanup and the One-Click Tune-Up so the interop has one source of truth.
- `MarkdownTextBlock` — lightweight Markdown-to-WPF inline renderer.
- Value converters: `EqualityConverter`, `IntGreaterThanZeroConverter`,
  `ValueConverters` (boolean/visibility/inverse helpers).

## Dependency Injection

`ServiceRegistration.cs` configures `Microsoft.Extensions.DependencyInjection`.
`App.OnStartup` builds the `IServiceProvider` and exposes it as `App.Services`.
Most core services and ViewModels are registered as singletons — one shared
instance per app lifetime. The exception is `PowerShellRunner` /
`IPowerShellRunner`, registered **transient** so each consumer gets its own
instance, avoiding `LineReceived` event cross-talk between tabs (see
`ServiceRegistration.cs:24-25`). `MainWindowViewModel` resolves child VMs from
the container at runtime; falls back to manual creation in tests (no DI
dependency in the test project).

## Admin elevation

Features that require admin (Windows Update, SFC/DISM, system-wide winget
upgrades) check elevation via `AdminHelper.IsElevated()` and surface a banner
when running unelevated. The banner calls `AdminHelper.RelaunchAsAdmin()`,
which restarts the process with `runas` and the current command-line args.

## Threading

- Long-running work (ping loops, PowerShell runs, winget scans, deep-clean
  scans) runs on background tasks.
- View-model observable properties are updated on the UI thread via the
  dispatcher captured in `ViewModelBase`.
- SFC and DISM each have their own `IsSfcRunning` / `IsDismRunning` flags for
  UI state, but they are **mutually exclusive**: both share a single PowerShell
  runner, so a `SystemModification` `OperationLockService` lock prevents running
  them (or other system-repair operations) concurrently — starting one while the
  other runs is refused with a status message.

## Safety guardrails (Deep Cleanup)

`DeepCleanupService` is intentionally conservative:
- Scan first, clean second. Every category is opt-in and shows its size.
- Never touches browsers, passwords, registry, active drivers, Program
  Files, or actual game files in `steamapps\common`.
- Windows.old is tagged **Irreversible** and never selected by default.
- Large files finder has no delete action, even with admin rights.

## Logging

Serilog writes to a rolling file sink at
`%LOCALAPPDATA%\SysManager\logs\sysmanager-.log` (one file per day, 14 days
retained). The in-app Console mirrors the same stream per tab.

## Updates

`UpdateService` hits `api.github.com/repos/laurentiu021/SystemManager/releases`
at startup and on demand. Downloads land in
`%LOCALAPPDATA%\SysManager\updates\SysManager-{version}.exe` with a companion
`.sha256` so re-opening the app doesn't re-download a good copy. The "Install"
button verifies the download's SHA256 and Authenticode signature, then hands
off to `UpdateApplier`: the freshly-downloaded exe is relaunched with
`--apply-update`, which `App.OnStartup` intercepts before any window opens. That
process waits for the old instance to exit, swaps itself over the old executable
via a staged atomic move (a sibling `.new` file plus `File.Move`, so an
interrupted copy can never leave a half-written binary), and relaunches —
inheriting the original's elevation. No on-disk script is involved.

## Testing

See [TESTING.md](TESTING.md) for the xUnit unit / integration project and the
FlaUI UI-automation project.

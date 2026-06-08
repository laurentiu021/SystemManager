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
| System | `SystemHealthViewModel` · `WindowsUpdateViewModel` · `PerformanceViewModel` · `ServicesViewModel` · `StartupViewModel` · `WindowsFeaturesViewModel` · `PlaceholderViewModel` (Restore Points · Task Scheduler · Boot Analyzer) |
| Gaming & Profiles | `PlaceholderViewModel` (Gaming Profile · Standby List Cleaner · Timer Resolution · CPU Core Affinity · Display Profiles) |
| Monitor | `ProcessManagerViewModel` · `PlaceholderViewModel` (Resource History · Privacy Monitor · File Lock Detector · Settings Watchdog · Bandwidth Monitor) |
| Cleanup | `CleanupViewModel` · `DeepCleanupViewModel` · `ShortcutCleanerViewModel` · `PlaceholderViewModel` (Scheduled Maintenance) |
| Storage | `DiskAnalyzerViewModel` · `DuplicateFileViewModel` |
| Network | `PingViewModel` · `TracerouteViewModel` · `SpeedTestViewModel` · `NetworkRepairViewModel` (shared: `NetworkSharedState`) · `DnsHostsViewModel` |
| Apps | `AppUpdatesViewModel` · `BulkInstallerViewModel` · `UninstallerViewModel` |
| Privacy & Security | `PrivacyViewModel` · `FileShredderViewModel` · `AppBlockerViewModel` · `AppAlertsViewModel` · `PlaceholderViewModel` (Debloater & Ads · Browser Cleaner · Edge/OneDrive Remover · Defender Tweaks · Notification Blocker) |
| Customization | `ContextMenuViewModel` · `PlaceholderViewModel` (Dark Mode Scheduler · Volume Control · Environment Variables) |
| Info | `DriversViewModel` · `BatteryHealthViewModel` · `LogsViewModel` · `PlaceholderViewModel` (System Report) · `AboutViewModel` |
| Advanced | `PlaceholderViewModel` (Profile Export/Import · CLI Interface) |

- `DashboardViewModel` — real-time system vitals (CPU/RAM/GPU at 300ms polling),
  temperatures (LibreHardwareMonitor + NvAPIWrapper), storage overview, system
  alerts (auto-scan at boot), quick actions with inline progress, health score,
  and recent activity log. IsActive pattern pauses polling when tab not visible.
- `AppUpdatesViewModel` — winget scan and bulk upgrade.
- `WindowsUpdateViewModel` — PSWindowsUpdate wrapper with auto-check.
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

## Services

Thin wrappers around the underlying platform. Each service is designed to be
unit-testable — where possible, they depend on interfaces or accept seams for
swapping the underlying process runner.

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
- `DiskHealthService` — pulls SMART data through WMI.
- `MemoryTestService` — scans WHEA / MemoryDiagnostics events.
- `EventLogService` + `EventExplainer` — read Windows Event Log and attach
  human-readable explanations.
- `HealthAnalyzer` — raw SMART / ping data into verdict pills.
- `SystemInfoService` — OS / CPU / RAM / uptime snapshot.
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
- `DuplicateFileService` — two-pass duplicate finder (size grouping →
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
- `OperationLockService` — prevents concurrent destructive operations
  via named semaphore acquisition with timeout.
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
  `Set-DnsClientServerAddress` with preset support (Google, Cloudflare, etc.).
- `HostsFileService` — parses and edits the Windows hosts file with
  add/remove/toggle operations.
- `ContextMenuService` — scans and toggles Explorer context menu shell
  extensions via registry enumeration.
- `SystemReportService` — generates comprehensive system info reports
  (hardware, OS, network, drivers) for export or clipboard.
- `AppIconService` — downloads and caches application favicons for UI display.
- `TemperatureService` — aggregates CPU, GPU, and disk temperatures from
  LibreHardwareMonitor (admin) and NvAPIWrapper (non-admin NVIDIA). Real-time
  polling with 2s interval.
- `ActivityLogService` — persists last 20 user actions to JSON file for
  Dashboard recent activity display.
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
- `MarkdownTextBlock` — lightweight Markdown-to-WPF inline renderer.
- Value converters: `EqualityConverter`, `IntGreaterThanZeroConverter`,
  `ValueConverters` (boolean/visibility/inverse helpers).

## Dependency Injection

`ServiceRegistration.cs` configures `Microsoft.Extensions.DependencyInjection`.
`App.OnStartup` builds the `IServiceProvider` and exposes it as `App.Services`.
Core services and ViewModels are registered as singletons — one shared instance
per app lifetime. `MainWindowViewModel` resolves child VMs from
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
- SFC and DISM each have their own `IsSfcRunning` / `IsDismRunning`
  flags so they don't block unrelated UI or each other.

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
`%LOCALAPPDATA%\SysManager\updates\SysManager-{version}.exe` with size
checksum so re-opening the app doesn't re-download a good copy. The "Install"
button launches the new exe and closes the current instance; Windows swaps
them cleanly.

## Testing

See [TESTING.md](TESTING.md) for the xUnit unit / integration project and the
FlaUI UI-automation project.

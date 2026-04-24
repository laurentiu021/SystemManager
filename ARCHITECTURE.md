# Architecture

SysManager is a tabbed WPF desktop app on .NET 8, written in C# 12. It follows
a standard MVVM layout with a thin service layer that wraps Windows APIs,
PowerShell, and external CLIs (winget, Ookla `speedtest`). It's gamer-focused:
network presets for CS2 / PUBG / Streaming and safe cleanup for Steam / Epic
/ Battle.net / Riot / GOG / EA caches.

Built by [laurentiu021](https://github.com/laurentiu021) · MIT licensed.

## Solution layout

```
SysManager/
├── SysManager/                 # main WPF app
│   ├── Models/                 # POCOs (snapshots, samples, reports, cleanup categories)
│   ├── Services/               # Windows / PowerShell / CLI wrappers
│   ├── ViewModels/             # one VM per tab + MainWindowViewModel
│   ├── Views/                  # XAML views + code-behind
│   ├── Helpers/                # AdminHelper, converters, gateway lookup
│   ├── Resources/              # icons and assets
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)
│   └── SysManager.csproj
├── SysManager.Tests/           # xUnit unit tests (CI-safe, no system deps)
├── SysManager.IntegrationTests/# xUnit integration tests (local only)
└── SysManager.UITests/         # FlaUI UI-automation tests
```

## Tabs (view models)

- `DashboardViewModel` — OS / CPU / RAM / disk snapshot + live uptime.
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
- `NetworkViewModel` — ping monitor, traceroute, speed tests, presets.
- `DriversViewModel` — driver inventory + Windows Update driver scan.
- `LogsViewModel` — friendly Event Log viewer.
- `AboutViewModel` — version info, auto-update, release history.

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
- `DiskHealthService` — pulls SMART data through WMI.
- `MemoryTestService` — scans WHEA / MemoryDiagnostics events.
- `EventLogService` + `EventExplainer` — read Windows Event Log and attach
  human-readable explanations.
- `HealthAnalyzer` — raw SMART / ping data into verdict pills.
- `SystemInfoService` — OS / CPU / RAM / uptime snapshot.
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
- `PerformanceService` — power plan, visual effects, Game Mode, Xbox
  Game Bar, NVIDIA GPU, processor state. Snapshot-based restore.

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
`%LOCALAPPDATA%\SysManager\logs\sysmanager-.log` (one file per day, 7 days
retained). The in-app Console mirrors the same stream per tab.

## Updates

`UpdateService` hits `api.github.com/repos/laurentiu021/SysManager/releases`
at startup and on demand. Downloads land in
`%LOCALAPPDATA%\SysManager\updates\SysManager-{version}.exe` with size
checksum so re-opening the app doesn't re-download a good copy. The "Install"
button launches the new exe and closes the current instance; Windows swaps
them cleanly.

## Testing

See [TESTING.md](TESTING.md) for the xUnit unit / integration project and the
FlaUI UI-automation project.

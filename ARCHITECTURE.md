# Architecture

SysManager is a tabbed WPF desktop app on .NET 10, written in C# 14. It follows
a standard MVVM layout with a thin service layer that wraps Windows APIs,
PowerShell, and external CLIs (winget, Ookla `speedtest`). It's gamer-focused:
network presets for CS2, PUBG, FACEIT and streaming plus a general one, and safe
cleanup for Steam / Epic / Battle.net / Riot / GOG / EA caches.

Built by [laurentiu021](https://github.com/laurentiu021) · MIT licensed.

## Solution layout

```
SystemManager/                      # the repository
├── SysManager/                     # the solution: SysManager.sln, Directory.Build.props, and
│   │                               #   Directory.Packages.props, which pins every package version
│   ├── SysManager/                 # main WPF app
│   │   ├── Data/                   # static data files (ProcessDescriptions.json)
│   │   ├── Models/                 # POCOs (snapshots, samples, reports, cleanup categories)
│   │   ├── Services/               # Windows / PowerShell / CLI wrappers
│   │   ├── ViewModels/             # one VM per tab, MainWindowViewModel, and the row and chart
│   │   │                           #   view-models the tabs build (NavItem, UndoChangeRow, …)
│   │   ├── Views/                  # XAML views + code-behind, plus six shared controls:
│   │   │                           #   AdminBanner (the elevation banner, 31 tabs)
│   │   │                           #   EmptyState  (icon + title + message for an empty list)
│   │   │                           #   DevelopmentBanner (the PREVIEW notice)
│   │   │                           #   StatusFooter (progress bar + status line, 22 tabs)
│   │   │                           #   ConsoleView (the live output console, five tabs)
│   │   │                           #   ThemePopup (the Appearance panel)
│   │   ├── Helpers/                # AdminHelper, converters, collections, parsers, the signature
│   │   │                           #   and path checks
│   │   ├── Resources/              # icons and assets
│   │   ├── App.xaml(.cs), MainWindow.xaml(.cs)
│   │   ├── ServiceRegistration.cs  # DI container configuration
│   │   ├── AssemblyInfo.cs         # InternalsVisibleTo for the two test projects that need it
│   │   ├── app.manifest
│   │   └── SysManager.csproj
│   ├── SysManager.Tests/           # xUnit unit tests (CI-safe, no system deps)
│   ├── SysManager.IntegrationTests/# xUnit integration tests (CI, non-blocking)
│   └── SysManager.UITests/         # FlaUI UI-automation tests (CI, non-blocking)
├── docs/                           # screenshots, GIFs, manual-smoke.ps1
├── .github/                        # workflows, issue and PR templates, Dependabot
├── global.json                     # SDK pin, and the Microsoft.Testing.Platform runner
└── publish.ps1                     # the release build
```

## Tabs (view models)

The sidebar organises tabs into 12 groups (11 collapsible + a flat top-level Dashboard) via `NavGroup` →
`NavItem` hierarchy built by `BuildNavGroups()` using `Group()` plus one of two item helpers:
`Tab<TVm>()`, which defers resolving the view-model until the tab is first opened, and `EagerItem()`
for the few entries whose view-model must exist at startup. Dashboard renders as a flat top-level entry.
Each group carries an icon, passed to `Group()` as a Segoe Fluent Icons code point and asserted distinct;
leaves carry none, so the icon column belongs to the twelve headings rather than the fifty-nine pages.
Collapsed groups show a child count badge, a written two-line subtitle passed to `Group()` and asserted to
fit that budget, and a tooltip still generated from the child labels. Exactly one group starts expanded —
`InitiallyExpandedGroupId` (`grp-cleanup`) — set in the same loop that adds the groups; `#1519`'s arithmetic
is why it is one and not two. `MainWindowViewModelTests.NavGroups_ExactlyTheCleanupGroupStartsExpanded`
(integration) builds a real `MainWindowViewModel` and asserts that exactly one group opens and that it is the
constant's, which also catches a constant naming a group that no longer exists;
`ArchitectureTests.TheStartupExpansion_GoesThroughTheNamedConstant` checks that the source goes through the
constant rather than a repeated literal. It costs no view-model: the child rows bind only `NavItem`'s own
properties, none of which touches `Content`.
`MainWindowViewModel.SelectedNav` mirrors selection into `NavItem.IsSelected`.
`NavItem` also mirrors the tab's `IsBusy`, `Progress` and `IsProgressIndeterminate` out of its view-model,
and `MainWindowViewModel.MapTaskbarProgress` turns the selected tab's pair into the Windows taskbar
button's `ProgressState`/`ProgressValue` (bound in `MainWindow.xaml`). Both progress signals are read,
not one: 37 view-models set the indeterminate flag and 7 set a percentage. Deep Cleanup, File Shredder
and Speed Test set neither — they report progress through properties of their own — so their work does
not reach the taskbar yet. The mapping reads
the MIRRORED values and never `NavItem.Content`, because touching Content materialises the view-model and
would rebuild every lazy tab the shell asked about.
The flat Dashboard row and grouped leaf rows are invokable buttons styled
`SidebarNavButton`. Their inner visuals consume selection through the shared `SidebarNavRow`,
`SidebarNavText`, and `SidebarActiveMark` styles, while `SelectionStatus` exposes
the same state to UI Automation. Group headers are keyboard-focusable toggles;
collapsed content is disabled so visually hidden leaves cannot receive focus.

Keyboard focus is drawn by ONE shared resource, `FocusRing` in `App.xaml`, applied through
`FocusVisualStyle` on the button, toggle and chip styles whose template replaces the default one — a
custom template otherwise takes the framework's focus adorner with it. The ComboBox and its items, the
DataGrid column headers and the sidebar's group headers do not set it. It is a two-`Rectangle` adorner (white inner
stroke, near-black outer) rather than a themed border, because the ring must stay visible on
`PrimaryButton`'s accent fill, `DangerButton`'s red, a raised grey and a card surface; any single
colour, accent included, falls to 1.00:1 against at least one of those.
`ArchitectureTests.NoStyle_SuppressesTheKeyboardFocusIndicator` forbids
`FocusVisualStyle="{x:Null}"` anywhere and asserts the ring keeps both strokes.

`ButtonBase` carries `MinWidth`/`MinHeight` of 28, and the six styles derived from it in `App.xaml`
(Primary, Secondary, Ghost, DangerGhost, Admin and Danger) inherit it, as does `SidebarNavButton` in
`MainWindow.xaml`. Without a floor a
button was exactly its padding plus its content, and many trim their padding to fit a compact row or toolbar.
Every one of the 110 buttons in the views naming an explicit `Padding` was rendered on the STA thread with its
own style, padding, font size and label: **30 measured under WCAG 2.5.8 AA's 24 × 24**, across 16 files — 26
failing on height alone, 4 on both axes, none on width alone. The worst two are DNS & Hosts' remove-entry `X`
at 20.2 × 19.3, which is the smallest destructive target in the app, and the sidebar's clear-search glyph at
49.1 × 15.3 (#1556).

28 rather than 24 because a floor at the threshold leaves nothing for a fractional DPI scale, and because
28 × 28 is already the size of the sidebar footer chips. The floor also **clamps an explicit size upward** —
WPF resolves a size as `Max(MinWidth, Min(MaxWidth, Width))` — so the clear-search button, which then carried
`Width="20"`, came out 28 wide, and the reserved right padding on the TextBox it overlays had to grow from 22 to
32 with it. That was the one knock-on the change had, found by measuring rather than on screen; the explicit
width has since been removed.

`SysManager.IntegrationTests.HitTargetSizeTests` **measures** rather than reading the setters — a test
asserting `MinWidth` would pass while a derived style, a template or a call-site `Padding` made the rendered
box smaller, and the rendered box is what a user has to hit. Its floor is the WCAG 24, deliberately below the
28 the style sets, so a deliberate rise to 32 does not fail a guard meant to catch a regression. Two companion
tests bound the cost: one asserts the ordinary buttons are still sized by their content, since a minimum is
only free while it does not reshape what was already large enough; the other pins the `Max(MinWidth, Width)`
resolution, because the intuitive belief is the opposite one.

The type scale in `App.xaml` runs in two families. **Text**: `Display` (28) → `Heading` (20) →
`SectionTitle` (14) → `Body` (13) → `Subtle` (12) → `Caption` (11). **Numbers**: `MetricHero` (30),
`MetricLarge` (26), `Metric` (22), `MetricSmall` (20), `MetricCompact` (16). Every rung except `Display`
was added for a size views were *already* rendering with raw `FontSize` attributes, so adopting one has
never changed how anything looks — only where the number comes from. `Body` is the largest of those: 13 is
what the app writes in, 54 TextBlocks set it by hand before the rung existed, and having no name for it was
why a contributor reached for the raw value (#1634). `Heading` was restored alongside its first real user —
the sidebar wordmark — having been deleted once for having none (#1630).

`Heading` and `MetricSmall` share a size and are deliberately separate: one is text, the other a number,
and System Logs' severity counts take the metric one for that reason. Two 16/SemiBold headings on DNS &
Hosts stay raw for the mirror-image reason — they are headings, and the nearest heading token would shrink
them.

Every rung except `Display` is `BasedOn` the implicit `TextBlock` style, and that is load-bearing rather
than tidy: an explicit style REPLACES the keyless `<Style TargetType="TextBlock">`, so without it a
repointed TextBlock loses `TextRenderingMode="ClearType"` and — unless it names its own colour — its
foreground with it. `Metric` was the first token found without it, which made it a latent trap: it rendered
correctly only because all ten of its call sites set `Foreground` locally, and the eleventh would have been
black text on a dark surface.

`Caption`, `Subtle`, `SectionTitle` and `SectionLabel` were the four still missing it, which is what blocked
the rest of #1634: while they lacked `BasedOn`, swapping a raw `FontSize="11"` for the token *looked* like a
no-op and was not. With it the swap is appearance-neutral wherever the token's colour and weight are already
what the element gets, and those are the ones now converted. The elements where the token would visibly
differ — no colour of their own at 11 or 12, no weight of their own at 14 — deliberately keep their raw size,
and the guard's condition below is what tells the two apart, so no allowlist is involved.

`SectionLabel` is defined next to the other text tokens rather than beside the control templates it used to
sit in, and it had to move: `BasedOn` is a `StaticResource` reference, and a `StaticResource` cannot resolve a
key defined later in the same dictionary. Left where it was it would have thrown while `App.xaml` parsed.

`Display` stays out. #1634 proposed including it because the tab titles would move from
`TextFormattingMode=Display` to `Ideal`; measured on a real TextBlock, that premise is wrong — `Ideal` is
WPF's default, so they already render that way, and the only difference `BasedOn` would make is
`TextRenderingMode`, `Auto` to `ClearType`. Left out because the decision was taken on the larger claim and
the smaller one deserves its own look at 28px. `TypographyTokenTests.Display_StillStandsAloneAndStillResolvesItsColour`
fails if someone completes the set, which is the prompt to do that look first.

Two guards hold the scale. `ArchitectureTests.EveryMetricRungSize_IsReachedThroughItsRung` enforces both
directions — no raw `FontSize` on a styleless text `TextBlock` at a rung's size, and no reference to a rung
`App.xaml` does not define, the latter because a `{StaticResource}` inside a `DataTemplate` resolves at
runtime and would otherwise crash a tab rather than fail a build. It skips icon glyphs, because a
`FontSize` on a Segoe Fluent `TextBlock` is a glyph box rather than typography, and 13 and 16 are icon
dimensions as well as text ones, and it skips the five elements that set `Style` through a
`<TextBlock.Style>` child, which an attribute scan cannot see. At 11, 12 and 14 it demands the token only
where the swap cannot change what is drawn, which is the same condition described above.
`EveryTypographyStyleUse_ResolvesAColour` covers the other failure: a
`TextBlock` under a token that resolves no colour, and which names none itself, would render in the WPF
default brush. `SysManager.IntegrationTests.TypographyTokenTests` reads the resolved size, weight,
foreground and rendering mode off a real `TextBlock` on an STA thread, which is the only way to check that
`BasedOn` is actually doing its job — XAML compiles either way.

Every tab is backed by a real view and view-model. A shared WIP placeholder view
existed while tabs were still being built; the last tab graduated off it, so it was
removed rather than left as unreachable code. A tab that is implemented but not yet
QA-verified is marked with `IsInDevelopment` (surfaced as a PREVIEW badge) instead.

| Group | View Models |
|-------|-------------|
| Dashboard | `DashboardViewModel` |
| System | `SystemHealthViewModel` · `WindowsUpdateViewModel` · `PerformanceViewModel` · `ServicesViewModel` · `StartupViewModel` · `WindowsFeaturesViewModel` · `UndoChangesViewModel` · `RestorePointsViewModel` · `TaskSchedulerViewModel` · `BootAnalyzerViewModel` · `SystemFixesViewModel` |
| Gaming & Profiles | `GamingProfileViewModel` · `StandbyMemoryViewModel` · `TimerResolutionViewModel` · `CpuAffinityViewModel` · `DisplayProfileViewModel` |
| Monitor | `ProcessManagerViewModel` · `ResourceHistoryViewModel` · `PrivacyMonitorViewModel` · `SettingsWatchdogViewModel` |
| Cleanup | `CleanupViewModel` · `DeepCleanupViewModel` · `ShortcutCleanerViewModel` · `ScheduledMaintenanceViewModel` |
| Storage & Files | `DiskAnalyzerViewModel` · `LargeFilesViewModel` · `DuplicateFileViewModel` · `FileLockViewModel` |
| Network | `PingViewModel` · `TracerouteViewModel` · `SpeedTestViewModel` · `BandwidthMonitorViewModel` · `NetworkRepairViewModel` (shared: `NetworkSharedState`) · `DnsHostsViewModel` |
| Apps | `AppUpdatesViewModel` · `BulkInstallerViewModel` · `AppAlertsViewModel` · `UninstallerViewModel` |
| Privacy & Security | `PrivacyViewModel` · `FileShredderViewModel` · `AppBlockerViewModel` · `DebloaterViewModel` · `BrowserCleanerViewModel` · `EdgeOneDriveViewModel` · `DefenderViewModel` |
| Customization | `ContextMenuViewModel` · `DarkModeViewModel` · `AudioMixerViewModel` · `NotificationBlockerViewModel` |
| Info | `DriversViewModel` · `BatteryHealthViewModel` · `LogsViewModel` · `SystemReportViewModel` · `LegacyPanelsViewModel` · `AboutViewModel` |
| Advanced | `ProfileViewModel` · `CliInterfaceViewModel` · `EnvironmentVariablesViewModel` |

- `DashboardViewModel` — real-time system vitals (CPU/RAM/GPU at 300ms polling),
  temperatures (LibreHardwareMonitor + NvAPIWrapper), storage overview, system
  alerts (checked at launch, on Scan system and after Update All Apps; a check that
  could not run is amber, never green), quick actions with inline progress, health score,
  and recent activity log. IsActive pattern pauses polling when tab not visible.
- `AppUpdatesViewModel` — winget scan and bulk upgrade.
- `WindowsUpdateViewModel` — user-triggered Windows Update scan/install via the WUA COM API.
- `SystemHealthViewModel` — SMART, memory diagnostic, multi-drive chkdsk, and the BIOS and board details with
  a link to the maker's support site.
- `CleanupViewModel` — TEMP, Recycle Bin, component store (background-aware).
- `DeepCleanupViewModel` — scan-first deep cleanup over the safe-to-remove category list.
- `LargeFilesViewModel` — read-only biggest-files listing for a chosen folder or drive. Split out of
  `DeepCleanupViewModel` in #1523 and moved to the Storage group, beside the other two read-only
  space-analysis tabs. The two halves shared nothing — the locations list, the drive enumeration and
  the size threshold were read only by the large-file scan — so Deep Cleanup LOST two dependencies
  (`LargeFileScanner`, `FixedDriveService`) rather than gaining a shared seam, and no longer needs an
  async init at all.
- `StartupViewModel` — startup program management (enable/disable via registry). Also attributes Windows' own boot-delay measurements to entries, reading them from the same `BootAnalyzerService` the Boot Analyzer tab uses (one shared singleton) and only when elevated, since those events cannot be read otherwise. Attribution is whole-string on the entry name or its executable file name and fails closed, because a near-match would blame the wrong program on the one tab whose action is to disable it. `DescribeScan` says when other programs' scheduled tasks are missing from the list, and why.
- `DuplicateFileViewModel` — duplicate file finder with partial-hash pre-filter.
- `DiskAnalyzerViewModel` — disk space breakdown by folder with drill-down. Remembers the last scan of each root via `DiskScanHistoryService` and shows a "since last scan" delta; the read-and-remember is best-effort, so a history failure degrades to no delta rather than breaking a completed scan. A folder that could not be measured sets `LastFailure`, says why, and is not remembered as a scan.
  `Map` draws the same entries as blocks; `ShowMap` is remembered through `DiskAnalyzerPreferenceService`.
- `DiskTreemap` — the Disk Analyzer's map: the list's entries as `TreemapTile` blocks, laid out by
  `Helpers/TreemapLayout` for the size the view reports through `Helpers/SizeObserver`. Folders too small for their
  name, and the loose files, merge into one grey "Other" block. The other blocks are the theme's accent, lighter
  for smaller ones, with `ThemeService.OnColor` choosing the text, and are rebuilt on a theme change.
- `ProcessManagerViewModel` — running processes with kill, filter, sort. The kill path has three
  tiers, because one message cannot be true for all of them: `BootCriticalProcesses` (13 names) is
  refused outright, `HighConsequenceProcesses` (Defender's engine, Windows Installer and the servicing
  processes) is confirmed with a prompt naming the real damage, and any other Windows component gets
  the "a feature may look broken until you sign out" warning. Both sets are matched by process NAME,
  not by the description database's `safety` field — that field is provenance, and treating it as
  criticality is what made the app refuse to end Notepad while claiming a BSOD. Kill passes the listed
  start time with the PID, so a process that closed while the confirmation was open, and whose ID Windows
  gave to another program, is reported as already closed and left alone; SysManager's own process is
  refused.
- `BatteryHealthViewModel` — charge %, health %, wear, cycle count via WMI. A failed read sets `ReadFailed` and says so rather than "no battery"; a failed refresh keeps the last reading.
  After the battery read, and only when there is a battery, it reads the capacity history through
  `IBatteryReportService` into `CapacityChart`, `WearVerdict` and the card's messages; a failed history read after
  a good one keeps what was shown. Its internal constructor takes both reads, so a test needs neither WMI nor
  `powercfg`.
- `BatteryCapacityChart` — the battery's capacity history as a line in percent of new, with a dashed 100% line, on
  a scale from 70% (or lower) to just above 100%, so a small loss is not drawn as a cliff. The same paint lifecycle
  as `SpeedTrendChart`.
- `UninstallerViewModel` — winget-based app uninstaller with batch support, and the Left behind card
  that follows an uninstall.
- `PerformanceViewModel` — per-tweak performance tuning with snapshot restore. Refresh is off while `IsBusy`,
  and a read leaves `NeedsReboot` alone, so a graphics change's reboot notice lasts the session (#2607).
- `PingViewModel` — live ping monitoring with latency chart and health verdict.
- `TracerouteViewModel` — auto-traceroute + manual trace. Its Start/Stop drive the auto-trace monitor in
  `NetworkSharedState`, which traces the Ping tab's targets plus the host typed here.
- `SpeedTestViewModel` — HTTP (Cloudflare) and Ookla speed tests. `DescribeFinished` writes the line under a
  finished run, naming an upload or ping that was not measured. Its internal constructor takes the engine, so a
  test runs it without a network. `OoklaTrend` and `HttpTrend` are redrawn from their history list on every
  change to it, so a trend can never show a run its table does not.
- `SpeedTrendChart` — one engine's history as a chart: download, upload and a dashed usual-download line, plus
  the sentence `SpeedTrendAnalyzer` writes. Builds its SkiaSharp paints once, repaints them through
  `ChartTheme` on a theme change, and releases them and the theme subscription in `Dispose`.
- `NetworkRepairViewModel` — DNS flush, Winsock reset, TCP/IP reset.
- `NetworkSharedState` — shared targets, buffers, pinger, tracer and health verdict for the Ping, Traceroute,
  Speed Test and Network Repair view-models.
- `ServicesViewModel` — Windows services management with gaming recommendations.
- `DriversViewModel` — driver inventory via Win32_PnPSignedDriver. `ReadScan` reads the exit code together with the output: a failed scan keeps the last list and says so (`ListFailed`), and an error part-way keeps what was listed, marked incomplete.
- `LogsViewModel` — friendly Event Log viewer. Adds events in batches of 50 through `UiThread.Post`, which
  asks the dispatcher whether it is on the UI thread; comparing `SynchronizationContext` instances, as it once
  did, fails there after the first await, and `ArchitectureTests.NothingComparesSynchronizationContextInstances`
  keeps that comparison out of the code.
- `AboutViewModel` — version info, auto-update, release history. BUILD is `BuildCommitOf` the informational
  version, the commit the build stamps after its '+', rather than the exe's file date (#2611).
- `WindowsFeaturesViewModel` — list, enable, disable Windows optional features. Takes the shared `ISessionRestorePoint` snapshot before the first toggle of the session — after the confirmation and after the elevation refusal, so neither declining nor being unelevated spends the one point Windows grants per day.
- `AppAlertsViewModel` — monitors new app installations via FileSystemWatcher + registry.
- `ShortcutCleanerViewModel` — scans and removes broken desktop/Start Menu shortcuts.
- `AppBlockerViewModel` — block/unblock apps via IFEO (Image File Execution Options) registry mechanism. A block list that could not be read sets `ListFailed`; a failed refresh keeps the list and its warning.
- `FileShredderViewModel` — secure multi-pass file overwrite and deletion.
- `BulkInstallerViewModel` — batch app installation via winget with progress tracking. Install Selected confirms first, because winget turns an install of an installed app into an upgrade.
- `DnsHostsViewModel` — DNS server configuration and hosts file editor in one tab.
- `PrivacyViewModel` — Windows privacy and telemetry toggles via registry. Apply takes the shared
  `ISessionRestorePoint` snapshot first — after the confirmation, so declining costs nothing.
  `Grouping` lists the switches by topic or by reach (`PrivacyToggle.Reach`, from the hive), with
  `GroupByReach` building the "Just you" and "Everyone on this PC" sections: the grouping the Tweaks Hub
  tab added over this one, which listed exactly these switches and was merged in (#1517). As an
  `ISearchDestination` it opens by reach when the sidebar search that found it said "tweak" or "tune
  windows", the searches that used to find Tweaks Hub. Each moved
  switch is `IsPending`, and `ApplyText` carries the count. Privacy choices imported from a profile
  arrive through `IPrivacyChoicesHandoff` and are staged as pending changes, never written: when the
  tab is shown (`IsActive`, set by `MainWindowViewModel.SetActive`) or when a load of its toggles
  finishes, but not while a load is running or while Apply waits for its restore point, which stages
  them once its write is done. A switch the profile does not name keeps what the user had, the filter
  goes back to All, and the status says how many switches moved and how many of those need
  administrator rights that this session does not have.
- `ContextMenuViewModel` — scan and manage Explorer right-click context menu entries.
- `SystemReportViewModel` — generate a read-only full-system snapshot, opening with a health verdict and recent problems, and export it as text, HTML, or JSON.
- `EnvironmentVariablesViewModel` — view/edit User and System environment variables with a dedicated PATH editor (reorder, dedupe, missing-folder detection); staged edits with a one-time backup.
- `CliInterfaceViewModel` — read-only reference tab listing the headless CLI commands (sourced from `CliRunner.Commands`) with copy-to-clipboard; documents the flags, runs nothing itself.
- `ScheduledMaintenanceViewModel` — register/update/remove a single recurring Windows task that runs SysManager headless (temp cleanup) daily/weekly; shows last/next run + last result. Create and remove are confirmed; only SysManager's own task is touched. The standby purge is not offered: it needs administrator rights the task does not have, and `StandbyPurgeWarning` says so for a purge schedule saved before it was taken out (#2593).
- `UndoChangesViewModel` — the Undo Changes tab (#1525): lists what `IUndoChangesService.ScanAsync` finds as
  `UndoChangeRow`s, each putting one change back after a `DialogService.Confirm` that says what will change,
  then writing the activity log when something changed, and looking again. A row that needs administrator
  rights SysManager does not have is marked and cannot be pressed; the Settings Watchdog row opens that tab
  instead. Looks again whenever it is shown (`IsActive`, set by `MainWindowViewModel.SetActive`), and once
  more when a look was asked for while one ran; Refresh is off while a look or a put-back runs. A look or a
  put-back that throws says so rather than leaving its progress line. Also lists the tabs whose switches are
  their own undo (`UndoSwitch`) and, looked for only when shown and outside Busy, the newest restore point —
  asked for only as an administrator, one question at a time, called off when the tab is disposed. No "undo
  everything".
- `RestorePointsViewModel` — list, create, and restore Windows System Restore points (admin for all three, since Windows refuses a standard user the list, which the empty state says rather than reporting none; restore reboots, gated by confirmation).
- `LegacyPanelsViewModel` — one-click launcher for the fixed catalog of classic Windows applets (pure launchers, no system modification).
- `SystemFixesViewModel` — consolidated one-click repairs (SFC, DISM `/RestoreHealth`, Windows Update reset, WinGet reinstall, Restart Explorer, Rebuild icon & thumbnail cache) with per-fix confirmation + live output; opens netplwiz for secure auto-logon. The network-stack resets live on Network Repair.
- `BootAnalyzerViewModel` — read-only boot-time history + slow-component breakdown from the Diagnostics-Performance log, with a trend vs recent average; needs admin to read the log.
- `TimerResolutionViewModel` — request the finest Windows timer resolution (≈0.5 ms) for lower game input latency, or release it; shows the live effective value.
- `FileLockViewModel` — find which processes are holding a file/folder (Restart Manager) and optionally end a selected one after confirmation; critical processes are protected. A failed check and a path that does not exist are reported as such, never as "no process". End process passes the locker's start time with its ID, so a locker that closed and whose ID Windows gave to another program is reported as already closed rather than that program ended (#2514); an ended or closed locker is followed by a fresh check, with the outcome put in front of its result.
- `DisplayProfileViewModel` — list displays and supported resolution/refresh modes and switch between them; applies for the session with a 15-second auto-revert safety net.
- `CpuAffinityViewModel` — pin a running process to specific logical CPUs with P-core/E-core labels on hybrid CPUs; per-process and reverts on process exit. The picker has a name/PID filter over a backing list (like `ServicesViewModel`) and preserves the selection by PID and start time across a refresh. Every read and change passes `RunningProcess.StartTime` with the PID, and a change that fails for a process that has closed refreshes the list and says so (#2514); `RunningProcess.PinnedDisplay` surfaces the already-read affinity mask as a neutral "N of M cores" marker via the pure, tested `DescribeAffinity` helper.
- `DefenderViewModel` — view Microsoft Defender status, toggle PUA / Controlled Folder Access, and manage scan-exclusion folders; every change is admin-gated, confirmed, and verified by read-back (Tamper Protection can silently reject). All four changes share one `RunOperationAsync` funnel that takes the shared `ISessionRestorePoint` snapshot before the first of them, so no command can skip it; each keeps its own failure wording, passed in.
- `TaskSchedulerViewModel` — browse Windows scheduled tasks with a safety classification and enable/disable them (reversible, never deletes); system tasks warn before disabling, changes verified by read-back. Holds TWO cancellation sources: one for the task-list scan (driven by the Cancel button) and one for the per-selection run-info query, which each new selection supersedes so arrow-keying the grid cannot queue a PowerShell round-trip per row. Enable/disable is deliberately NOT cancellable — its script writes then reads back, so a cancel between the two would leave the task toggled while the grid showed the old state.
- `DarkModeViewModel` — switch the Windows light/dark theme manually or on a fixed-time schedule (DispatcherTimer poll while the app runs); persists the schedule.
- `AudioMixerViewModel` — per-app volume mixer (Volume Control tab): lists apps playing on the default render device with a volume slider, mute toggle, and a live peak meter. Two loops drive it, both idle while the tab is hidden (`IsActive`) and both sampling off the UI thread: membership reconciles on a ~1&#160;s cadence, and the meters refresh every 50&#160;ms via one batched `GetPeaks` call per tick. The peak loop *parks* on an activation gate while hidden rather than ticking and skipping — at 50&#160;ms a skip-check still queues 20 Dispatcher continuations a second. (Per-row `GetPeak` calls on the UI thread were the 1.65.11 stutter; one batched call off it was the fix.) Rows reconcile in place by session id (a wholesale replace would drop a slider mid-drag). Adds per-app output-device routing (via the guarded `AudioPolicyConfigFactory`; falls back to guiding the user to Windows sound settings when the OS lacks the interface) and named volume presets (persisted by `VolumePresetService`, keyed by exe name so they re-apply across restarts). Row VMs (`AudioSessionRowViewModel`) propagate volume/mute/route to the service, with a re-entrancy guard so an external change surfaced by a refresh is not echoed back. The reconcile writes its app count and the device all sound plays through via `ViewModelBase.ShowRefreshStatus`, so it never replaces an outcome on the status line. Above the rows, `PcVolumeViewModel` is the This PC card (#1588): the whole PC's volume, mute and level — read on the same one-second pass and metered on the same 50&#160;ms tick, with the row's echo guard and drag hold — and a picker that moves all sound to another device, followed at once by a fresh read of the devices and the apps. Where the switch cannot bind, the card names the device and offers Windows' sound settings. The view's slider handlers reach both through `IAdjustableVolume`. Neither the PC volume nor the device is part of a preset.
- `StandbyMemoryViewModel` — live memory stats (2s poll) with on-demand and threshold-based auto-purge of the Windows standby list; purge needs admin. Built at startup, so a saved auto-purge watches from launch without the tab being opened.
- `GamingProfileViewModel` — one-click game mode (Gaming Profile tab, Preview): gathers the desired reversible optimizations plus an optional running-game target and delegates to `IGamingProfileService` to apply/revert them as a unit. Reports the batch outcome honestly (applied / needs-admin / failed), seeds its toggles from the last-used config, and offers to restore a leftover session on startup (crash recovery). The game target carries the listed start time, the selection survives a refresh only for the same process, and a game that had closed is refused with a refresh of the list (#2559). A game that closed while game mode was starting is reported, with anything that could not be restored, and the list is refreshed (#2563). Fully reversible; killing background apps and named per-game profiles are intentionally out of scope for the preview.
- `ProfileViewModel` — export/import SysManager's config as a portable JSON profile with selective sections and version checking. The file sections are whatever `ProfileService.Catalog` lists (nine today: theme, speed-test history, update-check preference, dark-mode schedule, gaming profiles, volume presets, close-button behaviour, standby-memory preference, app-icon fetching), and a section is skipped on export when its file does not exist yet. A tenth section, the Privacy & Telemetry choices, is listed once a protection is on. Export passes the ticked keys to `ProfileService.BuildProfile`, which reads the files and the toggles at that moment, and the list is re-read whenever the tab comes back on screen (`IsActive`, set by `MainWindowViewModel.SetActive`), so the tab never exports contents it read earlier in the session. Import writes the file sections only; the privacy choices are read by `ProfileService.ReadPrivacyChoices`, left in `IPrivacyChoicesHandoff`, and the user is taken to the Privacy & Telemetry tab through `INavigationService` to review them (#1530).
- `DebloaterViewModel` — list and remove preinstalled Store apps with a curated bloat preset; system-critical packages are denylisted; removal is per-user and reversible via the Store. Takes the shared `ISessionRestorePoint` snapshot before the first removal, and words it honestly: System Restore does not bring Appx packages back, so the Store reinstall leads and the point is described as covering the rest of the system.
- `BrowserCleanerViewModel` — scan per-browser cache/history/cookies/sessions with sizes and clean the selected categories; cookies/sessions default unticked. Its second half, switched with the same pills as Privacy & Telemetry's grouping, is the Extensions view (#1526): "Look for extensions" runs `IBrowserExtensionService.ScanAsync`, builds one `ExtensionGroupViewModel` per profile (rows worded by `ExtensionPresenter`, icons decoded and frozen off the UI thread) and counts them in the toolbar; "Manage in …" opens the browser on its own extensions page and puts the page on the clipboard, since whether it opened cannot be seen, and while SysManager runs as administrator it starts nothing and says why. It never changes a browser. A sidebar search for "extensions", "add-ons" or "browser ads" opens this half (`ISearchDestination`), and F5 runs whichever half is shown.
- `EdgeOneDriveViewModel` — reversibly de-integrate Edge and OneDrive (Edge/OneDrive Remover tab): OneDrive is fully removed per-user (no admin) with restore; Edge is only disabled & de-integrated (background/startup-boost policy + auto-update tasks, admin-gated) with restore — never uninstalled; guides the user to Windows settings to change the default browser. Every action confirms first and reports its honest outcome (success / needs-admin / not-applicable).
- `PrivacyMonitorViewModel` — read-only camera/mic/location access history from the consent store; hands off to Windows settings to change permissions. `Describe` and `DescribeEmpty` name only the capabilities that were read; a read that got nothing keeps the previous list.
- `ResourceHistoryViewModel` — draws the samples the always-on `ResourceHistoryService` sampler records, for the
  chosen range and downsampled for the chart; sets the retention and exports the visible range as CSV.
- `SettingsWatchdogViewModel` — saves a baseline of the watched settings (asking first, and keeping the old file
  aside, when the saved one cannot be read), lists what has drifted since, exports the drift as CSV, and restores
  every drift `SettingDrift.CanWriteBack` allows after one confirmation. Undo Changes' row for it opens this tab.
- `NotificationBlockerViewModel` — lists the apps that have shown notifications with a per-app mute and the master
  switch; flips stay pending until Apply, which confirms and writes them, or Discard.
- `BandwidthMonitorViewModel` — live total download/upload speed with a rolling throughput chart and a per-app usage list (Bandwidth Monitor tab). Polls the active `IBandwidthMonitorService` on a ~1&#160;s loop, paused while the tab is hidden (`IsActive`) and wrapping every sample in one `Task.Run` so no source runs its work on the render thread, reconciling rows in place by PID so icons/order don't flicker. Defaults to the no-admin connection source; when elevated and opted in, switches to the ETW source for precise per-app rates and falls back automatically if ETW can't start. Threshold-alert derivation and rate formatting come from `BandwidthFormat`/`FormatHelper`. A stored range is loaded through a function its internal constructor takes, so a test decides when the load finishes; a load that finishes after `Dispose` changes nothing, like a poll that does. The poll writes its app count through `ViewModelBase.ShowRefreshStatus`, so an export, a refusal or a loaded range stays on the status line. Read-only.
- `ConsoleViewModel` — shared, per-tab scrollable console (each tab gets its own
  instance; lines capped at 5000 to bound memory) backing the in-app Console mirror
  used by Cleanup, Windows Update, System Health, App Updates, and System Fixes —
  the views whose XAML references it.

## Services

Thin wrappers around the underlying platform. Each service is designed to be
unit-testable. Services that a view-model needs to substitute in tests sit behind
an interface seam. Twenty-six are registered against their implementation in `ServiceRegistration.cs` and
constructor-injected: `IPowerShellRunner` (PowerShellRunner), `IWingetService` (WingetService),
`ITuneUpService` (TuneUpService, the Dashboard's Quick Tune-Up and Quick Cleanup),
`IAppBlockerService` (AppBlockerService), `IBatteryReportService` (BatteryReportService, Battery Health's capacity
history), `IBrowserExtensionService` (BrowserExtensionService, Browser Cleaner's Extensions view),
`ICleanupPreScanService`, `IContextMenuService`, `ILeftoverService`,
`ICpuAffinityService`,
`IFileLockService`, `INotificationBlockerService`, `ISettingsWatchdogService`, `ITimerResolutionService`,
`IUpdateService`, `IWindowsThemeService`, `IWindowsUpdateService` (WindowsUpdateService,
shared by the Windows Update tab and the Dashboard's check), `ISpeedTestService` (SpeedTestService, for the
Dashboard's quick test; it forwards to the concrete singleton the network tabs take), `IAudioMixerService`, `INavigationService`
(NavigationService), `IPrivacyService` (PrivacyService, read by Privacy & Telemetry and the
profile), `IPrivacyChoicesHandoff` (PrivacyChoicesHandoff), `IUndoChangesService` (UndoChangesService, the Undo
Changes tab), `IPutBackSignal` (PutBackSignal, which tells a tab that Undo Changes put its change back),
`IGamingProfileService`, and `ISessionRestorePoint` (the last two via a factory). `INavigationService`, like
`ISpeedTestService`, forwards to the concrete singleton.

`INavigationService` is the seam a tab uses to send the user to another tab, so a tab that diagnoses
something can offer the tab that fixes it. It is **late-bound**: the shell builds the tab view models and
those view models take the service, so the service cannot take the shell in its constructor — the shell
calls `NavigationService.Bind(this)` instead, implementing `INavigationTarget`. A destination that
implements `IFilterable` can be arrived at pre-filtered, which is how Boot Analyzer opens Services already
narrowed to the service that slowed boot. This replaced a
`Application.Current.MainWindow.DataContext as MainWindowViewModel` lookup in `DashboardViewModel`;
`NoViewModelReachesTheShellThroughTheLiveWindow` stops that returning.
A tab opened from the sidebar search that implements `ISearchDestination` is told what was searched for,
which is how a search for "tweaks" or "tune windows" opens Privacy & Telemetry grouped by reach (#1517),
and one for "extensions" or "browser ads" opens Browser Cleaner on its Extensions view (#1526).

Four further seams exist but are reached differently, so grepping `ServiceRegistration.cs` for them
finds nothing:

- `IDialogService` — consumed through the static `DialogService.Instance`, which tests swap for a
  stub. That is why the swapping tests must sit in the serialized `ProcessWideStatics` xUnit collection;
  a fitness function in `ArchitectureTests` enforces it.
- `IBandwidthMonitorService` — the one seam with two shipping implementations
  (`ConnectionBandwidthSource` and `EtwBandwidthSource`), injected as factories so the Bandwidth
  Monitor can switch source at runtime. See the sources note further down.
- `ICleanupRoots` — the directories Deep Cleanup's scan is built from, taken by
  `DeepCleanupService`'s second constructor. Production's constructor takes only the `IPowerShellRunner`
  and passes `SystemCleanupRoots`, so `AddSingleton<DeepCleanupService>()` needs no change; a test that
  wants the real roots passes `new SystemCleanupRoots()` itself (#2602). It exists because the scan read
  `Environment.GetFolderPath` and `DriveInfo.GetDrives()` inline, which left its logic assertable only
  as "the size is non-negative"; with the roots injected a test points it at a tree it built and asserts
  exact counts, the 30-day cutoff and which categories arrive pre-selected. A fitness function keeps the
  machine reads out of the service.
- `ILeftoverEnvironment` — the folders, uninstall entries and registry keys the Uninstaller's leftover
  search reads, taken by `LeftoverService`'s second constructor beside the Recycle Bin and key-deletion
  delegates. Production's constructor passes `SystemLeftoverEnvironment`, `RecycleBinHelper.SendToRecycleBin`
  and an HKCU delete; the test constructor's two delegates default to refusing, so a test that leaves them out
  cannot reach the real Recycle Bin or registry (#1527).

Key services:
- `PingMonitorService` / `TracerouteService` / `TracerouteMonitorService` —
  network probes on `System.Net.NetworkInformation.Ping`. Traceroute walks the TTL itself
  (`PingOptions(ttl, true)`, reading `TtlExpired` replies) rather than shelling out to a
  command-line tool, which is why it needs no admin rights.
  `PingMonitorService` takes an optional `TimeProvider` (defaulting to `TimeProvider.System`) that
  supplies its between-tick delay, so a test drives the cadence rather than measuring it: the
  interval-change test used to sleep 1.2 real seconds and count samples, which reported the host's
  spare CPU and went red on a loaded machine. It now asserts the exact delay the pump asks for.
- `SpeedTestService` — HTTP speed test against Cloudflare plus the Ookla CLI,
  auto-downloaded on first use. Behind `ISpeedTestService` for the Dashboard's quick test. A ping nobody
  answered and an upload the server refused are null, not 0: `MeasurePingAsync` is handed the single ping and
  `UploadMbps` is pure, so both are tested without a network. `SpeedTestResult` formats them, "—" when missing.
- `PowerShellRunner` — wraps `System.Management.Automation` to run scripts
  and stream output line-by-line. Always launches spawned processes from
  `System32` so `Access is denied` never bites on `chkdsk` etc. Every runspace,
  elevated or not, is an isolated Windows PowerShell 5.1 child with module discovery
  limited to canonical machine-owned roots, avoiding process-global environment
  mutation. Never in process: that runspace would be PowerShell 7 hosted from the SDK
  package alone, which loads `Microsoft.PowerShell.Core` and nothing else, and until
  #2476 a standard user's session used it, so every script that named a Utility,
  Management, Appx or Defender cmdlet failed and returned nothing. Scripts started as
  a separate `powershell.exe` (`RunScriptViaPwshAsync`) keep per-user modules when
  unelevated, which is where PSWindowsUpdate lives.
  A runner REUSES its runspace across calls, because starting that child
  and completing a remoting handshake with it is the slow part and the services
  that use it call in bursts (`DnsService` six times, `EdgeOneDriveService` four).
  Three properties make reuse safe: the runspace state is re-checked on every
  lease, so a child killed from outside is discarded and rebuilt rather than
  failing every later call; pipelines are serialised behind a gate, because a
  runspace runs one at a time and a shared one turns concurrent calls into a
  conflict; and the runspace is evicted after ~20 s idle, which matters because
  eleven runners are constructed directly in `MainWindowViewModel`'s designer graph
  and nothing ever disposes them — without eviction a dozen `powershell.exe`
  processes would be resident for the whole run. `Dispose` releases it
  deterministically where a consumer is disposed.
- `WingetService` — shells out to `winget` and parses its table output.
- `WindowsUpdateService` — drives Windows Update through the WUA COM API
  (scan, select, install) with progress reporting, behind `IWindowsUpdateService`; backs
  `WindowsUpdateViewModel`, and the Dashboard's Check Windows Updates action, which only scans.
- `WindowsUpdatePolicyService` — reads/writes the documented Windows Update
  deferral policy keys (defer feature updates, bounded pause, restore default).
  Injectable registry root for tests; deliberately offers no permanent
  disable-updates option, only a bounded pause. `Read` returns null for a read Windows refused,
  which the tab reports as not known (`WindowsUpdateViewModel.DescribePolicy`); no policy key at all
  is the defaults.
- `DiskHealthService` — pulls SMART data through WMI.
- `MemoryTestService` — scans WHEA / MemoryDiagnostics events. A log it cannot read is thrown,
  never returned as zero errors, so the Dashboard and System Health can say the check did not run.
- `EventLogService` + `EventExplainer` — read Windows Event Log and attach
  human-readable explanations. Exposes `LastOutcome` (`Ok` / `AccessDenied` /
  `LogNotFound` / `Unavailable`) alongside the streamed entries, because a refused
  log and an empty log are otherwise indistinguishable to the caller — the Security
  log needs elevation, and swallowing that made the UI report "0 events". Reset at
  the start of every query so a past refusal cannot outlive it. Because the outcome is instance
  state, the System Report gets its own `EventLogService` (a factory registration) rather than
  the Logs tab's, so neither can be told the other's answer. `EventExplainer.TryExplain` returns
  the written explanation for a known (source, ID) only, which is what the report uses.
- `HealthAnalyzer` — the Ping tab's per-target latency, jitter and loss into one network verdict: is it the PC,
  the ISP or the server. Pure and static; `NetworkSharedState` calls it.
- `SpeedVerdictAnalyzer` — a speed-test result into a plain-English verdict
  ("Fast connection", what that allows) plus a comparison against the previous
  run on the same engine. Pure and static like `HealthAnalyzer`, so the
  judgement is unit-testable without a network; deliberately never emits the
  failure colour, because a slow plan is not a fault.
- `SpeedTrendAnalyzer` — one engine's saved runs read as a trend: the median download as "usual", and one
  sentence about runs under half of it (only the latest, several in a row, or one earlier). Pure and static
  like `SpeedVerdictAnalyzer`; a run that measured no download is not counted.
- `SystemInfoService` — OS / CPU / RAM / uptime snapshot. Static hardware (OS caption,
  CPU model, disk models, DIMM inventory) is queried once via WMI and cached; the three
  DYNAMIC values are syscalls, because the Landing tab polls this every 300 ms —
  `GetSystemTimes` deltas for CPU load, `GlobalMemoryStatusEx` for memory,
  `Environment.TickCount64` for uptime. The syscalls are constructor-injected, so the
  delta arithmetic is unit-testable without a real machine.
- `BiosService` — read-only BIOS/firmware + motherboard info (Win32_BIOS,
  Win32_BaseBoard, UEFI/Secure-Boot registry) plus a pure manufacturer
  support-URL resolver for BIOS updates; never flashes firmware. Consumed by
  `SystemHealthViewModel`.
- `TuneUpService` — orchestrates the Quick Tune-Up wizard: temp cleanup,
  Recycle Bin, shortcut scan, disk SMART, uptime/RAM checks. Deletes the temp files outright and empties the
  Recycle Bin, both behind the Dashboard's confirmation. Behind `ITuneUpService` for
  the Dashboard, whose tests run both quick actions against a substitute. The temp sweep itself is also a
  static method, shared with the Cleanup tab and the CLI. The shortcut, disk and vitals checks are internal
  static methods handed the call to make. Each returns null when it could not run, which the result lists in
  `TuneUpResult.NotChecked`, so a failed check is never counted as a passed one.
- `HealthScoreService` — aggregates disk health, free space on the system drive (a quarter of the score), RAM,
  uptime, and battery wear into a single 0–100 score with color-coded verdict and recommendations.
  `OverallScore` weights the battery only when `BatteryWasMeasured`: a battery whose capacities could not be read is left out, like a
  desktop's, and named in `UnavailableComponents`. `ComputeAsync` gathers and then calls the static
  `Evaluate`, which scores evidence already in hand; the System Report calls `Evaluate` with the snapshot and
  disks it read itself, so the report and the Dashboard share one copy of the scoring.
- `TrayIconService` — system tray icon with background monitoring (60s),
  tooltip updates, context menu, and Windows toast notifications. The menu's status header
  is refreshed from the 60s poll on `Opened` rather than rebuilt per tick, so a menu nobody
  opens costs nothing. Its shortcuts come from one `QuickJumps` table and route through a
  caller-supplied `Action<string>` — the View layer knows the shell view-model, this service
  does not, which is what keeps Services free of a ViewModels dependency. Navigation and
  read-only only: no system-mutating verb is reachable from the tray, because a confirmation
  dialog is not visible there.
- `LogService` — Serilog wrapper with a rolling file sink, wrapped by
  `UserPathScrubbingSink`. That wrapper renders each event and runs the finished line through
  `SanitizePath` before writing, so the Windows user name cannot reach the log regardless of
  which call site produced it. Sanitizing at the sink rather than per call site is deliberate:
  `SanitizePath` was called at 15 of 75 path-logging sites, so exposure depended on which
  service happened to fail, and no call-site fix can reach a path inside exception text —
  Serilog renders that separately from the message properties.
- `FixedDriveService` — enumerate fixed NTFS/ReFS volumes.
- `DeepCleanupService` — scan-first safe cleanup (vendor caches, gaming
  launcher caches, Windows caches). Per-file try/catch so locked files
  are skipped, not forced. The Delivery Optimization bucket (`IsDeliveryOptimizationCache`) is the one
  cleaned through the runner, with `Delete-DeliveryOptimizationCache -Force`, and its freed total is measured
  on its folders before and after; without a runner, or when Windows refuses, it reports that and deletes
  nothing itself (#2602).
- `CleanupPreScanService` (`ICleanupPreScanService`) — sizes the temp folders and the current user's
  Recycle Bin for the two labels Quick Cleanup shows before the user asks for anything. Behind an
  interface because the work used to sit inline in `CleanupViewModel`, whose constructor fires it and
  forgets it: building the view-model started a recursive walk of both locations, 30 times over in one
  unit-test file, and one test then asserted the walk finished inside fifteen seconds.
- `LargeFileScanner` — read-only biggest-files discovery; skips `$Recycle.Bin`, System Volume Information,
  `Windows\WinSxS`, `Windows\System32\config` and `Windows\CSC` as subtrees, and pagefile/hiberfil/swapfile by
  exact file name. The two are separate lists because the subtree one is only ever
  asked about a directory, which is why the three file names sat in it unread.
- `UpdateService` (`IUpdateService`) — GitHub Releases API client with explicit
  `SocketsHttpHandler`, retry, and surfaced error messages. The seam carries every
  instance member so a consumer never has to hold both it and the concrete class;
  the version/repo statics stay static, since they read assembly metadata and the
  local filesystem and so need no double. Without it a test that lets the About
  tab's startup check run had to call `api.github.com` for real.
- `UpdateApplier` — runs on relaunch to swap the freshly-downloaded exe over the
  old one and restart, before any DI/UI is built (see the Updates flow below).
- `StartupService` — enumerate and toggle startup programs across seven kinds of
  location, each a distinct `StartupSource` because the enable/disable state lives
  in a different place per kind: the `Run`/`RunOnce` keys in both hives, the
  `Wow6432Node` pair (approved-state in `StartupApproved\Run32`), both shell Startup
  folders (per-user approved under HKCU, Common under HKLM), the
  `Policies\Explorer\Run` key (no approved-state at all — shown, never toggled), and
  third-party scheduled tasks read from the `TaskCache` registry (`\Microsoft\` and
  `\Windows\` excluded; toggled through `schtasks /Change`, so the same task the
  Task Scheduler tab owns). `TaskCache` has no enabled flag, so a task's state comes from
  its definition under `System32\Tasks` (`Settings/Enabled`), read without DTDs and only
  inside that folder. `TaskCache` is readable by SYSTEM and Administrators only, so
  `ScanAsync` returns a `StartupScan` that says whether the tasks could be listed, rather
  than a list that silently has none. Enriches each entry from `ProcessDescriptionService`
  (plain-language description + `ProcessSafety`) keyed on the executable's base
  name; unrecognised programs are left blank so the UI never guesses a safety. A second
  post-pass, `VerifySignatures`, answers "who really made this" with a certificate rather
  than `FileVersionInfo.CompanyName`, which is the string the `Publisher` column shows and
  which any program can set to "Microsoft Corporation". It uses the shared
  `Helpers/SignatureVerdict`, which asks Windows via `WinVerifyTrust` — **not** the managed
  chain the two fail-closed gates build. Results are cached per resolved executable path,
  since several entries pointing at one exe is normal. `ResolveExecutablePath` is the single
  answer to "which file is this entry", shared with `ExtractPublisher`, so the Publisher and
  the certificate can never describe different files.
- `Helpers/SelectionCarry` — keeps the user's ticks when a bound list is rebuilt, defined once for
  the nine tabs that rebuild one from a fresh scan: System Health drives, Deep Cleanup categories,
  Shortcut Cleaner, Browser Cleaner, App Updates, Profile Export, App Blocker, Debloater and
  Uninstaller. The six it started with used to re-derive the tick from a default, and in four the
  fresh rows arrive pre-selected — so a refresh did not forget the choice but REVERSED it, and the
  next action then deleted, upgraded or exported exactly what had been excluded. `RefreshOnF5`
  reaches all six (#2300, #2301, #2304). Rows opt in through `ISelectableRow`, so the helper needs a
  key selector rather than getter/setter delegates. Three rules live here rather than at nine call
  sites: an EMPTY previous list is the first population (a present list with nothing selected is a
  *decision* and is honoured — testing "is anything selected" instead would re-tick everything, which
  is the bug); the optional `carriedADecision` filter exists only for Deep Cleanup, whose default is
  *measured*, so an empty category unticked by the scan rather than the user takes the default again
  once it has content (the other eight default from constants and pass null); and the key's
  uniqueness is now CHECKED rather than assumed. A key claimed by two rows neither throws nor shows
  — the grid looks right and a tick turns up on a row nobody touched, which is what two identically
  named Firefox profiles cost in Browser Cleaner (#2402). `Apply` reports the colliding keys and logs
  them at Debug without changing what a collision does, and `DuplicateKeys` is public so a service
  can assert the invariant over its own scan output — the only way to catch a duplicate that comes
  from the DATA rather than from the choice of key (#2405).
- `Helpers/SettlingProgress` — the `IProgress<T>` a tab hands to a service it awaits, defined once
  for the eleven sites across nine view models whose callback writes a property their own post-await
  code writes again as its final value. `Progress<T>` captures the `SynchronizationContext` in its
  own constructor and delivers each report by POSTING to it, so the caller's terminal write wins
  only by queue order — an ordering nothing states, and one `ConfigureAwait(false)`, one `Task.Run`
  or one continuation that drains later revokes it. It has already happened: an erased file stayed
  labelled `"Shredding pass 2/3..."` forever, and the required unit check went flaky because a test
  has no context at all, which makes both writes unordered pool work (#2391, #2392). The worst of
  the rest replaces an instruction the user has to act on — "Download complete. Click Install to
  restart with the new version." — with a byte count. `SettleAfterAsync` is the entire surface: the
  caller awaits the operation THROUGH the reporter, so the flag is set in the wrapper's `finally`
  and the success arm, every catch arm and the enclosing `finally` are all past it without a line
  each. Exposing the latch instead would have made the correct use the optional one. Dashboard's
  Quick Tune-Up keeps the raw type on purpose — its callback writes nothing the method writes after
  the await — and `ArchitectureTests` asserts it stays in the non-racing set, which is the tripwire
  for the "from the first await onward" bound that decides which sites the rule reaches. That guard
  checks BOTH halves, because they fail independently: the TYPE at each construction, and the
  HANDOVER at every later use of the reporter. A site that builds the wrapper and then passes it to
  the service directly is the original defect with the primitive sitting unused beside it.
- `Helpers/SafeFileWalk` — the one directory walk every service that deletes or overwrites what it
  finds goes through. Four rules live here rather than at eight copies of a stack loop: never enter a
  reparse point (the root, a directory, **or a file**), skip the excluded subtrees, honour the
  cancellation token between directories, and absorb a listing that throws from `MoveNext` rather
  than from the call that created the enumerator. It exists because there WERE five copies, each
  with a comment claiming it mirrored the others, and two of those four rules had been added to one
  copy and not the rest — the reparse-point-file skip and the `MoveNext` guard. Per-service tests
  could not see it: every copy passed its own. `EnumerateGuarded` returns a list rather than yielding,
  and that is load-bearing — a `yield return` cannot sit inside a `try` with a `catch`, which is
  exactly why four of the five copies never enclosed their own iteration. `SafeWalkOptions` is a
  record rather than optional parameters so no caller can bind an exclusion path to the search
  pattern by position. Three read-only scanners (`DiskAnalyzerService`, `DuplicateFileService`,
  `LargeFileScanner`) still keep their own loop: they prune the traversal by a path predicate, and
  one needs an access-denied tally, neither of which `SafeWalkOptions` carries. That exemption is a
  named list in `ArchitectureTests`, and it is **partial and asserted**: each exempted scanner must
  still test its traversal root through `SafeFileWalk.IsReparsePoint`, because the root is the one
  the user picks and a link there sends the whole scan somewhere else. The guard fails if an
  exempted file stops walking a tree (a stale name nobody would remove) or stops guarding its root.
- `Helpers/Csv` — RFC 4180 field escaping for the Export CSV buttons, defined once. It exists
  because the first exporter (`ResourceHistoryService.ToCsv`) writes its fields raw, which is
  safe for numbers and fixed-format timestamps but not for the app names, setting descriptions
  and folder paths the later exports carry: `C:\Users\me\Music\Grieg, Peer Gynt` is an ordinary
  folder name that silently becomes two columns without quoting. `Field` quotes only when the
  value contains a comma, a quote, CR or LF; `AppendRow` terminates with CRLF because this is a
  file format rather than console output. Nothing is trimmed or substituted — a lossy export of
  a path is worse than a quoted one.
- `Helpers/TreemapLayout` — squarified treemap layout (Bruls, Huizing and van Wijk): one rectangle per
  weight, areas in proportion, as square as the order allows, tiling the bounds without a gap. Pure.
- `Helpers/SizeObserver` — attached properties that push an element's laid-out size into a binding,
  because `ActualWidth` cannot be bound back to the source. The Disk Analyzer map lays itself out with it.
- `Helpers/ChartAxisLabels` — the time-axis tick text for the charts that plot saved samples
  (Resource History, the Speed Test trend, the battery's capacity history), defined once with the
  guard that prints nothing for a value that cannot be a sample time, so an axis with no data never
  shows dates in the year 1 (#2371).
- `Helpers/Authenticode` — the two Authenticode operations, defined once: `ReadSigner`
  (three-way `Signed`/`Unsigned`/`Unreadable`, never throws) and `ValidateChain` (one strict
  policy — `ExcludeRoot`, `NoFlag`, fail-closed — with the revocation mode as a parameter).
  `PolicyFor` builds that policy, and it exists because **`Offline` revocation alone does not
  make a chain build local**: a missing intermediate is still fetched over AIA under a separate
  switch that defaults to on, so a caller that chose `Offline` to avoid a request per file made
  one anyway and, with no network, waited out `UrlRetrievalTimeout` for each. The two settings
  are therefore derived from the one argument rather than offered separately —
  `DisableCertificateDownloads` is on exactly when revocation is `Offline`.
  Deliberately holds no policy about what an answer MEANS: an unsigned file is fatal for the
  Ookla download and expected for our own build, so each caller keeps that decision. Two
  calls rather than one because both fail-closed callers compare the subject BEFORE building
  a chain, and a single "inspect" would add a revocation fetch on the path where the subject
  already failed.
  **Its chain validation has only those two gates as callers**, and `SignatureVerdict` calls
  `ReadSigner` for the publisher's name alone. The informational columns used it for their
  verdict and moved off it, because an offline managed chain cannot answer for an arbitrary
  file: measured over 82 running process images it verified 1 and reported failure for 47, on
  `RevocationStatusUnknown` (46 of 48, no cached CRL) and `PartialChain` (29, intermediate not
  local). Loosening the flags enough to pass means `AllowUnknownCertificateAuthority`, which
  accepts any certificate authority and verifies nothing.
- `Helpers/QuitGuard` — the one question every exit asks while something is still running,
  defined once. Closing disposes the tabs, each cancels its work, and a cancelled repair or install
  is ended part-way, so the tray's Exit, closing the window, `AdminHelper.RelaunchAsAdmin` and About's
  install and go-back all go through it. It reads what is running from `OperationLockService`, which
  the repairs, installs, clean-ups, scans and tweaks that take a lock register with, rather than
  keeping a second list. An operation that takes no lock is not seen by it.
  `ArchitectureTests.EveryExit_AsksFirstWhileSomethingRuns` fails any `App.RequestShutdown()` caller
  that does not ask.
- `Helpers/WindowsTrust` — `WinVerifyTrust` behind a four-state answer
  (`Trusted`/`NoSignature`/`Expired`/`NotTrusted`), the mechanism Explorer's Digital Signatures
  tab and Process Explorer use. Over the same 82 images: 46 trusted, 1 genuine failure. Called
  with `WTD_REVOKE_NONE` plus `WTD_CACHE_ONLY_URL_RETRIEVAL`, which is a supported way to say
  "answer from this machine only" — the guarantee `Offline` was reached for and does not give.
  `Classify` is a pure HRESULT map, kept `internal` and tested directly, because it is what
  decides the colour a user sees and the previous mechanism's wrong verdicts came from
  classification rather than from reading. Costs ~25 ms per file and does not get cheaper warm,
  so callers cache and stay off the UI thread.
  **Two questions, in order.** `WTD_CHOICE_FILE` sees only the signature embedded in a file, and
  Windows signs most of its own components through a `.cat` catalogue — 35 of those 82 images carry
  no embedded signature and 12 of the 35 verify through a catalogue (`powershell.exe`, `cmd.exe`,
  `conhost.exe`, the search host). So a `NoSignature` answer gets a second question:
  `CryptCATAdminAcquireContext2` with **SHA-256 by name** (the older `CryptCATAdminAcquireContext`
  implies SHA-1), hash the file, `CryptCATAdminEnumCatalogFromHash`, then `WinVerifyTrust` again with
  `WTD_CHOICE_CATALOG`. The hash is both the lookup key and the member tag; the tag is upper-case hex
  because that is the documented shape, and measurement showed it is **not** load-bearing (forcing lower
  case left every real verification passing). Only `NoSignature` falls through — an expired
  or untrusted embedded signature is an answer, and looking for a catalogue that might disagree would
  be picking the more flattering verdict. Two native handles and an allocation are released on every
  path including the failures, because a leak here is once per unsigned file per refresh on a tab
  that polls. `CatalogSignatureTests` (integration) is the only place this can be verified for real:
  no file a unit test can create is catalog-signed.
- `DuplicateFileService` — three-pass duplicate finder (size grouping →
  partial hash pre-filter → full SHA-256). Read-only, never deletes.
- `DiskAnalyzerService` — folder-level space breakdown with progress
  reporting and system-path skipping. `AnalyzeAsync` returns an `Analysis`: the folders,
  or why the chosen one could not be measured at all (`NotFound`, `IsLink`, `Unreadable`),
  so a failure is never an empty scan. `LooseFilesName` names the entry for files loose in the
  folder, which the drill-in and the map both treat as not a folder.
- `ProcessManagerService` — enumerate running processes, kill by PID,
  open file location. `KillProcess(pid, startTime)` ends that process alone, never its tree, and
  returns a `KillOutcome` (`Ended`, `NotRunning`, `Refused`) so the tab can tell a process that had
  already closed from one Windows would not end. The start time is the listed one: a process ID
  reused while the confirmation was open names a different program, which is left alone.
  `VerifySignatures(entries, cache)` fills the Signature column from the
  running image's certificate, through the shared `Helpers/SignatureVerdict`.
  **Deliberately NOT called from `Snapshot`.** It was, and the cost was measured: ~25 ms per
  file over ~82 distinct images, so `SnapshotAsync` took ~3.7–4.2 s — spent before the list
  appeared, on the tab someone opens *because* something is wrong. Moving it out took the same
  call to ~1.5 s cold and ~0.65 s warm, with nothing verified yet.
  `ProcessManagerViewModel.FillSignaturesAsync` now runs it in batches of ten after the list
  renders: each batch is verified on a background thread and applied when the `await` resumes
  on the UI thread, so **no bound row is ever written from a background thread** — the one
  threading rule this shape has to respect, and the reason verdicts go into a cache first and
  onto rows second. `StartSignatureFill` allows one pass at a time, because the tab
  auto-refreshes every second while a full pass takes seconds; a running pass re-reads the
  unverified rows before each batch, so processes that start mid-pass are picked up.
  The cache is the caller's **for one pass, not the tab's lifetime**: only newly-started
  processes are ever unverified, so it lives exactly as long as the work, and a longer-lived
  one would owe an answer for a file replaced on disk. `NewSignatureCache()` exists so a
  caller does not have to know the comparer — an ordinal one would verify
  `explorer.exe` and `EXPLORER.EXE` separately, a silent cost no other assertion would catch.
  Keyed on the path, because one browser runs as a dozen processes from one executable.
  The signature pair MUST stay in `ReconcileInto`'s
  identity group: a fresh entry for a tracked PID carries no path, so its verdict is
  `Unknown`, and copying it across would blank the column one tick after it appeared.
- `Helpers/SignatureVerdict` — the file-path-to-three-state answer both the Startup Manager
  and the Process Manager render, plus the sentence shown on hover. Separate from
  `Helpers/Authenticode` on purpose: that type answers only the mechanical questions and
  holds no policy, because the two fail-closed gates disagree with each other on what an
  unsigned file means. This one carries exactly one policy — the informational one, for
  columns that describe many files and admit no code: unsigned is ordinary, nothing reaches
  the network, every answer comes with a readable sentence. A gate adopting those would stop
  being a gate. Shared rather than copied so two tabs cannot describe one certificate in
  two different sentences.
  The verdict comes from `Helpers/WindowsTrust`; the certificate is still read, but **only for
  the publisher's name**, never for the verdict. That ordering is the fix: a name is cosmetic,
  so failing to read one costs a phrase in a tooltip, while the verdict decides a colour. Every
  phrasing here has a form that works with no name, which is what lets the name be optional.
- `WindowsFeaturesService` — list, enable, disable Windows optional features
  via `Get-`, `Enable-` and `Disable-WindowsOptionalFeature`, each in its own `powershell.exe`. All three
  need administrator rights, listing included, and every one of them checks the exit code:
  an unelevated list request fails rather than returning an empty list.
- `UninstallerService` — winget-based uninstall + registry UninstallString
  fallback for local apps not in winget. Uninstall execution is standard-session
  only; validated local commands use the shell runner so their own manifests own
  any UAC request instead of inheriting SysManager elevation. After a local uninstaller
  returns, `IsStillRegistered` checks that Windows no longer lists the app before the tab
  calls it removed: the exit code belongs to the launched process, and an NSIS uninstaller
  hands over to a copy of itself and exits at once. The uninstall roots it reads are
  injectable, so the check is tested against a redirected registry. The scan also records each
  entry's `InstallLocation`, which the leftover search starts from.
- `LeftoverService` (`ILeftoverService`) — the Uninstaller's Left behind card (#1527). `FindAsync` runs
  `LeftoverFinder` for one uninstalled app and measures each folder with `SafeFileWalk`, refusing a folder
  that holds a link; `RemoveAsync` sends folders to the Recycle Bin and deletes an app's own HKCU key only
  after `reg.exe export` has written its `.reg` backup to `Backups\Uninstaller` (newest three per key, pruned
  by `ContextMenuService.PruneBackups`). Every folder is re-checked by the finder's rules just before it goes.
  Folders under Program Files or ProgramData found without administrator rights are kept in
  `uninstaller-leftovers.json` and offered again, re-checked and unticked, in an elevated session: the record
  is user-writable, so an administrator session removes only what is ticked in it.
- `LeftoverFinder` — the pure rules behind it: what an uninstalled app left, ranked Certain (the install
  folder its entry named, read before uninstalling), Probably (a folder named after the app directly under
  AppData, Local AppData or ProgramData), its own key (`HKCU\Software\<Publisher>\<App>`) and Guess (a
  publisher folder in AppData when no installed app shares the publisher). Refuses Windows, the roots, the
  user's own folders, SysManager's, names Windows and many apps share, links, non-fixed drives, and any
  folder an installed app still lives in — judged against every uninstall entry Windows holds, hidden ones
  included, not only the list on screen.
- `PerformanceService` — power plan, visual effects, Game Mode, Xbox
  Game Bar, NVIDIA GPU, processor state, restore point creation, RAM
  working set trim, hibernation toggle. The trim works through a process list and a trim call the
  constructor takes. The public constructor passes every process; both trim through `EmptyWorkingSet`
  unless a test passes its own call, and the test one defaults to no processes, so a test that passes no
  list trims nothing (#2557). Its
  timestamped restore snapshot is persisted locally, bounded and validated at load, then rehydrated by
  `PerformanceViewModel` before live profile probes during initialization.
  `RestoreOriginalAsync` puts the snapshot back and then deletes it, for Restore All and Undo Changes alike, and
  `PerformanceViewModel` reads the snapshot from disk before every change and every Restore All, since Undo
  Changes can delete it while the tab holds a copy (#1525).
  `LoadSnapshot(out SnapshotProblem)` tells no snapshot from one that could not be read or is
  invalid, so the first Apply captures a baseline only when there is none: an unreadable one
  stops the change, and an invalid one is set aside by `StoreFile` and the change stopped once.
  The graphics step of a restore (`RestoreGraphics`) asks what has become of the NVIDIA card the snapshot
  names — still there, gone, or unreadable (`RecordedAdapter`) — and leaves out only one that is gone, so
  Restore All can finish once the card was taken out or its driver removed. `RestoreOriginalAsync` is handed the
  card as the question about the restore found it (`AdapterOf`, which asks `FindAdapter`), so the restore does what
  the question said. The lookup is a constructor seam, so what a question says does not depend on the graphics card
  of the PC running a test, and the read of the card's key behind `FindRecordedAdapter` is a parameter, so a read that
  fails is tested as not known rather than gone. `RestoreGraphics` takes the write as a parameter too, so its answers
  are tested with a stand-in; the service's own restore passes the real `SetGpuMaxPerformance`, so no test runs a
  whole restore of a record that names a card. Performance Mode's own NVIDIA Apply goes through
  `WriteGpuMaxPerformance`, a constructor seam the public constructor fills with the registry write and the test
  constructor leaves finding no card (#2607).
- `NetworkRepairService` — DNS flush, Winsock reset, TCP/IP reset via
  system commands with live output capture.
- `ServiceManagerService` — enumerate Windows services, gaming
  recommendations, start/stop/disable with admin checks. Also reads each
  service's dependents (`ServiceController.DependentServices`, disposing the
  handles it returns) so the stop and disable prompts can name what else
  breaks. The opposite direction, `ServicesDependedOn`, is deliberately not
  read: six times the cost for the question the user is not asking. For each
  Automatic service it also asks the service control manager, through
  `QueryServiceConfig2`, whether the start is delayed: `ServiceStartMode` has
  no delayed member, and the `DelayedAutostart` registry value is not a
  substitute, because a per-user service instance has none of its own. That
  flag is what lets Disable and Enable round-trip "Automatic (Delayed Start)".
  `StopServiceAsync` refuses a service that accepts no stop request instead of
  returning as if it had stopped, and `IsSafeForScExe` is the one name check
  that the Services commands (before they prompt), `SetStartupTypeAsync`
  (before it builds the sc.exe command line) and Undo Changes' scan (which skips a
  ledger record whose name would fail it) apply. `PutBackStartupTypeAsync` is the
  one put-back that Enable on the Services tab and Undo Changes share (#1525): it
  sets the recorded startup type through `SetStartupTypeAsync` (Manual when none is
  recorded) and only then forgets the ledger record, so a change that failed keeps
  it. `ReadEntry` reads one service as Windows reports it now, for a caller that
  needs a handful rather than the whole list, and `IsDisabled` is the one test for
  the state Enable undoes.
- `AppAlertService` — monitors for new application installations via
  FileSystemWatcher and registry polling.
- `AppBlockerService` — blocks/unblocks app execution via Image File
  Execution Options (IFEO) debugger redirect. Injectable registry root for tests.
  One predicate decides both which targets are refused and which existing blocks
  cannot be lifted from inside the app, so refusal and detection cannot disagree;
  `GetBlockedApps` stamps that verdict onto each row it returns. Detection only —
  the service never removes a block without going through the confirmed unblock path.
  When the IFEO key cannot be read, `GetBlockedApps` and `IsBlocked` return null, so a
  failed read is never reported as "nothing blocked".
- `NotificationBlockerService` — mutes app notification nags via the documented
  per-user registry switches Windows Settings writes (per-app `Enabled` under
  `Notifications\Settings`, plus the `ToastEnabled` master toggle). Injectable
  registry root for tests; per-user, reversible, no window hooking. Every master-toggle write
  is counted in `notification-master-writes.json`, which Gaming Profile's `NotificationsTweak`
  compares at apply and at revert to keep a mute the user made during a game.
  `ReadMasterToggleWriteCount` returns null for a ledger that cannot be read or parsed, and the
  revert then restores; a write still counts from 0 over such a ledger, because its number only
  has to change.
- `BatteryService` — battery charge, health, wear level and cycle count via WMI
  (`Win32_Battery`, then the `root\WMI` capacity classes). `GetBatteryInfo` returns null
  when the `Win32_Battery` query fails, so a failed read is never reported as no battery.
  The capacity classes answer only an elevated process, and without them health is left
  unmeasured rather than claimed.
- `BatteryReportService` (`IBatteryReportService`) — runs `powercfg /batteryreport /xml` through
  `IPowerShellRunner` into a new, randomly named file in the temp folder, reads it and deletes it, with a
  30-second timeout on an injectable `TimeProvider`. Reports `Read`, `NoHistory` (a desktop's entries all carry
  0 capacity) or `Failed`, and never one as another. Needs no administrator rights.
- `BatteryReportParser` — pure reader of the report's `History` section: elements matched by local name, no
  DTD, entries without a capacity dropped, and everything before the newest `BatteryChanged` entry dropped so a
  replacement is not drawn as a recovery.
- `BatteryWearAnalyzer` — pure: the loss over the last six months, each end the median of up to three entries,
  against what ordinary use costs, as four verdicts that never use the failure colour. `DescribeTooShort` writes
  the card's message for less than a month of history.
- `DialogService` — centralized confirmation/message dialogs (replaces
  direct MessageBox calls for testability).
- `IconExtractorService` — extracts application icons from executables
  for display in process/app lists; caches results.
- `OperationLockService` — prevents concurrent conflicting operations by
  category (Disk / Network / SystemModification / Shell / Install) via a thread-safe
  `ConcurrentDictionary` try-acquire. Returns a disposable handle, or `null`
  immediately if that category is already locked (non-blocking; no timeout).
  `Shell` is deliberately separate from `SystemModification` rather than folded into
  it: the two must NOT exclude each other, because an SFC scan runs for up to fifteen
  minutes and a user whose taskbar froze during one still has to be able to restart the
  shell. What must be exclusive is two shell restarts.
  `Install` is separate for the same kind of reason. App Updates, Bulk Installer, Uninstaller and the
  Dashboard's Update All Apps exclude one another because Windows Installer runs one installation at a time,
  and an MSI package started while another runs fails with 1618 (#2510; Update All Apps joined at #2553).
  None of them services Windows, so an SFC scan does not hold them up. Reset Windows Update stops the Windows
  Installer service, so it holds `Install` as well as `SystemModification` (#2553).
  `Network` covers the speed tests, traceroute, Network Repair, and DNS & Hosts' DNS changes. Saving or
  restoring the hosts file takes no lock, from DNS & Hosts or from Undo Changes: both write it only through
  `HostsFileService`, which swaps a finished copy into place (#2553).
  `SystemModification` covers everything that services or restarts the running Windows image: the SFC and
  DISM repairs, the component-store cleanup, Windows feature changes, Windows Update installs, Reset
  Windows Update, and creating or restoring a restore point, besides every tab that writes a system-wide
  registry value or changes a Windows service (Performance Mode, Gaming Profile, Environment Variables,
  Preinstalled Apps, Privacy & Telemetry, Defender Tweaks, Edge/OneDrive Remover, Services), and Undo Changes,
  which takes it under its own name for the three put-backs whose tabs take it: Performance Mode, the services
  and the environment variables (#1525).
  Feature changes, update installs, Reset Windows Update and the Restore Points tab took no lock until
  #2484; the last five joined at #2510, mainly so closing SysManager mid-change can name them — overlap
  between any two of them is not itself dangerous the way an SFC repair racing a feature change is.
  System Fixes' `RunFixAsync` takes it only for the fixes that name
  a lock, so Reinstall WinGet, which conflicts with none of them, still runs during a repair. Its
  `stopsWindowsInstaller` flag adds `Install` under the same name, for Reset Windows Update.
  Deep Cleanup takes it in addition to `Disk` when one of its two Windows Update caches is ticked: both sit
  inside `SoftwareDistribution`, which an update install reads from and Reset Windows Update renames. It gives
  it back after the delete, before the read-only rescan (#2510).
- `ProcessDescriptionService` — the built-in process database (`ProcessDescriptions.json`, an embedded
  resource) behind the static `Instance`: `Lookup` gives a process's plain-language description, category and
  `ProcessSafety`, for Process Manager and Startup Manager.
- `SpeedTestHistoryService` — persists speed test results to JSON for
  historical charting and trend analysis. One instance, shared by the Speed Test tab and the
  Dashboard's quick test; its `Saved` event is how a result recorded from the Dashboard reaches a
  Speed Test tab that has already loaded its list. An upload or ping that was not measured is left out of
  the file rather than written as null, so a version from before they could be missing still reads it.
  `LoadAsync` returns null for a history that could not be read, and the tab says so; a save or a
  one-engine clear then writes nothing, and a history that does not parse is set aside by `StoreFile`.
- `DiskScanHistoryService` — remembers the last Disk Analyzer scan per root (one snapshot each,
  capped roots and capped folders-per-root) in `disk-scan-history.json`, so the tab can show what
  changed since last time. Same never-throw-on-IO contract and `configDir` test seam as
  `SpeedTestHistoryService`, and the same rule for a history that could not be read: `LoadAsync` is
  null, `FindAsync` says it is not `Readable` rather than "never scanned", and `SaveAsync` writes
  nothing over it. Machine-specific by nature (absolute paths + sizes on this disk), so it
  is deliberately absent from `ProfileService.Catalog` and pinned OUT by a test.
- `ShortcutCleanerService` — scans the Desktop and Public Desktop, both Start Menus, Quick Launch and Recent
  Items for broken shortcuts (dead targets) and offers safe removal. A network target is never judged gone.
- `BulkInstallerService` — installs apps via winget in batch with
  per-item progress and error reporting.
- `FileShredderService` — secure multi-pass file overwrite (DoD 5220.22-M
  style) and deletion.
- `PrivacyService` (`IPrivacyService`) — reads and writes Windows privacy and telemetry
  registry toggles (activity history, advertising ID, diagnostics, etc.). Each toggle carries a
  stable `Key` that a privacy profile names it by; the list is pinned by a test, because renaming a
  key would make every profile already exported drop that choice.
- `UndoChangesService` (`IUndoChangesService`) — finds the changes SysManager can put back from the copies
  five tabs already keep: Performance Mode's record of the original settings (compared with the settings now,
  through `PerformanceService.TakeSnapshotAsync`, its graphics setting read from the card the record names; held
  back while a Gaming Profile session is on, left on, or not known to be off, whose settings they are until it
  ends), the Services tab's `ServiceStartupLedgerService` records of
  services still off, the copy beside a hosts file SysManager has written while the two differ
  (`HostsFileService.ReadBackupState`), the environment variables' kept copy while a variable the restore can
  write differs (`EnvironmentVariableService.PreviewRestore`), and a Gaming Profile session that is on or was
  left on; plus, as one row that opens that tab, the Settings Watchdog settings that drifted and that it can
  write back. A copy that could not be read, could not be compared with how things are now, could not be used, or
  is damaged, is reported as an `UndoProblem` of that kind, never as nothing. Game mode is read once per look,
  for both rows it decides.
  Owns no file. `PutBackAsync` takes the lock that tab's own restore takes, reads the copy again under it and
  changes nothing unless it still says what the question said, then goes through the restore the tab itself
  uses — `PerformanceService.RestoreOriginalAsync`, `ServiceManagerService.PutBackStartupTypeAsync`,
  `HostsFileService.RestoreBackup`, `EnvironmentVariableService.RestoreFromBackup`,
  `IGamingProfileService.RevertAsync`/`RecoverPendingAsync` — and raises `IPutBackSignal` afterwards, whenever
  something may have been written. What a restore that stopped part-way left is read again and compared, line
  by line, with what its question listed (`WhatWentBack`), rather than assumed or counted. `LookForRestorePointAsync` asks for
  the newest restore point apart from the scan, since that question waits behind the PowerShell runner a
  restore point being created holds. The calls that would read or change this PC are seams on the internal
  constructor.
- `PutBackSignal` (`IPutBackSignal`) — the one event Undo Changes raises after every put-back that may have
  changed something, finished or not, and the tab that made the change listens to: Performance Mode, Services, DNS & Hosts, Environment
  Variables and Gaming Profile each read their state again, on the UI thread, and unsubscribe when disposed.
  A tab says only what it found when it read again; whether the put-back worked is said on Undo Changes. Raised
  only by Undo Changes, so a tab's own put-back is not heard twice. Every tab is built once and kept for the
  session, and without this the tab would go on showing the change: Performance Mode's Restore All and switches
  would describe the settings as they were before (its next change no longer depends on the signal, because it
  reads its record from disk first), and Gaming Profile would keep Start off. It is raised on the thread that
  tried the put-back, which is why each listener posts its re-read to the UI thread.
- `PrivacyChoicesHandoff` (`IPrivacyChoicesHandoff`) — the one slot the profile import and the
  Privacy & Telemetry tab share: the import offers the choices it read, and the tab takes them once,
  when it is next shown or finishes loading. A slot rather than a call, because that tab's view model
  is built the first time it is opened, which may be after the import.
- `DnsService` — manages DNS server configuration via PowerShell
  `Set-DnsClientServerAddress` with preset support (plain resolvers plus
  ad/malware/family-blocking variants), IPv4 + IPv6, and reversible snapshots.
  Every script stops on an error, so a failed read is "Unavailable" rather than an answer:
  `CurrentDnsScript` and `ActiveInterfaceIndexScript` are internal so the integration suite runs
  them in real Windows PowerShell with the cmdlets shadowed (`DnsScriptTests`).
- `HostsFileService` — parses and edits the Windows hosts file with
  add/remove/toggle operations; keeps a one-time pristine backup and can
  restore it (`HasBackup` / `RestoreBackup`); `ReadBackupState` says whether the file still differs from
  that copy and whether SysManager has written it (its header), for Undo Changes, since the copy is never
  deleted and need not be SysManager's. `SaveHosts` re-reads the file to keep the lines it
  does not manage, and throws before writing when that read fails. `DnsHostsViewModel` refuses
  Save until a read of the file has succeeded, since Save rewrites it from the list.
- `ContextMenuService` — scans Explorer context menu registrations in both
  shapes Windows stores them in: `shell` verbs (a name and a command line,
  hidden with `LegacyDisable`) and `shellex\ContextMenuHandlers` COM
  extensions (a class id, resolved to a friendly name and its server DLL).
  Both are hideable, by different mechanisms — a verb through `LegacyDisable`
  with an HKCU override fallback, a COM handler through the machine-wide
  `Shell Extensions\Blocked` list, which needs elevation. Takes injectable
  stand-ins for `HKEY_CLASSES_ROOT` and `HKEY_LOCAL_MACHINE`, so both the scan
  and the block/unblock writes are testable against a disposable hive.
- `BackupRegistry` exports the affected key to `%LocalAppData%\SysManager\Backups\ContextMenu`
  before each change, and `PruneBackups` keeps only `BackupsKeptPerKey` (3) per key. Ordered by
  FILE NAME, whose `yyyyMMdd_HHmmss` tail sorts chronologically and cannot be rewritten by a copy
  the way `CreationTime` can — same reasoning as `DiagnosticsBundleService.NewestLogs`. The
  timestamp shape is re-checked in code rather than trusted to the glob, because one key's
  sanitised name can be a PREFIX of another's (`A` and `A_B` both yield files starting `A_`), so a
  bare `A_*.reg` sweep would delete a second key's history. Both the export and the prune are
  best-effort: neither may fail the change the user asked for. Nothing reads these files back —
  deliberately, since importing a stale export would re-create entries the user has since removed
  — so the tab states they exist and the depth it keeps (#2369).
- `SystemReportService` — gathers a comprehensive system snapshot once
  (OS, CPU, memory, GPU, motherboard, storage health, network) into a
  `SystemReportData` payload, then renders it to plain text, self-contained
  HTML, or JSON so all three exports share a single source of truth.
  Every format opens with `SystemReportData.Health` (#1508): the health score and
  recommendations from `HealthScoreService.Evaluate`, each component's score or "could not
  be read", and up to five recent System-log problems from `SummarizeProblems` — critical and
  error events of the last 7 days grouped by source and ID, critical first, with the written
  explanation where `EventExplainer` has one and never the event's own message. The log read
  has a 15-second budget; a log that could not be read is said so, never reported as clean.
  `GenerateDataAsync` applies `WithoutMachineIdentifiers` before returning, so **every**
  format — text, HTML, JSON, the on-screen report and the diagnostics bundle — replaces each
  adapter's MAC with "(not included)" and masks the last two parts of its IPv4 address. That
  choke point is the design: redaction used to live in a separate `GenerateSharableReportAsync`
  that only the bundle called, so the
  four export commands and the tab itself carried the full values (#2352). The separate
  method is gone — one path cannot disagree with itself, and a format added later inherits
  the protection instead of having to remember it.
  `ArchitectureTests.EveryReportFormat_GoesThroughTheRedactingDataPath` rejects any public
  generator that reads `BuildData` directly. Redacted from the DATA rather than by
  pattern-matching the rendered text: a four-part version string has four valid octets, so a
  generic IPv4 regex over the finished report would rewrite the version line while claiming
  to protect an address.
- `DiagnosticsBundleService` — packages the sharable report, the About tab's
  environment block and the three newest rolling log files into one zip the user
  chooses where to save. Nothing is uploaded and there is no network code in the
  class. Logs are picked by FILENAME rather than by `LastWriteTime`, because the
  names sort chronologically and a sync client touching a file would otherwise
  select the wrong three; an oversized one is cut from its FRONT, since a log is
  chronological and the failure being reported is at the tail. `PackAsync` is the
  internal seam that takes an already-gathered report, so the packaging is unit
  testable while the redaction decision stays on the one path a caller can reach.
  `SysManager.IntegrationTests.DiagnosticsBundleRedactionTests` asserts against
  this machine's real adapters that the bundled report carries none of their
  hardware addresses or full IPv4 addresses, and that the report shows the redaction
  markers — `(not included)` where a MAC would be, `.x.x` in a masked address — so
  "nothing found" cannot pass for "something was removed". No failure message prints
  a value, because they reach a public CI log.
- `EnvironmentVariableService`: reads/writes User and Machine environment
  variables directly through HKCU/HKLM so `REG_EXPAND_SZ` values round-trip
  without flattening; `BroadcastSettingChange` sends `WM_SETTINGCHANGE` once,
  after a caller's batch. It provides name
  validation and pure PATH split/join/dedupe helpers. Reversibility uses
  independent one-time snapshots in matching registry hives: User state under
  HKCU and Machine state in access-controlled HKLM storage. Legacy LocalAppData
  data remains read-only compatibility input for User restore and is never
  authoritative for an elevated Machine restore. `PreviewRestore` says what
  `RestoreFromBackup` would change now — each variable put back, changed back or
  removed, by scope, leaving out a name the restore can neither write nor remove —
  so Undo Changes lists the copy only while restoring it would change something
  (#1525). A copy that is there and cannot be used is refused before the first
  write, by the preview, the restore and `EnsureBackup` alike, and the two kinds
  are told apart: `InvalidDataException` (and `RestoreResult.InvalidBackup`) for a
  damaged one, which looking again will not change, and
  `BackupUnreadableException` (and `UnreadableBackup`) for one that could not be
  read just now — another program had it open, say.
- `RestorePointService` — lists (`Get-ComputerRestorePoint`), creates
  (`Checkpoint-Computer`), and restores (`Restore-Computer`) System Restore points
  through the `IPowerShellRunner` seam; the output parser is a pure, unit-tested
  static method. Create and restore report success only through a sentinel their
  script prints last, never the absence of an exception, and create stops on
  warnings too: Windows PowerShell 5.1 reports the one-a-day limit as a warning,
  which `-ErrorAction Stop` does not catch. Listing needs administrator: Windows
  answers a standard user with "Access denied", so the list stops on that error and
  `ListAsync` returns null for a refusal and an empty list only for a PC with no
  restore points, which lets the tab say which one it is. The integration suite runs
  all three scripts in real Windows PowerShell with the cmdlets shadowed by functions.
- `SessionRestorePoint` — the single owner of the AUTOMATIC restore point (`ISessionRestorePoint`).
  Each of the six consumers listed below calls `EnsureAsync` before its first mutation; the first
  call wins and the rest are no-ops, so a session takes at most one point no matter how many of them
  the user visits — Windows grants roughly one per 24 hours, so two independent attempts meant the
  second reported "no snapshot" while a good one existed. Takes `RestorePointService.CreateAsync` as
  a delegate rather than the sealed service, which keeps it substitutable without unsealing
  production code. Returns true only when THIS call created a point, so no caller can claim one that
  Windows refused. Consumed by `GamingProfileService`, `EdgeOneDriveViewModel`,
  `DebloaterViewModel`, `PrivacyViewModel`, `DefenderViewModel` and `WindowsFeaturesViewModel`. The
  tabs outside that list take none: Undo Changes only looks for the newest point
  (`UndoChangesService.LookForRestorePointAsync`), and Performance Mode and Restore Points create one
  only when asked, through `RestorePointService.CreateAsync`.
  `ConfirmationNotice` is what each of their confirmations appends: the attempt turns System
  Protection back on when it is off, and `RestorePointService.ProtectionNotice` is the one sentence
  that says so, shared with the Restore Points tab and Performance Mode. It is empty once the
  session's attempt has been made and when not elevated, because nothing can change then.
  `GamingProfileService` passes it on as `RestorePointNotice`.
- `DebloaterService` — lists (`Get-AppxPackage`) and removes (`Remove-AppxPackage`,
  per-user) Windows Store apps through the `IPowerShellRunner` seam. A hard-coded
  denylist of system-critical package families is enforced in code; the parser and
  denylist check are pure, unit-tested static methods. `ListAsync` returns null for a read that
  failed and an empty list only for an empty answer: `ListScript` throws when `Get-AppxPackage`
  listed nothing and reported an error, and keeps what it listed when only some packages failed.
  `IsRetired` flags the families Microsoft has retired (Skype, Mail & Calendar), which the Store no
  longer offers, so the confirmation and the result line never promise them back.
- `BrowserCleanerService` — scans + cleans per-browser data (Chromium family +
  Firefox) under injectable LOCALAPPDATA/APPDATA roots. Chrome, Edge, Brave and Vivaldi share
  one per-profile expansion; Opera is the family exception (no `\Default\` segment, two roots)
  and is driven by a channel table — Stable, GX, Beta, Developer — so each channel is its own
  row rather than a copy of the code. Scan is read-only (sizes);
  Clean deletes only discovered files, skips locked files, and never follows
  reparse points. Cookies/sessions are flagged sensitive. Firefox cache lives under
  Local and its cookies/sessions under Roaming (like Opera's split); each targets
  specific named files under the profile, never the profile root (logins.json, key4.db,
  prefs.js, places.sqlite). Firefox History is intentionally NOT offered — places.sqlite
  holds bookmarks as well as history. Firefox's salted profile folders are enumerated at
  scan time over the *union* of both roots and named once, so a profile split across Local
  and Roaming still carries a single name: the release default (or a lone legacy `.default`)
  takes the bare `Firefox`, every other profile takes its readable suffix
  (`Firefox — dev-edition`), falling back to the full folder name when two profiles share a
  suffix. A tie for the default yields no bare name at all. Uniqueness of `(Browser,
  Category)` is a contract, not a nicety — `BrowserCleanerViewModel` keys tick carry-forward
  on that pair, so two rows sharing it would apply one row's decision to the other.
  Profile discovery lives in `BrowserProfiles`, shared with the extension list so both name a profile alike.
- `BrowserProfiles` — every browser profile Browser Cleaner reads: each Chromium browser's `Default` and
  `Profile N`, every Opera channel, each Firefox profile, and the name the tab shows for each. The cleaner
  never goes through a Chromium profile folder that is a link; the extension list is told which ones are, and
  which Firefox profile Firefox opens, from its own `profiles.ini`.
- `BrowserExtensionService` (`IBrowserExtensionService`) — the extensions in every profile `BrowserProfiles`
  finds, ordered as the list shows them (added by another program, then those that can read every website,
  then by name), and the command that opens a browser on its extensions page. Read-only. The data roots, the
  launcher and the elevation check are injectable, and only a known browser, a known page and a real profile
  folder name reach the command line. Nothing is started while SysManager runs as administrator: the browser
  would run elevated too, and what `chrome.exe` resolves to is a per-user setting. Opera's channels share one
  program name, so the list names their page instead, and Firefox is started only for the profile it opens,
  as its `profiles.ini` says. Every Chromium profile, `Default` included, is opened with its own folder, and a
  browser listed more than once names the profile in the status, as does a Firefox profile other than the one
  Firefox starts in. A profile, data folder or Firefox profiles
  folder that is a link is not followed: it is listed as one whose extensions could not be read, and says it
  sits behind a link.
- `ChromiumExtensionReader` — one Chromium profile's extensions: each id folder's newest version's
  `manifest.json` (name, with the translation lookup; version; icon; what it asks for) and the profile's
  `Secure Preferences` (on or off, install date, and where it came from, from Chromium's install location).
  Without the settings an extension is still listed, claiming nothing about its origin; one loaded from a
  folder elsewhere is found through them. Built-in component extensions have no folder and are not listed.
  One whose manifest cannot be read, whose folder (or newest version) is a link, or whose folder is off this
  PC's drives is listed as "An extension whose name could not be read" with what its settings say.
- `FirefoxExtensionReader` — one Firefox profile's extensions from `extensions.json`, with each `.xpi`
  package read in memory for its icon and the pages it replaces. Themes, dictionaries, language packs and
  hidden add-ons are left out; a profile without the file has none, one with an unreadable file says so.
  A package off this PC's drives is opened only when it sits inside the profile being read, which folder
  redirection can put on a share.
- `ExtensionFiles` — the bounded, fully shared reads and tolerant JSON parsing both readers use, and the
  only way they read a value or a key from that JSON (arrays are walked once their kind is checked): each value
  read by its kind and each key's name read by itself,
  so a number written as text, bytes that are not UTF-8, or a key the parser accepts but its own lookup
  throws on are simply absent and cost nothing else, a rule
  `ArchitectureTests.TheExtensionReaders_ReadTheirJsonOnlyThroughExtensionFiles` keeps; the check that an
  extension's own path stays inside its folder and passes through no link; what is
  at a path, seen without following a link; and whether a path is on one of this PC's drives or inside a folder.
- `EdgeOneDriveService` — reversibly de-integrates Edge and OneDrive through the
  `IPowerShellRunner` seam plus injectable HKCU/HKLM roots. OneDrive is fully removed
  per-user (`OneDriveSetup.exe /uninstall` + nav-pane unpin, no elevation); Edge is
  never uninstalled — only its background/startup-boost Group-Policy keys and its two
  auto-update scheduled tasks (a fixed, injection-safe allowlist) are toggled, which
  needs admin. Each action has a matching restore. All scripts are hard-coded constants
  (no user input); the pin/policy logic is unit-tested against a redirected registry, and
  an optional setup-path override lets the remove/restore outcomes, which the setup's exit
  code decides, be tested on a machine without OneDrive.
- `PrivacyMonitorService` — read-only reader of the CapabilityAccessManager consent
  store (camera/microphone/location access history). Injectable registry root;
  friendly-name decoding and FILETIME conversion are pure, unit-tested static methods.
  `Read` returns a `PrivacyAccessReport`: the entries, and the capabilities whose key
  could not be read, so a failed read is never presented as no access recorded.
- `BootAnalyzerService` — read-only reader of the Diagnostics-Performance log
  (event 100 boot durations; 101–110 slow-component events). Event-ID→kind mapping
  and event-XML field parsing are pure, unit-tested static methods; reading the log
  needs admin. Both reads return null when the log could not be read and an empty list
  only when it was read and holds nothing. A read gives up after
  `MaxConsecutiveReadFailures` failures in a row, keeping what it had already read. The
  live log sits behind an internal `IBootEventReader` seam, so the loop is tested with
  scripted readers.
- `TimerResolutionService` — thin wrapper over ntdll `NtQueryTimerResolution` /
  `NtSetTimerResolution`. The request is a per-process contribution Windows reverts
  on exit, so it's fully reversible and needs no admin; the 100ns→ms conversion and
  the high-resolution and at-default detection are pure, unit-tested members of the model.
  Windows keeps one request per process (a second request does not stack; one release
  drops it), so the Timer Resolution tab and Gaming Profile share it: the Gaming Profile
  step releases only a request it made itself.
- `FileLockService` — Restart Manager (`rstrtmgr.dll`) wrapper that lists the processes
  using a file, or any of the files inside a folder, and can terminate one. Termination goes
  through `ProcessManagerService.KillProcess` with the start time Restart Manager reported,
  which is the creation time `Process.StartTime` reads, so a process whose ID Windows has
  given to another program is left alone (#2514). Restart Manager
  tracks files only (`RmGetList` refuses a folder with `ERROR_ACCESS_DENIED`, elevated or
  not), so a folder is checked through its first `MaxFolderFiles` (1,000) files, found with
  `SafeFileWalk`, and the returned `FileLockScan` says how many were checked and whether
  that was all. A failed check is null rather than an empty list, and a path that does not
  exist throws `FileNotFoundException`. It uses classic `[DllImport]` (not `[LibraryImport]`):
  `RM_PROCESS_INFO` has inline `ByValTStr` buffers and `RmStartSession` needs a `StringBuilder`,
  neither supported by the source generator. Six other files use it too — 13 declarations in
  all, against 48 `[LibraryImport]` elsewhere.
- `DisplayProfileService` — `user32` display APIs (`EnumDisplayDevicesW` /
  `EnumDisplaySettingsW` / `ChangeDisplaySettingsExW`) to read and switch resolution +
  refresh rate. Session-only apply (reverts on reboot); validated with `CDS_TEST` first.
  Also classic `[DllImport]` (DEVMODE has non-blittable inline buffers).
- `CpuAffinityService` — gets/sets per-process CPU affinity via `Process.ProcessorAffinity`
  and detects P-core/E-core topology via kernel32 `GetLogicalProcessorInformationEx`
  (variable-length buffer walked by each record's `Size`). The mask helpers
  (build/test/all-cores) are pure, unit-tested static methods. Each process it lists
  carries its start time, and `GetAffinity`, `TrySetAffinity`, `GetPriority`,
  `TrySetPriority` and `HasExited` take it with the ID: a process with that ID that started
  at another time is not the one listed (#2514, #2559).
- `AudioMixerService` (`IAudioMixerService`) — per-app volume/mute/peak on the default
  render endpoint via Windows Core Audio, using raw `[ComImport]` interop for ten documented
  interfaces (`IMMDeviceEnumerator`, `IMMDevice`, `IMMDeviceCollection` and `IPropertyStore` for the
  devices; `IAudioSessionManager2` → `IAudioSessionEnumerator` → `IAudioSessionControl2` /
  `ISimpleAudioVolume` / `IAudioMeterInformation` for the apps; and `IAudioEndpointVolume`) — no
  NuGet audio dependency, keeping the portable single .exe.
  Groups sessions by owning process (Volume Mixer mental model), resolves name/icon from
  the PID (fallback-safe for protected processes), drops expired sessions, and flags the
  system-sounds pseudo-session. Holds the manager/enumerator handle open across polls and
  releases every COM RCW deterministically in `Dispose` (never finalizer-only). It is a
  singleton, built when the Volume Control tab is first opened and disposed with the container.
  All COM types stay inside the concrete class; the interface exposes only plain models so the VM
  unit-tests with no audio hardware. Also
  enumerates render devices (documented device API, with each device's kind from its documented
  form factor) and performs per-app output routing via the UNDOCUMENTED `IAudioPolicyConfig`
  (see `AudioPolicyConfigFactory`), feature-detected so a build without it degrades to the
  guided fallback rather than failing. The whole PC's volume, mute and level come from the
  documented `IAudioEndpointVolume` and `IAudioMeterInformation` on the same default endpoint,
  opened only by the worker-thread read (writes from the UI thread use what a read opened), and
  switching the device all sound plays through goes through `PolicyConfigClient`. The endpoint
  held open follows the Windows default: a switch made here drops it at once, and one made
  elsewhere is noticed on the next device enumeration, so the app list and the PC volume move
  with the sound instead of staying on the device that was the default when the tab opened.
- `PolicyConfigClient` — a defensive, isolated wrapper over the undocumented `IPolicyConfig`
  interface, the way an app moves all sound to another device (what the Windows sound flyout
  does), for each of the three defaults Windows keeps. Feature-detected (`TryCreate` returns null
  when the client does not answer to the interface) and guarded (any COM failure returns false).
  Its vtable slot is pinned by a test; the switch itself can only be runtime-verified on real
  audio hardware.
- `AudioPolicyConfigFactory` — a defensive, isolated wrapper over the undocumented
  `IAudioPolicyConfig` interface (the mechanism EarTrumpet uses to route one app to a specific
  output device). Feature-detected (`TryCreate` returns null when it can't bind), guarded (the
  SET call is invoked only after a successful `QueryInterface` for the exact IID and any failure
  returns false), and its endpoint-id/process-token string helpers are pure + unit-tested. The
  routing SET path can only be runtime-verified by running the app on real audio hardware. The READ path (`GetPersistedDefaultEndpoint`) is deliberately unimplemented and returns
  null, so `IAudioMixerService.GetSessionOutputDevice` has a three-state contract — an endpoint id,
  `string.Empty` for "read succeeded, no override", or null for "could not read" — and the row VM
  renders null as unknown rather than as the default device.
- `VolumePresetService` — persists named per-app volume/mute presets as JSON under
  `%LocalAppData%\SysManager\volume-presets.json`, keyed by exe name so a preset re-applies to
  whatever instance of an app is running. Save/parse/upsert and the "apply preset → live
  sessions" plan are pure, unit-tested static helpers; the file IO never throws to the caller.
  `Load` returns null for a file that could not be read, and `Save`/`Delete` return null, writing
  nothing, when the file could not be read or written; one that does not parse is set aside by
  `StoreFile` first.
- `ClosePreferenceService` — remembers what the window's close button should do, as JSON under
  `%LocalAppData%\SysManager\close-preference.json`. `MainWindow.OnClosing` asks once
  (`IDialogService.AskCloseOrMinimize`, a three-way prompt) and honours the stored answer
  silently afterwards. Same shape as `VolumePresetService`: injectable config directory, pure
  unit-tested `Serialize`/`Parse`, file IO that never throws. Every untrusted or unrecognized
  value degrades to `Ask` rather than to a concrete action, so a damaged file can never exit an
  app the user wanted kept in the tray.
- `UpdateCheckPreferenceService` — gates the startup version check, one of the two network calls the app
  makes on its own (the other is the Dashboard's `winget upgrade` alert, which has no switch yet). Persists
  whether the startup version check may run, plus when it last ran, as JSON under
  `%AppData%\SysManager\update-check.json` (Roaming, like `theme.json`: a stated preference should
  follow the user between machines). The check used to be hardcoded on with no setting and no
  memory of the previous run, so every launch made two calls to `api.github.com` — which both
  contradicted the "network only for features you explicitly use" claim and could exhaust GitHub's
  anonymous limit (60/hour/IP) across repeated restarts. `ShouldCheckAtStartup` is a pure static
  taking the clock as a parameter, so the 24h throttle is unit-tested without sleeping; a
  future-dated timestamp is treated as stale so a bad clock cannot suppress checks indefinitely.
  Malformed input degrades to ENABLED (unlike `ClosePreferenceService`'s `Ask`), because defaulting
  to off would silently close the only channel that tells the user about a fix. `AboutViewModel`
  applies it on the startup path only — the manual "Check for updates" and "Retry" buttons always
  bypass the throttle. Registered in `ProfileService`'s catalog so it survives profile
  export/import. `RecordCheck` writes nothing over a file it could not read, since that file may
  hold "off"; `SetCheckOnStartup` writes the user's choice regardless, losing at most the last-run
  time. A file that does not parse is set aside first.
- `DiskAnalyzerPreferenceService` — whether the Disk Analyzer map is shown, as JSON under
  `%LocalAppData%\SysManager\disk-analyzer-preference.json`, in the same shape as
  `StandbyPreferenceService` below. Anything unreadable shows the map. Deliberately absent from
  `ProfileService.Catalog`: one yes or no is not worth a section of a profile.
- `StandbyPreferenceService` — persists the Standby List Cleaner's auto-purge toggle and
  threshold as JSON under `%LocalAppData%\SysManager\standby-preference.json`. Auto-purge is a
  set-and-forget setting, so losing it on every restart made it effectively unusable. Same shape
  as the two above: injectable config directory, pure unit-tested `Serialize`/`Parse`, file IO
  that never throws. An unreadable file falls back to auto-purge OFF, and an out-of-range
  threshold resets only that field while keeping the toggle — arming an automatic system action
  on the strength of a corrupt file would be the wrong way to fail. What `Load` found decides
  what `Save` may do: after a read that failed it writes nothing and returns false, which the VM
  shows as `ViewModelBase.ChangeNotSavedStatus`, and a file that does not parse is set aside first.
- `ServiceStartupLedgerService` — records what startup type a service had before SysManager
  disabled it, as JSON under `%LocalAppData%\SysManager\service-startup-ledger.json`, so Enable
  restores the original instead of guessing. The value used to live in a plain property on
  `ServiceEntry`, and every scan rebuilds those objects — so Disable → Refresh → Enable brought an
  Automatic service back as Manual (`StartTypeToScToken` maps an unknown value to `demand`) while
  reporting success. Same shape as the three above: injectable config directory, pure unit-tested
  `Serialize`/`Parse`, file IO that never throws. Only the types Enable can restore are stored —
  `ServiceManagerService.RestorableStartTypes`, the one list the mapping to sc.exe tokens reads too:
  `Automatic`, `Automatic (Delayed Start)` and `Manual` — and rehydration applies only to services
  Windows currently reports as `Disabled`, so a stale entry can never override the machine.
  `Load` returns null for a ledger that could not be read, and `Remember` returns false and writes
  nothing then, so Disable records before it changes a service and refuses when it cannot. A ledger
  that does not parse is set aside by `StoreFile` before a fresh one is written. `Load` reads one that
  does not parse as empty; the internal `Read` also says it did not parse, so Undo Changes reports
  that ledger as damaged rather than as nothing to put back, since the services it recorded are still
  off (#1525).
- `CrashMarkerService` — records that the process died from an unhandled exception, as JSON under
  `%LocalAppData%\SysManager\last-crash.json`, so the next launch can say so. `App.OnDomain` writes
  the marker (a domain-level unhandled exception kills the process with no UI at all, so this is the
  only chance to record it) and `DashboardViewModel` consumes it on init, mirroring Gaming Profile's
  leftover-session recovery. Both sides go through the shared `CrashMarker` record rather than an
  anonymous object, so the writer and reader cannot drift into a file that parses to nothing. The
  read DELETES the marker, so `DashboardViewModel` takes it as a REQUIRED constructor argument
  rather than one defaulting to `new CrashMarkerService()` — the convenience default resolved the real
  profile, so every test that built the ViewModel consumed a genuine crash report before the user was
  ever shown it. A destructive read is exactly where an optional dependency must not be optional. One
  crash therefore notifies exactly once; markers older than 7 days, or future-dated ones (clock change,
  file copied from another machine), are dropped. It carries no stack trace — only the exception's type
  and message, the message as written, so a path inside it is not scrubbed of the user name the way the
  log is. It exists to answer "did the last run crash?", not to duplicate the log. Never written from `OnUi`,
  which handles the exception and keeps running. Same shape as the persisted-preference services
  above: injectable directory, pure unit-tested `Parse`, file IO that never throws.
- `GamingProfileService` (`IGamingProfileService`) — a pure ORCHESTRATOR behind the Gaming
  Profile tab: it composes the already-audited services (`PerformanceService`,
  `ITimerResolutionService`, `ICpuAffinityService`, `StandbyMemoryService`,
  `ServiceManagerService`, and the HKCU notifications key) into an ordered set of
  reversible `IGamingTweak` steps and applies/reverts them as a unit — it never
  reimplements a tweak. A machine-wide `GamingSnapshot` is captured before the first change
  and persisted to its OWN `gaming-profiles.json` (never the Performance tab's snapshot);
  revert undoes each applied step in reverse order, and a leftover on-disk session is
  offered for restore the next time the Gaming Profile tab is opened (crash recovery) — Undo Changes
  lists it as left on until then. The store is read through `StoreFile`:
  one that could not be read is never written over, so Apply refuses before any change, and one
  that does not parse or that a newer build wrote is set aside first. `ReadPendingRecovery` tells a session
  left on — and whether undoing it needs administrator rights — from none and from a record that could not be
  read or used, and `RecoverPendingAsync` refuses a record it cannot read rather than report it reverted
  (#1525). Every read-modify-write of
  it holds `_storeLock`, and never across an await. Every revert path — Stop, the
  automatic revert when the game exits, the end of a session whose game closed while it was
  starting, and recovery — returns a `GamingRevertResult` naming
  the steps whose undo failed, so the tab never announces a restore it did not get. The
  apply/revert engine (order, admin-skip, failure-isolation, reverse-revert) is two internal
  static methods, `RunApplyAsync` and `RunRevertAsync`, exercised by unit tests with fake steps — no
  real system call.
  `CpuAffinityService` gained a small `Get`/`TrySetPriority` capability (behind
  `ICpuAffinityService`) for the game's priority. The game is a `GameTarget` that carries its
  start time: Apply refuses a game that has closed before changing anything
  (`GamingApplyResult.GameClosed`), every per-game read, change and restore passes the start time
  with the ID, and the auto-revert binding watches the process only if it is still that game, so
  `BoundGamePid` is set only when the binding held (#2559). The binding is the last thing Apply
  does, so a game that closed while Apply ran has no exit left to wait for: Apply ends the session
  before it returns (`GamingApplyResult.EndedAtStart`), through the same gate-held revert as
  `RevertAsync`. An Apply that changed nothing watches nothing (#2563).
- `DefenderService` — Microsoft Defender via the Defender PowerShell module
  (`Get-MpPreference` / `Set-MpPreference` / `Add`/`Remove-MpPreference`) through
  `IPowerShellRunner`. Normalizes the inverted `Disable*` booleans; exclusion paths are
  bound parameters (never interpolated); every change is verified by reading the value
  back (Tamper Protection can silently reject). The status read stops if either cmdlet
  fails, so an unread status is reported as unavailable rather than as protection "On".
  Windows shows the exclusion lists only to an administrator, so for a standard user
  they are emptied and marked unreadable, and the tab says they are hidden. The parse
  helpers are pure, unit-tested.
- `TaskSchedulerService` — the `ScheduledTasks` PowerShell module (`Get-ScheduledTask`
  / `Get-ScheduledTaskInfo` / `Enable`/`Disable-ScheduledTask`) through `IPowerShellRunner`.
  Disabling is reversible and never unregisters; toggles are verified by read-back. The
  `ClassifyTask` safety heuristic (telemetry/system/third-party) is a pure, unit-tested method.
  `ListTasksAsync` returns null for a read that failed, so the tab never reports one as no tasks.
- `WindowsThemeService` — reads/writes the per-user Windows light/dark theme (HKCU
  `AppsUseLightTheme`/`SystemUsesLightTheme`, no admin) and broadcasts
  `WM_SETTINGCHANGE("ImmersiveColorSet")` for immediate effect. Persists the schedule JSON;
  the overnight-aware `ShouldBeDark` evaluation is a pure, unit-tested method. Distinct from
  `ThemeService` (which themes SysManager's own WPF UI). `SaveSchedule` returns false, and writes
  nothing, after a `LoadSchedule` that could not read the file; one that does not parse is set
  aside first. `DarkModeViewModel` saves after it evaluates the schedule, so the not-saved status
  is the last word on the status line.
- `StandbyMemoryService` — `GlobalMemoryStatusEx` for stats (no admin) and ntdll
  `NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)` to purge,
  after enabling `SeProfileSingleProcessPrivilege` via `AdjustTokenPrivileges` (the
  RAMMap/ISLC mechanism). Purge is non-destructive (standby is clean disk-backed cache).
  All-`[LibraryImport]`; checks `ERROR_NOT_ALL_ASSIGNED` to detect a non-elevated token.
- `LegacyPanelService` — opens classic Windows applets (Control Panel, Sound,
  Device Manager, …) via their `control`/`*.cpl`/`*.msc` commands. The catalog is
  hard-coded and `Launch` re-validates catalog membership, so no input reaches
  `Process.Start`; pure launchers, no system modification.
- `SystemFixService` — one-click repairs (reset Windows Update, reinstall WinGet) via
  hard-coded PowerShell scripts through the
  `IPowerShellRunner` seam; streams output and returns an honest success/failure
  `SystemFixResult`. Auto-logon is delegated to the built-in netplwiz dialog, never
  a plaintext credential write. SFC and DISM run from the tab through the runner, the two
  desktop fixes through `ExplorerShell`, and the network-stack resets are Network Repair's
  (`NetworkRepairService`).
- `ProfileService` — bundles SysManager's own config files (theme, speed-test
  history, …) into a versioned, portable JSON profile and applies it back; only
  catalog-known sections are written (a tampered profile can't drop arbitrary
  files), and the config directory is injectable for tests. `BuildProfile` takes
  section keys, not contents, and reads the files when it is called, so an export
  cannot write what a caller read earlier. The privacy section is built from
  `IPrivacyService` and is never written: `ReadPrivacyChoices` returns it,
  checked against the known toggle keys, for the Privacy & Telemetry tab to stage.
  A profile carrying it is format 2, one without it stays format 1 so an older
  build still imports it, and `Deserialize` drops a section with no key or no
  content, where the import used to throw.
- `AppIconService` — downloads and caches application favicons for the Bulk Installer, only once the user
  turns it on: the switch is off by default, kept in `icon-fetch.json` and carried by a profile export.
- `TemperatureService` — aggregates CPU, GPU, and disk temperatures from
  LibreHardwareMonitor (admin) and NvAPIWrapper (non-admin NVIDIA); without admin it reads
  only the NVIDIA GPU and the disks' SMART temperatures. It does not poll: the Dashboard asks
  every 2 s while it is visible, and the Resource History sampler every 10 s.
- `ActivityLogService` — persists the last 60 user actions to a JSON file for the
  Dashboard's recent-activity card: 22 kinds of action, from 29 call sites. The six destructive
  operations (deep cleanup, browser clean, privacy write, uninstall, shred, shortcut delete) are
  recorded as counts and sizes only, never file names; the others say what changed, and Undo Changes
  records each put-back that changed something (#1525). Takes a `configDir` seam so tests never
  write to the user's real history. The file is shared with command-line and scheduled
  runs, which are separate processes, so `GetRecent` reads it rather than a list held since
  startup, and `Log` reads, adds and writes as one step under an exclusive handle on
  `activity.json.lock` beside it, a file lock so the lock is bound to the file it protects.
  Without that, the open app's next write erased a scheduled run's entry. A file that could not
  be read is never written over: the entry waits in `_unsaved`, is listed by `GetRecent`, and is
  written by the next `Log` that can read the file. One that does not parse is set aside first.
- `ResourceHistoryService` — always-on background sampler (started at app startup,
  runs while minimized to tray) that records CPU/RAM/GPU usage + CPU/GPU temperatures
  every 10s as append-only NDJSON in `%LocalAppData%\SysManager\resource-history.ndjson`,
  with 7/14/30-day retention (periodic prune). Reuses `SystemInfoService` + NvAPIWrapper +
  `TemperatureService`; serialize/parse/prune/downsample/CSV are pure, unit-tested static
  helpers, and the directory is injectable — like `BandwidthHistoryService` — so tests cover the
  load and retention paths without touching the user's own history. A retention setting that
  cannot be read or used is not taken as 7 days: `PruneWindowDays` keeps what the longest option
  would until the user chooses again. Strictly local — no system writes, nothing leaves the machine.
- Bandwidth Monitor sources — `IBandwidthMonitorService` is the seam with two implementations:
  `ConnectionBandwidthSource` (default, no admin) sums `NetworkInterface` byte counters for total
  throughput and reads the extended TCP/UDP tables via iphlpapi P/Invoke (`GetExtendedTcpTable`/
  `GetExtendedUdpTable`) to attribute active connections to PIDs; `EtwBandwidthSource` (admin) opens
  a kernel ETW session (TraceEvent) for true per-process byte rates and falls back cleanly if the
  session can't start; it drops PIDs idle for ten minutes so the per-tick sort tracks what is
  currently active rather than everything the session has ever seen. Both sources are deliberately
  SYNCHRONOUS — `BandwidthMonitorViewModel.PollOnceAsync` owns the single off-UI-thread hop, so a new
  source cannot forget it (the per-source arrangement is what let precise mode keep sampling on the
  render thread through 1.61.9-1.65.11;
  `ArchitectureTests.EveryBandwidthSource_LeavesTheOffloadToItsConsumer` now pins it).
  `BandwidthHistoryService` persists total-throughput samples as NDJSON in
  `%LocalAppData%\SysManager\bandwidth-history.ndjson` (serialize/parse/prune/downsample are pure,
  unit-tested; the directory is injectable so tests never touch the user's own history), and the VM
  reads it back through a range picker — Live plus last hour/24 hours/7 days, capped at the service's
  own retention so no range can promise data that was pruned. `LoadAsync` walks the append-ordered
  file from the end and stops at the first sample outside the window, so a week-long file costs the
  window, not the whole file; the loaded series is downsampled to 400 points before plotting. The
  range summary integrates each rate over the gap to the next sample rather than summing rates, and
  skips gaps longer than 4× the write cadence — samples are only written while the tab is open, so
  crediting the last known rate across a closed-tab hour would fabricate traffic. `BandwidthFormat`
  holds the pure rate-delta/port-summary/threshold math (formatting delegates to `FormatHelper`).
  Strictly local and read-only.
- `SettingsWatchdogService` — snapshots a curated catalog of settings Windows Update
  tends to reset (telemetry, web search, widgets, lock-screen ads, Start suggestions, …)
  as a JSON baseline in `%LocalAppData%\SysManager\settings-baseline.json`, then diffs the
  live registry against it and restores drifted values on request. `DetectChanges` is a
  pure, unit-tested diff; registry access reuses the validated HKCU/HKLM helper pattern
  from `PrivacyService`. Reads/writes only well-known values; nothing leaves the machine.
  `DetectDrift` has two shapes: the no-argument one reads the live values itself, and the
  overload takes a snapshot the caller already read. A caller that also DISPLAYS those values
  must use the overload, so the drift verdict and the displayed value describe one moment —
  two reads let a setting move in between and appear settled while it had changed. A baseline
  file that is there and did not load is not "no baseline": `BaselineFileExists` tells the two
  apart, the VM asks before replacing it, and `SaveBaseline` sets it aside under `_saveLock`
  first, throwing `IOException` rather than replacing it when it cannot.
- `CliRunner` — the headless command-line entry point (dispatched from `App.OnStartup`
  before the single-instance mutex, attaching to the parent console). Exposes only
  read-only/safe verbs (`--health`, `--cleanup`, `--purge-standby` — with `--trim-ram`
  retained as an alias for schedules registered under the old name — `--version/--help/--list`)
  with `--json`/`--silent` modifiers and conventional exit codes (0/1/2). `Parse` and
  `ExecuteAsync` are pure/return-value-based, so the whole CLI is unit-tested without
  launching the process; `IsCliInvocation` is strict so the elevation/update-applier args
  never trigger CLI mode. The two MUTATING verbs write to `ActivityLogService` on success,
  under the same action names the GUI uses, with the command-line origin in the detail — so
  an unattended scheduled run is visible in the app's own history. `--health` deliberately
  does not: it changes nothing, and polling it would evict the 60-entry history that is the
  only record of what the app changed. `OnlyTheMutatingCliVerbs_RecordAHeadlessRun` pins both
  halves.
- `MaintenanceSchedulerService` — registers/reads/removes a single SysManager-owned Windows
  scheduled task (`\SysManager\Scheduled Maintenance`). `GetStatusAsync` returns null for a read that
  failed: `StatusScript` treats only `Get-ScheduledTask`'s `ObjectNotFound` as "not registered" and
  throws on any other error. The task launches the app's own exe with a
  allowlisted CLI verb on a daily/weekly trigger, via the `ScheduledTasks` module through the
  `IPowerShellRunner` seam. Registered in the current-user context (no admin); only ever
  touches its own task — never enumerates or modifies others. The command is built from a
  fixed argument allowlist (`MaintenanceSchedule.CliArguments`), so no free-form input reaches
  the scheduler; result-code description is a pure, unit-tested helper. The task's power and idle
  policy is splatted into `New-ScheduledTaskSettingsSet` from typed `[bool]` parameters, so it is
  still an allowlisted value rather than free-form text; `RegisterParameters` is a pure `internal`
  helper precisely so a test can assert the policy reaches the script without registering a real
  task on the machine running it. `AllowStartIfOnBatteries` defaults to `$false` in that cmdlet,
  and inheriting the default is what kept an unplugged laptop from ever running the schedule —
  the settings block now opts in unless the user unticks it. The status read also selects
  `NumberOfMissedRuns`, because Windows expresses "the conditions blocked this run" by simply not
  running: there is no result code for it, so the count is the only honest explanation available.
  It returns the action's arguments too, which `IsStandbyPurge` reads through `CliRunner.Parse`, the way
  the scheduled run will, to flag a standby-purge schedule: the task runs at the limited level, so every
  run of one fails (#2593). `DescribeResultCode` puts the CLI's own exit codes, 1 and 2, into words.
- `SafetyDatabase` — curated safety ratings for Windows services and optional features. A service it does
  not know counts as Critical, so Services refuses to stop or disable it; a feature it does not know counts
  as Caution.
- `ThemeService` — runtime theme switching with 12 presets and persistence. Two corrections run inside `Shade`, on the already-shifted colours so nothing downstream can undo them: `PanelThatAdmitsReadableText` nudges a Surface toward the Background until this mode's most extreme text clears AA on it, and `Legible` then fits the text to per-surface floors (AAA on the Background, AA on the panels). Both are no-ops for the shipped presets. `ResetToDefault` restores the shipped preset and shade — the only undo Custom mode has, since the four typed colours are persisted and reloaded on every launch. A fourth mode, `AutoMode`, follows the Windows light/dark setting: it resolves through the same `GetCompanionPreset` pairing the Dark/Light pills use, so the colour family survives the switch, and it deliberately does NOT go through `SetPreset` — that would take the mode from the resolved preset and overwrite "auto" the moment it was chosen. `Initialize` re-resolves rather than restoring the saved arm, and one process-lifetime `SystemEvents.UserPreferenceChanged` subscription keeps it live, marshalled to the dispatcher and released by `Shutdown` from `App.OnExit`. The OS read is a `Func<bool>` seam, because the real one reads HKCU and a test without it would assert whatever the developer's machine is set to. Hover is two derived brushes, not one: `RowHover` lifts the surface toward the text colour by `RowHoverLerp(isDark)` — 0.15 on dark presets, 0.25 on light, because darkening a light surface by the same fraction buys about a third less contrast ratio — and `RowHoverMark` is the 3px bar on a hovered sidebar row, `RowHoverMarkColor(theme)` = `TextMuted`. The bar is the part that clears WCAG 1.4.11's 3:1 (6.05:1 or better on all twelve presets); a background tint on a dark theme cannot reach it at all. Both are exposed as `internal static` methods so `ThemeTextContrastTests` asserts the floors against the service rather than against a copy of the numbers, and the mark is deliberately NOT the accent — the selected row draws an Accent bar in the same 3px, so reusing it would make hover indistinguishable from selection. `Save` writes nothing after a `Load` that could not read `theme.json`, and a file that is not a theme it can use is set aside before the first write; the broad catch in `Load` stays, because every failure there means the file is not a usable theme.
- `ToastService` — global glass-style toast notifications.

## Helpers

Key utility classes that don't fit neatly into Services or ViewModels (not an exhaustive list):

- `AdminHelper` — elevation check (`IsElevated()`) and UAC relaunch.
- `AtomicFile` — the single way every service writes a whole file of user data (the two NDJSON
  histories append a line instead, and rewrite through it when they prune). Writes a
  uniquely-named temp file beside the destination, flushes it onto the device,
  then swaps it in with one filesystem operation, so an interrupted save leaves
  either the old file or the new one and never half of each. Load-bearing
  because the loaders that read these files treat an unparseable file as "no
  data" at Debug level, so a torn save would erase presets, profiles or history
  without reporting anything. Services that must stage their own temp file — the
  hosts-file writer, which has to keep the system file's hardened DACL — call
  `SwapIntoPlace` for the same flush-then-swap, and the update path calls
  `FlushOntoDevice` alone, because replacing the app's own executable must not
  inherit the outgoing build's attributes. A fitness function asserts nobody
  swaps a file into place without one of the two.
  The swap retries a refused attempt three times over ~155 ms, because
  `File.Replace` has to delete the destination and anything holding that file
  open — an antivirus scanner mid-read, a backup agent, Windows Search — refuses
  it. Without the retry a lock that would be gone in 50 ms lost the save
  silently. Only `IOException` is retried: a permission failure will not improve
  on a second attempt. After the budget the exception propagates exactly as
  before and the previous file is intact, so the class's promise is unchanged.
  The pause is a parameter so the retry can be tested without sleeping.
- `StoreFile` — the read half of a load-modify-save. `ReadText` and `ReadTextAsync` tell a missing
  file ("") from one that could not be read (null), which a store must never write over, because the
  write would replace everything the file held with the one change. A cancelled async read throws
  rather than reading as unreadable. `SetAside` moves a file that does not parse to
  `<name>.unreadable` (then `-2`, `-3`) before a fresh one is written, keeping its bytes.
- `BulkObservableCollection<T>` — `ObservableCollection` subclass whose `ReplaceWith` swaps the
  whole contents with one `Reset` notification instead of one per item, and refuses to run from
  inside a change handler (in `ObservableCollectionExtensions.cs`).
- `WingetTableParser` — parses the fixed-width table output from `winget`
  CLI commands into structured objects.
- `WingetFailure` — the one translation of winget outcomes into plain language:
  why an install or uninstall failed, whether an install found the app already
  there, and the missing-App-Installer sentence. `UpgradeWarning` is the one
  sentence every confirmation that can upgrade an app shows (App Updates, the
  Dashboard's Update All Apps, the Bulk Installer), and
  `ArchitectureTests.TheUpgradeWarning_IsWrittenInOnePlace` keeps it there. `ThrowIfQueryFailed` tells a
  query that failed from one that found nothing; every query that parses
  winget's table calls it, because a failed query prints no table and used to
  read as an empty result. winget's own result codes are named constants on
  `WingetExitCodes` (in `Models`), each pinned by a test to the number
  winget-cli's header gives it, and a fitness function keeps those numbers out
  of every other file.
- `FormatHelper` — byte-size and transfer-rate formatting, and `JoinForSentence` for an
  "a, b and c" list. Durations are `EtaCalculator.FormatTimeSpan`'s.
- `GatewayHelper` — default gateway IP lookup for network tabs.
- `EtaCalculator` — estimates time remaining for long-running operations.
- `KnownFolders` — resolves Windows Known Folder paths via shell API.
- `SystemPaths` — the one place the tools SysManager launches are resolved. A bare tool
  name becomes a full path under System32 (or the Windows PowerShell 5.1 folder), and winget
  resolves to its admin-only-writable install rather than the per-user alias, so nothing
  launches by name off the PATH. It also owns the two temp exclusions every wholesale %TEMP%
  sweep must honour:
  `BundleExtractionRoot`, the shared folder single-file apps unpack into, and
  `OwnExtractionDirectory`, this build's own leaf under it. Deleting either
  breaks a running program's later lazy loads, which no in-use check can
  prevent because nothing holds those files open yet.
- `RecycleBinHelper` — empties the Recycle Bin via the shell API (`SHEmptyRecycleBin`); shared
  by Quick Cleanup, Deep Cleanup and the Dashboard's Quick Tune-Up so the interop has one source
  of truth. `SendToRecycleBin` is the one `SHFileOperation` recycle, shared by Shortcut Cleaner and the
  Uninstaller's leftovers; its `RecycleFlags` include `FOF_WANTNUKEWARNING`, so an item the bin cannot
  take is never deleted for good without Windows asking.
- `ExplorerShell` — stops, starts and restarts the Windows shell, and owns the
  `thumbcache_*.db` / `iconcache_*.db` pattern list that Deep Cleanup's cache category
  shares. Shared by Context Menu (applying a menu style) and System Fixes (a frozen
  taskbar, a rebuilt icon cache) so the kill loop has one source of truth — it ends each
  instance individually, so one unkillable process cannot abort the loop and leave the
  user with no shell. `Stop`/`Start` are exposed separately from `Restart` because the
  cache delete has to happen *between* them: Explorer holds those files open, so a delete
  with the shell running removes only the files that are not the problem. Both callers
  hold the `OperationCategory.Shell` lock, since two overlapping restarts can leave the
  user with no desktop and a per-view-model `IsBusy` cannot see another tab
  (`EveryCallerThatEndsTheShell_HoldsTheShellLock`,
  `NothingKillsExplorer_OutsideTheSharedHelper`). The cache sweep takes the directory as
  a parameter so the part with logic is testable; the process control is not tested,
  because a test that ran it would end the desktop.
- `UiThread` — the one way background work updates the UI. Runs the action inline
  when already on the UI thread and posts it otherwise, so the caller never waits
  for the dispatcher. It replaced ten hand-written synchronous marshals, each
  guarded on whether an `Application` existed — which is not the question, because
  an `Application` can exist while nothing pumps its dispatcher and a blocking
  `Invoke` then waits on a queue no one drains. The two callers that need the
  update to have landed before their next statement await
  `Dispatcher.InvokeAsync` instead: an un-resumed continuation costs nothing, a
  blocked thread costs a thread. A fitness function asserts nothing marshals
  synchronously, including via a short local holding the dispatcher.
- `MarkdownTextBlock` — lightweight Markdown-to-WPF inline renderer.
- Value converters: `EqualityConverter`, `IntGreaterThanZeroConverter`, and `ValueConverters` —
  17 converters (among them visibility, inversion, hex to brush, and the colour, background and
  text of the safety, process-safety and signature pills) plus the two palettes those pills share.
- `ExtensionPermissions` — turns what a browser extension asks for into the plain-language lines the
  Extensions view shows, in a fixed order, amber for what changes what the user sees, reaches every
  website or can control the browser like a developer tool. A permission it does not know is shown by
  its own name, never dropped (#1526).

## Dependency Injection

`ServiceRegistration.cs` configures `Microsoft.Extensions.DependencyInjection`.
`App.OnStartup` builds the `IServiceProvider` and exposes it as `App.Services`.
Most core services and ViewModels are registered as singletons — one shared
instance per app lifetime. The exceptions are `IPowerShellRunner` / `PowerShellRunner`
and `IWingetService` / `WingetService`, both registered **transient** so each
consumer gets its own runner instance, avoiding `LineReceived` event cross-talk
between tabs (e.g. Dashboard and App Updates running winget concurrently — see the
transient registrations at the top of `ServiceRegistration.cs`).

For a few singletons the single instance is the contract, and `ServiceRegistration.cs` says why beside
each. Undo Changes raises `IPutBackSignal` and the tab that made the change listens, so a second instance
would put a change back without that tab hearing of it (#1525). `IPrivacyChoicesHandoff` is the one slot
Profile Export / Import leaves imported privacy choices in. `ISessionRestorePoint` makes "one restore point
per session" mean the whole app. `NavigationService` is registered as itself and as `INavigationService`, one
object behind both, and `ISpeedTestService` forwards to the `SpeedTestService` singleton the network tabs
take. Some app-wide services are not in the container at all and are reached through a static `Instance`:
`ThemeService`, `OperationLockService`, `DialogService`, `ToastService`, `ActivityLogService` and
`ProcessDescriptionService`.

`MainWindowViewModel` resolves child VMs from the container **lazily**: each tab's
`NavItem` holds a `ContentFactory` and builds its view-model from DI only when the tab
is first opened (`NavItem.Content`). Of the 59 tabs, 56 are registered this way and one of
those, Standby, is still built at startup, as below; that avoids constructing the other 55 tab
VMs at startup — most kick off a background scan/timer in their constructor, so eager
construction ran that work up front for tabs the user might never open.

**Exactly four** stay eager, each because its constructor drives always-on, app-wide
behavior independent of its tab: `Dashboard` (the initially-selected tab), `DarkModeViewModel`
(owns the theme-schedule poll), `AboutViewModel` (its startup update-check feeds the
app-shell version label and update banner), and `StandbyMemoryViewModel` (owns the auto-purge
poll, which is set-and-forget like the theme schedule; it polls only while armed and elevated).
Standby's tab is still registered lazily, and opening it resolves the same singleton. That list
is not maintained by hand here — `ArchitectureTests.OnlyTheJustifiedTabs_AreBuiltAtStartup`
guards the nav table, and `TheShellConstructor_ResolvesExactlyTheJustifiedViewModels` pins
what the shell constructor resolves, both with the same reasons. The Network tabs were the last
exception to go: their view-models are lazy now, while the one `NetworkSharedState` they share is
still resolved at startup, so the shell can dispose it explicitly — its constructor starts nothing.

In tests/designer (no DI container) every VM is built eagerly via a manual dependency graph.

## Admin elevation

Features that require admin (Windows Update installs, SFC/DISM, system-wide winget
upgrades, …) check elevation via `AdminHelper.IsElevated()` and surface a banner
when running unelevated — `AdminBanner`, on 31 tabs. Its button calls
`AdminHelper.RelaunchAsAdmin()`, which first asks through `QuitGuard` when something is
still running, then starts a new copy with `runas` and the single argument
`--relaunched-elevated` (the original command line is not passed on) and returns true;
the caller then closes this instance with `App.RequestShutdown()`. The elevated copy
waits up to 5 seconds for the single-instance mutex to be handed over instead of exiting
as a duplicate.

## Keyboard accelerators

Four keys are handled at the shell. Two of them ask the OPEN TAB what to do, through a seam on
`ViewModelBase` rather than a name the shell guesses at:

- `EscapeCancel` — the command Escape runs, returned only while the tab has something to
  stop. One property rather than a flag beside a command, because the app answers "is
  something running?" five different ways (`IsBusy` on most tabs, plus `IsShredding`,
  `IsScanning`, `IsHttpTesting`, `IsOoklaTesting`), so a shell testing `IsBusy` would skip
  four tabs. 16 tabs override it.
- `RefreshOnF5` — the command F5 runs. A property per view model because the tabs do not
  agree on a name: 11 distinct spellings bind to a refresh-shaped button, and two views bind
  two candidates each, so a convention-matching shell would have to guess. 41 tabs override
  it — Browser Cleaner's returns the read of whichever half is on screen — and every command
  it can return must begin with Refresh/Rescan/Reload/Scan/Load, which mechanically keeps
  Clean, Delete, Apply and Uninstall off a bare keypress.

`Ctrl+F` is the third, and takes no seam at all: `Helpers/FilterBoxes` walks the visual tree under
`ContentHost` — the element the shell binds the live tab into — for the first `TextBox` whose `Text`
binds one of four property names (`FilterText`, `SearchText`, `SearchQuery`, `Filter`), then focuses
and selects it. Focusing a control is a View concern, so routing it through a view model would have
put UI manipulation where the MVVM rules forbid it, and 12 tabs already state which box is the filter
by what it binds. The list was measured rather than assumed: three names looked complete until a scan
of every `TextBox` announced as a filter or search box found Task Scheduler binding plain `Filter`.
`ArchitectureTests.EveryFilterBox_BindsANameCtrlFRecognises` holds it against the views in both
directions, using the ACCESSIBLE NAME as the independent test of "is this a filter box" so the check
is not circular.

`F1` is the fourth, and the only one that asks the tab nothing: it opens About, where the version, the
release notes, "Report a problem" and "Ask a question" already lived — behind the last entry of the
eleventh of twelve sidebar groups, in a group that starts closed, so none of it was findable (#1640). The
`?` chip in the sidebar footer is the pointing-device half of the same route, and it goes through
`ShellAcceleratorCommand(Key.F1)` rather than executing `OpenAboutTabCommand` itself, so the chip and the
key cannot drift into meaning two different things.

`ShellAcceleratorCommand(Key)` is deliberately separate from `AcceleratorCommand` rather than a case inside
it. That one returns `null` for a tab whose content is not built, which is right for F5 and Escape — nothing
to refresh or cancel on a tab nobody opened — and would have made F1 silent on the first frame, exactly when
a lost user reaches for it. `MainWindowViewModelTests.F1_ResolvesToAbout_WithoutConsultingTheOpenTab` pins
the distinction by asserting both lookups side by side, and
`AcceleratorRoutingTests.TheShell_AsksForAShellAccelerator_BeforeTheOpenTabsOwn` pins the ORDER in the
handler — the per-tab branch returns early for anything that is not Escape or F5, so a shell lookup placed
after it would resolve F1 correctly and never be reached.

`MainWindowViewModel.AcceleratorCommand(NavItem?, Key)` is the pure routing decision, extracted
so it is testable without a `Window`; `MainWindow.xaml.cs`'s bubbling `KeyDown` handler executes
what it returns. Two rules live on that path: `AcceleratorCommand` never reads `NavItem.Content`
unless `IsContentCreated` (a keypress must not build a lazy tab), and the handler consults
`CanExecute` for F5 only — `EscapeCancel` already gates itself, and a second gate there would be a
way for Escape to go quiet. `ArchitectureTests.EveryCancellableTab_LetsEscapeReachItsOwnCancelCommand` and
`EveryRefreshableTab_AnswersF5WithItsOwnRefreshCommand` derive both contracts from the views, so a
tab cannot ship a refresh or cancel button the keyboard cannot reach.

## Threading

- Long-running work (ping loops, PowerShell runs, winget scans, deep-clean
  scans) runs on background tasks.
- View-model observable properties are updated on the UI thread through `UiThread.Post`, or an
  awaited `Dispatcher.InvokeAsync` where the update must land first; nothing captures a dispatcher
  in `ViewModelBase`. `NothingMarshalsToTheDispatcherSynchronously` and
  `NothingComparesSynchronizationContextInstances` hold the rule.
- SFC and DISM live on `SystemFixesViewModel` and each have their own `IsSfcRunning` /
  `IsDismRunning` flag for UI state, but they are **mutually exclusive**: every repair on
  that tab streams into one console and drives one progress bar, so `CanRunFix` gates the four
  elevated repairs, and `CanRunShellFix` the two desktop fixes, on `!IsAnyRunning`, and a
  `SystemModification` `OperationLockService` lock additionally excludes the system-repair
  operations on OTHER tabs. `CanExecute` is not treated as the
  guard — `ExecuteAsync` runs a command body regardless of it, so each repair re-checks
  elevation and its own running flag in the body.
- Quick Cleanup's two component-store operations take the same lock, under one
  `IsStoreRunning` flag. They are **two commands over one flag on purpose**: `AnalyzeComponentStore`
  is read-only and always available, while `CleanComponentStore` additionally requires
  `CanCleanStore`, which only a completed analysis that Windows itself recommended can set —
  and which a completed cleanup clears again, because the analysis it was based on is then
  stale. `/ResetBase` is never passed (it discards the ability to uninstall installed
  updates), and `NoDismCall_PassesResetBase_AndEveryWindowsRepairCommandIsBound` enforces both
  that and the presence of the bindings.

## Safety guardrails (Deep Cleanup)

`DeepCleanupService` is intentionally conservative:
- Scan first, clean second. Every category shows its size, and one with something in it starts
  ticked unless it is tagged Irreversible (below).
- Never touches browsers, passwords, the registry, active drivers, or actual game files in
  `steamapps\common`. Inside Program Files it reaches only named launcher folders: Steam's
  `appcache`, `htmlcache`, `depotcache`, `logs` and shader cache, and the League of Legends logs.
- Every walk goes through `Helpers/SafeFileWalk`, so no link is followed and the folders single-file
  apps unpack into under TEMP are left alone. The servicing-logs bucket takes only files older than
  30 days, and the Recycle Bin is emptied through `RecycleBinHelper`, not by deleting its folders. The
  Delivery Optimization cache is emptied through Windows' `Delete-DeliveryOptimizationCache` for the same
  reason: the files belong to the service that keeps them.
- Windows.old is tagged **Irreversible** and never selected by default, as are the
  blue-screen memory dumps: they are the only record of why a machine crashed.
- A bucket may restrict itself to files matching a wildcard (`FilePatterns`), and the
  restriction is carried on the `CleanupCategory` rather than looked up at clean time —
  the cleaner is handed categories and walks their `Paths`, so a filter it could not see
  would show an honest size and delete the whole folder. Two buckets need it: the dumps
  (`*.dmp`, among `.etl` traces) and the Explorer thumbnail cache, whose folder also holds
  the jump lists that are the user's recent-files history. A filtered bucket also leaves
  its emptied folders in place, because it owns files and not the folder.
- The Large Files tab, split out of Deep Cleanup, has no delete action, even with admin rights.

## Logging

Serilog writes at Debug level to a rolling file sink at
`%LOCALAPPDATA%\SysManager\logs\sysmanager-.log`: a new file each day, or sooner when a
day's file reaches 10 MB, keeping the 14 newest files — about 140 MB at most. The in-app
Console is not the log: it shows what the PowerShell and tools a tab runs print, per tab,
on the five tabs that have one, capped at 5000 lines.

## Updates

`UpdateService` hits `api.github.com/repos/laurentiu021/SystemManager/releases`
on demand, and at startup only when `UpdateCheckPreferenceService` allows it — the user
can switch the startup check off in About, and it is throttled to at most once a day
regardless. Downloads land in
`%LOCALAPPDATA%\SysManager\updates\SysManager-{version}.exe` with a companion
`.sha256` so re-opening the app doesn't re-download a good copy. After a successful
download, `PruneOldDownloads` deletes superseded binaries, their stale hashes, and
orphaned `.tmp` files, keeping the current pair and the previous build kept for going back,
with its hash — each build is ~85 MB and
nothing removed the old ones, so the cache grew by that much per update. It only
matches the `SysManager-*.exe` names the service itself writes, never throws, and
treats a file locked by a running instance as ordinary (it survives to the next
round). The "Install"
button verifies the download's SHA256 against the published `.sha256` — the actual
integrity gate — and additionally inspects the file for an Authenticode signature.
`VerifyAuthenticode` accepts a binary with no signature at all — SysManager ships
unsigned, so that is the live path — and rejects a signature that cannot be parsed.
When a signature is present and a publisher is pinned, it is a real publisher check:
the signer must contain `ExpectedSignerSubject` and its certificate chain must build
with online revocation, or the method returns false. That pin is a single `const`, and
it is empty until a code-signing certificate exists, so today a signed build passes on
the SHA256 comparison alone and its signer is only logged. Writing the check now is
what stops it quietly becoming a no-op the day signing is switched on — without the
pin, merely *carrying* a signature would pass, and an attacker's self-issued
certificate would be accepted like a legitimate build. The policy deliberately mirrors
`SpeedTestService.VerifyOoklaSignature`, which already pinned subject + chain for a third-party download.

SHA256 remains the integrity gate rather than a fallback: `CreateFromSignedFile`
reads the signer certificate without validating the file against it, so it cannot
detect a tampered signed binary, and the hash comparison runs first.

It then hands off to `UpdateApplier`: the freshly-downloaded exe is relaunched with
`--apply-update`, which `App.OnStartup` intercepts before any window opens. That
process waits for the old instance to exit, swaps itself over the old executable
via a staged atomic move (a sibling `.new` file plus `File.Move`, so an
interrupted copy can never leave a half-written binary), and relaunches —
inheriting the original's elevation. No on-disk script is involved.

Before that move, `PreserveCurrentBuild` copies the outgoing executable to
`SysManager-previous.exe` in the same updates folder, and writes that copy's SHA256
beside it. The atomic move makes an
*interrupted* update safe; it does nothing for an update that *succeeds* into a
build that will not start, and this project has shipped two such regressions. One
generation is retained (each copy overwrites the last), `PruneOldDownloads`
explicitly skips that name and its hash — both match the `SysManager-*.exe*` pattern
and would otherwise be deleted by the next download — and retention is best-effort: if
the folder cannot be written the update still proceeds. `AboutViewModel.CanRollBack`
surfaces a "Go back to the previous version" button only when the copy and its hash
both exist; the rollback opens the copy through `TryOpenVerifiedPreviousBuild`, which
checks it against that hash and fails closed without one, and then reuses this same
applier path rather than a second file-copy implementation. `IsValidApplyTarget` guards
`--apply-update`'s target, which arrives straight from the command line: it must be an existing
file carrying SysManager's own product resource, outside the system folders, or nothing is
replaced.

## Testing

See [TESTING.md](TESTING.md) for the three test projects: the xUnit v3 unit suite, which
holds `ArchitectureTests` — the fitness functions this document cites — the integration suite
against real Windows APIs, and the FlaUI UI-automation suite.

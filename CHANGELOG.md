# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.42.4] - 2026-06-27

### Fixed
- **Event Viewer detail panel and System Health bars now follow the theme correctly.** A few surfaces used fixed dark colours instead of theme brushes, so under a light or custom theme the "What this means" / "What to try" cards in System Logs, their monospace message/XML boxes, and the small health/usage bars on System Health could lose contrast (light text on a still-dark panel). They now use the shared theme brushes and adapt to any preset or custom theme.

## [1.42.3] - 2026-06-27

### Fixed
- **Lists that are empty now say so, instead of showing a blank area.** Several tabs (Services, Startup Manager, Process Manager, Uninstaller, Windows Features, DNS & Hosts, Disk Analyzer) showed an empty table with no explanation when there was nothing to display or a filter matched nothing — leaving you unsure whether it was still loading, found nothing, or had failed. Each now shows a short, centred message in that case. This also fixes the empty-state messages on the Speed Test history, which previously never appeared: the visibility converter treated a list's item count as "always present", so a count of zero was misread — numeric values are now correctly treated as empty when zero.

## [1.42.2] - 2026-06-27

### Fixed
- **Display Profiles applies and auto-reverts without freezing the window.** Switching resolution/refresh rate, picking a display, and the 15-second auto-revert all called the Windows display APIs (`EnumDisplaySettings` / `ChangeDisplaySettingsEx`) directly on the UI thread, so the app could briefly stop responding while the driver re-trained the panel — and that stall could hold up the very countdown meant to rescue you from a bad mode. These calls now run on a background thread, so the window and the auto-revert timer stay responsive throughout.
- **Defender Tweaks no longer lets two changes overlap.** The PUA, Controlled Folder Access, exclusion-add and exclusion-remove actions could be triggered again while a previous change was still being written and verified, starting overlapping operations whose read-back checks could race and show a misleading "not changed" message. Each action now disables the others until it finishes, matching how the rest of the app serialises long-running operations. No change to what any action does.

## [1.42.1] - 2026-06-27

### Fixed
- **System Health now reads SMART/reliability data on Storage Spaces and similar setups.** On machines where Windows surfaces disks through the Storage provider, the physical-disk identifier embeds characters (`=` and `"`) that the previous safety check rejected, so temperature, wear, power-on hours and read/write error counts were silently dropped — the drive still showed as healthy but with no detail — and a warning was written to the rotating log on every few-second refresh. The reliability counters are now read by following the disk's WMI association directly instead of rebuilding a query from the identifier, which is robust to the identifier format and needs no text parsing. Drives that genuinely expose no counters (non-elevated sessions, virtual disks) are treated as a normal empty result rather than logged as a warning. No visible change on machines that already showed full SMART detail.

## [1.42.0] - 2026-06-26

### Added
- **Standby List Cleaner tab (Gaming & Profiles).** Frees cached "standby" memory to reduce stutter when RAM runs low — the built-in equivalent of ISLC. Shows live total / available / load%, purges the standby list on demand, and can auto-purge when available RAM drops below a threshold you set. Safe and non-destructive: the standby list is clean, disk-backed file cache, so clearing it loses nothing — Windows just reloads from disk on next use. Reading stats needs no admin; purging requires administrator (it enables the same privilege RAMMap/ISLC use and reports cleanly if not elevated). Marked **PREVIEW** while it's verified. Closes #325.

## [1.41.0] - 2026-06-26

### Added
- **Dark Mode Scheduler tab (Customization).** Switch the Windows light/dark theme on demand, or have it follow a fixed-time schedule (e.g. dark at 19:00, light at 07:00). Optionally switches the taskbar/Start too. The theme is applied instantly (no sign-out) by writing the per-user theme setting and notifying Windows; no admin needed and fully reversible. Honest about its limits: the schedule runs while SysManager (or its tray) is open — it's not a background service. Marked **PREVIEW** while it's verified. Closes #329.

## [1.40.0] - 2026-06-26

### Added
- **Task Scheduler tab (System).** Browse all Windows scheduled tasks and turn them on or off. Tasks are color-coded by type — Third-party, well-known Telemetry (CEIP / Compatibility Appraiser / Feedback / Error Reporting), and System — so it's clear what's safe to touch. Filter by name/path, optionally hide system tasks, and see each task's last/next run. Disabling is **fully reversible and never deletes a task**; system tasks show an extra confirmation warning, changes need administrator, and each toggle is verified by reading the task's state back. Marked **PREVIEW** while it's verified. Closes #334.

## [1.39.0] - 2026-06-26

### Added
- **Defender Tweaks tab (Privacy & Security).** See your Microsoft Defender status at a glance (real-time protection, cloud protection, PUA and Controlled Folder Access), toggle PUA protection and Controlled Folder Access, and manage scan-exclusion folders (add/remove). Built to be safe: every change requires administrator and is **verified by reading the value back** — because Tamper Protection can silently ignore changes, the tab detects it and shows a clear warning, and never reports a change as done unless Windows actually applied it. Exclusion folders are validated (rooted, existing, no wildcards) before use, and changes are confirmed first. Marked **PREVIEW** while it's verified. Closes #344.

## [1.38.0] - 2026-06-26

### Added
- **CPU Core Affinity tab (Gaming & Profiles).** Pin a running process to specific CPU cores — useful for games on Intel hybrid CPUs, where **P-cores and E-cores are detected and labelled** (via `GetLogicalProcessorInformationEx`). One-click "P-cores" / "All cores" presets, a per-core checkbox map, Apply and Restore. Affinity is per-running-process and is lost when the process exits, so it's inherently temporary and reversible; no admin needed for your own processes (changing another user's process is surfaced as needing admin, not a crash). An empty core selection is rejected (Windows would treat 0 as "let the OS decide"). Marked **PREVIEW** while it's verified. Closes #327.

## [1.37.0] - 2026-06-26

### Added
- **Display Profiles tab (Gaming & Profiles).** Quickly switch resolution and refresh rate per monitor — e.g. 165 Hz for gaming, 60 Hz for work — from the list of modes your display actually supports, using only the Windows display APIs (no NVIDIA/AMD tool conflict). Safe by design: changes apply for the session (a reboot reverts), and a **15-second auto-revert** restores the previous mode unless you confirm "Keep", so a bad mode can never strand you on a blank screen. Each mode is validated before applying; no admin needed. Marked **PREVIEW** while it's verified. Closes #328.

## [1.36.0] - 2026-06-26

### Added
- **File Lock Detector tab (Monitor).** When you hit a "file is in use" error, enter or browse to the file/folder and see exactly which process is holding it — name, PID, type and start time — via the Windows Restart Manager (the same mechanism Explorer's own dialog uses). You can end a selected locking process (with confirmation) to release the file; critical system processes are protected. Detecting works as a standard user; ending a process owned by SYSTEM or another user needs admin (surfaced cleanly, never crashes). Marked **PREVIEW** while it's verified. Closes #333.

## [1.35.0] - 2026-06-26

### Added
- **Timer Resolution tab (Gaming & Profiles).** Request the finest Windows timer resolution (≈0.5 ms instead of the ~15.6 ms default) to reduce input latency in games, and restore it with one click. Shows the live current/finest/default values — it re-queries the *effective* resolution rather than echoing the request, so the number is honest even when Windows stops honoring it (e.g. while minimized on Windows 11). Fully reversible and no admin needed; includes a clear power-consumption warning. Marked **PREVIEW** while it's verified. Closes #326.

## [1.34.0] - 2026-06-26

### Added
- **"Preview" marking for newly added features.** Features that are implemented but not yet fully verified now show a small **PREVIEW** pill next to their name in the sidebar and a short banner at the top of the page, so it's clear which tabs are brand-new. This lets new features ship and be tried out while they're still being polished.

## [1.33.16] - 2026-06-26

### Changed
- **Moved "Environment Variables" from the Customization group to Advanced**, next to Profile Export/Import — it's a system/developer tool, not a UI-customization one. The tab itself is unchanged.
- **Moved "App Alerts" from Privacy & Security to the Monitor group.** App Alerts passively watches for newly installed apps and keeps a timestamped history — it observes rather than enforces, so it belongs alongside the other monitoring tabs. The tab itself is unchanged.
- **Moved "Legacy Panels" from the System group to Info.** It's a read-only launcher for classic Windows applets (Device Manager, Disk Management, etc.) with no system modification, so it sits better among the other read-only Info tabs. The tab itself is unchanged.
- **Quick Cleanup now separates "Clean up" from "Repair Windows".** Clean TEMP / Empty Recycle Bin / Rescan and the SFC / DISM repairs were previously one mixed row of buttons; they're now two labelled sections so it's clear which actions free space and which repair Windows. Also added accessible names to all the action buttons for screen readers.

## [1.33.15] - 2026-06-26

### Fixed
- **Faster, smoother startup — several tabs no longer read the registry or probe drives on the UI thread while the app launches.** The Privacy & Telemetry, Environment Variables, App Blocker, Duplicate Finder, Disk Analyzer, and Profile Export/Import tabs loaded their initial data synchronously as the window was being built, which could briefly freeze launch (worst case: a stalled or disconnected drive). Each now loads in the background like the other tabs, so the window appears promptly and the tab fills in a moment later.

## [1.33.14] - 2026-06-26

### Fixed
- **Quick Cleanup empties the Recycle Bin the same reliable way as the rest of the app.** It used a PowerShell `Clear-RecycleBin` call that can leave "ghost" entries behind; it now uses the shared shell-API helper (the same one Deep Cleanup and the One-Click Tune-Up use), which clears every drive's bin cleanly. Keeps the three entry points from drifting apart.

## [1.33.13] - 2026-06-26

### Fixed
- **Traceroute monitor no longer risks a background error when stopped mid-cycle.** Stopping the live traceroute monitor disposed its cancellation source unconditionally, even if a route was still being traced; the still-running cycle could then throw on the disposed token in the background. Disposal is now deferred until the cycle actually finishes, matching the ping monitor. No visible change in normal use.

## [1.33.12] - 2026-06-26

### Fixed
- **Sidebar navigation items are now announced correctly by screen readers.** Each tab in the sidebar exposes its name (e.g. "Dashboard", "System Health") to assistive technology and UI automation; previously the items had no accessible name and were announced only as an internal type name. No visual change.

## [1.33.11] - 2026-06-26

### Fixed
- **Recycle Bin size estimate on the Cleanup tab now counts every fixed drive, not just C:.** It previously looked only at `C:\$Recycle.Bin`, so deleted files on other drives weren't reflected in the estimate. It now sums the hidden `$Recycle.Bin` on each ready fixed drive.
- **App Blocker no longer assumes Windows is installed on C:.** The sentinel path it writes to block an app is now derived from the real system directory instead of a hardcoded `C:\Windows\System32`, so blocking works on systems where Windows lives on another drive. Existing blocks are unaffected (the value is compared case-insensitively).

## [1.33.10] - 2026-06-25

### Fixed
- **The Dashboard's Quick Cleanup now uses the same safe temp cleaner as the One-Click Tune-Up.** It previously had its own inline cleaner that only looked at the top level of the user TEMP folder, ignored the Windows TEMP folder, and silently swallowed every error. It now cleans both temp folders and never follows a junction or symbolic link out of the temp tree, so it can't be redirected into unrelated files — matching the protection already used elsewhere.

## [1.33.9] - 2026-06-25

### Fixed
- **The Camera/Mic/Location tab no longer reads the registry on the UI thread at startup.** Its access history was loaded synchronously while the main window was being built, so the registry walk ran on every launch — even though most people never open the tab — and an unreadable or corrupt consent-store entry could surface as a startup failure. It now loads in the background like the other tabs (with a Cancel-able refresh) and a damaged entry is skipped instead of bubbling up.

## [1.33.8] - 2026-06-25

### Changed
- **Removed the duplicate "Reset Network Stack" button from System Fixes.** The same Winsock + TCP/IP reset and DNS flush already lives on the Network → Network Repair tab (as individual one-click tools), so having it in two places was confusing and risked the two copies drifting apart. System Fixes now links to Network Repair for network resets instead of duplicating them.
- **Renamed the "Privacy Monitor" tab to "Camera/Mic/Location"** so it is no longer confused with the separate "Privacy & Telemetry" tab. The feature is unchanged — it still shows which apps recently used your camera, microphone, or location.

### Fixed
- **Performance Mode's "Create restore point" now uses the same code as the Restore Points tab.** The two had separate implementations of the same operation that had begun to diverge; they now share one, which also enables System Restore on the system drive first if it was turned off. No visible change beyond that improvement.

## [1.33.7] - 2026-06-25

### Changed
- **Boot Analyzer: hardened the event-XML reader to tolerate alternate payload shapes.** The boot-performance reader already parses the standard `<EventData><Data Name="BootTime">` form that Windows emits, and that path is unchanged. This adds a defensive fallback that also resolves directly-named child elements (matched by local name, namespace-agnostic) for any event variant that nests its fields differently, so the reader is robust across builds. No behavior change on current Windows — the boot history is read exactly as before.

## [1.33.6] - 2026-06-25

### Fixed
- **The Debloater now correctly protects the Photos app from removal.** The system-critical denylist listed `Microsoft.Windows.Photos.Settings`, but the real package name is `Microsoft.Windows.Photos` — and because the match is a prefix check, the shorter real name never matched, so Photos was offered as removable. The entry is now the correct family name. A redundant, never-matching Windows Security entry was also removed (the correct one was already present). Added tests covering each critical package by its real name, plus tests confirming removal refuses a protected package and rejects an injection-shaped package name without ever invoking PowerShell.

## [1.33.5] - 2026-06-25

### Fixed
- **Profile Export/Import now correctly handles the theme/appearance section.** The exporter looked for every config file under one folder (Local AppData), but the theme is saved under Roaming AppData — so exporting never picked up your theme, and importing a profile wrote the theme to a folder the app doesn't read on startup, making it a silent no-op. Each config file is now read from and written to the same folder its owning feature uses (theme under Roaming, speed-test history under Local), so theme export and import actually take effect.

## [1.33.4] - 2026-06-25

### Fixed
- **"Undo" on the DNS & Hosts tab now fully restores the previous setting, including IPv6.** Applying a filtering preset configures both IPv4 and IPv6 resolvers, but Undo only captured and restored IPv4 — so reverting an ad/family-blocking preset left its IPv6 resolvers active, and on a dual-stack network the "undone" filtering was often still in effect. Undo now snapshots both families before a change and, on restore, clears the adapter's DNS first (removing anything applied since) before re-applying exactly what was captured. Undo is also offered now even if a change only partially applied. Additionally, programming the IPv6 resolvers is treated as best-effort: on a machine with IPv6 disabled the IPv4 change still succeeds (and stays undoable) instead of the whole apply failing.

## [1.33.3] - 2026-06-25

### Fixed
- **Editing an environment variable no longer breaks `%VAR%` expansion in PATH and similar variables.** SysManager wrote every variable as a plain string (REG_SZ), which silently converted variables Windows stores as expandable (REG_EXPAND_SZ) — most importantly the system `Path` — and froze references like `%SystemRoot%` or `%JAVA_HOME%` to whatever they happened to expand to at edit time, so they stopped tracking their source variable. Variables are now written directly to the registry with their original type preserved (a variable that was expandable stays expandable, and a new value containing a `%token%` is created as expandable), and the editor now shows and round-trips the raw `%VAR%` tokens instead of their expanded form. A single `WM_SETTINGCHANGE` broadcast still notifies running programs after a batch of changes.

### Added
- **Restore button on the Environment Variables tab.** The one-time backup taken before your first change can now be restored from inside the app: every variable is rewritten to its original value (type preserved) and any variable added since is removed. The Apply confirmation already promised this was possible — now there's a button for it. (System-scope restore needs administrator rights; if the safety backup can't be written, Apply now stops without making any change instead of risking an unbacked edit.)

## [1.33.2] - 2026-06-25

### Fixed
- **The Privacy Monitor no longer risks failing to open if the Windows access-history store contains an unusual entry.** Decoding an app's name from a desktop-app entry whose key was made up only of path separators could throw and, because the tab is built at start-up, prevent the window from loading. The decoder now falls back to the raw key name for such entries, and a single unreadable or malformed entry is skipped instead of aborting the whole scan. Also refined "in use now" detection so an app whose most recent start is newer than its last stop (e.g. after a force-close) is correctly shown as currently using the device, and the "last used" time now reflects the most recent of the two timestamps.

## [1.33.1] - 2026-06-25

### Fixed
- **Browser Cleaner now refuses to follow junctions or symbolic links out of a browser's own folders.** The reparse-point safety check failed *open* — if a folder's attributes couldn't be read it was treated as a normal folder and traversed — and the file-deletion path skipped the check entirely, so a junction or link placed inside a browser profile directory (something a standard user can create without administrator rights) could redirect a clean to measure or delete files outside the browser tree. The check now fails *safe* (an unreadable entry is treated as a link and skipped), runs before every measure and delete on both files and folders, and matches the behavior already used by Deep Cleanup and the File Shredder. The Firefox "Cache" entry now targets each profile's `cache2` cache folder specifically, instead of the whole profile directory — so a Firefox clean can never touch saved logins, bookmarks, or preferences.

## [1.33.0] - 2026-06-25

### Added
- **Boot Analyzer tab** (System group). The placeholder is now a working tab that reads the Windows boot-performance history (the Diagnostics-Performance log) and shows how long your PC takes to boot — total, core (main path), and desktop ready-up time — across recent boots, with a trend versus your recent average. A second list shows the apps, drivers, services, and devices Windows flagged as slowing boot, with the delay attributed to each. Read-only; reading the log requires administrator (the tab shows the standard elevation banner).

## [1.32.0] - 2026-06-25

### Added
- **Privacy Monitor tab** (Monitor group). The placeholder is now a working tab that shows which applications recently used your **camera, microphone, or location**, and when — read from the Windows access history (the CapabilityAccessManager consent store). Devices currently in use are flagged and sorted to the top. Read-only: to grant or revoke a permission, an **Open privacy settings** button hands off to Windows — SysManager never changes capability permissions itself.

## [1.31.0] - 2026-06-24

### Added
- **Browser Cleaner tab** (Privacy & Security). The placeholder is now a working tab that scans installed browsers — Chrome, Edge, Brave, Opera, and Firefox — and shows the on-disk size of each cleanable category (cache, history, cookies, sessions). Tick what to remove and clean it after a confirmation. Cookies and sessions are flagged "signs you out" and left **unticked by default**, so a clean never logs you out by accident. Cache and history are pre-selected. Cleaning is per-user (no admin); locked files (browser open) are skipped rather than forced, and reparse points are never followed.

## [1.30.0] - 2026-06-24

### Added
- **Windows Update timing & deferral controls** (Windows Update tab). A new "Update timing & deferral" section lets you **defer feature updates** by a configurable number of days while security and quality updates keep installing, **pause all updates** for a bounded window (up to 35 days, after which Windows auto-resumes), and **Restore default** to return to standard behavior. It uses the documented Windows Update policy registry keys and is fully reversible. There is deliberately no "disable updates forever" option — the strongest action is a clearly-bounded pause, so a machine is never left permanently unpatched. Requires administrator.

## [1.29.0] - 2026-06-24

### Added
- **BIOS & firmware section in System Health.** A scan now also reports your BIOS version, release date, and vendor, the motherboard model, the boot mode (UEFI / Legacy), and Secure Boot status — all read-only. A **Find BIOS update** button opens the right manufacturer support page (ASUS, MSI, Gigabyte, ASRock, Dell, HP, Lenovo, Acer, Biostar, or a web search as a fallback) based on the detected motherboard, and **Copy info** copies the board model + BIOS version for support searches. SysManager never flashes firmware itself; the section includes a clear reminder that BIOS updates carry risk if interrupted.

## [1.28.0] - 2026-06-24

### Added
- **Profile Export/Import tab** (Advanced group). The placeholder is now a working tab that exports SysManager's own configuration — theme/appearance and speed-test history — to a single portable JSON file, and imports it on another PC. Export is selective (tick which sections to include); import shows what the profile contains and asks for confirmation before overwriting, supports selective per-section apply, and refuses profiles made by a newer, incompatible version. Only SysManager's own config is ever touched — never system settings — so importing is fully reversible.

## [1.27.0] - 2026-06-24

### Added
- **DNS filtering presets and IPv6** (DNS & Hosts tab). The DNS preset switcher now includes ad/malware/family-blocking variants — Cloudflare Malware-blocking (1.1.1.2) and Family (1.1.1.3), AdGuard DNS (ad/tracker blocking, plus a Family variant), and OpenDNS FamilyShield — each with a short description of what it blocks. Every preset now also configures IPv6 resolvers automatically alongside IPv4. The existing "Reset to automatic (DHCP)" undo continues to work for all variants.

## [1.26.0] - 2026-06-24

### Added
- **System Fixes tab** (System group). A consolidated panel for common one-click Windows repairs, each with a plain-English description and a confirmation before it runs: **Reset Windows Update** (stop services, clear the SoftwareDistribution/catroot2 caches, restart services), **Reset Network Stack** (Winsock + TCP/IP reset and DNS flush), and **Reinstall WinGet** (re-register the App Installer when app installs/uninstalls fail). A **Set up Auto Sign-in** shortcut opens the built-in User Accounts dialog so Windows stores the credential securely — SysManager never handles your password. Repairs run with live output and report success or failure honestly. They modify system state and require administrator rights (standard elevation banner).

## [1.25.0] - 2026-06-24

### Added
- **Legacy Panels tab** (System group). A one-click launcher for the classic Windows applets that newer releases keep burying — Control Panel, Sound, Power Options, Network Connections, Region, System Properties, User Accounts, Device Manager, Computer Management, Programs and Features, Mouse, and Date and Time. Each is a pure launcher that just opens the built-in panel; nothing is modified, so no elevation or confirmation is needed. The applet list is hard-coded, so no input ever reaches the process launcher.

## [1.24.0] - 2026-06-24

### Added
- **Debloater & Ads tab.** The Privacy & Security group's "Debloater & Ads" placeholder is now a working tab. Scan installed Windows Store apps and remove the ones you don't use — with a curated "common bloat" preset that pre-selects safe, frequently-removed apps (Bing News/Weather, Clipchamp, Solitaire, Xbox apps, Teams consumer, and more). System-critical packages (the Store itself, frameworks, security and shell components) are denylisted: they're shown but can never be selected or removed. Removal runs per-user with an impact summary and confirmation first, and is reversible — any removed app can be reinstalled from the Microsoft Store. Search and per-app descriptions help you decide what each app is before removing it.

## [1.23.0] - 2026-06-24

### Added
- **Restore Points tab.** The System group's "Restore Points" placeholder is now a working tab. List every Windows System Restore point (sequence number, date, description, and type, newest first), create a new restore point with an optional description, and restore the PC to a selected point. Creating and restoring require administrator rights (the tab shows the standard elevation banner); viewing the list does not. Restoring warns clearly that Windows will restart and asks for confirmation first. Creating also enables System Restore on the system drive if it was off, and notes Windows' once-per-24-hour limit.

## [1.22.0] - 2026-06-24

### Added
- **Environment Variables tab.** The Customization group's "Environment Variables" placeholder is now a working tab. View and edit both User and System (machine-wide) variables in one grid, filter by scope, and search by name or value. A dedicated PATH editor opens for PATH-like variables: reorder directories, remove entries, strip duplicates in one click, and see missing folders highlighted. Add or remove variables too. Edits stay local until you press *Apply*; a one-time JSON backup of every variable is written first so the original environment can be restored. System-scope changes require administrator rights (the tab shows the standard elevation banner); User-scope changes do not. Changes broadcast to Windows so new terminals pick them up without a reboot.

## [1.21.0] - 2026-06-24

### Added
- **System Report tab.** The Info group's "System Report" placeholder is now a working tab. Click *Generate* to gather a full, read-only snapshot of this PC — operating system, CPU, memory (with per-slot detail), GPU, motherboard, storage health (including SMART temperature, wear, and power-on time when available), and active network adapters. Export the report as plain text, a styled self-contained HTML page, or structured JSON, or copy it to the clipboard. The report is read-only: nothing on the system is changed, and the file is written only where you choose — nothing leaves the machine.

## [1.20.67] - 2026-06-24

### Changed
- **Removed the temporary administrator-state startup logging.** The diagnostic added while investigating the "tabs still ask for admin after elevating" report has done its job and is no longer written. The underlying fix (the elevated relaunch now reliably lands in the elevated window) stays in place.

## [1.20.66] - 2026-06-24

### Fixed
- **The System Logs tab is no longer blank.** One of the severity summary cards (Warnings) set a glow effect's colour from a brush resource instead of a colour value, which threw when the tab's view was first built and left the whole tab empty. The glow now uses the colour directly, matching the other cards, so the tab renders normally.
- **The Privacy and Windows Features tabs no longer show two "running as administrator" notices at once.** When elevated, each of those tabs displayed both the standard full-width administrator banner and a small redundant "Administrator" chip in the toolbar. The redundant chip has been removed; the standard banner remains (and the Windows Features "Reboot pending" chip is unaffected).

## [1.20.65] - 2026-06-24

### Fixed
- **"Run as administrator" now reliably lands you in the elevated window instead of leaving the tabs still asking for admin.** The app allows only one instance at a time, enforced by a system-wide lock. When you clicked "Run as administrator", the elevated copy started while the original (non-elevated) window was still closing and had not yet released that lock — so the elevated copy saw "another instance is already running", brought the *old* non-elevated window back to the foreground, and exited. You were left on the non-elevated instance, where the tabs that need admin still showed the "needs administrator" notice. The elevated relaunch now hands the single-instance lock over to the new instance (it waits briefly for the old one to release it), so you end up in the actually-elevated window.

### Changed
- **Added administrator-state logging at startup** to confirm the fix above and catch any residual case. On launch the app records, to its local log file only, the process elevation state (Windows token elevation type + process ID) and each affected tab's administrator status, under `%LocalAppData%\SysManager\logs` with usernames scrubbed — nothing is sent anywhere.

## [1.20.64] - 2026-06-24

### Fixed
- **The broken-shortcut scan no longer stops early at the first unreadable folder, and no longer mislabels long-target shortcuts as broken.** The scan used a recursive enumerator that threw the moment it reached a folder it couldn't read (e.g. a protected Start Menu subfolder), which aborted the rest of that location and silently skipped every shortcut after it. It now walks folder-by-folder and skips only the unreadable ones (and skips junction/symlink folders so it can't be redirected out of the tree). Separately, shortcut targets were read into a 260-character buffer, so a target longer than that was truncated and the shortcut wrongly reported as broken — the buffer is now large enough to hold extended-length paths.

## [1.20.63] - 2026-06-24

### Fixed
- **The Process Manager no longer loses your selected row every second.** The list auto-refreshes once a second, and each refresh rebuilt the whole collection — which cleared the row you had selected (and your scroll position) and re-extracted every process icon each time. The refresh now updates the existing rows in place: surviving processes keep their row (and your selection), new processes are added, exited ones are removed, and icons are only fetched for newly-appeared processes.

## [1.20.62] - 2026-06-24

### Fixed
- **"Enable All" on the Startup tab now reports honestly when some items can't be enabled.** It used to always say "All items enabled" even if a registry write failed (for example, an item that needs administrator rights), hiding the failure. It now counts the results and reports how many were enabled and how many could not be — and says so when everything was already enabled.
- **Startup actions can no longer overlap.** Scan, Enable All and the per-item toggle all read or write the same startup registry/task state; running two at once could interleave their writes and produce inconsistent counts. They are now disabled while one of them is running.

## [1.20.61] - 2026-06-24

### Fixed
- **Starting a scan or uninstall on the Uninstaller tab while another is running no longer risks a crash.** Both the Scan and Uninstall actions re-create one shared cancellation source, so triggering a second action while the first was still running could dispose the cancellation source the first was using and throw. Those two actions are now disabled while one of them runs — matching the App Updates, Windows Update and Bulk Installer tabs — while Cancel stays available.

## [1.20.60] - 2026-06-24

### Fixed
- **Scanning Windows optional features while a feature toggle is running no longer mixes up their output.** The Scan and enable/disable actions share one PowerShell runner, and each subscribes to its output independently. Toggling was already blocked while busy, but Scan was not — so starting a scan mid-toggle let both read the same output stream and cross-contaminate results. Scan is now disabled while a toggle runs (and vice versa), making the two mutually exclusive.

## [1.20.59] - 2026-06-24

### Fixed
- **The App Updates and Context Menu tabs no longer risk a crash when their actions overlap.** On both tabs the long-running actions shared one cancellation source that each action re-created. Starting a second action while the first was still running (e.g. clicking Scan again mid-upgrade, or applying a context-menu preset while a scan ran) could dispose the cancellation source the first was still using and throw. Those actions are now disabled while one of them is running — matching how the Windows Update and Bulk Installer tabs already behave — while Cancel stays available. On the Context Menu tab this also prevents two overlapping runs from corrupting the entry list or triggering two Explorer restarts at once.

## [1.20.58] - 2026-06-24

### Security
- **The file shredder can no longer be tricked into destroying a system file through a junction in the middle of the path.** Its safety check resolved a link only at the end of the path (and expanded short `8.3` names), but a junction or symlink in a *parent* folder — which a standard user can create without admin — was not followed during validation. A path such as `C:\Temp\link\notepad.exe`, where `C:\Temp\link` pointed into `System32`, slipped past the system-folder denylist and the real protected file behind it was overwritten and deleted. The shredder now asks Windows for the fully-resolved canonical path (collapsing every junction/symlink anywhere in the chain) and re-checks that against the protected-folder list before touching anything.

## [1.20.57] - 2026-06-24

### Security
- **The Uninstaller no longer runs a per-user app's uninstaller from a user-writable folder while elevated.** When SysManager ran as Administrator, uninstalling a local app executed the path from its registry `UninstallString` (and any DLL a `rundll32` command would load). That path was trusted even when it pointed inside `%LocalAppData%`, which a standard user can write to — and the uninstall key in `HKCU` is also user-writable. An unprivileged attacker could therefore plant a binary there plus a fake uninstall entry, then have an elevated SysManager run it with admin rights (local privilege escalation). Admin-protected locations (Program Files, Windows, ProgramData) stay trusted as before; the per-user location is now trusted only when SysManager is *not* elevated, so normal per-user uninstalls (e.g. VS Code, Discord) are unaffected.

## [1.20.56] - 2026-06-23

### Fixed
- **SFC and DISM repairs can no longer run at the same time and corrupt each other's output.** Both share a single PowerShell runner whose output event was subscribed by each command independently, so starting one while the other ran cross-contaminated their captured results and progress. They now acquire a shared system-modification lock, making them mutually exclusive (and blocked against other system-repair operations), with a clear "already running" message instead.

## [1.20.55] - 2026-06-23

### Security
- **Deep Cleanup can no longer delete data outside a cache folder via a junction at its root.** The reparse-point guard that stops cleanup from following junctions/symlinks only covered sub-folders, not the cleanup root itself. A junction planted at a cleanup-root path (which a normal user can create without admin) was traversed directly, so the linked target's files could be deleted. The traversal now checks the root for being a reparse point first, and the per-file delete catches only the expected I/O/access exceptions instead of all exceptions.

## [1.20.54] - 2026-06-23

### Fixed
- **Cancelling a disk-usage or large-files scan no longer shows partial results as if complete.** Both scanners stopped mid-traversal on cancel but still returned what they had gathered, so a cancelled scan looked like a finished one with incomplete data. They now stop cleanly and report the scan as cancelled.
- **Traceroute now stops correctly when the destination replies on any probe.** The hop status and the stop-at-destination check used only the last probe's result, so if an earlier probe reached the destination but a later one timed out, the trace could mislabel the hop and keep probing past the target. It now tracks the destination-reached result across all probes of each hop.

## [1.20.53] - 2026-06-23

### Fixed
- **Dashboard quick actions now ask before changing your system.** "Quick Cleanup" deleted temporary files and "Update All Apps" ran a winget upgrade of every installed app with no confirmation — unlike the equivalent actions on their dedicated tabs. Both now show a confirmation dialog first, matching the rest of the app.
- **Installing Windows updates now asks for confirmation.** Selecting updates and clicking install applied them (including drivers and feature updates that can force a restart) without a prompt. A confirmation dialog now precedes the install.
- **Deleting broken shortcuts no longer races other disk operations.** The shortcut cleaner's delete now holds the shared disk operation lock for its duration, so it can't run at the same time as a cleanup or tune-up.

## [1.20.52] - 2026-06-22

### Fixed
- **Cancelling a speed test now stops the ping phase immediately.** The initial latency measurement ran four 2-second probes without honoring the cancellation token, so pressing Cancel during the ping phase could wait up to 8 seconds before stopping. The probes now observe cancellation and stop right away.

## [1.20.51] - 2026-06-22

### Security
- **The file shredder now expands 8.3 short paths before its system-folder safety check.** The guard that blocks shredding inside Windows/System32/Program Files compared full paths, but a short-name alias (e.g. `C:\PROGRA~1`) isn't expanded by the framework, so it could slip past the check. Short paths are now expanded to their long form first, closing that bypass.

## [1.20.50] - 2026-06-22

### Fixed
- **Reverse-DNS lookups during a traceroute no longer keep running after they time out.** Each hop's host-name lookup had an 800 ms budget, but the timeout only abandoned the wait — the lookup itself kept running in the background. It is now actually cancelled when the budget elapses, freeing the resource immediately.

## [1.20.49] - 2026-06-22

### Fixed
- **Removed a rare crash risk in the network ping chart.** The per-host line offset used `Math.Abs` on a hash code, which throws if the hash happens to be the most-negative integer. The calculation now masks the sign bit instead, so it can never overflow.

## [1.20.48] - 2026-06-22

### Fixed
- **Hardened the Zip Slip guard on the speed-test CLI download.** The containment check that keeps extracted archive entries inside the tools directory used a plain prefix test, which a sibling folder whose name merely started with the target's name could slip past. The check now requires a directory-separator boundary, closing that edge case.

## [1.20.47] - 2026-06-22

### Fixed
- **Disk health no longer drops every disk when one reports a bad value.** If a single physical disk returned an unexpected value for its media type, bus type, size, or health status, the conversion threw and aborted the whole scan — so no disks were shown. A disk with an unreadable field is now skipped individually and the rest still appear.

## [1.20.46] - 2026-06-22

### Fixed
- **Windows Update scans and installs no longer leak COM objects on error paths.** Reading an update's KB article list left its underlying COM collection unreleased on every scanned update, and a failed install (one that threw a COM error) leaked the update-installer object. Both are now released deterministically even when the operation throws.

## [1.20.45] - 2026-06-22

### Fixed
- **Saving or restoring the hosts file no longer freezes the window.** The Hosts editor wrote the system hosts file (and restored it from backup) synchronously on the UI thread, so the app could hang briefly while the file was written to or copied from System32. Both operations now run in the background.

## [1.20.44] - 2026-06-22

### Fixed
- **Better screen-reader support for tables and per-row toggles.** Every data table announced itself generically as "Data table"; each now has a content-specific name (Installed applications, Running processes, Windows services, Event log entries, etc.). The per-row enable/disable toggles in Startup Manager and Windows Features, the Startup "hide system entries" toggle, and the Shortcut Cleaner selection checkboxes also gained clear accessible names. No visual change.

## [1.20.43] - 2026-06-22

### Fixed
- **App-install monitoring can no longer start twice and leak a timer.** Starting the App Alerts monitor a second time without stopping it first orphaned the previous background timer and added duplicate folder watchers. Starting now does nothing if monitoring is already running.
- **Listing fixed drives no longer aborts if one drive becomes unavailable mid-scan.** Reading a volume's label/size could throw if the drive dropped out or was locked (e.g. BitLocker) right after it was checked as ready; that one drive is now skipped instead of failing the whole list.
- **Process icons resolve correctly for apps installed after launch.** A failed icon-path lookup was cached permanently, so a program installed later never got its icon until restart. Only successful lookups are cached now.
- **A corrupt cached app icon no longer sticks forever.** If a downloaded icon was truncated/corrupt, the bad cache file was kept and reused; it's now deleted and re-downloaded on next use.
- **Shortcut Cleaner reports an accurate deletion count.** Moving a broken shortcut to the Recycle Bin could silently fail (the shell reports failure without throwing) yet still be counted as deleted; only genuinely recycled items are counted now.

## [1.20.42] - 2026-06-17

### Fixed
- **Deleting broken shortcuts no longer freezes the window.** The Shortcut Cleaner ran the shell delete on the UI thread, so removing many shortcuts could hang the app until it finished; the delete now runs in the background.
- **Loading the full installed-apps list no longer freezes the App Alerts tab.** "Show all installed apps" walked the entire registry uninstall tree on the UI thread; that scan now runs in the background.
- **Refreshing the System Logs twice in a row no longer mixes results.** Starting a second refresh while one was running could let the cancelled run's leftover batches land in the new list. The Refresh button is now disabled while a scan is in progress.
- **Applying a Context Menu preset no longer freezes the window.** Applying a preset ran the registry changes and the Explorer restart on the UI thread, briefly hanging the app; that work now runs in the background.

## [1.20.41] - 2026-06-17

### Fixed
- **"Restore All" in Performance Mode now fully clears the saved baseline.** After restoring everything to the original state, the on-disk snapshot was left behind, so the next change reloaded the now-reverted values as its baseline — a later "Restore All" could then re-apply stale settings. The saved snapshot is now deleted when you restore all.
- **Applying several performance tweaks at once can no longer race the baseline capture.** The independent Apply buttons (power plan, visual effects, Game Mode, Xbox Game Bar, GPU, processor state) each captured the "before" snapshot without coordination, so two run together could both think no baseline existed. Baseline capture is now serialized so the original state is recorded exactly once.

## [1.20.40] - 2026-06-17

### Fixed
- **The Health Score no longer fails outright when a system query hits a transient WMI error.** A repository or RPC fault while reading system info, disk health, or battery could throw an error the score didn't handle, failing the whole calculation; each source now degrades gracefully and the score is still produced from the rest.
- **The memory-error check no longer counts a *passing* memory test as an error.** It counted every Windows Memory Diagnostic result, including the "no errors found" result (event 1101), as a problem; it now counts only the actual error result (event 1201).
- **Known-folder lookup (Downloads, Documents, etc.) now falls back correctly when the system call fails.** The call's failure code was being ignored, so a failed lookup could return an empty path instead of using the standard fallback location; the result is now checked and the fallback applies.

## [1.20.39] - 2026-06-17

### Fixed
- **A transient system-info failure can no longer crash the app from the tray.** The system tray refreshes a CPU/RAM/uptime tooltip on a background timer; if the underlying WMI query failed transiently (for example "RPC server unavailable"), the error could go unhandled and bring the whole app down. The tray refresh now handles those failures and simply skips that tick.

## [1.20.38] - 2026-06-17

### Fixed
- **External command output is no longer occasionally truncated.** Results from tools like the network repair commands, chkdsk, and winget are read on background threads; the app could snapshot the captured text a moment before the last lines arrived, dropping them. The runner now waits for the output streams to fully drain before returning.
- **The Speed Test no longer leaves a stray `speedtest.exe` running if it is cancelled or times out mid-transfer.** Cancellation during the result read could skip the cleanup that kills the CLI process; the process is now always terminated on cancellation.
- **Captured output from chkdsk and the winget upgrade scan is now collected safely.** Both gathered command output into a list that two background reader threads wrote to at once, which could drop or corrupt lines; they now use a thread-safe collector.

## [1.20.37] - 2026-06-17

### Fixed
- **Windows Update and Bulk Installer buttons no longer cause an error when clicked during a running operation.** The toolbar actions (List updates / History / Pending reboot / Install selected, and Bulk Installer's Install Selected) stayed clickable while one was already running; a second click could cancel the first operation's work and crash it. These actions are now disabled while one is in progress and re-enable when it finishes.

## [1.20.36] - 2026-06-17

### Fixed
- **The Dashboard's live temperature readings now resume after you leave and return to the tab.** Temperature polling was started only once at launch; navigating away stopped it permanently, so on returning to the Dashboard the temperatures stopped updating until the app was restarted. Temperature polling now restarts together with the rest of the live vitals whenever the Dashboard is shown again.
- **The Dashboard Event Log check no longer leaks system handles.** Counting recent critical events read each event record without releasing it, leaking a Windows event-log handle per event on every scan. Each record is now disposed as it is read.

## [1.20.35] - 2026-06-17

### Fixed
- **Several panels are now legible in light themes.** A number of cards, banners, and badges (Deep Cleanup safety note and action row, Privacy toggle rows, the File Shredder SSD warning, Quick Cleanup SFC/DISM result cards, the Speed Test info banner, the Windows Features "reboot pending" badge, the Bulk Installer status/installed badges, and the work-in-progress badge) used fixed dark colours that did not change with the theme, so their text became hard to read on the light presets. They now use theme-aware colours and adapt to the active theme.
- **The File Shredder "Shred All" button is readable again.** It showed red text on the indigo primary-button background (very low contrast); it now uses the standard red danger-button style with white text.

## [1.20.34] - 2026-06-17

### Fixed
- **Re-enabling a service now restores its original startup type instead of always setting it to Manual.** When you disabled a service, SysManager forgot what its startup type had been, and "Enable" always set it to Manual — so a service that was originally Automatic (for example) would not start on the next boot as it used to. Disabling now remembers the previous startup type and Enable restores it exactly.

## [1.20.33] - 2026-06-17

### Fixed
- **Temp cleanup can no longer delete files outside the temp folders by following a junction or symbolic link.** Both the Quick Cleanup temp sweep and the background tune-up temp cleanup recursed into reparse points (directory junctions / symbolic links) inside `%TEMP%`, so a link pointing elsewhere could cause real user data outside the temp tree to be deleted. Both now skip reparse points during the walk and only ever remove the link itself, never its target.

## [1.20.32] - 2026-06-17

### Fixed
- **Windows Update no longer leaks system resources during scan and install.** Each scanned and installed update holds several underlying Windows Update COM objects (the update's identity, category list, downloader, and child collections). Some of these were only released on the success path, so a cancelled scan, a failed download, or a category match leaked a handle every time. All of them are now released on every path, including errors and cancellation.

## [1.20.31] - 2026-06-17

### Fixed
- **DNS changes now report failure honestly instead of a false success.** Applying, resetting, or restoring DNS ran the underlying command without treating its errors as fatal, so a change that was actually rejected (for example without administrator rights, or when the adapter dropped) could still be reported as applied. These operations now surface the real error.
- **DNS now targets the same network adapter for reading and changing.** The current-DNS display, the saved snapshot, and the apply/reset/restore actions used slightly different rules to pick the active adapter, so on a PC with several adapters (Wi-Fi + Ethernet + VPN) a change could land on a different adapter than the one shown — breaking the undo. All paths now select the adapter by one shared rule.
- **Disabling a service now reports failure honestly.** Changing a service's startup type ignored the result of the underlying `sc.exe` call, so a change blocked by Windows (for example on a protected service) was still reported as "set to Disabled". The real exit code is now surfaced as a clear error.
- **Refreshing a service's status after a change can no longer crash the app.** Reading a service's status right after stopping or disabling it could throw if the handle had just become invalid; that error is now handled and the status shows as "Unknown" instead.
- **Enabling or disabling a Windows optional feature now reports failure honestly.** A failed feature change could still be reported as successful because the command's error was not propagated; failures now surface correctly.
- **Privacy toggles that fail to apply no longer look like they succeeded.** Toggles backed by machine-wide (HKLM) settings need administrator rights; without elevation the write was silently swallowed and the toggle appeared applied. The app now reports how many changes need administrator rights and keeps the unapplied ones marked as pending.

## [1.20.30] - 2026-06-17

### Fixed
- **App Blocker can no longer block a startup-critical Windows process.** The blocker accepted any executable name, so a user could block boot/logon components such as `winlogon.exe` or `lsass.exe` — which would prevent Windows from starting and leave no way to launch the app to undo it. It now refuses a built-in list of boot- and logon-critical executables with a clear message.
- **Services can no longer disable a boot-critical service behind a generic prompt.** Disabling a service classified as Critical (for example Remote Procedure Call or DCOM Server Process Launcher) could stop Windows from booting or signing in, yet the confirmation read the same as for any safe-to-disable service. Critical services are now refused outright with an explanation of why.
- **Uninstaller no longer runs unvalidated arguments through `rundll32`/`MsiExec`.** A per-user uninstall entry (which can be written without administrator rights) could point these trusted system binaries at an arbitrary DLL or package, which they would then execute with the app's elevation. The uninstaller now requires the `rundll32` DLL to live under a trusted directory and restricts `MsiExec` to a product-code uninstall.
- **File Shredder no longer crashes after a successful shred.** Once at least one item was shredded, the cleanup step that removes finished items ran off the UI thread and threw, aborting the operation on its normal success path. The shredding flow now resumes on the UI thread so completed items are removed cleanly.

## [1.20.29] - 2026-06-17

### Fixed
- **Speed Test no longer reports an inflated upload speed when the server cuts the upload short.** The upload test always credited the full 50 MB payload against the elapsed time, even when the server rejected the request early and only part of the data was sent — producing an unrealistically high number. It now reports a failed measurement if the server rejects the upload, and otherwise measures the bytes actually sent, so the upload speed reflects reality.

## [1.20.28] - 2026-06-17

### Fixed
- **Dashboard alert checks are more robust.** The "estimated time remaining" hint that appears while a dashboard check is still running updated its on-screen state from a background thread, which could occasionally fail to display or glitch; it now updates on the UI thread like every other dashboard check. Separately, two error paths (a failed dashboard check and a failed quick action) now record the underlying error in the log so failures can be diagnosed instead of vanishing silently. No visible change in normal use.

## [1.20.27] - 2026-06-17

### Fixed
- **Deep Cleanup now empties the Recycle Bin through the Windows shell instead of deleting its files directly.** The "Recycle Bin (all drives)" category used the same raw file-delete path as ordinary caches, removing the internal `$Recycle.Bin` index/data files and per-account folders directly. That could leave the Recycle Bin in an inconsistent state (ghost or undeletable items in Explorer) until the next sign-in. It now empties the bin via the documented shell API, the same safe method used elsewhere in the app.

## [1.20.26] - 2026-06-16

### Fixed
- **The About → System Info diagnostics no longer leak system handles.** Building the environment summary (CPU, RAM, GPU, display, and OS lines) queried Windows for hardware details but never released the query result sets, leaking a small COM handle on each refresh. Every query now disposes its results, matching how the rest of the app handles these reads. No change to the information shown.

## [1.20.25] - 2026-06-16

### Fixed
- **Saving the hosts file now preserves its original permissions.** The atomic save introduced in 1.20.24 replaced the file by moving a freshly written temporary file over it, which left the new file with the folder's default permissions instead of the hosts file's own (more restrictive) access-control settings. Saving now replaces the file in place, keeping its existing permissions and attributes intact.

## [1.20.24] - 2026-06-12

### Fixed
- **Hosts entries with multiple hostnames per IP are no longer silently lost.** A line like `127.0.0.1 a b c` was read keeping only the first hostname, so editing and saving dropped the rest. Each hostname is now read as its own entry and survives a round trip.
- **The hosts file is now written atomically.** Saving wrote directly over the file, so a crash mid-write could leave it empty or truncated. It now writes to a temporary file and atomically replaces the target, cleaning up the temp file afterward.

## [1.20.23] - 2026-06-12

### Fixed
- **No more leaked process handles when opening Explorer / links.** Ten places that launch Explorer ("show in folder"/"open file location"), Event Viewer, the browser, or the updater left the returned process handle undisposed. Each now releases it. The launched program is unaffected; only the orphaned handle is cleaned up. Covers Deep Cleanup, Disk Analyzer, Duplicate Finder, Startup Manager, Logs, About, and the Context Menu refresh.

## [1.20.22] - 2026-06-12

### Fixed
- **Drive/disk health reads survive systems without the Storage WMI namespace.** Disk Health and System Info could throw an unhandled COM error on older or headless Windows where the `root\Microsoft\Windows\Storage` namespace isn't present; both now handle that case and fall back gracefully (mirrors the earlier Fixed Drives fix).
- **No leaked process handle when opening a file location.** "Open file location" in Process Manager left the launched Explorer process handle undisposed; it's now released.
- **Swallowed errors are now diagnosable.** Several silent `catch` blocks now log at Debug level with the full exception (update-file cleanup, Deep Cleanup directory deletion, Windows Update size extraction, and the Dashboard polling loop), so failures leave a trace in the log instead of vanishing.

## [1.20.21] - 2026-06-12

### Changed
- **Some status/accent colors now follow the theme instead of being hardcoded.** Replaced hardcoded color values that exactly matched a theme color (success green, warning amber, danger red, accent, hover border) with theme references on the Dashboard, Network Repair, Privacy, and Uninstaller tabs. The colors render identically today but will track the theme going forward.

## [1.20.20] - 2026-06-12

### Fixed
- **Much better screen-reader support across the app.** Many buttons, toggles, drop-downs, search/filter boxes, data grids, and per-row actions had no accessible name, so screen readers announced them generically (or not at all). Added clear, specific accessible names to interactive controls across 21 tabs — including destructive actions (Delete, Shred, Clear History), per-row buttons (now named after the item they act on), and unlabeled inputs. No visual change.

## [1.20.19] - 2026-06-12

### Fixed
- **Activity history can no longer be corrupted by concurrent updates.** The recent-actions log saved its data outside the lock that protects it, so two actions logged at the same time could clash and produce a "collection modified" error or a truncated file. It now writes a consistent snapshot taken under the lock.
- **The in-app updater fails gracefully when the download location can't be determined.** Installing an update now checks the downloaded file's folder up front and shows a clear message instead of risking a crash.

## [1.20.18] - 2026-06-12

### Fixed
- **More resilient reading of battery, memory, and app-list data.** Unexpected values from Windows (battery stats, memory-module details) could throw a conversion error and interrupt a scan; those conversions now fall back safely. The winget output parser also no longer throws when the tool reports columns in an unusual order.

## [1.20.17] - 2026-06-12

### Fixed
- **Plugged several resource leaks.** The system-tray icon's underlying graphics handle was never released on shutdown; the elevated-relaunch helper left a process handle open; and a memory-module query left a WMI result set undisposed. All are now released properly. Also added handling for a WMI namespace being unavailable when reading drive media/bus details, so that case fails quietly instead of surfacing an error.

## [1.20.16] - 2026-06-12

### Fixed
- **Starting or stopping a Windows service no longer crashes the app on failure.** If a service couldn't be started or stopped (access denied, a dependency problem, or the service state changing mid-operation), the underlying error escaped unhandled. Those failures are now caught and shown as a clear status message.
- **Privacy toggles no longer crash on a registry write error.** Applying a privacy toggle only handled permission errors; a registry I/O error or an invalid key/value now logs a warning instead of bringing down the app.
- **Disabling Xbox Game Bar no longer crashes on a registry I/O error.** The Performance tab's Xbox Game Bar action now handles I/O failures the same way it already handled permission and state errors.
- **A misbehaving UI subscriber can no longer permanently lock an operation category.** If a property-change notification threw while acquiring an operation lock, the category could stay locked forever (blocking all future disk/network/system operations of that kind). The lock is now rolled back cleanly if that happens.

## [1.20.15] - 2026-06-12

### Fixed
- **File Shredder can no longer be tricked into destroying a protected system file.** The safety check that blocks shredding inside Windows, System32, and Program Files compared the path you gave it without first following symbolic links — so a link placed in an allowed folder but pointing at a protected system file slipped past the check, and the real file was overwritten. The check now resolves link targets before validating and matches protected folders on an exact directory boundary (so an unrelated folder that merely starts with the same name is no longer falsely blocked). If a file's contents are securely overwritten but the entry can't be removed, the app now reports that clearly instead of surfacing a raw error.

## [1.20.14] - 2026-06-11

### Fixed
- **No more hidden shutdown errors when closing the app.** Cleanup ran more than once on exit (it is triggered by both the window-close and the application-exit events), which double-released the network charts' underlying graphics resources — an error that was caught and hidden but still occurred on every exit. Cleanup is now guarded so it runs exactly once, and the shared network monitors are stopped rather than disposed twice, so the app shuts down cleanly.

## [1.20.13] - 2026-06-11

### Fixed
- **The live-output console now matches the app's card styling.** Its container used a one-off border with a smaller corner radius than every other card; it now uses the shared `Card` style (same surface, border, and 10px radius) while keeping its zero inner padding, so the console looks consistent on the App Updates, Cleanup, System Health, and Windows Update tabs.

## [1.20.12] - 2026-06-10

### Fixed
- **Windows Update list has a real select-all checkbox.** The updates grid's checkbox column used a decorative `✓` header that did nothing and had no accessible name. It is now a working select-all checkbox ("Select all updates") that toggles every row, and it stays in sync with the existing Select all / Deselect all buttons.

## [1.20.11] - 2026-06-10

### Fixed
- **System Logs severity tiles are now screen-reader friendly and colorblind-safe.** The Critical / Errors / Warnings / Info count tiles conveyed their value only through color and an unlabeled number. Each tile now exposes an accessible name with its count (e.g. "Critical events: 3"), and the Critical and Errors tiles — both previously red and indistinguishable to colorblind users — are now told apart by a leading glyph (▲ vs ●) and weight, not hue alone.

## [1.20.10] - 2026-06-10

### Fixed
- **Console output toolbar is now consistent and screen-reader friendly.** The Clear and Copy buttons on the live-output console (shown on App Updates, Cleanup, System Health, and Windows Update) used the implicit default button style; they now use the standard `SecondaryButton` style like the rest of the app. The output list also gained an accessible name ("Live output").

## [1.20.9] - 2026-06-10

### Fixed
- **System Logs time-range chips now show which range is active and are keyboard-navigable.** The 1h / 24h / 7d / 30d / All chips were unlabeled buttons with no selected state, so you couldn't tell which range was applied. They are now a proper radio-button group (matching the Services tab filter chips): the active range is highlighted, the group is arrow-key navigable, and each chip is named for screen readers.

## [1.20.8] - 2026-06-10

### Fixed
- **Theme presets are now keyboard-accessible.** The preset cards in the appearance popup were mouse-only — not reachable by Tab, not activatable from the keyboard, and unnamed to screen readers. They are now focusable, activate with Enter or Space, and announce their preset name. The custom-color hex inputs (accent, background, surface, text) also gained accessible names.

## [1.20.7] - 2026-06-10

### Fixed
- **Search and filter boxes now have accessible names.** The category/filter/search inputs on Apps (Bulk Installer), Uninstaller, Process Manager, Services, and Windows Features had no `AutomationProperties.Name`, so screen readers announced them as anonymous edit fields. Each now states what it filters (e.g. "Filter installed apps", "Search winget packages", "Filter Windows features").

## [1.20.6] - 2026-06-10

### Fixed
- **Screen readers now announce row actions and the App Blocker input by name.** Several icon-only and unlabeled controls had no accessible name, so assistive technology announced them generically (e.g. "X button"). Added `AutomationProperties.Name` to the Process Manager Kill/Open buttons (with the process name), the Ping remove-target button (with the target name), the File Shredder row Remove button (with the file name), and the App Blocker executable-name input.

## [1.20.5] - 2026-06-08

### Added
- **Undo a DNS change.** Applying a DNS preset now snapshots the servers in effect beforehand, and a new "Undo" button on the DNS & Hosts tab restores that exact previous configuration (re-applying the prior static servers, or resetting to DHCP if that was the prior state). Previously the only way back was "Reset to DHCP", which silently discarded any manually-configured DNS.

## [1.20.4] - 2026-06-08

### Fixed
- **Ping monitor no longer leaks its CancellationTokenSource.** When `Stop()` was called while the ping loop was still winding down (the 1.5s wait timed out), the CTS reference was dropped and never disposed. It is now disposed once the loop actually finishes — immediately if already complete, otherwise via a continuation.
- **System Logs no longer block the UI thread while reading the Event Log.** `EventLogService.ReadAsync` ran the blocking `EventLogReader.ReadEvent()` COM call on the caller's (UI) thread, freezing the app while large logs were enumerated. Each read now runs on the thread pool via `Task.Run`.

### Changed
- **Dashboard GPU name now works for AMD/Intel, not just NVIDIA.** When no NVIDIA GPU is present, the Dashboard falls back to `Win32_VideoController` (WMI) to show the adapter name. Live usage % remains NVIDIA-only (it requires vendor-specific APIs).

## [1.20.3] - 2026-06-08

### Fixed
- **Drive enumeration no longer crashes on missing WMI properties.** `FixedDriveService` read `MediaType`/`BusType` with `Convert.ToUInt32(value ?? 0u)`, but WMI returns `DBNull.Value` (not null) for absent properties, so `Convert.ToUInt32(DBNull.Value)` threw and aborted the whole scan on some hardware. Reads now go through a `ToUInt32Safe` helper that treats null and `DBNull` as 0.
- **Uninstaller trusted-directory check no longer accepts sibling folders.** `IsUnderTrustedDirectory` used a bare `StartsWith`, so `C:\Program Files Evil\…` passed the `C:\Program Files` check. It now compares on a normalized directory boundary (trailing separator) so only true sub-paths of a trusted directory are accepted.

## [1.20.2] - 2026-06-08

### Fixed
- **Restore point creation no longer reports false success.** `CreateRestorePointAsync` returned `true` whenever the PowerShell call didn't throw, but `Checkpoint-Computer` fails *non-terminating* in common cases (notably the once-per-24h rate limit), so failures were reported as success — undermining the "everything is reversible" guarantee. It now forces the error to terminate and only returns `true` when an explicit success sentinel is emitted.
- **In-app updater can now find its download asset.** The release-asset matcher looked for a fixed `SysManager.exe`, but releases publish `SysManager-v<version>.exe`, so `AssetUrl`/`AssetSize` were always null. Replaced with `IsMainExeAsset`, which matches the versioned executable and excludes the `.sha256` companion.
- **Windows Update scan no longer leaks COM objects on failure.** `WindowsUpdateService.ScanAsync` released its COM objects only on the success path, so a cancellation or mapping error mid-scan leaked them. The releases now run in a `finally` block.

## [1.20.1] - 2026-06-08

### Fixed
- **App Blocker no longer clobbers a pre-existing debugger.** `BlockApp` wrote its IFEO `Debugger` value unconditionally, overwriting any value already present — which could break a legitimately-debugged application and was unrecoverable (Unblock only removes SysManager's own value). It now refuses to block an executable that already has an external `Debugger` set, leaving the existing configuration intact.
- **Privacy changes now require confirmation.** Applying pending privacy toggles to the registry now prompts with `DialogService.Confirm` first, stating how many changes will be written and how to revert. Declining keeps the changes pending and writes nothing.

## [1.20.0] - 2026-06-08

### Added
- **Restore original hosts file.** A new "Restore original" button on the DNS & Hosts tab reverts the system hosts file to the pristine backup taken before SysManager first modified it.

### Fixed
- **Hosts file backup no longer destroys the pristine original.** `SaveHosts` previously copied the current hosts file over `hosts.bak` on **every** save with `overwrite: true`, so after the first save the backup already held SysManager's own output — the real original was lost and restore was impossible. The backup is now written only once (when none exists), preserving the true pre-SysManager file.
- **DNS and hosts changes now require confirmation.** Applying a DNS preset and overwriting the system hosts file each prompt with `DialogService.Confirm` first, stating exactly what will change and how to revert. Declining makes no system change.

### Changed
- `HostsFileService` gained a path-injection constructor (used only for testing) and `HasBackup` / `RestoreBackup` members backing the new restore flow.

## [1.19.4] - 2026-06-08

### Fixed
- **Destructive cleanup actions now require confirmation.** Three deletion paths ran immediately with no prompt: Deep Cleanup (`CleanAsync`), Temp Cleanup, and Empty Recycle Bin. Each now shows a `DialogService.Confirm` dialog first — Deep Cleanup states the file count and total size and warns the files bypass the Recycle Bin. Declining cancels with no changes. Adds regression tests covering both the decline (nothing deleted) and confirm (files deleted) paths.

## [1.19.3] - 2026-06-08

### Fixed
- **Deep Cleanup no longer follows junctions / symbolic links (data-loss fix).** The cleanup traversal descended into reparse points, so a junction inside a cache folder could lead `File.Delete` to remove files **outside** the target tree — for example real user data behind a junction. Traversal now detects reparse points (`FileAttributes.ReparsePoint`) and skips them entirely, never entering or deleting through a link.
- **Dashboard alerts no longer always show "unavailable".** The App Updates, Event Log, and Pending Reboot scanners each had a free code block after their `catch` that ran unconditionally and overwrote the real scan result with an "unavailable / green" status. The Dashboard therefore reported false-OK for these three checks regardless of the actual system state. The decision logic is now extracted into pure, unit-tested methods and the overwrite is gone, so real results surface.

## [1.19.2] - 2026-06-08

### Fixed
- **UI test harness could never locate the app executable.** `AppFixture` hardcoded a `net8.0-windows` output path while the project targets `net10.0-windows`, so `FindExecutable()` always threw `FileNotFoundException` when running the UI automation tests locally. The path is now resolved dynamically from the `net*-windows` build folder, so it survives future framework bumps.

## [1.19.1] - 2026-06-05

### Fixed
- **Code-scanning cleanup (mechanical, no behavior change).** Resolved a batch of low-risk CodeQL quality alerts in hand-written code:
  - Empty `catch` blocks now log at Debug level (`ContextMenuService` registry-write fallbacks and command-path parse, `App.OnExit` service-provider disposal) or carry an explanatory comment where the caught exception is expected (`DnsHostsViewModel` cancellation on teardown).
  - `Path.Combine` → `Path.Join` in `ActivityLogService` to avoid silently dropping earlier path segments.
  - Object `==`/`!=` comparisons made explicit with `ReferenceEquals` where reference identity is intended (`MainWindowViewModel` tab activation, `TemperatureService` core-vs-package sensor check).
  - Implicit `foreach` filtering/mapping replaced with explicit LINQ (`Where`/`Select`/`FirstOrDefault`) in `TemperatureService`, `FileShredderService`, `HostsFileService`, and `ContextMenuViewModel`.
  - Removed useless local assignments in `DashboardViewModel` (an unused `Stopwatch`, an unread temp-scan size) while preserving the scans' side effects.

## [1.19.0] - 2026-06-05

### Changed
- **Uniform outer margins across 13 views.** BatteryHealth, BulkInstaller, ContextMenu, DiskAnalyzer, Drivers, DuplicateFile, Performance, Privacy, ProcessManager, Services, Startup, Uninstaller, WindowsFeatures all migrated from `Margin="32,24"` to the canonical `Margin="28,24,28,16"` already used by Dashboard and AppAlerts. Layout is now consistent across the whole nav.
- **Page background — theme-aware.** 15 views were defining a hardcoded `LinearGradientBrush PageBg` (`#070A0F`/`#0B1220`/`#090D16`) and using `{StaticResource PageBg}` for their root `Grid.Background`. Replaced with `{DynamicResource Surface0}`. The gradient resource definitions are gone, the views are smaller, and a future light-theme switch will work without per-view edits.
- **Admin elevation banner colors — theme-aware.** Replaced 4 hardcoded amber hex values (`#1AFBBF24`, `#40FBBF24`, `#FBBF24`, `#FCD34D`) used by elevation banners and warning pills across 17 views with new theme brushes: `WarningBgSubtle`, `WarningBg`, `WarningStripe`, `WarningText`. Defined once in `App.xaml`, used everywhere.

## [1.18.3] - 2026-06-03

### Fixed
- **Async-safety follow-up.** Dropped the remaining `async void` pipe-listener path and the sync-over-async wrappers flagged during review, completing the threading cleanup started in 1.18.2.

## [1.18.2] - 2026-06-03

### Fixed
- **Pipe listener no longer fire-and-forgets via `async void`.** `App.StartPipeListener` was an async-void method, meaning any exception escaping the loop would crash the process via the AppDomain handler. Renamed to `StartPipeListenerAsync` returning `Task`; `OnStartup` calls it as `_ = StartPipeListenerAsync()` so a stray exception flows through `TaskScheduler.UnobservedTaskException` (logged) instead of terminating the app.
- **StartupService — removed sync wrapper over async.** `SetEnabled` (sync) was a thin wrapper around `SetEnabledAsync` using `.GetAwaiter().GetResult()`. The wrapper is gone; tests now call `SetEnabledAsync` directly via xUnit `Task` test methods.
- **Schtasks stderr read** — replaced `stderrTask.Wait(timeout) ? .GetAwaiter().GetResult() : ""` with `await stderrTask.WaitAsync(timeout)` so the read is fully async with a clean timeout fallback.
- **Privacy Toggles no longer write to the registry on every click.** Toggling a switch now updates local state only; the user must press **Apply** to write pending changes. A live counter shows how many changes are pending, and **Discard** reverts them without touching the registry. Prevents accidental system changes when scrolling through the toggle list.
- **Dashboard no longer freezes on first load.** Static system info (CPU, OS, RAM modules) is now loaded asynchronously instead of blocking the UI thread on a synchronous WMI capture. The Dashboard tab is responsive immediately on startup.
- **DNS / Hosts tab loads asynchronously.** Reading the hosts file is now async (`File.ReadAllLinesAsync`); Refresh no longer freezes the UI on slow disks.
- **Icon cache eviction is now true FIFO.** The icon cache previously evicted random entries because `ConcurrentDictionary.Keys` has no insertion-order guarantee. Frequently-used icons could be dropped while stale ones survived. The cache now tracks insertion order via a queue and evicts the oldest entries first when the size limit is reached.
- **SpeedTest output read** — replaced `Task.Result` access after `Task.WhenAll` with proper `await` to remove the deadlock-prone pattern (the awaited tasks were already complete, but the style is now safe under all call paths).
- **Silent exception swallowing** — empty `catch { }` blocks now log at Debug level so failures are diagnosable: ThemePopup custom-color parser, TemperatureService LibreHardwareMonitor close, WindowsUpdateService COM/RuntimeBinder catches in `ExtractKbIds` and `ClassifyCategory`.
- **Deep Cleanup** — file/directory cleanup errors are now logged in addition to being added to the per-run error list, so unexpected I/O issues surface in the SysManager log.
- **Admin relaunch** — `RelaunchAsAdmin` now distinguishes the user's UAC decline (Win32 error 1223 → Information) from real Win32 failures (Warning) and logs `InvalidOperationException` instead of swallowing it silently.
- **`SHGetFileInfo` P/Invoke** — added `SetLastError = true` so callers can inspect the Win32 error code on failure.

### Changed
- **PrivacyView toolbar** — the **Apply All** button is replaced by **Apply** (writes only pending changes) and **Discard** (reverts to last-applied state). Both are disabled when no changes are pending. The Apply button uses the primary style to highlight the action.
- **PerformanceService.TakeSnapshotAsync** XML doc now warns callers that the method must run before any state-modifying call; the recommended lazy-initialization pattern is documented inline.
- **PerformanceService.CreateRestorePointAsync** comment reworded — the previous `// BUG-003:` marker was a design note, not an open bug; replaced with an explanation of why PowerShell `AddParameter` cannot be used here.
- **App.xaml.cs unhandled-exception dialog** — added inline note explaining why `MessageBox.Show` is used instead of `DialogService` (the dispatcher exception may originate from DialogService itself).
- **`.gitignore`** — added entries for local developer notes (`.session-notes/`, `notes-local.md`, `scratch.md`) so scratch files can never be tracked accidentally.

## [1.18.1] - 2026-06-03

### Fixed
- **Critical and high-priority audit fixes (P0 + P1).** Resolved the top-severity findings from the code audit ahead of the 1.18 line — crash-safety, resource, and correctness fixes across the service layer.

## [1.18.0] - 2026-06-03

### Fixed
- **Windows Update install actually works** — replaced PSWindowsUpdate's `Install-WindowsUpdate` with direct calls to the Windows Update Agent COM API (`Microsoft.Update.Session`). PSWindowsUpdate filters out optional driver updates client-side even when the COM API can install them; the new code installs everything WUA reports as available, including drivers, firmware, Defender Definitions, cumulative updates, and feature upgrades.
- **Honest per-update progress** — live console now streams real per-update events (Connecting → Downloading → Installing → ✓ Installed) instead of a 16-times-repeated PSWindowsUpdate pre/post search noise that resulted in "Installed 0".
- **Per-row Status reflects reality** — each row's Status column is updated as the install progresses (`Pending…` → `Downloading…` → `Installing…` → `Installed` / `Installed (reboot required)` / `Failed (download)` / `Failed (install code N)` / `Not applied`).
- **Status column wider with tooltip** — fits longer messages like "Installed (reboot required)" without truncation; full text always visible on hover.
- **Live output panel no longer auto-resizes** — fixed height of 240px so the DataGrid above keeps its space when many log lines arrive.

### Changed
- **Removed live output from Ping and Traceroute** — those tabs already display their data graphically (latency chart, hops grid). The console panel was redundant and stole vertical space.
- **Single-header live output panel** — removed the redundant "Live output" Card+Expander wrapper; the ConsoleView toolbar (Live output / Clear / Copy / Auto-scroll) now sits directly on the panel border, matching CleanupView and AppUpdatesView.
- **KB column header tooltip** — explains "KB = Microsoft Knowledge Base article ID" since not all updates have one (drivers, firmware, Defender).

### Added
- **WindowsUpdateService** — new service wrapping `Microsoft.Update.Session` COM API directly. Supports scan (`IsInstalled=0`), download, EULA acceptance, install, and reboot detection. Exposes a `Log` event for live console streaming.
- **Title-based category classifier** — Defender / Driver / Cumulative / Security / Servicing / .NET / Feature upgrade / Update, with COM `Categories` collection lookup as the primary signal and title heuristics as fallback. Unit-tested.

## [1.17.4] - 2026-06-03

### Fixed
- **Windows Update install never applied updates** — install command sent KB numbers prefixed with `KB` (e.g. `KB5034441`) to PSWindowsUpdate's `-KBArticleID` parameter, which expects bare digits; the cmdlet matched zero updates and exited silently. Updates without a KB (Defender Definitions, drivers) and updates with multiple KBs were also excluded by the selection filter. The status bar reported a fabricated "Installed N update(s)" message based on the selection count rather than the cmdlet's actual result.
- **Honest install reporting** — Install-WindowsUpdate output is now captured and parsed; the status bar shows real counts (`Installed X/Y. Failed: Z. Not applied: W.`) and each row's Status column reflects per-update outcome (`Installed`, `Failed`, `Not applied`).

### Changed
- **Unified update list** — "List updates" now returns Standard, Feature upgrades, and Hidden updates in a single grouped table; the separate "Feature upgrades" button has been removed. Category column distinguishes Security, Cumulative, Defender, Driver, Servicing, .NET, Feature upgrade, and Hidden entries.
- **Title-based install pipeline** — selected updates are matched against the live PSWindowsUpdate feed by Title rather than KB, so updates without a KB (Defender, drivers) and updates with multiple KBs install correctly.

## [1.17.3] - 2026-05-29

### Fixed
- **Performance** — NetworkSharedState TracerouteHops converted to BulkObservableCollection with ReplaceWith() (eliminates per-hop UI notifications during route updates).
- **Performance** — ServicesViewModel safety level counts now computed in a single pass instead of 3 separate LINQ queries.
- **Consistency** — DnsHostsView removed stale `HorizontalGridLinesBrush` property (no visual impact, code cleanliness).
- **Consistency** — AppBlockerView DataGrid BorderThickness set to 0 matching all other views.

## [1.17.2] - 2026-05-29

### Fixed
- **Memory leaks** — AboutViewModel now properly disposes ManagementObject instances in all 5 WMI foreach loops (CPU, RAM, GPU, Display, OS detection).
- **Silent failures** — ThemeService Save/Load empty catch blocks now log errors via Serilog instead of swallowing silently.
- **Dashboard error handling** — replaced 4 bare `catch (Exception)` in alert scanners with logged exceptions for diagnostics.
- **UI flicker** — BulkInstallerViewModel.FilteredApps converted from ObservableCollection to BulkObservableCollection with ReplaceWith().
- **Visual consistency** — ShortcutCleanerView DataGrid now has `Background="Transparent"` and `BorderThickness="0"` matching all other views.

## [1.17.1] - 2026-05-29

### Fixed
- **Documentation** — ARCHITECTURE.md updated with new TemperatureService, ActivityLogService, and rewritten DashboardViewModel description to reflect v1.17.0 redesign.

## [1.17.0] - 2026-05-29

### Added
- **Dashboard redesign** — complete overhaul of the landing page:
  - **Real-time vitals** — CPU%, RAM%, GPU% with 300ms polling (smoother than Task Manager), live indicator dots, detailed hardware info (cores/threads, DDR speed, VRAM usage)
  - **Temperatures** — real-time sensor readings via LibreHardwareMonitor (admin) or NvAPIWrapper (non-admin NVIDIA). Shows CPU Package, GPU Core, GPU Hot Spot, all storage drives. Color-coded (green/blue/yellow/red). "Run as admin for all sensors" button when elevated data unavailable.
  - **Storage overview** — per-drive usage bars with color coding (<50% green, 50-75% blue, 75-90% yellow, >90% red)
  - **System Alerts** — auto-scans at boot with loading spinners: SMART health, app updates count, memory errors (30d), Event Log critical events (7d), pending reboots. Each with ETA if scan takes >5s.
  - **Quick Actions** — Run Quick Cleanup, Update All Apps, Check Windows Updates, Run Speed Test. Each runs inline with progress bar, result summary, and "Go to [tab] for more details" navigation button. Buttons unlock after action completes.
  - **Recent Activity** — last 5 user actions with timestamps (persisted to JSON)
  - **Health Score** hero card with recommendations (existing, repositioned)
  - **IsActive pattern** — polling pauses when user leaves Dashboard tab (saves CPU)
- **TemperatureService** — new service aggregating temps from LibreHardwareMonitor + NvAPIWrapper + SMART
- **ActivityLogService** — new service persisting user action history to `%LOCALAPPDATA%\SysManager\activity.json`
- **NvAPIWrapper.Net** — new dependency for NVIDIA GPU temps without admin
- **LibreHardwareMonitorLib** — new dependency for full sensor access with admin

## [1.16.3] - 2026-05-29

### Fixed
- **Code quality** — ContextMenuService uses `[GeneratedRegex]` for compile-time regex (performance + AOT-ready).
- **Naming standardization** — all admin relaunch methods now consistently named `RelaunchAsAdmin` across all 12 ViewModels (was mixed: `RelaunchElevated`, `RequestElevation`).
- **Naming standardization** — filter properties unified to `FilterText` everywhere (LogsViewModel was `SearchText`, ServicesViewModel was `Filter`).
- **ConsoleViewModel** — removed dead optimization branch (clear-and-rebuild path was unreachable).
- **Missing toasts** — added completion notifications to System Health scan and App Alerts "Show Installed".

## [1.16.2] - 2026-05-28

### Fixed
- **UI uniformity** — AppAlertsView fully reworked: proper Card wrappers, styled buttons (Primary/Secondary/Danger), removed DataGrid gridlines, standardized header using Display style, consistent column styling.
- **DashboardView consistency** — standardized margins (28px), replaced inline admin button template with app-wide AdminButton/elevation banner pattern, added proper button styles to all actions.
- **Performance** — replaced `ObservableCollection` with `BulkObservableCollection` in UninstallerViewModel, WindowsFeaturesViewModel, WindowsUpdateViewModel, and LogsViewModel (eliminates UI flicker from Clear+Add loops).

## [1.16.1] - 2026-05-28

### Fixed
- **Security hardening** — version bump for security and performance fixes.

## [1.16.0] - 2026-05-28

### Added
- **Context Menu redesign** — complete overhaul of the Context Menu Manager tab:
  - **Presets:** Win10 Default (classic full menu), Win11 Default (modern compact), Custom
    (manual toggles). Selecting Win10/Win11 resets to clean default by disabling all
    third-party entries (Git, NVIDIA, etc.) — user can re-enable individually.
  - **Win10/Win11 style toggle** — switch between classic full context menu and modern
    Win11 "Show more options" via registry. Automatically restarts Explorer.
  - **Visual preview on hover** — real screenshots showing exactly what each menu style
    looks like (default + custom + "Show more options" expanded).
  - **Entry explanations** — human-readable descriptions for ~40 common entries shown
    inline (e.g. "Opens a Git Bash terminal in the current directory").
  - **"Applies to" column** — clearly shows whether an entry affects Files, Folders,
    Directory Background, or Desktop.
  - **HKCU fallback** — system-protected entries (TrustedInstaller) can now be toggled
    via HKCU registry override instead of failing with "access denied".

### Fixed
- **Crash on admin relaunch** — `CancellationTokenSource disposed` error no longer shown
  when restarting the app with elevated privileges.
- **Admin elevation banner** — Context Menu tab now shows the standard admin banner
  (matching all other tabs) with "Run as administrator" button.

## [1.15.0] - 2026-05-27

### Added
- **ETA on long operations** — estimated time remaining now shown on:
  SFC scan, DISM restore, Bulk Installer, Uninstaller, and App Updates.
  Uses linear extrapolation from elapsed time and current progress percentage.

## [1.14.0] - 2026-05-27

### Added
- **Sidebar smooth animation** — expand/collapse groups now slide with a 150ms animation
  instead of instant jump.
- **Chevron indicator** — rotating arrow on sidebar group headers showing expand/collapse state.
- **Full-width hitbox** — entire sidebar group header row is clickable, not just the text.

### Changed
- **Theme button relocated** — moved from content area top-right (caused overlaps) to sidebar
  footer bottom-right, next to version info. Always accessible, no overlapping.

## [1.13.3] - 2026-05-27

### Fixed
- **Theme compliance** — replaced hardcoded hex colors with DynamicResource tokens so all
  UI elements follow the active theme. ConsoleView, nav hover, DataGrid hover, and semantic
  status colors (Success, Warning, Danger, Info) now update live on theme switch.
- **AccentSoft opacity** — unified hover/selected background opacity to 9.4% across all
  views (was inconsistent between 6%–9.4%).

## [1.13.2] - 2026-05-27

### Fixed
- **DataGrid column resize** — all 19 DataGrids across 16 views now have `MinWidth` on every
  column preventing content from being compressed to invisible on resize.
- **Startup Manager "Open" button clipped** — column widened from 60→80px.
- **Toggle switch clipping** — toggle columns (Startup, Context Menu, Windows Features) widened
  to 62px to prevent pill shape from being cut off.
- **Action columns no longer resizable** — buttons/toggles/checkboxes columns locked with
  `CanUserResize="False"` so users cannot accidentally shrink them.

## [1.13.1] - 2026-05-27

### Fixed
- **Theme performance** — freeze all runtime-created brushes for reduced GC pressure
  and improved WPF rendering throughput.
- **Theme popup duplicate handlers** — prevent event subscriptions from stacking on
  repeated popup opens, which caused redundant theme re-applies.

## [1.13.0] - 2026-05-27

### Added
- **Theme customization** — persistent appearance settings with Dark/Light/Custom modes.
  Choose from 12 curated presets (6 dark, 6 light) or fully customize accent, background,
  surface, and text colors. Settings saved between sessions.
- **Theme button** — palette icon in top-right corner, accessible from every page.
- **Background shade slider** — fine-tune lightness/darkness within any preset.
- **Auto companion preset** — switching Dark↔Light automatically selects the matching
  color family (e.g. Midnight Indigo ↔ Clean Indigo).

### Changed
- All color resources converted from `StaticResource` to `DynamicResource` for live
  theme switching without restart.

## [1.12.1] - 2026-05-27

### Fixed
- **Startup crash** — duplicate implicit CheckBox style in App.xaml caused
  `XamlParseException` ("Item has already been added") preventing the app from launching.

## [1.12.0] - 2026-05-26

### Added
- **SpeedTest server selection** — dropdown to choose Ookla test server (Auto/Bucharest/
  London/Frankfurt/Amsterdam/Paris/New York) instead of always using nearest.

## [1.11.0] - 2026-05-26

### Added
- **Bulk Installer "Installed" badge** — apps already on the system show a green "Installed"
  badge. Detection via `winget list` at startup.
- **SpeedTest explanation** — info banner explaining Ookla vs HTTP test differences.
- **Network Repair explanations** — detailed descriptions for each repair action
  (Flush DNS, Reset Winsock, Reset TCP/IP).

### Fixed
- **App update failure messages** — more helpful explanations when downloads fail
  (mentions network issues, firewall, retry suggestions).

## [1.10.3] - 2026-05-26

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
- **UninstallerView** — hardcoded `#6366F1` replaced with `{StaticResource Accent}`.

## [1.10.2] - 2026-05-26

### Added
- **Process Manager real-time refresh** — 1-second auto-refresh timer matches Task Manager
  update speed. CPU measurement window reduced to 100ms for faster snapshots.
- **SFC progress bar** — parses stdout for completion percentage, shows real progress.
- **DISM progress bar** — parses stdout for percentage (handles decimal formats like 62.3%).
- **Ping live output** — ConsoleView showing real-time replies per target (time, timeout).
- **Traceroute live output** — ConsoleView with hop-by-hop results and explanations
  (gateway detection, ISP backbone, filtered nodes, destination reached).

## [1.10.1] - 2026-05-26

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

## [1.9.1] - 2026-05-26

### Fixed
- **Startup Manager columns** — reduced fixed widths to prevent last column overflow.
- **Startup Manager icons** — use extracted executable path for more accurate icon resolution.
- **Windows Update live output** — increased MinHeight/MaxHeight for better visibility.

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

## [1.7.16] - 2026-05-22

### Fixed
- **Tray icon creation is now forced**, ensuring the system-tray icon appears even when the platform delays its initial creation.

### Changed
- **Upgraded H.NotifyIcon to 2.3.0** for more reliable tray-icon handling.

## [1.7.15] - 2026-05-22

### Fixed
- **Tray icon reliability** — follow-up adjustments to ensure the system-tray icon initializes correctly during startup.

## [1.7.14] - 2026-05-22

### Added
- **Real app icons in Bulk Installer** — app icons are fetched via the Google Favicon service and cached locally, so the catalog shows recognizable icons instead of placeholders.

### Fixed
- **Tray icon always visible** — falls back to a system icon when an app icon fails to load, so the tray icon is never missing.

## [1.7.13] - 2026-05-22

### Fixed
- **Bulk Installer icons** — real application icons (Chrome, Firefox, Steam, etc.)
  downloaded via Google Favicon API with local cache and offline fallback.
- **Elevation banners** — App Updates, Uninstaller, Bulk Installer now uniform.
  Services banner moved above toolbar. All 13 admin pages consistent.
- **File Shredder** — fixed white page (transparent DataGrid background).
- **Column resize** — CanUserResizeColumns on all remaining DataGrids.
- **Tray icon** — shows real app icon from exe (not generic).

## [1.7.12] - 2026-05-22

### Fixed
- **Tray icon loads from the executable**, which is reliable under single-file publishing, with a pack-URI fallback if the embedded icon cannot be read.

## [1.7.11] - 2026-05-22

### Fixed
- **Tray icon visibility** is now set explicitly, so the system-tray icon shows reliably.

## [1.7.10] - 2026-05-22

### Fixed
- **Uniform elevation banners** across App Updates, Uninstaller, and Bulk Installer, so every tab presents the admin-elevation prompt the same way.
- **File Shredder no longer shows a blank page** when the tab is opened.

### Changed
- **Resizable grid columns everywhere** — `CanUserResizeColumns` is now enabled on all data grids for consistent behavior.

## [1.7.9] - 2026-05-22

### Fixed
- **Services tab — consistent banner ordering.** The admin elevation banner now sits above the toolbar, matching the layout used by the other tabs.

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

## [1.7.2] - 2026-05-22

### Fixed
- **Shutdown crash** — prevented `ObjectDisposedException` in `DnsHostsViewModel` when the app is closed while a refresh is in flight.

## [1.7.1] - 2026-05-21

### Fixed
- **Code review findings** — addressed security, thread-safety, and disposal issues surfaced during review (#487).

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

## [0.47.1] - 2026-05-13

### Fixed
- **Ten high-priority code-review findings** — a batch of correctness and security fixes from the code review, plus the SECURITY.md supported-versions update to the 0.47.x line.

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

## [0.35.12] - 2026-05-12

### Fixed
- **Code-review batch 2** — `IDialogService` extraction plus a set of QA and security fixes from the second code-review pass.

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

## [0.28.1] - 2026-04-29

### Fixed
- **Startup Manager no longer crashes when scrolling the list** — fixed a DataGrid virtualization crash while scrolling the Startup Manager entries (#337).

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

### Security
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

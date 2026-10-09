# Security Policy

SysManager is a local Windows desktop tool. It runs on your machine, uses
elevated privileges for some features, and executes PowerShell scripts and
native system utilities on your behalf. Because of this, the security of the
app and its releases matters — thank you for helping keep it safe.

## Supported versions

Security fixes are applied to the latest minor release only. If you're on an
older build, the first step is usually to update.

| Version  | Supported          |
| -------- | ------------------ |
| 1.127.x  | :white_check_mark: |
| < 1.127  | :x:                |

The supported line is always the newest minor on the
[releases page](https://github.com/laurentiu021/SystemManager/releases/latest) — if that page shows a
newer minor than the table above, the newest one is what's supported and this table is simply behind.
Report the issue anyway; being on a build newer than this table never means you are unsupported.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security problems.** Public
issues are visible to everyone and may put users at risk before a fix is
available.

Use **GitHub private vulnerability reporting**: go to the
[Security tab](https://github.com/laurentiu021/SystemManager/security/advisories/new)
and open a draft advisory. Only the maintainer sees it, you keep a thread to
discuss the fix, and you are credited automatically when it is published.

A free GitHub account is required, because an advisory is the only channel this
project has that is genuinely private — there is no published email, and asking
you to send vulnerability details to an unencrypted inbox would be worse advice
than asking you to sign up.

Please include:

- A short description of the issue and its impact.
- Steps to reproduce (proof-of-concept, screenshots, or a minimal script
  if applicable).
- SysManager version (visible in the **About** tab).
- Windows version and whether the app was running elevated.
- Any suggested mitigation, if you have one.

## What happens next

- **Acknowledgement** within 72 hours.
- **Initial assessment** within 7 days (is it reproducible, how severe,
  which versions are affected).
- **Fix timeline** depends on severity:
  - Critical (RCE, privilege escalation, arbitrary file deletion triggered
    remotely): patch released as soon as possible, usually within 7 days.
  - High (local privilege issues, data disclosure): 14 days.
  - Medium / low: next scheduled minor release.
- **Public disclosure** happens only after a fix is available. The reporter
  is credited in the release notes unless they prefer to stay anonymous.

## Security model

What the app can and cannot do by design:

### By design — allowed

- Read system information (WMI, CIM, registry).
- Run read-only disk checks (`chkdsk` without `/f`).
- Run PowerShell scripts that are part of the app (DNS & Hosts, Preinstalled Apps,
  Defender Tweaks, Edge/OneDrive Remover, Restore Points, Scheduled Maintenance, Task
  Scheduler, Drivers, System Fixes, Windows Features, and the Windows Update history).
- Launch Windows' own tools — `sc.exe`, `powercfg`, `netsh`, `ipconfig`, `DISM`, `sfc`,
  `chkdsk`, `schtasks`, `reg`, `powershell.exe` — always from their System32 path, plus
  `winget`, the Ookla `speedtest` CLI, OneDrive's own setup and a program's own
  uninstaller. Ping and traceroute are sent by SysManager itself, not through `ping.exe`
  or `tracert.exe`.
- Change Windows settings through the registry on the tabs built for it — Privacy &
  Telemetry, Context Menu, Startup Manager, Notification Blocker, Performance Mode, Gaming
  Profile, Environment Variables, and Windows Update's deferral and pause policy. App
  Blocker keeps programs from starting through Image File Execution Options, and refuses
  the executables Windows needs to boot or to elevate.
- Change a service's startup type with `sc.exe` (Services), recording the type it had so
  it can be turned back on.
- Edit the hosts file and switch DNS servers (DNS & Hosts).
- Remove preinstalled Store apps for your account, run a program's own uninstaller or
  winget's, and uninstall OneDrive.
- Remove what an uninstalled app left behind, only the items you tick on the Uninstaller's
  Left behind list. Folders go to the Recycle Bin; the app's own key under
  `HKEY_CURRENT_USER\Software` is saved as a `.reg` file before it is deleted. Nothing inside
  Windows, your own folders, SysManager's folders, a link or a folder holding one, a folder
  any installed app still uses, or anything not on this PC's own drives is ever offered, and
  each folder is checked again just before it goes.
- Run Windows' own repairs and resets: SFC, DISM `/RestoreHealth`, component-store
  cleanup (never `/ResetBase`), Winsock and TCP/IP resets, Windows feature changes and
  Windows Update installs.
- Create and restore System Restore points, turning System Protection on for the Windows
  drive first if it is off.
- Securely overwrite and delete files and folders you pick (File Shredder). It refuses
  anything under Windows, System32 or Program Files, checks the real target of a link
  before writing a byte, refuses a file with more than one hard link, and never follows
  a link inside a folder it shreds.
- Delete files in the cleanup categories you tick (Deep Cleanup, Quick Cleanup, Quick
  Tune-Up). Each category is a fixed list of folders; junctions and symlinks are never
  followed, the folders single-file .NET apps unpack into under TEMP are left alone, and
  files are deleted outright, not sent to the Recycle Bin. The Delivery Optimization cache
  is emptied through Windows' own `Delete-DeliveryOptimizationCache` instead.
- Empty the Recycle Bin.
- Clear per-browser cache, history, cookies, and sessions (Browser Cleaner tab)
  — only for the categories ticked when you press Clean. Cache and history start
  ticked; cookies and sessions are marked sensitive and start unticked so a clean
  never silently signs you out; locked files (browser open) are skipped, and
  reparse points are never followed out of the browser's own folders.
- Download application updates from the official GitHub Releases API.
- The confirmation SysManager shows before a change defaults to No, so pressing Enter
  never confirms one.

### By design — forbidden

- Reading or exfiltrating saved passwords or any browser password store.
- The Deep Cleanup engine touching browser data — that engine never reads or
  deletes browser caches/cookies; only the dedicated Browser Cleaner tab does,
  and only with the explicit per-category consent described above.
- Registry cleaning: searching the registry for entries to delete. The one key SysManager
  removes that it did not create is an uninstalled app's own key under
  `HKEY_CURRENT_USER\Software`, from the Uninstaller's Left behind list, and only when you
  tick it, after saving it as a `.reg` file.
- The cleanup engines deleting game files, installed programs, or any active driver
  folder. They never touch `steamapps\common` or installed game/program
  executables; it does remove specific launcher cache and log subfolders that
  happen to live under `Program Files` (e.g. Steam `appcache` / `htmlcache` /
  `depotcache` / `logs` / `shadercache`, Riot/League logs) — but never the games themselves.
  Removing a program is a separate, explicit action: Uninstaller, Preinstalled Apps
  and the Edge/OneDrive Remover use the program's own uninstall route.
- Deleting from Large Files (Storage & Files → Large Files) — it is intentionally
  read-only, even with admin rights: results offer only Show in Explorer and Copy path.
- Sending telemetry, or contacting any server other than the ones an action you
  started needs (ping targets, speed-test servers, winget, Windows Update, the
  PowerShell Gallery, GitHub Releases) and the two startup checks listed under
  [When the app uses the network](#when-the-app-uses-the-network).
- Elevating silently — every admin action surfaces a banner first and
  uses the standard `runas` UAC prompt.

### Things to be aware of

- **PowerShell execution**: several tabs run PowerShell — among them DNS & Hosts,
  Preinstalled Apps, Restore Points and the Windows Update history. (Windows Update's
  scan and install use Windows' update API directly, and SMART data comes from WMI.)
  Scripts are part of the app, not downloaded at runtime.
  Every PowerShell runspace, in an administrator session or not, runs in a
  Windows PowerShell 5.1 child process whose module discovery is restricted to
  canonical machine-owned locations under Program Files and System32, without
  changing the parent process environment. Per-user modules and optional module
  installation remain available only to scripts started as a separate
  `powershell.exe` while the app runs without elevation (the Windows Update
  history's PSWindowsUpdate module).
- **Putting changes back**: Performance Mode, Services, DNS & Hosts (for the hosts file),
  Environment Variables and Gaming Profile each keep a copy from before their first change.
  The Undo Changes tab lists the changes those copies can still put back, plus the Settings
  Watchdog settings that have drifted, and puts back one at a time. Each one asks first and
  names what will change, reads the copy again — under the lock its own tab's restore takes,
  for the three that take one — and changes nothing if what it finds is no longer what the
  question described. It keeps no file of its own, so deleting a copy removes that row. It
  names the newest restore point only when SysManager runs as administrator, because Windows
  lists restore points only to an administrator. Files deleted outright or shredded, removed
  preinstalled apps and uninstalled programs cannot be put back.
- **External CLI downloads**: the Ookla speed-test CLI is downloaded from
  `install.speedtest.net` the first time it's used. If that URL changes,
  the feature fails safely rather than substituting an alternative. The CLI must
  carry a valid Authenticode signature from Ookla whose chain validates with online
  revocation, and it is checked again on every run, because it is cached in a folder
  your account can write to.
- **A kernel driver for temperatures, only as administrator**: when SysManager runs
  elevated, the Dashboard and Resource History read temperatures through the bundled
  LibreHardwareMonitor library, which loads its own kernel-mode driver to reach the
  CPU, motherboard and storage sensors. Without administrator rights it loads nothing
  and reads only NVIDIA GPU and disk temperatures.
- **Unattended cleanup**: `SysManager.exe --cleanup` deletes temporary files from your
  TEMP and Windows TEMP folders without asking, and Scheduled Maintenance can run it
  on a schedule. The schedule is one task, `\SysManager\Scheduled Maintenance`, which
  runs without administrator rights. It does not offer the standby-list purge, which
  needs them: a task given administrator rights would start, with those rights, whatever
  replaced SysManager's exe in a folder your account can write to. A purge schedule saved
  by an earlier version still runs without them, so it fails, and the tab says so. You
  confirm when you create it, not on each run. Each run that completes is recorded in
  Recent activity.
- **Signature checks on lists, and what they cost**: the Signature columns in the Startup Manager and
  the Process Manager ask Windows for its verdict, through the same `WinVerifyTrust` API that Explorer's
  *Digital Signatures* tab uses. That is a different job from the two gates in this section — the Ookla
  CLI check above and the update check below — which compare a specific publisher and so build a
  certificate chain themselves, using the network to look up revocation and to fetch a missing
  intermediate certificate (the update check does so only once a publisher is pinned). A column covering
  every startup entry, or every program running right now, cannot do that — it would mean a request per
  file, and on a disconnected PC a wait per file — so it asks for **no revocation check** and tells
  Windows to answer from this machine's caches only. No network request is made for a column.
  - The trade-off, stated plainly: a certificate revoked since your PC last refreshed its lists still reads
    as verified. That is the right bias for a column that informs you and the wrong one for a gate that
    admits code, which is why the two differ.
  - **Both kinds of signature are read.** Windows signs most of its own components through a catalogue file
    (`.cat`) rather than inside the program, so the check asks about the embedded signature first and, when
    there is none, looks the file up in the machine's catalogues. Neither step contacts anything. A file
    reported as unsigned after both is genuinely unsigned.
  - Reading the publisher's *name* is a separate step from deciding whether the signature holds, and it
    only ever affects the wording of a tooltip. A verdict is never derived from it.
- **Local diagnostic log**: SysManager keeps up to 14 rolling log files — a new one
  each day, each capped at 10 MB — in `%LocalAppData%\SysManager\logs`. They never
  leave the machine on their own — there is no upload path. On every line, including
  inside exception messages, the folder name after `C:\Users\` in any path is
  replaced with `[user]` before it is written, so a log you choose to share for a bug
  report does not carry your account name in its paths. The
  replacement happens in the log sink rather than at each logging call, so a new
  code path cannot forget it. Paths outside your user profile, such as
  `C:\Program Files\...`, are recorded as-is.
- **Auto-update**: new builds are downloaded from the official GitHub
  Releases endpoint. The app does not auto-install without an explicit
  click. Before applying, the downloaded binary's SHA256 is compared against
  the `.sha256` published with the release — that comparison is the integrity
  gate, and it is what catches a modified download. The binary is also inspected
  for an Authenticode signature: an unsigned build is accepted, because SysManager
  currently ships unsigned, while a signature that cannot be parsed is rejected.
  The publisher comparison is a single constant that is empty until a code-signing
  certificate exists, so no publisher is pinned yet: today a signed build is accepted
  on the strength of the SHA256 comparison alone. Once that constant is filled in, a
  present signature has to match the pinned publisher AND its certificate chain has to
  validate to a trusted root with online revocation, or the update is refused — the same
  policy already applied to the third-party Ookla CLI. Writing both halves now is what
  stops the check from quietly becoming a no-op the day signing is switched on: without
  the pin, merely *carrying* a signature would pass, and a binary signed by an attacker's
  own self-issued certificate would be accepted like a legitimate build. Note that
  Authenticode inspection reads the signer certificate; it does not by itself
  validate the file against the signature, which is why SHA256 remains the
  integrity gate rather than a fallback. The swap is then performed
  from within the downloaded executable itself (no intermediate script on
  disk) using a staged atomic file move, so an interrupted update cannot
  leave a half-written, unstartable binary. Separately, the build being replaced
  is copied aside first, so an update that *succeeds* into a version that does not
  work is also recoverable: the About tab offers "Go back to the previous version"
  whenever a retained copy and the checksum recorded with it exist. Exactly one generation is kept, in
  `%LocalAppData%\SysManager\updates`, and retaining it is best-effort — if it
  cannot be written the update still proceeds rather than failing. You can also
  download manually and verify the binary yourself.
- **Portable distribution model**: the standard distribution is a portable,
  self-contained `.exe` (also published to winget as a portable package),
  which lives in a per-user, user-writable location. This means a process
  already running under your account could replace the executable on disk —
  a property inherent to any user-writable portable app, independent of the
  update flow. If you run SysManager elevated, only run a build you obtained
  from the official Releases page and verified. A machine-scope installed
  build under `Program Files` (not user-writable) is planned alongside code
  signing once a certificate is available — see
  [ROADMAP.md](ROADMAP.md#trust-and-distribution) for why the two are sequenced
  together, and the README for
  [why the app ships portable at all](README.md#why-portable-and-why-there-is-no-installer).

## Privacy

**SysManager collects nothing.** There is no telemetry, no analytics, no crash
reporting service, no account, and no cloud component. Nothing about you, your
machine, or your usage is transmitted anywhere — there is no server side to
transmit it to. The application is a single portable executable that reads and
writes only on the machine it runs on.

That covers code written here. It does not automatically cover code SysManager
hosts, so one dependency is worth naming: several features run PowerShell, and
SysManager hosts the PowerShell 7 engine in-process to drive the Windows PowerShell
5.1 processes its scripts run in. That engine has telemetry of
its own, independent of ours, which Microsoft gates on a single environment
variable. SysManager sets `POWERSHELL_TELEMETRY_OPTOUT=1` for its own process
before any PowerShell session is created, so the hosted engine stays silent too.
The variable is set for the running process only — your saved environment is never
modified — and a test asserts it, so the opt-out cannot be dropped unnoticed.

### What the app stores, and where

All of it stays on your PC, and almost all of it inside your own user profile. None
of it is encrypted, because none of it is secret: the settings and histories open in a
text editor, and you can delete any of them without breaking the app. Two copies kept
to undo a change are the exception to the user profile, because of what they copy —
see the last two rows.

| What | Where | Why it exists |
|---|---|---|
| Appearance and theme choice | `%AppData%\SysManager` | So the app looks the same next launch |
| Dark-mode schedule | `%AppData%\SysManager` | Your chosen on/off times |
| Speed-test history | `%LocalAppData%\SysManager` | So you can compare results over time |
| Recent-activity list | `%LocalAppData%\SysManager` | One line per action you performed — counts and sizes, and names you gave things such as a volume preset or a restore point — never file names |
| Settings-watchdog baseline | `%LocalAppData%\SysManager` | A snapshot of the Windows settings you chose, to detect later drift |
| When a changed setting was noticed | `%LocalAppData%\SysManager` | Beside the baseline: when each watched setting that differs from it was first seen, and when it went back, so Recent Changes can say when it changed. Forgotten when you save a new baseline |
| The list of installed programs | `%LocalAppData%\SysManager` | The name and publisher of each program installed at your last look at Recent Changes, and what changed between looks, kept 120 days, so the next look can tell what is new |
| New App Alerts' detections | `%LocalAppData%\SysManager` | The name, publisher, folder and time of each install New App Alerts noticed, the last 200, until you clear its history |
| Resource history | `%LocalAppData%\SysManager` | CPU / RAM / temperature samples, for the history graphs |
| Diagnostic log | `%LocalAppData%\SysManager\logs` | Up to 14 rolling files — a new one each day, or sooner at 10 MB — so a problem can be diagnosed |
| Downloaded updates | `%LocalAppData%\SysManager\updates` | The build you downloaded, plus one previous version for rollback |
| Startup version-check on/off | `%AppData%\SysManager` | The About-tab checkbox that controls the once-a-day version check |
| Your saved sets and choices | `%LocalAppData%\SysManager` | Gaming profiles, volume presets, what closing the window does, the standby-cleaner choice, whether the Bulk Installer may load icons from the web, whether the Disk Analyzer map is shown |
| State the app keeps to undo its own changes | `%LocalAppData%\SysManager` | Performance Mode's record of your original settings, the startup type of each service it turned off, game mode's record of a session still on, a counter Gaming Profile uses to put notifications back, a `.reg` export of each right-click menu key before it changes (`Backups\ContextMenu`, newest three per key), and a `.reg` export of each registry key the Uninstaller removes as a leftover (`Backups\Uninstaller`, newest three per key) |
| Uninstaller leftovers waiting for administrator rights | `%LocalAppData%\SysManager` | The paths of folders an uninstalled app left under Program Files or ProgramData, so the Uninstaller can offer them again in a session that runs as administrator. Deleted once nothing is left on it |
| Crash marker | `%LocalAppData%\SysManager` | Whether the last session crashed, with the error's type and message, the message scrubbed of your account name as the log is |
| Disk Analyzer history | `%LocalAppData%\SysManager` | The ten biggest folders, by name and size, of each of the last 20 locations you scanned, so the next scan can show what changed |
| Bandwidth history | `%LocalAppData%\SysManager` | Total download and upload rates, kept seven days, for the Bandwidth Monitor graph |
| Downloaded tools and icons | `%LocalAppData%\SysManager\tools`, `…\IconCache` | The Ookla speed-test CLI, downloaded the first time you run that test; app icons, only if you turned web icons on |
| The app's native libraries | `%TEMP%\.net\SysManager` | Unpacked there by .NET each time SysManager starts, because a single-file app cannot load them from inside itself |
| The original hosts file | `%SystemRoot%\System32\drivers\etc\hosts.bak` | Copied once, the first time SysManager saves the hosts file, and kept beside it so the original can be restored |
| A copy of your environment variables | Registry: `HKCU\Software\SysManager\Backups\Environment`; machine-wide ones in `HKLM\SOFTWARE\SysManagerEnvironmentBackup` | Taken before SysManager first changes them. The machine-wide copy is locked so a standard user cannot change it |

Apart from those last two, everything sits inside your own user profile, in two folders only
because Windows separates roaming settings from machine-local data; nothing is hidden in either.
If you set up Scheduled Maintenance, Windows also keeps one task, `\SysManager\Scheduled Maintenance`,
in Task Scheduler. Deleting a copy kept to undo a change does not break the app, but that change can
then no longer be put back: Undo Changes lists only the copies that are still there.

On every log line, including inside error messages, the folder name after
`C:\Users\` in any path is replaced with `[user]`, so a log you choose to share does
not carry your account name in its paths. The crash marker's message is scrubbed the same way.

### When the app uses the network

Only for things you explicitly ask for, plus two checks that run on their own when the app
starts:

- **Version check** — at startup the app asks GitHub's public releases endpoint
  which release is newest, so it can tell you when a fix is available, and fetches
  the notes of the last ten releases for the About tab. Nothing about you or your PC
  is sent. Once a check has succeeded it waits a day before the next one, and the
  About tab has a checkbox that switches it off entirely. When that setting cannot be
  read, the check does not run. The manual **Check for updates** button still works
  either way.
- **App update check** — each time SysManager starts, the Dashboard asks winget which
  of your installed apps have updates (`winget upgrade`, accepting winget's source
  agreements). winget answers from its package sources, which Microsoft runs, so this
  reaches Microsoft on every launch. SysManager adds nothing about you to it. There is
  currently no switch to turn it off.
- **Network diagnostics** — ping and traceroute contact the targets on the Ping tab
  (your router and, until you change the preset, Google DNS, Cloudflare, Quad9 and
  google.com), and only after you press Start, plus the host you enter on the
  Traceroute tab when you press Trace now or Start auto-trace; traceroute also looks up
  each hop's name through your DNS server. The speed test's own engine always uses Cloudflare
  (`speed.cloudflare.com`). The Ookla engine downloads Ookla's CLI from
  `install.speedtest.net` the first time you use it, checks its certificate with the
  issuing certificate authority on every run, and accepts Ookla's licence and privacy
  notice for you when it runs the test against an Ookla server.
- **Apps through winget** — searching for, installing, updating and uninstalling apps
  (Bulk Installer, App Updates, Uninstaller) runs winget, which talks to Microsoft's
  package sources and downloads installers from each app's publisher.
- **Windows Update** — checking for and installing updates goes through Windows' own
  update service to Microsoft. Installing the PSWindowsUpdate module, which the update
  history needs, downloads it from the PowerShell Gallery, and only in a session
  without administrator rights.
- **App icons in the Bulk Installer** — **off by default.** Only if you tick
  "Load app icons from the web" does it fetch them from Google's favicon
  service.

Nothing else leaves your PC. Apart from those two startup checks, there is no
background phone-home.

### Third parties

The application talks to no third-party service beyond those listed above: GitHub,
Google's favicon service, Cloudflare, Ookla, Microsoft (winget, Windows Update, the
PowerShell Gallery), the publishers whose installers winget downloads for an app you
install or update, and the ping and traceroute targets you choose. GitHub's own
privacy policy covers the project's development infrastructure — source code,
releases and discussions — and applies whenever your PC talks to GitHub: when you
visit the repository or download a release, and when SysManager checks for or
downloads an update.

### Questions

Privacy questions can be raised through
[GitHub Issues](https://github.com/laurentiu021/SystemManager/issues), or
privately through the channel described under
[Reporting a vulnerability](#reporting-a-vulnerability).

## Verifying a release

Every release on GitHub ships a versioned `SysManager-v<version>.exe` and a matching
`SysManager-v<version>.exe.sha256`, and every release since 1.56.6 a
`SysManager-v<version>.sbom.json` dependency inventory too. There are two independent checks, and they answer
different questions.

**Did the file arrive intact?** Compare the hash (replace `<version>` with the
version you downloaded):

```powershell
Get-FileHash .\SysManager-v<version>.exe -Algorithm SHA256
# Compare the output to the contents of the .sha256 file from the release page.
```

**Was the file built from this source?** Every release since 1.56.6 is covered by a
GitHub build attestation — a SLSA provenance statement signed during the build and
recorded in the public [Sigstore](https://www.sigstore.dev/) transparency log,
binding that binary's digest to this repository, the release workflow, and the
commit that produced it. Verify it with the [GitHub CLI](https://cli.github.com/):

```powershell
gh attestation verify .\SysManager-v<version>.exe --repo laurentiu021/SystemManager
```

The attestation is the stronger claim. The `.sha256` file is computed and published
from the same job and onto the same release as the binary it describes, so the two
share a single trust root — effective against transport corruption and against
local tampering with a cached copy, but not against a replaced release asset. The
attestation is signed by GitHub's infrastructure at build time and its subject
digest, source repository, workflow, and commit are recorded in an append-only
public log, so the binding between a binary and its origin cannot be rewritten
after publication. It does not, by itself, assert that the source was reviewed or
that the maintainer's account was not compromised — it proves origin, not intent.

The build is **not** currently code-signed, so Windows SmartScreen shows a
warning on first launch; this is expected until a code-signing certificate is
available. The README walks through
[what that dialog says and what to click](README.md#first-launch-windows-will-warn-you),
with hash verification as the precondition. The
[code signing policy](README.md#code-signing-policy) states who may commit, who
reviews, and who approves a release for signing.

## Dependencies and supply chain

- Dependencies are tracked via NuGet, every version pinned in one central file, and kept
  current by [Dependabot](.github/dependabot.yml), which also keeps the GitHub Actions
  current.
- CI builds and runs the unit test suite on every pull request. The integration tests,
  which use real OS APIs, and the UI automation tests run in CI too, as non-blocking
  jobs: a failure there is reported but does not stop a merge.
- CodeQL scans the C# code with its security-and-quality queries on every pull request,
  on every push to `main` and weekly.
- Before anything is published, the release workflow runs the unit tests again, checks
  that the version stamped into the binary matches the tag, and starts the published
  `.exe` to confirm it runs. A weekly job confirms that every version tag has a release
  (five old tags that never had one are listed as known gaps) and that the newest release
  carries its `.exe` and `.sha256`.
- The release workflow builds the binary from source on a clean GitHub
  Actions runner and publishes the `.exe`, its SHA256 sum, and a CycloneDX
  SBOM together.
- Every release since 1.56.6 carries a signed build-provenance attestation (see
  [Verifying a release](#verifying-a-release)). The privileged token that
  produces it is scoped to the build job alone; the workflow's default
  permission is read-only.
- Each release since 1.56.6 ships a CycloneDX SBOM (`SysManager-v<version>.sbom.json`) listing
  every NuGet package resolved for the published `win-x64` build, with version,
  package URL, and hash, so the dependency set can be audited against a
  vulnerability feed without unpacking the single-file executable. It is a
  resolved-dependency inventory rather than a byte-level manifest: a handful of
  entries are RID-specific placeholders for other platforms or build-time-only
  transitives that carry no payload into the shipped binary.
- Every GitHub Action in every workflow is pinned to a full commit SHA, and the SBOM
  tool to a version; release builds are deterministic
  (`ContinuousIntegrationBuild` + `Deterministic`).
- **The winget publishing credential, and what it cannot reach.** Publishing to winget
  means opening a pull request against `microsoft/winget-pkgs`, which no token belonging
  to this repository can do — so the release workflow uses a separate maintainer-held
  token (`WINGET_TOKEN`) whose only job is to sync a fork of that repository and open the
  manifest pull request. It is the only credential in the whole pipeline that is not
  either the ephemeral, repo-scoped `GITHUB_TOKEN` or the coverage upload token.
  - **It acts only after the release already exists.** Every step that uses it runs after
    `Create GitHub Release` has published the `.exe`, its SHA256 sum, the SBOM and the
    build-provenance attestation, and none of those steps writes to this repository's
    releases. The token itself is broader than that job: winget-releaser accepts only a
    classic token, which needs at least the `public_repo` and `workflow` scopes, and those
    reach this repository too. What makes misuse detectable is the attestation, recorded
    before the token is first used: a replaced `.exe` fails `gh attestation verify`. The
    app's own update check compares against the `.sha256` on the same release, so it would
    not catch an asset and a hash replaced together. Replacing this token is tracked in
    [#1676](https://github.com/laurentiu021/SystemManager/issues/1676).
  - **What it could affect** is the winget manifest: the URL and hash that a
    `winget install laurentiu021.SysManager` reads. Microsoft's own review sits between
    that pull request and users, but the manifest is the one place where the download
    users receive is asserted outside this repository, which is why it is called out here
    rather than left implicit. Verifying a download against the published SHA256 and the
    attestation is what makes that channel checkable independently of the token.

## Scope

In scope:

- Arbitrary code execution or privilege escalation through the app.
- Path traversal or symlink attacks that let the cleanup engine delete
  files outside advertised categories, or let the File Shredder overwrite a file
  under Windows or Program Files.
- Credential or token exposure (shouldn't apply — the app stores neither).
- Update channel attacks (spoofed releases, signature bypass).

Out of scope:

- Social engineering that requires the user to deliberately override a
  safety prompt.
- Vulnerabilities in third-party binaries the user chooses to install, or that
  SysManager downloads for a feature you use (winget packages, PSWindowsUpdate, the
  Ookla CLI for the speed test).
- Denial of service caused by scanning huge folder trees (the UI stays
  responsive; scans are cancellable).

Thanks for reading, and thanks in advance for any responsible disclosure.

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
| 1.65.x   | :white_check_mark: |
| < 1.65   | :x:                |

The supported line is always the newest minor on the
[releases page](https://github.com/laurentiu021/SystemManager/releases/latest) — if that page shows a
newer minor than the table above, the newest one is what's supported and this table is simply behind.
Report the issue anyway; being on a build newer than this table never means you are unsupported.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security problems.** Public
issues are visible to everyone and may put users at risk before a fix is
available.

Instead, use one of these private channels:

1. **GitHub private vulnerability reporting** (preferred) —
   go to the [Security tab](https://github.com/laurentiu021/SystemManager/security)
   of this repo and click **"Report a vulnerability"**. Only the maintainer
   sees the report.
2. **Email** the maintainer at the address on the
   [GitHub profile](https://github.com/laurentiu021). Use a subject line
   starting with `[SysManager security]`.

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
- Run PowerShell scripts bundled with the app (Windows Update, SMART
  queries, etc.).
- Launch external CLIs: `winget`, Ookla `speedtest`, `tracert`, `ping`.
- Delete files in user-selected cleanup categories (Deep Cleanup tab).
- Empty the Recycle Bin.
- Clear per-browser cache, history, cookies, and sessions (Browser Cleaner tab)
  — only for categories the user explicitly selects. Cookies and sessions are
  marked sensitive and unticked by default so a clean never silently signs the
  user out; locked files (browser open) are skipped, and reparse points are
  never followed out of the browser's own folders.
- Download application updates from the official GitHub Releases API.

### By design — forbidden

- Reading or exfiltrating saved passwords or any browser password store.
- The Deep Cleanup engine touching browser data — that engine never reads or
  deletes browser caches/cookies; only the dedicated Browser Cleaner tab does,
  and only with the explicit per-category consent described above.
- Touching the Windows registry for cleanup.
- Deleting game files, installed binaries, or any active driver folder. The
  cleanup engine never touches `steamapps\common` or installed game/program
  executables; it does remove specific launcher cache and log subfolders that
  happen to live under `Program Files` (e.g. Steam `appcache` / `htmlcache` /
  `depotcache` / `shadercache`, Riot/League logs) — but never the games themselves.
- Deleting from the Large Files scan (in the Deep Cleanup tab) — it is
  intentionally read-only, even with admin rights.
- Sending telemetry or contacting any server other than the ones needed
  for an explicit user action (ping targets, speed-test hosts, GitHub
  Releases).
- Elevating silently — every admin action surfaces a banner first and
  uses the standard `runas` UAC prompt.

### Things to be aware of

- **PowerShell execution**: Windows Update and SMART features invoke
  PowerShell. Scripts are bundled with the app, not downloaded at runtime.
  Administrator sessions isolate PowerShell runspaces in Windows PowerShell 5.1
  child processes whose
  module discovery is restricted to canonical machine-owned locations under
  Program Files and System32, without changing the parent process environment.
  Per-user modules and optional module installation remain available only when
  the app runs without elevation.
- **External CLI downloads**: the Ookla speed-test CLI is downloaded from
  `install.speedtest.net` the first time it's used. If that URL changes,
  the feature fails safely rather than substituting an alternative.
- **Local diagnostic log**: SysManager keeps 14 days of rolling log files in
  `%LocalAppData%\SysManager\logs`. They never leave the machine on their own —
  there is no upload path. Your Windows user name is replaced with `[user]` on
  every line before it is written, including inside exception messages, so a log
  you choose to share for a bug report does not carry your account name. The
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
  If a signature IS present, the signer must match the pinned publisher and its
  certificate chain must validate to a trusted root with online revocation —
  the same policy already applied to the third-party Ookla CLI. That pin is a
  single constant, empty until a code-signing certificate exists, so the check
  cannot quietly become a no-op the day signing is switched on: without it, merely
  *carrying* a signature would pass, and a binary signed by an attacker's own
  self-issued certificate would be accepted like a legitimate build. Note that
  Authenticode inspection reads the signer certificate; it does not by itself
  validate the file against the signature, which is why SHA256 remains the
  integrity gate rather than a fallback. The swap is then performed
  from within the downloaded executable itself (no intermediate script on
  disk) using a staged atomic file move, so an interrupted update cannot
  leave a half-written, unstartable binary. Separately, the build being replaced
  is copied aside first, so an update that *succeeds* into a version that does not
  work is also recoverable: the About tab offers "Go back to the previous version"
  whenever a retained copy exists. Exactly one generation is kept, in
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
  signing once a certificate is available.

## Privacy

**SysManager collects nothing.** There is no telemetry, no analytics, no crash
reporting service, no account, and no cloud component. Nothing about you, your
machine, or your usage is transmitted anywhere — there is no server side to
transmit it to. The application is a single portable executable that reads and
writes only on the machine it runs on.

### What the app stores, and where

All of it stays on your PC, inside your own user profile. None of it is
encrypted, because none of it is secret: you can open any of these files in a
text editor, and you can delete any of them at any time without breaking the
app.

| What | Where | Why it exists |
|---|---|---|
| Appearance and theme choice | `%AppData%\SysManager` | So the app looks the same next launch |
| Dark-mode schedule | `%AppData%\SysManager` | Your chosen on/off times |
| Speed-test history | `%LocalAppData%\SysManager` | So you can compare results over time |
| Recent-activity list | `%LocalAppData%\SysManager` | Counts and sizes of actions you performed — never file names |
| Settings-watchdog baseline | `%LocalAppData%\SysManager` | A snapshot of the Windows settings you chose, to detect later drift |
| Resource history | `%LocalAppData%\SysManager` | CPU / RAM / temperature samples, for the history graphs |
| Diagnostic log | `%LocalAppData%\SysManager\logs` | 14 days of rolling files, so a problem can be diagnosed |
| Downloaded updates | `%LocalAppData%\SysManager\updates` | The build you downloaded, plus one previous version for rollback |
| Startup version-check on/off | `%AppData%\SysManager` | The About-tab checkbox that controls the once-a-day version check |
| Your saved sets and choices | `%LocalAppData%\SysManager` | Gaming profiles, volume presets, what closing the window does, the standby-cleaner choice, saved environment variables |
| State the app keeps to undo its own changes | `%LocalAppData%\SysManager` | A performance snapshot to restore from, a ledger of the service startup types it changed, whether the last session crashed, cached app-icon lookups |

Every one of these sits inside your own user profile, opens in a text editor, and can be
deleted without breaking the app. Two folders are involved only because Windows separates
roaming settings from machine-local data; nothing is hidden in either.

Your Windows user name is replaced with `[user]` in every log line — including
inside error messages — so a log you choose to share does not carry your account
name with it.

### When the app uses the network

Only for things you explicitly ask for, plus one optional check:

- **Network diagnostics** — ping, traceroute and speed test contact the servers
  you select, because that is the measurement.
- **App updates** — installing an application you chose downloads it through
  winget.
- **App icons in the Bulk Installer** — **off by default.** Only if you tick
  "Load app icons from the web" does it fetch them from Google's favicon
  service.
- **Version check** — at startup the app asks GitHub's public releases endpoint
  which release is newest, so it can tell you when a fix is available. Nothing
  about you or your PC is sent. It runs at most once a day, and the About tab
  has a checkbox that switches it off entirely; the manual **Check for updates**
  button still works either way.

Nothing else leaves your PC. There is no background phone-home.

### Third parties

The application talks to no third-party service beyond those listed above. The
project's development infrastructure — GitHub, for source code, releases and
discussions — is covered by GitHub's own privacy policy, which applies to you
only if you visit the repository or download a release in a browser.

### Questions

Privacy questions can be raised through
[GitHub Issues](https://github.com/laurentiu021/SystemManager/issues), or
privately through the channel described under
[Reporting a vulnerability](#reporting-a-vulnerability).

## Verifying a release

Every release on GitHub ships a versioned `SysManager-v<version>.exe`, a matching
`SysManager-v<version>.exe.sha256`, and a `SysManager-v<version>.sbom.json`
dependency inventory. There are two independent checks, and they answer
different questions.

**Did the file arrive intact?** Compare the hash (replace `<version>` with the
version you downloaded):

```powershell
Get-FileHash .\SysManager-v<version>.exe -Algorithm SHA256
# Compare the output to the contents of the .sha256 file from the release page.
```

**Was the file built from this source?** Every release is covered by a GitHub
build attestation — a SLSA provenance statement signed during the build and
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

- Dependencies are tracked via NuGet and kept current by
  [Dependabot](.github/dependabot.yml).
- CI builds and runs the unit test suite on every pull request.
  Integration tests (which access real OS APIs) run locally only.
- The release workflow builds the binary from source on a clean GitHub
  Actions runner and publishes the `.exe`, its SHA256 sum, and a CycloneDX
  SBOM together.
- Every release carries a signed build-provenance attestation (see
  [Verifying a release](#verifying-a-release)). The privileged token that
  produces it is scoped to the build job alone; the workflow's default
  permission is read-only.
- Each release ships a CycloneDX SBOM (`SysManager-v<version>.sbom.json`) listing
  every NuGet package resolved for the published `win-x64` build, with version,
  package URL, and hash, so the dependency set can be audited against a
  vulnerability feed without unpacking the single-file executable. It is a
  resolved-dependency inventory rather than a byte-level manifest: a handful of
  entries are RID-specific placeholders for other platforms or build-time-only
  transitives that carry no payload into the shipped binary.
- All GitHub Actions used in the release pipeline are pinned to full commit
  SHAs, and release builds are deterministic
  (`ContinuousIntegrationBuild` + `Deterministic`).

## Scope

In scope:

- Arbitrary code execution or privilege escalation through the app.
- Path traversal or symlink attacks that let the cleanup engine delete
  files outside advertised categories.
- Credential or token exposure (shouldn't apply — the app stores neither).
- Update channel attacks (spoofed releases, signature bypass).

Out of scope:

- Social engineering that requires the user to deliberately override a
  safety prompt.
- Vulnerabilities in third-party binaries the user chooses to install
  (winget packages, PSWindowsUpdate, Ookla CLI).
- Denial of service caused by scanning huge folder trees (the UI stays
  responsive; scans are cancellable).

Thanks for reading, and thanks in advance for any responsible disclosure.

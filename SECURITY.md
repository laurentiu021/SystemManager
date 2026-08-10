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
| 1.58.x   | :white_check_mark: |
| < 1.58   | :x:                |

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
  gate. The binary is also inspected for an Authenticode signature, but since
  SysManager ships unsigned that check is informational: an unsigned build is
  accepted, and a signature that cannot be parsed is rejected. It is not a
  publisher check and does not detect a tampered signed binary; the SHA256
  comparison is what catches a modified download. The swap is then performed
  from within the downloaded executable itself (no intermediate script on
  disk) using a staged atomic file move, so an interrupted update cannot
  leave a half-written, unstartable binary. You can also download manually
  and verify the binary yourself.
- **Portable distribution model**: the standard distribution is a portable,
  self-contained `.exe` (also published to winget as a portable package),
  which lives in a per-user, user-writable location. This means a process
  already running under your account could replace the executable on disk —
  a property inherent to any user-writable portable app, independent of the
  update flow. If you run SysManager elevated, only run a build you obtained
  from the official Releases page and verified. A machine-scope installed
  build under `Program Files` (not user-writable) is planned alongside code
  signing once a certificate is available.

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
with hash verification as the precondition.

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

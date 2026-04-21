# Security Policy

SysManager is a local Windows desktop tool. It runs on your machine, uses
elevated privileges for some features, and executes PowerShell scripts and
native system utilities on your behalf. Because of this, the security of the
app and its releases matters — thank you for helping keep it safe.

## Supported versions

Security fixes are applied to the latest minor release only. If you're on an
older build, the first step is usually to update.

| Version | Supported          |
| ------- | ------------------ |
| 0.5.x   | :white_check_mark: |
| 0.4.x   | :x:                |
| 0.3.x   | :x:                |
| < 0.3   | :x:                |

## Reporting a vulnerability

**Please do not open a public GitHub issue for security problems.** Public
issues are visible to everyone and may put users at risk before a fix is
available.

Instead, use one of these private channels:

1. **GitHub private vulnerability reporting** (preferred) —
   go to the [Security tab](https://github.com/laurentiu021/SysManager/security)
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
- Download application updates from the official GitHub Releases API.

### By design — forbidden

- Touching browser caches, cookies, or password stores.
- Touching the Windows registry for cleanup.
- Deleting files inside `steamapps\common`, `Program Files\*`, or any
  active driver folder.
- Deleting from the Large Files Finder — it is intentionally read-only,
  even with admin rights.
- Sending telemetry or contacting any server other than the ones needed
  for an explicit user action (ping targets, speed-test hosts, GitHub
  Releases).
- Elevating silently — every admin action surfaces a banner first and
  uses the standard `runas` UAC prompt.

### Things to be aware of

- **PowerShell execution**: Windows Update and SMART features invoke
  PowerShell. Scripts are bundled with the app, not downloaded at runtime.
- **External CLI downloads**: the Ookla speed-test CLI is downloaded from
  `install.speedtest.net` the first time it's used. If that URL changes,
  the feature fails safely rather than substituting an alternative.
- **Auto-update**: new builds are downloaded from the official GitHub
  Releases endpoint. The app does not auto-install without an explicit
  click. You can also download manually and verify the binary yourself.

## Verifying a release

Every release on GitHub ships a `SysManager.exe` and a matching
`SysManager.exe.sha256` file. You can verify the binary before running it:

```powershell
Get-FileHash .\SysManager.exe -Algorithm SHA256
# Compare the output to the contents of SysManager.exe.sha256 from the release page.
```

The build is **not** currently code-signed. Windows SmartScreen may show a
warning on first launch; this is expected until a code-signing certificate
is available. Verifying the SHA256 hash is the recommended mitigation in
the meantime.

## Dependencies and supply chain

- Dependencies are tracked via NuGet and kept current by
  [Dependabot](.github/dependabot.yml).
- CI builds and runs the full test suite on every pull request.
- The release workflow builds the binary from source on a clean GitHub
  Actions runner and publishes both the `.exe` and its SHA256 sum together.

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

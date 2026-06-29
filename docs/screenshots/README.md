# Screenshots

This folder holds the screenshots referenced from the main
[README.md](../../README.md). If you're here to add new ones, follow the
conventions below so they render cleanly in the README.

## File naming

Use zero-padded numbers so the file list stays in the same order as the
nav on the left rail. The current set is captured against the live app and
covers most tabs; the number matches the tab's position in the sidebar, so
a few numbers are intentionally skipped (work-in-progress tabs, or tabs whose
live data couldn't be shared even after redaction — see Privacy check below).

A short animated tour also lives under [`docs/gifs/`](../gifs/)
(`feature-tour.gif`, `cleanup-tools.gif`) and is embedded at the top of the
README's Screenshots section.

## Format and size

- **Format**: PNG. No JPEG (banding in the dark theme looks bad).
- **Width**: 1600–1920 px (pick one and stick with it across all shots).
- **Height**: whatever the window is at 1600×1000 or 1920×1200.
- **Compression**: run them through
  [tinypng.com](https://tinypng.com/) or `pngquant` before committing.
  Aim for each shot under 300 KB.

## Capturing

On the machine you use day-to-day:

1. Start SysManager.
2. Resize the window to roughly 1600×1000 (or whatever matches the width
   you picked above).
3. Navigate to each tab in order, let it populate, and take a shot with
   the Windows **Snipping Tool** (`Win+Shift+S`) using the **Window** mode.
4. Paste each into a new file and save as `NN-<tab>.png` here.
5. For tabs with live data (Network, Dashboard uptime), wait a few seconds
   so the charts have data to display.

## Privacy check before commit

Screenshots captured on a real machine will include personal data. Before
you commit, black out or blur:

- Windows username (visible in paths and the admin badge).
- Machine name / hostname (visible in System Health and Logs).
- Corporate Windows edition string and IP addresses.
- Installed-app and service lists, scheduled-task paths, and environment
  variables — these can name internal/work software. When in doubt, redact
  the whole Name/Publisher/Value column rather than guessing per row.
- Drive serial numbers and the battery manufacturer/ID line.

Generic hardware (CPU/GPU model) is fine to leave visible. The committed set
was redacted with opaque boxes over the items above; capture full-window
(no OS taskbar) and re-check each shot before committing.

## Linking from README

The README's **Screenshots** section uses this pattern:

```markdown
### Dashboard
![Dashboard](docs/screenshots/01-dashboard.png)
```

Once you've added new shots, update [README.md](../../README.md) to
reference them (see the Screenshots section for the current layout).

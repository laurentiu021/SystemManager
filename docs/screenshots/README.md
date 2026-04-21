# Screenshots

This folder holds the screenshots referenced from the main
[README.md](../../README.md). If you're here to add new ones, follow the
conventions below so they render cleanly in the README.

## File naming

Use zero-padded numbers so the file list stays in the same order as the
nav on the left rail:

```
01-dashboard.png
02-app-updates.png
03-windows-update.png
04-system-health.png
05-cleanup.png
06-deep-cleanup.png
07-large-files.png          (optional — sub-view of Deep cleanup)
08-network-ping.png
09-network-traceroute.png   (optional — sub-tab)
10-network-speed.png        (optional — sub-tab)
11-drivers.png
12-logs.png
13-about.png
```

Only the numbered, non-optional ones are strictly required. Optional views
are nice-to-have.

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
you commit, blur or crop out:

- Your Windows username (visible in paths and the admin badge).
- Machine name (visible in System health and Logs).
- Public IP addresses (visible in Network traceroute).
- Drive serial numbers (visible in System health SMART).
- Any event log entries mentioning people or apps you'd rather not share.

A quick pass in the built-in Photos app with the highlight/blur tool is
usually enough.

## Linking from README

The README's **Screenshots** section uses this pattern:

```markdown
### Dashboard
![Dashboard](docs/screenshots/01-dashboard.png)
```

Once you've added new shots, update [README.md](../../README.md) to
reference them (see the Screenshots section for the current layout).

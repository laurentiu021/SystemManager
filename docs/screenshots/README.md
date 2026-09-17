# Screenshots

This folder holds the screenshots referenced from the main
[README.md](../../README.md). If you're here to add new ones, follow the
conventions below so they render cleanly in the README.

## File naming

`<tab>.png`, where `<tab>` is the tab's sidebar label lowercased with every run of
non-alphanumeric characters turned into a single hyphen. No number. So "Standby List
Cleaner" is `standby-list-cleaner.png`, "Privacy & Telemetry" is
`privacy-telemetry.png`, "Camera/Mic/Location" is `camera-mic-location.png`, and
"Profile Export / Import" is `profile-export-import.png`.

These files used to carry a zero-padded number for the tab's position on the left rail,
and that number re-broke on every insertion: adding one tab shifts every tab below it
without touching a single filename, so 23 of the 43 files were numbered for a different
tab than the one they showed, one of them by eight places (#1664). A name has no such
failure mode — adding a tab now touches nothing that already exists.

The trade is that renaming a tab renames its screenshot, and
`ArchitectureTests.EveryScreenshotSlug_NamesARealTab` fails until you do. That is
deliberate rather than tolerated: a renamed tab almost always means the header inside
the image now says the old name too, so the file needs recapturing and not just moving.
The guard tells you which files, by name.

43 of the 59 tabs have a shot. The 16 without one are Bandwidth Monitor,
Camera/Mic/Location, Context Menu, DNS & Hosts, Duplicate Finder, Edge/OneDrive
Remover, Environment Variables, Large Files, Legacy Panels, Notification Blocker,
Process Manager, Services, Startup Manager, Task Scheduler, Uninstaller and Volume
Control. One of those — Notification Blocker — is still marked as a preview tab.
Fourteen of the rest are list-heavy pages: a usable shot of them is a screenful of
real service names, installed programs, file paths or environment values, and the
redaction cost is the reason they are not here yet (see Privacy check below). Large
Files is simply new, added in 1.109.0. None of the sixteen is missing because the tab
is unfinished.

Those two counts are held against the source by
`ArchitectureTests.TheScreenshotInventory_MatchesWhatIsOnDisk`, because both of them
went stale in the release that added a tab.

A short animated tour also lives under [`docs/gifs/`](../gifs/)
(`feature-tour.gif`, `cleanup-tools.gif`) and is embedded at the top of the
README's Screenshots section.

## Outstanding recapture

Every shot in this folder, and both GIFs under [`docs/gifs/`](../gifs/), predates two
changes to the left-hand rail. 1.76.1 gave the twelve groups their icons, so the rail in
each image is a column of text with an empty margin beside it. 1.76.2 replaced each
collapsed group's subtitle — a truncated list of page names — with a written two-line
description. 1.76.3 then gave the administrator strip at the top of 31 pages a single
geometry, so where a shot shows that strip its corners and height are also out of date.

The pages themselves are out of date too, and by more than the rail. The whole set was
captured in one pass at 1.51.x, and every view it shows has changed since — the diffs are
heaviest on Dashboard, System Logs, App Updates, Windows Update and Gaming Profile, which
have gained whole cards, columns and controls rather than just moved. Recapture in
descending order of that, so the shots a visitor sees first stop misrepresenting the app
soonest, but retake the rail-only ones in the same pass so the sidebar matches across all
of them. Until then treat every image as showing an older build, not just an older rail.

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
4. Paste each into a new file and save it here under the name the File naming
   section above derives from that tab's sidebar label.
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
![Dashboard](docs/screenshots/dashboard.png)
```

Once you've added new shots, update [README.md](../../README.md) to
reference them (see the Screenshots section for the current layout).

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

42 of the 60 tabs have a shot. The 18 without one are Bandwidth Monitor,
Camera/Mic/Location, Context Menu, DNS & Hosts, Duplicate Finder, Edge/OneDrive
Remover, Environment Variables, Large Files, Legacy Panels, Notification Blocker,
Process Manager, Recent Changes, Services, Startup Manager, Task Scheduler, Undo Changes,
Uninstaller and Volume Control. One of those — Notification Blocker — is still marked as a
preview tab. Twelve of the rest are list-heavy pages: a usable shot of them is a screenful of
real service names, installed programs, file paths or environment values, and the
redaction cost is the reason they are not here yet (see Privacy check below). Edge/OneDrive
Remover and Legacy Panels show nothing personal and have simply not been captured, and Large
Files, Undo Changes and Recent Changes are new, added in 1.109.0, 1.123.0 and 1.125.0. None of
the eighteen is missing because the tab is unfinished.

Those two counts are held against the source by
`ArchitectureTests.TheScreenshotInventory_MatchesWhatIsOnDisk`, because both of them
went stale in the release that added a tab.

Two short animated tours live under [`docs/gifs/`](../gifs/): `feature-tour.gif` is at the
top of the README, and `cleanup-tools.gif` opens its Screenshots section.

## Outstanding recapture

Every shot in this folder, and both GIFs under [`docs/gifs/`](../gifs/), shows an older
left-hand rail. In the shots every page in it carries an icon, where now only the twelve groups
do (1.76.1); each collapsed group's subtitle is a truncated list of page names, where now it is a
written two-line description (1.76.2); the administrator badge at its foot is a padlock, where
now it is a shield; and several tabs sit in other groups or under other names — Storage is now
Storage & Files, App Alerts is New App Alerts in Apps, Notification Blocker moved to
Customization, and File Lock Detector and Bandwidth Monitor have left Monitor. 1.76.3 also gave
the administrator strip at the top of 31 pages a single geometry, so where a shot shows that
strip its corners and height are out of date too.

The pages themselves are out of date too, and by more than the rail. The whole set was
captured in one pass at 1.51.x, and every view it shows has changed since — the diffs are
heaviest on Dashboard, System Logs, App Updates, Windows Update and Gaming Profile, which
have gained whole cards, columns and controls rather than just moved. Recapture in
descending order of that, so the shots a visitor sees first stop misrepresenting the app
soonest, but retake the rail-only ones in the same pass so the sidebar matches across all
of them. Until then treat every image as showing an older build, not just an older rail.

Five are out of date beyond that, and come first: `gaming-profile.png` shows the tab while it
was a placeholder; `preinstalled-apps.png`, `privacy-telemetry.png` and `new-app-alerts.png`
carry their old page titles ("Debloater & Ads", "Privacy Toggles", "App Installation Alerts");
and `deep-cleanup.png` still shows the Large files card that is now its own tab.

## Format and size

- **Format**: PNG. No JPEG (banding in the dark theme looks bad).
- **Width**: 1600–1920 px (pick one and stick with it across all shots).
- **Height**: whatever the window is at 1600×1000 or 1920×1200.
- **Compression**: run them through
  [tinypng.com](https://tinypng.com/) or `pngquant` before committing.
  Aim for each shot under 300 KB.

The current set does not meet these yet: every shot is 3866 × 2330, and 37 of the 42 are over
300 KB. The recapture above fixes both.

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

- Windows username (visible in file paths, and in event text on System Logs).
- Machine name (the Machine line of an event's details on System Logs).
- Corporate Windows edition string and IP addresses.
- Installed-app and service lists, scheduled-task paths, and environment
  variables — these can name internal/work software. When in doubt, redact
  the whole Name/Publisher/Value column rather than guessing per row.
- Drive serial numbers and the battery manufacturer/ID line.

Generic hardware (CPU/GPU model) is fine to leave visible. The committed set
was redacted with opaque boxes over the items above; capture full-window
(no OS taskbar) and re-check each shot before committing.

## Linking from README

The README's **Screenshots** section groups the shots as the sidebar does, one `<details>` block
per group, with each image linked to its full-size file and its `alt` set to the tab's exact
sidebar label:

```html
<a href="docs/screenshots/disk-analyzer.png"><img src="docs/screenshots/disk-analyzer.png" width="280" alt="Disk Analyzer"></a>
```

`ArchitectureTests.EveryScreenshotSlug_NamesARealTab` checks that alt text as well as the file
name.

Once you've added new shots, update [README.md](../../README.md) to
reference them (see the Screenshots section for the current layout).

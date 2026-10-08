# Roadmap

Direction, not dates. SysManager is one person's project with no deadlines, so this
describes what is being worked towards and in roughly what order — not what ships when.
Every item links to the issue where the actual discussion happens, and the issues are the
source of truth. If something here has no issue, it is an intention rather than a plan.

The open backlog is public: [all open issues](https://github.com/laurentiu021/SystemManager/issues).

## Not planned, on purpose

Worth stating first, because these are the questions people ask and the answers are not
"not yet".

- **No telemetry, no analytics, no crash-reporting service, no account, no cloud.** There
  is no server side and there is not going to be one. Diagnostics stay local and are only
  ever shared if you choose to export and send them yourself.
- **No Microsoft Store build.** The Store sandbox forbids most of what this app does —
  reading other processes' working sets, writing the hosts file, changing services and
  scheduled tasks, purging the standby list. A Store version would be a different, much
  smaller app wearing the same name.
- **No paid tier, no upsell, no bundled offers.** MIT, free, all features.
- **No registry cleaner, optimiser or defragmenter.** A registry tool that removes entries
  it does not understand is how utilities earned their reputation. Compacting the hives
  means rewriting them offline, where an interruption can leave Windows unable to start,
  and neither changes anything a user can notice. The reasoning is in
  [#1726](https://github.com/laurentiu021/SystemManager/issues/1726).

## Trust and distribution

The largest gap between what the app is and how it is received.

- **Code signing.** Windows shows a warning on first launch and some antivirus engines flag
  an unknown publisher. The intended route is the
  [SignPath Foundation](https://signpath.org/) programme for open-source projects, and it is
  not simply pending: the Foundation asks for a level of public visibility — stars, external
  write-ups, independent references — that a project this young does not have, which is a
  fair bar for a certificate issued in their name. So this is the one roadmap item whose
  timing is not the project's to decide, and the practical route to it is people finding the
  app useful enough to say so. The alternatives, their cost and their lead times were written
  up in [#1675](https://github.com/laurentiu021/SystemManager/issues/1675), which settled on
  that route. Until
  then, [the published SHA-256 and the build attestation](README.md#verifying-the-download)
  are what establish that a download is genuine, and the update path already refuses a
  signature it cannot read.
- **A machine-scope build under `Program Files`.** The portable exe lives somewhere your
  own account can write, which means another process running as you could replace it. An
  installed build in a location that is not user-writable removes that property. It belongs
  with signing rather than before it, because an installed build people trust should be a
  signed one. Background in
  [SECURITY.md](SECURITY.md#things-to-be-aware-of), under the portable distribution model.
- **Shorter-lived release credentials.** The winget publish step currently holds a
  long-lived cross-repository token;
  [#1676](https://github.com/laurentiu021/SystemManager/issues/1676) covers replacing it.

## Making the app explain itself

The target user is someone who wants their PC to run better and does not know what a
working set is. Several tabs still assume otherwise.

- **A first-run screen** — one page saying what this is, what is safe, and what needs
  administrator rights ([#1635](https://github.com/laurentiu021/SystemManager/issues/1635)).

Help inside the app (F1 and the `?` chip), one plain-language voice across the tab headers,
and a startup update check you can switch off, which runs at most once a day, have shipped
since this list was first written.

## Accessibility and keyboard use

Mostly done and pinned by tests: Process Manager and Services rows have a right-click menu, so
their actions no longer mean Tabbing through every cell, and custom theme colours are adjusted
so text stays readable. What is left:

- **Keyboard operability under test.** Source-level guards exist; driving real key presses
  through the UI does not ([#1552](https://github.com/laurentiu021/SystemManager/issues/1552)).

## One design system rather than 59 views that resemble each other

Corner radii now come from the `RadiusSm/Md/Lg` scale, held by a test, and the status footer
that 37 views used to copy is one shared control. What is left:

- **Typography scale** — raw `FontSize` values instead of the named text styles
  ([#1634](https://github.com/laurentiu021/SystemManager/issues/1634)).

Spacing has no token scale, on purpose: every view uses one of three documented layout
strategies with literal values, and a test pins how the per-section pages end. A scale would
land together with moving the views onto it, not before.

## Features next

Everything this section has listed so far has shipped: the Disk Analyzer map, Deep Cleanup's
biggest space hogs, the This PC card on Volume Control, where each startup entry lives, taskbar
progress, maintenance on battery, the diagnostics bundle, Undo Changes, the extensions view in
Browser Cleaner, what an uninstall leaves behind
([#1527](https://github.com/laurentiu021/SystemManager/issues/1527)), Recent Changes
([#1507](https://github.com/laurentiu021/SystemManager/issues/1507)), Services saying what else
stops with a service and marking the ones it has no rating for
([#1512](https://github.com/laurentiu021/SystemManager/issues/1512)), and the Dashboard's "Why is
it slow?" ([#1529](https://github.com/laurentiu021/SystemManager/issues/1529)). The next ones come
from the open issues, and this list names them once they are chosen.

## Documentation and presentation

- **A landing page** ([#1679](https://github.com/laurentiu021/SystemManager/issues/1679))
- **Screenshots for the tabs that have none**, several of them recent features
  ([#1664](https://github.com/laurentiu021/SystemManager/issues/1664))

## Under the floor

Not user-visible, and the reason the rest can move at all.

- **A type scale the views actually use** — the named text styles exist, and most body text
  still writes its own size ([#1634](https://github.com/laurentiu021/SystemManager/issues/1634))
- **Command-line parsing that refuses what it does not recognise, wherever it appears.** An
  unknown option is refused, except when a known command follows it: `-bogus --health` runs
  the health check and ignores `-bogus`

## How this list changes

Anything here can be reordered by a bug report. A crash or a data-loss defect goes to the
front; a polish item waits. If something you need is missing,
[open an issue](https://github.com/laurentiu021/SystemManager/issues/new/choose) — the
backlog is where this document comes from, not the other way round.

---

Crafted by laurentiu021 · [SysManager](https://github.com/laurentiu021/SystemManager) · MIT

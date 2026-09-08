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
- **No registry "cleaning" that deletes on a guess.** A registry tool that removes entries
  it does not understand is how utilities earned their reputation. If something lands here
  it will be inspection-first and reversible; the request is tracked in
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
  app useful enough to say so. The alternatives, their cost and their lead times are being
  written up in [#1675](https://github.com/laurentiu021/SystemManager/issues/1675). Until
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
- **Help you can reach from inside the app** — no F1, no menu, no link out to
  documentation today ([#1640](https://github.com/laurentiu021/SystemManager/issues/1640)).
- **One consistent voice across tabs.** Roughly twenty older tabs kept terse technical
  headers while newer ones explain themselves in plain language
  ([#1654](https://github.com/laurentiu021/SystemManager/issues/1654)).
- **Nothing reaches the network before you have seen the window.** The update check runs at
  startup ([#1653](https://github.com/laurentiu021/SystemManager/issues/1653)).

## Accessibility and keyboard use

Partly done and pinned by tests, partly not.

- **Keyboard operability under test.** Source-level guards exist; driving real key presses
  through the UI does not ([#1552](https://github.com/laurentiu021/SystemManager/issues/1552)).
- **Reaching a row's buttons without walking every cell**
  ([#1551](https://github.com/laurentiu021/SystemManager/issues/1551)).
- **Custom themes that cannot produce unreadable text.** The custom-colour mode accepts any
  four colours with no contrast check
  ([#1561](https://github.com/laurentiu021/SystemManager/issues/1561)).

## One design system rather than 58 views that resemble each other

Spacing, radii and type sizes are still literals in the views. The token scales exist and
are widely bypassed, so a change to one of them does not reach the screens it should.

- **Typography scale** — raw `FontSize` values instead of the named text styles
  ([#1634](https://github.com/laurentiu021/SystemManager/issues/1634)).
- **Corner radii** — raw values instead of the `RadiusSm/Md/Lg` scale
  ([#1633](https://github.com/laurentiu021/SystemManager/issues/1633)).
- **The repeated status footer**, carried near-identically by most views
  ([#1630](https://github.com/laurentiu021/SystemManager/issues/1630)).

Page-layout gutters are further along: the three page-layout strategies and both
page-ending shapes are pinned by a test rather than left to discipline.

## Features people have asked for

Ordered by how often they come up, not by effort.

- **A treemap for Disk Analyzer** ([#1592](https://github.com/laurentiu021/SystemManager/issues/1592))
- **Deep Cleanup reaching the three biggest Windows space hogs**
  ([#1577](https://github.com/laurentiu021/SystemManager/issues/1577))
- **Master output slider and default-device switching in the volume mixer**
  ([#1588](https://github.com/laurentiu021/SystemManager/issues/1588))
- **Startup entries showing where they live**
  ([#1587](https://github.com/laurentiu021/SystemManager/issues/1587))
- **Taskbar progress during long operations**
  ([#1584](https://github.com/laurentiu021/SystemManager/issues/1584))
- **Scheduled maintenance that does not fire on battery**
  ([#1578](https://github.com/laurentiu021/SystemManager/issues/1578))
- **A one-click local diagnostics bundle** — exported by you, sent by you, never
  automatically ([#1650](https://github.com/laurentiu021/SystemManager/issues/1650))

## Documentation and presentation

- **A landing page** ([#1679](https://github.com/laurentiu021/SystemManager/issues/1679))
- **Screenshots for the tabs that have none**, several of them recent features
  ([#1664](https://github.com/laurentiu021/SystemManager/issues/1664))
- **A README first screen that reads as an introduction rather than a wall of prose and
  badges** ([#1677](https://github.com/laurentiu021/SystemManager/issues/1677))
- **Discussions that are usable** — everything currently sits in Announcements and nothing
  is pinned ([#1665](https://github.com/laurentiu021/SystemManager/issues/1665))

## Under the floor

Not user-visible, and the reason the rest can move at all.

- **Spacing, radius and type scales that the views actually use.** Three token sets exist
  and the views mostly bypass them with raw numbers, so a change to the scale changes
  nothing ([#1633](https://github.com/laurentiu021/SystemManager/issues/1633),
  [#1634](https://github.com/laurentiu021/SystemManager/issues/1634))
- **Command-line parsing that rejects what it does not recognise.** Today an unknown
  `-something` is accepted and ignored
  ([#2159](https://github.com/laurentiu021/SystemManager/issues/2159))

## How this list changes

Anything here can be reordered by a bug report. A crash or a data-loss defect goes to the
front; a polish item waits. If something you need is missing,
[open an issue](https://github.com/laurentiu021/SystemManager/issues/new/choose) — the
backlog is where this document comes from, not the other way round.

---

Crafted by laurentiu021 · [SysManager](https://github.com/laurentiu021/SystemManager) · MIT

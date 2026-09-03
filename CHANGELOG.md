# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Every version entry opens with one or two sentences of plain English, before its first
`###` category, describing what changed in the words someone using the app would use.
That paragraph is not decoration: the release workflow copies each entry verbatim into
the GitHub release body and the announcement discussion, so it is the first thing a
prospective user reads. CI fails a pull request whose newest entry is missing it.

## [1.76.8] - 2026-09-03

Quick Cleanup's "Clean TEMP" could delete files that other running programs were relying on, and the amount it
reported freeing counted files it had not actually deleted. Both are fixed by having it use the same cleanup
engine the rest of the app already uses.

### Fixed
- **Clean TEMP no longer touches the folder running programs unpack themselves into.** Modern single-file
  Windows programs — including SysManager itself — unpack part of themselves into a folder inside Temp while
  they run. Two of the app's three cleanup paths already left that folder alone; this one did not, so cleaning
  temp files could break a program that was open at the time, or SysManager itself. It now shares the cleanup
  engine the other two use, which skips that folder by design.
- **"Freed X MB" now counts only what was actually deleted.** The old count added a file's size before trying
  to delete it, so a file that was locked and skipped still counted toward the total — on the same screen whose
  confirmation says files in use may be skipped. Skipped files are now reported separately, as a count.

## [1.76.7] - 2026-09-03

Two things that went wrong in yesterday's last two updates. A theme you built yourself drifted a little every
time you started the app, and the Volume Control output picker went back to naming a device it had not actually
read, about ten seconds after you opened the tab. Both are fixed.

### Fixed
- **A custom theme no longer drifts on every restart.** The app saved the theme *after* applying its
  readability adjustments, then read that adjusted version back as your starting point the next time — so each
  launch adjusted an already-adjusted theme. With the background-shade slider off centre the panels crept
  lighter or darker every time; even with the slider untouched the text colour walked, a little further on each
  start. It now saves the colours you actually chose. If your custom theme has already drifted, re-picking your
  colours once will fix it for good.
- **The output picker in Volume Control stops re-claiming a device it never read.** 1.76.4 made the picker say
  "Choose a device" rather than guessing your default, because Windows does not report which device an app is
  using. But the app re-reads the device list every ten seconds, and that refresh treated "we do not know" as
  "no preference set" — so the guess came back. It stays honest now.

## [1.76.6] - 2026-09-03

A theme you build yourself in the appearance panel is now kept readable the same way the built-in ones are.
Before, it was the one kind of theme the app applied exactly as typed, so four unlucky colour choices could
leave text you could not read — with no way back except deleting a file by hand.

### Fixed
- **A custom theme now gets the same readability protection as a built-in one.** Every built-in theme goes
  through a step that nudges its text until it stands out from the panels behind it. A custom theme skipped
  that step entirely and was applied exactly as typed, so white text on a white background was accepted. It
  goes through the same step now. Applying a custom theme also used to throw away the background-shade slider's
  position; it keeps it.
- **Green and mid-grey backgrounds no longer get the wrong warning colours.** Whether a custom background
  counted as dark was decided by adding its red, green and blue values together, which treats all three as
  equally bright when the eye does not. A pure green background was called dark, so the app installed the
  colours meant for dark backgrounds and its amber warning text landed on bright green — effectively invisible.
  Mid-grey had the same problem. Brightness is now measured the way the accessibility standard measures it.

## [1.76.5] - 2026-09-03

Small grey labels — the "DEFAULT" tag in Tweaks Hub, the administrator badge, and similar — were too faint to
read comfortably on six of the twelve colour themes, including the one the app starts with. They are slightly
clearer now.

### Fixed
- **Faint small text on layered panels.** The app builds two lighter panel shades out of each theme rather
  than picking them by hand, and it turned out the check that guards text legibility skipped exactly those two
  shades — on the reasoning that they were the safe ones, when they are the hardest. Measured across all
  twelve themes, small grey text fell below the accessibility standard on half of them, worst on Midnight
  Indigo, which is the default. The shade slider made it worse: on Warm Sand it dropped below the standard
  from the slider's own default position outward, and at the far end of the range some themes were down to
  3.84 against a 4.5 requirement. Six themes now use a slightly clearer grey, the slider's automatic
  correction covers those two panel shades as well, and the check that failed to notice any of it now looks
  at them.

### Changed
- **Six themes have a slightly clearer grey for small labels.** Midnight Indigo, Deep Ocean, Violet Night,
  Clean Indigo, Sky Breeze and Mint Fresh only — the smallest change that clears the standard on every panel
  shade, along the colour each theme already used. Sky Breeze's mid-grey moved too. The other six themes are
  untouched, and no layout, wording or behaviour changed anywhere.

## [1.76.4] - 2026-09-03

Volume Control no longer claims to know which speakers or headset an app is playing through when it does not.
The picker beside each app used to always show your default device, even for an app you had sent somewhere
else, so it could tell you an app was on the speakers while Windows was sending it to a headset.

### Fixed
- **The output-device picker stops naming a device it is only guessing at.** SysManager can send an app to a
  specific device, but Windows does not report which one an app is currently using — that read is not
  implemented. The picker filled the gap with your default device anyway, which looked like a fact and was
  frequently wrong: pick a headset for an app, restart the app, and the picker went back to saying "Speakers"
  while the headset override was still in force. It now shows "Choose a device" until you choose one, and its
  tooltip says why. Choosing still works exactly as before, and choosing your default device still clears the
  override. The same applies when an app is routed to a device that has since been unplugged: that reads as
  unknown too, rather than as your default.

## [1.76.3] - 2026-09-03

The strip at the top of a page that says whether SysManager is running as administrator changes when you
grant it. It used to change more than it needed to: the corners went from slightly rounded to more rounded,
and the strip got 26 pixels shorter, so everything below it jumped.

### Fixed
- **The administrator strip keeps its shape when you elevate.** Thirty-one pages draw two versions of that
  strip in the same place — one asking for administrator rights, one confirming you have them — and the two
  had been written with different corner rounding and different internal spacing. Swapping between them moved
  everything underneath by 26 pixels and visibly changed the corners, at the exact moment the app should look
  steady. Both now use the same rounding and spacing, so the only thing that changes is the wording, the
  colour, and the "Run as administrator" button disappearing once there is nothing left to click. That button
  is 18 of the original 26 pixels and cannot be helped; the other 8, and the corners, were an accident.

### Changed
- **The strip asking for administrator rights is slightly more rounded and 8 pixels shorter.** It adopted the
  rounding and spacing the confirming version already used, which is also the rounding every other card in
  the app uses. Nothing moved position and no wording changed.

## [1.76.2] - 2026-09-03

When a group in the left-hand list is closed, the line under its name now tells you what the group is for,
in plain words, on two lines. It used to try to list every page inside the group on one line, which meant it
listed two or three of them and cut the rest off mid-word.

### Fixed
- **The closed groups in the sidebar say what they are for.** That line was built by stringing together the
  names of every page in the group, which for System came to 175 characters in a space about 26 wide — so
  what you actually read was "System Health · Windows Upda…", two pages out of eleven. Ten of the twelve
  groups were cut off, Storage included, which only holds two pages. Each group now has a short written
  line instead, on two lines so it fits whole: System reads "Updates, startup, repairs, restore points",
  Privacy & Security reads "Tracking, ads and preinstalled apps". It also answers a more useful question —
  if ads keep appearing, "ads" is easier to spot than "Debloater & Ads". The count in brackets and the
  hover tooltip, which still lists every page by name, are unchanged.

## [1.76.1] - 2026-09-03

The twelve groups down the left of the window now have icons. They were meant to all along: the space for
one was reserved next to every entry in that list, and it was empty, so the whole rail was a column of
text with a blank margin beside it.

### Fixed
- **The sidebar icons appear.** Each entry in the navigation list had a slot for an icon and a property to
  hold it, and nothing ever put a character in that property — so the slot rendered as whitespace on all
  fifty-eight entries. Every one of the twelve groups now has its own symbol, all distinct: a house for
  Dashboard, a hard drive for Storage, a trash can for Cleanup, a shield for Privacy & Security, and so
  on. The individual pages inside a group are deliberately left without one. Fifty-eight icons in a rail
  this narrow is noise, and the group heading is what you actually scan; the reclaimed space goes to the
  page names, which now have room to be read in full.

### Changed
- **The "running as administrator" badge is a shield rather than a padlock.** The padlock is what the
  Privacy & Security group in the list directly above it now uses, and one symbol cannot mean both a
  subject and "this window has administrator rights" a few centimetres apart. The badge, its position, and
  its colour are otherwise unchanged.

## [1.76.0] - 2026-09-02

Pressing Escape now stops whatever the current tab is doing — a disk scan, a cleanup, a speed test. Until
now Escape did nothing anywhere in the app, and the Cancel button only appears once an operation is
already running, so if you were not using a mouse there was no reliable way to stop one.

### Added
- **Escape stops the operation on the open tab.** Sixteen tabs can cancel what they are doing, and the
  Cancel button for each is deliberately hidden until there is something to cancel — which is right for
  the mouse, but meant the button appeared in the middle of the keyboard order, at an unpredictable place,
  while the scan was already running. Escape now does it directly, on every one of those tabs. It only
  acts while something is actually running, and only after the control you are on has had its own chance
  to use the key, so it does not interfere with closing a drop-down or undoing an edit in a text box.
  Stopping is always the safe direction — nothing is left half-changed, because each of these operations
  was already built to be stoppable.

## [1.75.32] - 2026-09-02

If you opened the appearance panel with the keyboard, you landed nowhere: the panel appeared but the
keyboard was still outside it, so you could not reach any of the themes, and there was no key that closed
it again. Opening it now puts you inside it, Tab moves between the themes, and Escape closes it.

### Fixed
- **The appearance panel can now be used without a mouse.** Every theme in that panel was already set up
  to be reached with Tab and chosen with Enter, but opening the panel never moved the keyboard into it, so
  none of that could be got at — and nothing closed the panel except clicking outside it. Escape now
  closes it and returns you to the button you opened it from, so the next Tab carries on where you were
  instead of starting again at the top of the window.

## [1.75.31] - 2026-09-02

Exporting your settings and importing them on another PC carried everything except one choice: whether
the Bulk Installer may download app icons from the web. That one was quietly reset to off. It travels
with the profile now.

### Fixed
- **A profile now carries your app-icon choice.** Every other preference the app remembers was included
  in an exported profile — theme, dark-mode schedule, gaming profiles, volume presets, close-button
  behaviour, standby memory, the update check — but the "Load app icons from the web" switch was not, so
  importing on a second PC left it off no matter what you had chosen. It is a choice like the others and
  is now part of the export. As with everything in a profile, restart SysManager afterwards for it to
  take effect.

## [1.75.30] - 2026-09-02

In Task Scheduler, the "What it does" column was simply blank for most tasks, because Windows never
recorded a description for them — but a blank cell looks like something failed to load. It now says so.
Hovering a task's name shows the full text, which the column has to cut short.

### Fixed
- **"What it does" no longer looks broken on tasks Windows never described.** A great many Windows
  scheduled tasks carry no description at all. The column showed those as an empty cell, which reads as
  missing data rather than as data that was never there; it now reads "No description provided by
  Windows." A blank value made of spaces is treated the same way, since it looks identical.
- **The full description is now readable.** The column is narrow and cuts long text off with an ellipsis,
  and hovering it showed the publisher instead. Hovering the task's own name now shows the whole
  description, so nothing is displayed with no way to read the rest of it.

## [1.75.29] - 2026-09-02

If you move around the app with the keyboard instead of the mouse, pressing Enter on anything in the
left-hand menu did nothing at all. Space worked, Enter did not. Both work now.

### Fixed
- **Enter now opens a page or a group in the left-hand menu.** You could reach every entry with Tab and
  see where you were, but the key most people press to open the thing they have landed on was ignored —
  including on the group headings, so a closed group could be reached and then not opened. Only Space
  did anything. This is the key that opens a folder in File Explorer and an entry in the Start menu, so
  Enter doing nothing read as the menu being broken rather than as a different key being required.

## [1.75.28] - 2026-09-02

In Task Scheduler, clicking a task looks up when it last ran and when it runs next. Typing anything in the
search box threw that away: both columns went back to a dash and the task you had selected was deselected.

### Fixed
- **Task Scheduler keeps the run times after you search.** Selecting a task takes a moment because the app
  asks Windows for its last and next run time separately, one task at a time. That answer was only ever
  written to the list on screen, not to the full list the search box filters — so the first character you
  typed rebuilt the grid from the copy that never got the answer, the two columns reverted to "—", and the
  selection went with them. Searching again did not help: the information had been discarded, not hidden.
  Unchecking "Hide system tasks" did the same thing. Enabling or disabling a task was never affected, and
  the difference is what pointed at the cause — that path already updated both lists.

## [1.75.27] - 2026-09-02

On the six light colour themes you could not tell whether a switch was on or off — the little circle was white
on an almost-white background. On Warm Ember and Dark Forest the same thing happened the other way round, once
the switch was on.

### Fixed
- **Switches can now be read on every colour theme.** The circle inside a switch was always white. On the
  light themes the track behind it is very pale, so the circle was invisible and the only way to tell a
  switch's state was to click it and watch what happened. Checking every theme turned up the mirror image of
  the same problem: on Warm Ember and Dark Forest, the track turns amber or green when the switch is on, and a
  white circle on those is nearly as hard to see. The circle now takes its colour from whatever is behind it,
  which is different in each state, so it stands out in both. It affects every switch in the app.

## [1.75.26] - 2026-09-02

Cleaning temporary files could delete part of another program while it was running, which usually showed up
later as that program failing to do something it had worked fine at before.

### Fixed
- **Temporary-file cleanup no longer deletes pieces of other running programs.** Some programs, SysManager
  included, ship as a single file and unpack the parts they need into a shared folder inside your temp
  folder while they run. SysManager already knew to leave its own unpacked parts alone — it had learned
  that the hard way — but it only skipped its own, so everything belonging to every other program in that
  same shared folder was fair game. Deleting those does not usually fail loudly: the program keeps running
  on what it had already loaded and then breaks later, when it reaches for a part that is no longer there,
  with nothing to connect the failure to a cleanup that happened earlier. Both cleanups — the "Temporary
  files" category in Deep Cleanup and the temp step in One-Click Tune-Up — now leave that whole shared
  folder alone.
- **The description of that category no longer promises more than it delivered.** It said anything still in
  use is skipped automatically, which is only true of files a program is holding open. It now says that,
  and adds that the folder programs unpack themselves into is skipped entirely.

## [1.75.25] - 2026-09-02

Closing SysManager immediately after changing something on the Performance tab could leave the window frozen
for two seconds before it disappeared.

### Fixed
- **Closing the window during a performance change no longer freezes it for two seconds.** Before it changes
  anything, the Performance tab records what your settings were, so that "Restore All" can put them back.
  That recording can take a few seconds, because reading Windows power settings is slow. If you closed the
  window while it was still going, the shutdown waited for the recording to finish before letting go — and it
  waited on the very thing the recording needed in order to finish, so it could only ever run out of patience.
  The result was a two-second freeze that accomplished nothing: it gave up and carried on regardless. Shutdown
  no longer waits at all. Nothing about your recorded settings changes; the wait was the only thing that was
  ever going to happen, and now it does not.

## [1.75.24] - 2026-09-01

The previous release stopped a power cut from emptying a file SysManager had just saved. That fix covered the
shared code every settings file goes through — and five more places had never used it, including the one that
replaces SysManager's own program file when it updates itself.

### Fixed
- **An interrupted update can no longer leave SysManager unable to start.** Installing an update copies the
  new version into place and then swaps it with the old one. Windows reports the copy as finished once it has
  the data in memory, so losing power in between could leave the program file present and empty — and an
  empty program file does not open at all, with no obvious way back. SysManager now waits for the drive to
  confirm it has the new version before the swap.
- **The hosts-file editor and both history files got the same protection.** Saving your hosts entries,
  restoring the backup of them, and trimming the bandwidth or resource history all wrote a replacement file
  and swapped it in, and none of them waited for the drive. All four now go through the same code the rest of
  the app uses, which also means the hosts file keeps its own Windows permissions exactly as before.
- **Trimming the history files no longer leaves a leftover file behind.** Both used the same fixed name for
  the file they write before swapping, and neither deleted it if the swap failed, so a failure left it there
  for good. They no longer name it themselves.
- **A downloaded update is confirmed on the drive before it is stored.** The mildest of the five, because a
  damaged download is already caught by its checksum when it is next used, but it is the same one-line
  omission.

## [1.75.23] - 2026-09-01

If the power went out or the machine was reset while SysManager was saving, a file it had just saved could come
back empty the next time you opened the app — your presets, gaming profiles or history gone, with nothing on
screen to say so. The very first save of one of those files could also quietly do nothing.

### Fixed
- **A power cut can no longer empty a file SysManager had just saved.** Saving works by writing the new
  contents beside the old file and then swapping the two, so that being interrupted leaves you with one
  version or the other, never half of each. The swap was reliable, but the contents were not: Windows
  reports a save as finished once it has the data in memory, and it may write it to the drive seconds
  later. Pull the plug in between and the swap has happened while the contents have not, so the file
  exists under the right name and is empty. SysManager now waits for the drive to confirm it has the data
  before swapping, which is what the promise of "either the old file or the new one" always required.
  It matters most for the two files you would never think about: the record of what a gaming profile
  changed on your PC, and the snapshot taken before a performance change. If either came back empty, the
  app concluded there was nothing to undo while your PC was still changed.
- **The first save of a preset or profile no longer fails when something else creates it at the same moment.**
  Saving asked whether the file existed yet and then acted on the answer, and those are two separate steps.
  Two saves arriving together for a file that did not exist yet could both be told "it does not", and the
  second one then failed because the first had already created it. Failed saves are only recorded in the
  diagnostic log, so that save simply vanished. Only ever the first save of a given file; every save after
  that was already handled.

## [1.75.22] - 2026-09-01

Pressing "Run as administrator" could ask you whether to keep SysManager running in the notification area, and
then not restart with administrator rights at all. The same thing could happen when quitting from the
notification-area icon.

### Fixed
- **"Run as administrator" now actually restarts SysManager with administrator rights.** Every tab that needs
  administrator rights offers that button, and it works by starting a new copy of SysManager and closing the
  current one. Closing it went through the same path as pressing the X button — so if you had never told
  SysManager what X should do, it stopped to ask whether to keep running in the notification area. That
  question is the right one when you press X and completely wrong here, and while it sat on screen the new
  copy was waiting for the old one to finish letting go. It gave up after a few seconds. The result was a
  confusing question followed by nothing happening. The question is now only asked when you press X, which is
  the only time it makes sense.
- **Quitting from the notification-area icon no longer asks whether to keep running in the notification area.**
  The same cause, and the same nonsensical question — you had just told it to quit.

## [1.75.21] - 2026-09-01

A drive's temperature could be shown under a different drive's name, which is worse than showing no name at
all — you could be looking at a healthy drive while worrying about the wrong one. The Temperatures readings are
also cheaper to collect now, and a machine where the sensor library will not start no longer keeps retrying it
every two seconds.

### Fixed
- **A temperature is no longer shown under the wrong drive's name.** SysManager reads drive temperatures from
  one source and drive names from another, and it was pairing them purely by their order in each list — with
  nothing to confirm the first temperature belonged to the first name. When the two lists did not line up, and
  they do not whenever Windows cannot read one of the drives, every name after that point shifted onto the
  wrong reading. So a healthy drive's 38 °C could appear under a failing drive's name, hiding the one that was
  actually hot. Names are now only filled in where SysManager had no name of its own, and only when the two
  lists are the same length — because a different length is proof the pairing cannot be trusted. When it cannot
  be trusted, the generic "Drive 2" stays, which is honest rather than confidently wrong.
- **The Temperatures card no longer goes completely blank because of one drive.** Fetching drive names uses a
  part of Windows that occasionally reports a temporary error. That error was not being handled, and because
  the names are fetched before the sensors are read, it wiped out the whole card — processor, graphics card and
  motherboard readings included, not just the drive names. It is handled now, and a temporary failure is
  retried on the next reading instead of being remembered for the rest of the session.
- **A sensor library that cannot start is no longer retried every two seconds.** Reading processor and
  motherboard temperatures needs a low-level component that will not load on some machines — certain security
  settings block it. SysManager kept trying to load it on every reading, about thirty times a minute for as
  long as the app was open, and each failed attempt left something behind. It now tries once, and if that
  fails, stops trying and cleans up after itself.

### Changed
- **Drive temperatures are collected far less often when not running as administrator.** In that mode — the
  normal one — each Dashboard reading was doing a full drive-health inspection, every two seconds. Drive
  temperature changes over minutes, not seconds, so the result is now reused for up to 30 seconds. That is
  roughly fifteen times less work for a number that reads the same either way. Running as administrator was
  already doing this; only the ordinary path was missed.

## [1.75.20] - 2026-09-01

Two Dashboard tiles were telling you things SysManager had not actually checked. If your drive health could
not be read, the tile still said your drives were healthy. And the "No memory errors" tile was not looking at
memory errors at all — it was looking at how much memory was in use.

### Fixed
- **"All SMART indicators healthy" no longer appears when your drives could not be read.** Reading drive
  health needs a part of Windows that is sometimes broken, switched off, or blocked without administrator
  rights. When that happened SysManager got nothing back — and treated nothing back as a perfect result, so
  the tile reported your drives were in good health without having looked at them. It now says the health
  could not be read and points at the System Health tab. A drive that genuinely reports a problem is
  unaffected: those wordings and thresholds are unchanged.
- **The "No memory errors (30 days)" tile now actually looks for memory errors.** It was deciding based on how
  much memory was currently in use, which has nothing to do with whether your memory has faulted. So a
  computer with real memory hardware errors was told there were none as long as memory use was low, and a
  perfectly healthy computer running a lot of programs was warned about "memory issues" that the System Health
  tab then said did not exist. The tile now reads the same 30-day Windows error record the System Health tab
  reads, and words the result the same way, so the two can no longer disagree. If that check itself cannot
  run, it says so rather than reporting good news.
- **The overall health score no longer counts unread information as perfect.** Drive health, memory and uptime
  each scored full marks when their source returned nothing, so a computer where all three failed showed a
  perfect score with no advice at all. An unread component is now scored as unknown, which is what a single
  unreadable drive was already scored — the code said so in a comment while doing the opposite one screen
  above.

## [1.75.19] - 2026-09-01

The Logs tab could get stuck loading forever, with one processor core pinned at full speed and the Refresh
button greyed out. It also loads faster now, and pressing Cancel says "Cancelled" instead of pretending the
list finished.

### Fixed
- **The Logs tab can no longer get stuck loading with a core at 100%.** Windows event logs are a rolling
  buffer, so a busy log can move underneath a read that is already in progress. When that happened, SysManager
  retried the same failed read over and over with nothing in between: the tab stayed on "Loading events…"
  forever, Refresh stayed greyed out because it is disabled while loading, and one processor core sat at full
  speed until the tab was closed or Cancel was pressed. Retrying could never have worked — Windows does not
  move on to the next entry after that kind of failure — so SysManager now stops, keeps the events it already
  read, and says the list is incomplete.
- **Pressing Cancel now says "Cancelled" instead of reporting a finished list.** A cancelled load was
  reported as a completed one, so a list stopped halfway through was described as "Loaded 137 events" with
  nothing to indicate it was cut short. The same wrong message appeared when switching away from the tab
  mid-load.
- **A partly-read log now says so.** If a read stops early, the message under the list says how many events
  arrived and that the rest are missing, rather than presenting the shortened list as everything there was.

### Changed
- **The Logs tab loads faster and uses less memory, especially at the larger row counts.** SysManager was
  building the raw technical XML for every single event as it loaded, although that panel only ever shows the
  one row you click. With the list set to 5000 rows that was 5000 pieces of work and 5000 stored blocks of
  text nobody looked at. It is now produced for the row you select, when you select it. The panel behaves
  exactly as before.

## [1.75.18] - 2026-09-01

Several tabs open a built-in Windows program for you: File Explorer to show a file, Control Panel, Device
Manager, Event Viewer. SysManager now always opens the real one from the Windows folder, instead of asking
Windows to look up the name and trusting whatever it finds.

### Fixed
- **The built-in Windows programs SysManager opens are now named by their full location.** Ten places asked
  Windows to find a program by name alone — File Explorer when you use "Show in folder" in Disk Analyzer,
  Duplicate Finder, Deep Cleanup, Startup, Logs and the update screen; Control Panel, Sound, Power Options,
  Device Manager and the rest of the Legacy Panels tab; and Event Viewer from Logs. Looking a name up is not
  the same as finding the right program: Windows checks places that any program running as you can add
  entries to, so the name could have been pointed at something else entirely. It never was, and none of this
  needed fixing urgently, but SysManager is often run as administrator, and anything it opens then starts
  with those same rights. Every one of these now names the actual file in the Windows folder, so there is
  nothing to look up. Nothing changes in what you see or click.
- **A check now stops this being reintroduced.** These ten were only found by searching for them, and nothing
  would have stopped an eleventh being added later. The build now refuses any new code that opens a program
  by name alone.

## [1.75.17] - 2026-09-01

The speed test uses a small program from Ookla, downloaded the first time you run it. SysManager already
checked that program's signature before running it; now it also locks the file while it does so, closing a
gap where the file could have been switched for another one in between.

### Fixed
- **The Ookla speed-test program is locked from the moment it is checked to the moment it finishes.** The
  program is kept in your own user folder, which anything running under your account can write to. SysManager
  checked its digital signature every time before running it — that part was already right — but it checked
  the file by name and then started it by name, and in the gap between those two steps the file could have
  been replaced. The checked file and the started file would not have been the same one, which is the only
  thing a signature check is for. SysManager now holds the file open with changes and deletion blocked from
  before the check until after the speed test ends, so the program that was approved is the program that
  runs. This matters most when SysManager is running as administrator, where anything it starts inherits
  those rights.
- **A program that fails the signature check is now actually removed.** SysManager refuses to run it either
  way, so nothing unsafe was ever started. But the removal was attempted while the file was still locked, so
  it quietly failed and the rejected file stayed in the folder, to be rejected again on every later run. The
  lock is now released first, so the file really goes.

## [1.75.16] - 2026-09-01

File Shredder now tells you what it actually did. Before, a folder containing a shortcut to somewhere else
was reported as fully destroyed while it was still sitting on the disk, and when a file could not be shredded
the only thing shown was the word "Failed".

### Fixed
- **A folder holding a shortcut is no longer reported as destroyed.** File Shredder deliberately refuses to
  follow a shortcut that points somewhere outside the folder you picked, because overwriting through one
  would destroy files you never selected. That refusal is right and has not changed. What was wrong is that
  nothing said so: the folder was reported as shredded, no note was written anywhere, and the folder stayed
  on the disk because the shortcut was still inside it. So you were told your files were gone while looking
  at the folder holding them. The shredder now says how many shortcuts it left alone and that the folder is
  still there. A folder with no shortcuts in it reads exactly as before, with nothing extra added.
- **When a file cannot be shredded, the reason is now shown.** The shredder already worked out a clear
  explanation for every file it had to leave behind — which ones they were, and why, whether locked by
  another program or sharing its contents with a file elsewhere on the disk. That explanation went only into
  the log file. On screen the file was marked "Failed" and nothing more, which for a tool whose entire job is
  destroying data beyond recovery leaves out the only part that matters: those files are still on your
  computer and still readable. The explanation now appears beside the summary at the bottom of the tab.

## [1.75.15] - 2026-09-01

If your hosts file had a line with a typo in it, saving from the Hosts tab used to delete that line
without saying so. Those lines are now kept exactly as you wrote them.

### Fixed
- **A hosts line SysManager cannot read is no longer deleted when you save.** The Hosts tab rebuilds the
  whole file every time you save, from the entries it managed to read plus your comments. A line it could
  not read belonged to neither group, so it vanished — and only after you changed something else entirely,
  which made the two look unrelated. The kinds of line this affected are all small mistakes: a semicolon
  where a space belonged, an address with no website name after it, a fourth number above 255, a word on
  its own. Every one of them is a line somebody typed deliberately and got slightly wrong, so it is the
  best record of what they meant — and the mistake is exactly why the tab could not read it back. Those
  lines now survive a save untouched, alongside the comments that were already being kept. They still do
  not appear in the list of entries, because SysManager cannot tell what address they were meant to point
  at and will not guess.

## [1.75.14] - 2026-09-01

App Blocker refuses to block one more Windows file. Blocking it was possible, and it would have stopped
Windows ever asking you for permission again — including the permission needed to undo it.

### Fixed
- **App Blocker will no longer block the Windows permission prompt.** App Blocker stops a chosen program
  from starting, and it already refuses the handful of Windows files that the computer needs in order to
  start at all. Missing from that list was the small Windows program that draws the "Do you want to allow
  this app to make changes?" box. Nothing stopped you picking it — the file browser accepts it like any
  other program — and blocking it would have meant Windows could never ask for permission again. Undoing
  the block needs permission, so there would have been no way back from inside SysManager, or from anywhere
  else on the running computer. It is now refused like the others.
- **The refusal message says what is actually true.** That message read "is required for Windows to start …
  could leave the computer unable to boot", which was right for the files already on the list and wrong for
  this one: Windows starts perfectly well without the permission prompt, and the damage only shows up the
  first time something needs it. The message now covers both cases without claiming the wrong one.

## [1.75.13] - 2026-08-28

SysManager runs PowerShell behind the scenes for some features, and that engine has its own usage
reporting built in by Microsoft. It is now switched off. SysManager itself never sent anything, but the
engine it borrows could have.

### Fixed
- **The PowerShell engine SysManager uses no longer has its usage reporting enabled.** Several features
  work by running PowerShell, and rather than launching a separate window SysManager loads the PowerShell
  engine inside itself. That engine ships with Microsoft's own usage-reporting component, which is turned
  off by a single switch — and SysManager was not setting it. Nothing of yours was being collected by
  SysManager, which has never had anything to send it to, but the borrowed engine could have reported its
  own activity. The switch is now set before any PowerShell session starts, for this program only, so your
  saved Windows settings are untouched. A test checks it stays set, and the privacy section of SECURITY.md
  now names the dependency instead of only speaking for code written here.

## [1.75.12] - 2026-08-28

Green now means "safe" again. The filter buttons on Services and System Logs were all green, including
"Stopped" and the time ranges, so the colour that marks a safe service was also on buttons that say
nothing about safety.

### Changed
- **Filter buttons that say nothing about safety are no longer green.** Services marks a service Safe,
  Caution or Critical in green, amber and red — that part is intentional. But nine other filter buttons
  borrowed the green one: "Running", "Stopped", "Advanced" and "All" in Services, and the 1h / 24h / 7d /
  30d / All time ranges in System Logs. So a green "Stopped" sat one row under a green "Safe" in the same
  row of buttons, and "last 24 hours" carried the same colour as "this service is safe". Those nine are
  now plain grey, and turn purple when selected, which is the colour the rest of the app uses to show what
  you have picked. Safe, "Safe to disable" and "Keep enabled" stay green, because each of those really is
  a statement about safety.

## [1.75.11] - 2026-08-28

The background-shade slider in Appearance can no longer make text hard to read. Dragging it to either
end used to fade the writing into the background, with nothing telling you that was what happened.

### Fixed
- **The background slider keeps text readable at every position.** Appearance has a slider that makes the
  app's background lighter or darker to taste. It moved the backgrounds but left the text colours where
  they were, so at the far ends the writing and the panel behind it drifted close enough together to be a
  strain — and since the slider is presented as harmless personalisation, nothing connected the cause to
  the effect. Grey label text fell below the readable-text standard at a quarter of the positions the
  slider can reach. The text now adjusts just enough to stay readable, and only when it otherwise would
  not: if you have never moved the slider, nothing about your theme changes.

## [1.75.10] - 2026-08-28

If you turn notifications off yourself while a Gaming Profile is running, ending the profile no longer
turns them back on. Before, it quietly undid your change, so the switch looked like it did not stick.

### Fixed
- **Muting notifications during a game now sticks after the profile ends.** A Gaming Profile with "Silence
  notifications" turns them off while you play and turns them back on afterwards. If you got interrupted
  mid-game, went to Privacy & Security and turned notifications off yourself, ending the profile put them
  back on — because Windows records both changes identically, so SysManager could not tell your change
  from its own and assumed the switch was still its to undo. It now keeps a note of when the
  Notifications page writes that setting, so it can recognise your change and leave it alone. The note is
  saved to disk, so this still works if the app closes mid-game and cleans up on the next launch. The
  ordinary case is unchanged: if you do not touch the switch, ending the profile restores notifications
  exactly as before.

## [1.75.9] - 2026-08-28

Text on the coloured buttons is readable on every theme now. On most of the twelve themes the white
label on a purple, orange or green button was too faint against its own background, worst of all on
Warm Ember where it was barely there. Those labels switch to dark text where dark reads better.

### Changed
- **Labels on coloured buttons now pick black or white, whichever you can actually read.** The main
  action button on every tab takes its colour from the theme, and that colour ranges from a deep indigo
  to a bright amber, but the label on it was always white. On Warm Ember that put white text on orange,
  which measures as barely-there rather than merely low contrast; on ten of the twelve themes it was
  below the readable-text standard. The label now switches to dark text on the themes where dark reads
  better, which is eight of them. Ten themes look different as a result, and that is the fix: Lavender
  and Soft Blossom keep white text because white is genuinely better there.
- **The same applies to the red buttons and the checkbox tick.** "Delete", "Remove" and the other red
  buttons had the identical problem in reverse — their white label was too faint on the dark theme and
  fine on the light one — so they now follow the same rule per theme. The tick inside a ticked checkbox
  sits on the same theme colour and was likewise always white, so it follows too.

## [1.75.8] - 2026-08-28

The Services tab now explains what each service does. Most of those descriptions were showing as a
file path like `@%SystemRoot%\system32\spoolsv.exe,-2` instead of a sentence, which also meant
searching for a word in a description could not find it.

### Fixed
- **Service descriptions read as sentences instead of file paths.** Windows almost never stores a
  service's description as text. It stores a pointer into the program that owns the service, and the
  sentence lives inside that file so one copy of Windows can show it in any language. SysManager was
  showing the pointer. On a standard Windows 11 install that is 404 of the 461 services that have a
  description at all, so the column that is supposed to tell you whether something is safe to turn off
  was showing you `@%SystemRoot%\system32\spoolsv.exe,-2`. It now follows the pointer and shows what
  Windows itself shows: "This service spools print jobs and handles interaction with the printer."
  Search improved with it, since the search box looks at the description too — typing "print" could
  never match the Print Spooler while its description was a path. The few descriptions Windows already
  stores as plain text are untouched, and the handful that cannot be resolved now show nothing rather
  than the raw pointer.

## [1.75.7] - 2026-08-28

Five things you could see, or could not see, in the interface. On any light theme the Dashboard's
MEMORY and GPU figures were washed out; sliders gave no sign of which one the keyboard was on; the
per-drive checkboxes in System Health all sounded identical to a screen reader; Shortcut Cleaner's
delete button looked like an ordinary one; and pointing at a sidebar group made it look already open.

### Fixed
- **The Dashboard's MEMORY and GPU figures are legible on light themes.** Every other colour in the
  app is chosen per theme, but these two were fixed at the shades picked for the dark theme, so on a
  light preset the MEMORY bar and its dot sat at 2.42:1 against their own track — visible if you knew
  where to look, easy to miss otherwise — while the CPU card beside them, which uses a theme-aware
  colour, stayed crisp. The same blue also tints the Quick action status line and its bar, so that was
  washed out too. Both colours now darken on light themes to the same step already used by the badges
  elsewhere in the app, which clears the 3:1 contrast that applies to a bar or a dot. Dark themes are
  byte-for-byte unchanged.
- **Sliders show which one has keyboard focus.** Sliders are keyboard controls by definition, since the
  arrow keys change their value, but SysManager's slider draws its own track and thumb and that
  replaced the outline Windows normally puts around a focused control. Tabbing through Audio Mixer or
  Display Profiles moved focus invisibly. The slider now uses the same focus ring as every other
  control in the app.
- **Each drive's CHKDSK checkbox says which drive it is.** System Health lists one checkbox per drive,
  and a screen reader announced only the control type for all of them, so you could hear that there
  were six checkboxes but not which one was C:. Each now announces its own drive letter.
- **Shortcut Cleaner's "Delete Selected" is styled as the destructive action it is.** It deletes files
  from disk but wore the ordinary secondary style, so the three buttons in that row looked
  interchangeable and the one that removes files was the least conspicuous of them. It now carries the
  same red treatment the rest of the app uses for irreversible actions.
- **Hovering a sidebar group no longer looks like opening it.** The group headers tinted themselves on
  hover with the exact colour this window uses to mark the page you are on, so running the mouse down
  the sidebar made each group in turn look like the open one. They now use the hover tint the nav rows
  and table rows already use, which keeps the two meanings distinct.

## [1.75.6] - 2026-08-28

If you use a screen reader, SysManager's progress bars now tell you what they are doing. Before, 33
tabs all announced the same word, "Progress", and four bars announced nothing at all.

### Fixed
- **Every progress bar now says what it reports.** A screen reader announced the bare word "Progress"
  on 33 different tabs, so it told you something was happening but never what — and four bars had no
  announcement at all, including the strip under each sidebar entry and the spinner beside each
  Dashboard alert. All 60 bars are now named for the thing they track: "Cleanup progress", "Driver scan
  progress", "Update download progress", and so on. The bar under a sidebar entry names the tab it
  belongs to, and the spinner beside a Dashboard alert names the check it is waiting on. Five more that
  said only "Working", "Loading" or "Monitoring" got the same treatment.
- Nothing changed visually. A name is read out, never drawn.

## [1.75.5] - 2026-08-27

Tune-Up's temp cleanup was reaching into SysManager's own working files. That could make a perfectly
clean run report errors, and in the worst case stop a feature working until you restarted the app.

### Fixed
- **Cleanups no longer sweep the folder SysManager is running out of.** Because the app ships as a
  single file, Windows unpacks part of it into your temp folder while it runs, and both temp cleanups
  — Tune-Up's and Deep Cleanup's "Temporary files" — walked temp without knowing to leave that
  alone. Files the app had open refused to delete and were counted as errors, so a cleanup could report
  failures it caused itself. Files it had unpacked but not opened yet were deleted for real, which
  could break a feature later in the same session — the per-app bandwidth view was the most likely
  one, since it loads its extra components only when you start it. Both cleanups now skip that one
  folder and nothing else, so everything they used to remove is still removed.

## [1.75.4] - 2026-08-27

The in-app updater could not actually install an update: clicking Install closed SysManager and
nothing came back, and Roll back could quietly stop being offered. Both are fixed, so the updater
works end to end.

### Fixed
- **"Install update" now replaces the running build instead of doing nothing.** The updater only
  agreed to write over a file whose name matched the running program's, but the program doing the
  writing is the freshly downloaded `SysManager-<newversion>.exe` while the file being replaced is the
  older build — two different names, so the check could never pass and every update was silently
  refused after the app had already closed. It now identifies a SysManager build by the product stamp
  Windows keeps inside the file, which is the same across versions and survives you renaming the
  portable .exe. Downloading and installing an update now completes and relaunches.
- **Roll back no longer loses its safety copy when you download a newer update.** Cleaning up old
  downloads deleted the checksum file that Roll back needs to confirm the saved build is intact.
  Because that confirmation refuses to proceed without the checksum, Roll back would stop being
  offered after the next download. The checksum is now kept alongside the saved build.

## [1.75.3] - 2026-08-26

A drive that Windows itself reports as failing could still show a green 100% health score. Disk Health
would say "Drive is failing — back up now and replace it." on one line and 100% on the next, and the
Dashboard health score agreed with the 100%. If you have ever seen those two disagree, believe the
warning.

### Fixed
- **A failing drive can no longer report a healthy score.** The percentage was worked out from wear,
  temperature and error counts, and it only looked at Windows' own verdict when there were no such
  readings at all. So a drive with normal wear and temperature scored 100 even when Windows had
  flagged it as failing — which it does for problems those readings do not cover, such as
  reallocated sectors or a predictive-failure warning from the drive itself. Windows' verdict is now a
  ceiling: a drive it calls failing cannot score above 20, one it warns about cannot score above 60,
  and a drive already scoring worse than that keeps the worse number. The Dashboard health score
  follows the same rule, so the two tabs can no longer contradict each other.
- **A drive we know nothing about no longer counts as perfect** in the Dashboard score. It counts as
  80, because no readings and no verdict is not the same as a clean bill of health.

## [1.75.2] - 2026-08-26

Timer Resolution had the two ends of its range the wrong way round, so "Enable" asked Windows for the
slowest timer instead of the fastest one. The tab reported a 15.6 ms best case and a 0.5 ms worst
case — exactly backwards — and Gaming Profile's "Finest timer resolution" switch had the same
problem, promising 0.5 ms while requesting the 15.6 ms default.

### Fixed
- **Timer Resolution now really requests the fastest timer your hardware supports.** The Windows call
  that reports the achievable range returns the slowest value first and the fastest second; its
  parameter names say the opposite ("Minimum" means minimum precision, i.e. the longest interval), and
  SysManager had trusted the names. Measured on real hardware the call returns 15.625 ms, then
  0.5 ms, then the value in effect. The two are now read in the order the call actually uses, so the
  tab shows the right numbers, Enable asks for the fast end, and the Gaming Profile switch does what
  its label says. If you had used either one for lower input latency, it was not doing anything —
  it now does.

## [1.75.1] - 2026-08-26

"Restore original cores" on the CPU Core Affinity tab could report success while putting nothing back.
If you pinned a process and then refreshed the list, Restore quietly re-applied the cores you had just
pinned instead of the ones the process started with — and still said it had restored them.

### Fixed
- **CPU Core Affinity: Restore now really returns a process to the cores it started on.** The tab
  remembered "original cores" in a single slot that was re-read every time a process was selected, so
  refreshing the list after pinning overwrote the remembered value with the pinned one. Restore then
  wrote that same mask back — a no-op reported as a success. Each process's starting affinity is now
  captured once, the first time the tab sees it, and kept for the rest of the session. Two related
  edges are covered as well: the remembered value survives a refresh (so the button no longer
  disappears), and Restore is not offered at all for a process whose affinity could not be read,
  rather than being offered for a restore that would do nothing.

## [1.75.0] - 2026-08-25

Browser Cleaner now clears Firefox cookies and open-tab sessions, not just its cache. A Firefox user who
opened this tab to clear browsing traces was, until now, only ever clearing cache — while the tab's own
description promised the same categories it offers the other browsers.

### Added
- **Firefox: Cookies and Sessions can now be cleared**, alongside the cache it already handled. Both are
  unticked by default and flagged "signs you out", the same as the Chrome/Edge/Brave/Opera cookie rows,
  and each Firefox profile gets its own rows.

### Notes
- **Firefox History is deliberately not offered.** Firefox stores your history and your bookmarks in the
  same file (`places.sqlite`), so a "clear history" would delete bookmarks too. Rather than do that
  silently, the tab omits History for Firefox. Its cookies/sessions target only their own named files —
  saved logins, keys, bookmarks and preferences are never touched.

## [1.74.0] - 2026-08-25

The CPU Core Affinity process list is now filterable, and shows which processes are already pinned.
Finding one game in a dropdown of 300 `svchost` entries was a dead end; now there's a name/ID filter
like the other list tabs, and a process running on a subset of cores says so right in the list.

### Added
- **A filter box on the CPU Core Affinity process picker.** Type part of a name or the process ID to
  narrow the list — the same affordance the Services, Task Scheduler and Windows Features tabs already
  have. The selection is kept across a Refresh, even if the process's affinity changed in between.
- **A "pinned" marker in the list.** A process running on a subset of cores shows it — e.g.
  "chrome (1234) — 4 of 16 cores" — so you can see what you've already tuned instead of selecting each
  one to infer it from the core checkboxes. The tab already read this and discarded it. The wording is
  neutral (it reports the state, it doesn't claim SysManager set it), and a process on all cores or one
  whose mask can't be read shows no marker.

## [1.73.0] - 2026-08-25

Disk Analyzer now remembers your last scan of each folder and tells you what changed. Until now every
reopen started blank, even though the rest of the app remembers things; now, after you re-scan a folder,
a line reads e.g. "3.2 GB larger than your last scan on 12 Jul" - turning a one-off number into an
answer to "why is my disk filling up?".

### Added
- **Disk Analyzer: a "since last scan" delta per folder.** The last scan of each root is saved locally
  (disk-scan-history.json), and on the next scan of that folder the tab shows how much it grew or
  shrank and when it was last measured. It is always phrased as "since your last scan", never as
  continuous monitoring, because scanning is something you trigger. The history is bounded (a capped
  number of folders, a capped number of roots) and follows the same never-lose-the-tab-on-a-bad-file
  rule as the Speed Test history it is modelled on.

### Notes
- The scan history stays on this PC and is **not** part of Profile Export - folder paths and sizes are
  specific to one machine, so a delta from another PC's disk would be meaningless here.

## [1.72.1] - 2026-08-25

The Dashboard no longer tells you "~10s remaining" for a check it is not timing. When one of the
five startup checks took more than five seconds, the card showed a fixed "~10s remaining" that was
never measured — it read the same whether the check finished in a moment or stalled. It now says
"still checking…", which promises no time it cannot keep.

### Fixed
- **Dashboard: the slow-check hint no longer invents a countdown.** The five health checks are
  pass/fail probes with no progress to measure, so there is no honest number to show; the app's real
  time estimator stays where it belongs, on the operations that report progress (App Updates, Bulk
  Installer, Cleanup). The hint is also dropped the moment its check finishes, so it stops leaving
  five idle timers per refresh waiting to fire after the work is already done.

## [1.72.0] - 2026-08-25

The Task Scheduler scan can be stopped. On a PC with a full task tree, listing every scheduled task
takes a while, and until now there was no way to interrupt it — and pressing Refresh again during a
scan started a second one on top of the first. Both are fixed.

### Added
- **A Cancel button while the task scan runs.** Whatever was already listed stays on screen, and the
  status line says the scan was stopped rather than leaving "Loading…" underneath a half-filled grid.

### Changed
- **Refresh is disabled during a scan** instead of stacking a second one, which is how the Startup,
  Windows Features and Windows Update tabs already behave — this tab was left out of that change.
- **Selecting a different task now cancels the previous task's query.** Holding an arrow key down the
  list used to start one background query per row it passed over, all still running after you had
  moved on.

### Fixed
- Closing the tab mid-scan now stops the scan instead of leaving it running with nothing waiting for
  the result.

Enable/disable stays deliberately un-cancellable: it writes the new state and then reads it back, so
stopping it halfway could leave a task toggled while the list still showed the old value.

## [1.71.0] - 2026-08-25

Windows Features now takes a restore point before it changes anything. It was the last tab making a
servicing-level change to Windows without one, and the one where it matters most: turning a feature back
on is a second operation that can itself fail, possibly with a reboot already pending. Unlike removing a
Store app, a restore point genuinely does cover this kind of change.

### Added
- **Windows Features takes a restore point before the first toggle of the session.** Taken after the
  confirmation and after the "needs administrator" refusal, so neither declining nor being unelevated
  spends the single point Windows grants per day. Best-effort, and reported only when one was really
  created — never on a toggle that DISM rejected, since nothing changed in that case.

## [1.70.1] - 2026-08-25

Settings Watchdog could show a setting as unchanged while it had in fact changed. The page read your
live settings twice — once to work out what had drifted, once to fill in the list of what it watches —
and anything that moved between those two reads appeared in the list with its new value but without
being flagged. Now both come from a single read, so the two halves of the page can no longer disagree.

### Fixed
- **Settings Watchdog: the drift list and the watched list now come from one read.** This was most
  likely to bite in exactly the situation the tab exists for — the catalog watches settings that
  Windows Update flips back underneath you, so a change arriving mid-refresh is the expected case,
  not a rare one. The baseline file was also being loaded twice per refresh; now once.

## [1.70.0] - 2026-08-25

Defender Tweaks now takes a Windows restore point before it changes anything, which was the last tab
still without one. Every tab that changes system settings is now covered by the same single snapshot,
so the answer no longer depends on which page you opened.

### Added
- **Defender Tweaks takes a restore point before the first change of the session.** Its four changes
  — PUA protection, Controlled Folder Access, and adding or removing a scan exclusion — now run
  through one shared path instead of four near-identical copies, so none of them can skip the
  snapshot. Each keeps the exact wording it had when something goes wrong.

### Changed
- A change Defender rejected — Tamper Protection on, or no administrator — no longer mentions the
  restore point. Nothing was changed, so there is nothing for a snapshot to reassure you about, and
  saying otherwise read as though something had happened.

## [1.69.0] - 2026-08-25

Two more of the tabs that change your PC now take a Windows restore point first: Debloater & Ads and
Privacy & Telemetry. That was the whole point of the shared restore point added in 1.68.0 — the same
protection whichever page you happen to open — and these were the two doors still left without it.
Debloater says plainly what a restore point can and cannot undo, because it cannot bring removed
Store apps back and pretending otherwise would be worse than saying nothing.

### Added
- **Debloater & Ads takes a restore point before the first removal.** The wording is deliberately
  careful: System Restore does not restore removed Store apps, so reinstalling from the Microsoft
  Store is still the real way back, and that comes first in the message. The point is described as
  covering the rest of the system, which is what it actually does.
- **Privacy & Telemetry takes one before Apply**, the same point Tweaks Hub already took for the
  identical registry writes. Reaching a toggle from one tab or the other now gives the same answer.

### Changed
- The restore point is attempted **after** you confirm, not before, so declining a change no longer
  spends the one point Windows grants per day.
- Still only ever mentioned when a point was actually created. The progress text no longer announces
  the attempt either: saying "creating a restore point" and then failing — the common case, since
  System Restore ships switched off on many PCs — left you believing in a snapshot you did not have.

## [1.68.0] - 2026-08-25

The automatic restore point now covers the tab where you would most want it. Turning a privacy switch
off through Tweaks Hub took a Windows restore point first; making the identical change from another tab,
or removing OneDrive, took none. Same risk, different answer depending on which page you happened to
open — and the unpredictable version of a safety net is the worst kind.

### Added
- **Edge/OneDrive Remover now takes a restore point before its first change**, like Tweaks Hub already
  did. It is attempted before the change rather than after, because a snapshot taken afterwards records
  the state you are trying to get back from. The Restore buttons count too — putting Edge back is a
  system change like any other.
- **One restore point per session, not one per tab.** Windows allows roughly one a day, so two features
  each making their own meant the first one used up the allowance and the second reported "no restore
  point" while a perfectly good one existed. Tweaks Hub and Gaming Profile each had their own private
  copy of that logic; there is now a single shared one, and a test refuses a second.

### Changed
- The restore point is still only ever mentioned when one was actually created. System Restore is
  switched off on many PCs and needs administrator, so a silent "no" is normal — and claiming
  protection that did not happen would be worse than not offering it.

## [1.67.0] - 2026-08-24

Moving to a new PC now actually brings your setup with you. Profile Export carried your theme, your
speed-test history and your update-check choice — and silently left behind the dark-mode schedule, the
gaming profiles, the volume presets and two more preferences you had set. Eight sections travel now
instead of three.

### Added
- **Profile Export carries five more of your settings**: the dark-mode schedule, your gaming profiles,
  your volume presets, the close-button behaviour, and the standby-memory preference. Each is still
  optional — tick the ones you want, as before.
- **What it will not carry, on purpose.** Anything that describes *this* PC rather than your choices is
  deliberately left out: the undo baselines behind Performance Mode and Environment Variables, the
  Settings Watchdog's record of this machine's registry, the service-startup ledger and the local
  activity log. Copying those to another computer would restore it to settings it had never been on, or
  report differences that are only "a different PC". A test now keeps each of them out.
- **A gaming profile arrives without the other PC's unfinished session.** That file also holds a
  crash-recovery marker for a game running on the machine that exported it; imported as-is, this PC would
  offer to undo tweaks it never applied for a game that never ran here. Your actual profiles come across;
  that marker is dropped. If the section cannot be read at all it is skipped rather than written over a
  working file.
- The tab's empty-state message and the README section listed only the old three sections; both now
  describe what really travels.

## [1.66.5] - 2026-08-24

Performance Mode will no longer mistake a running game profile's settings for your own. Start a profile,
then change something in Performance Mode for the first time, and it used to write down the profile's
power plan as "how you had it" — so a later Restore All would put you back onto a gaming plan you had
never chosen.

### Fixed
- **Performance Mode asks you to stop a game profile before it records your original settings.** While a
  profile is running, the power plan and visual effects on the machine are the profile's, not yours. The
  tab now says so and refuses to save them as your baseline, instead of silently keeping borrowed values
  it would restore you to later. Nothing is changed when it refuses.
- A baseline saved **before** a profile started is still loaded and used normally, so the protection does
  not stop the tab working during a game — it only declines to invent a new baseline out of the profile's
  own settings.

## [1.66.4] - 2026-08-24

Two tabs were called one thing in the sidebar and another at the top of the page. Small, but it is the
kind of thing that makes you wonder whether you clicked the right item.

### Fixed
- **"App Alerts" and its page are both called "New App Alerts" now.** The sidebar said "App Alerts"
  while the page was headed "App Installation Alerts". The new name says what the tab is for — it tells
  you when something new installed itself.
- **"Profile Export / Import" is spelled the same in both places.** The sidebar wrote it without spaces
  around the slash, the page with them.
- Two tabs keep names that differ on purpose: **About** (the sidebar convention, with the usual longer
  heading) and **Context Menu**, whose rename is still being decided. Both are recorded as deliberate,
  so a future tab cannot quietly join them — a test now compares every tab's sidebar label against its
  page heading, and the two names are edited in different files, which is why they drifted unnoticed.
- The tab names offered in the GitHub issue forms were updated to match, so a bug report can name the
  tab the way the app does.

## [1.66.3] - 2026-08-24

Gaming Profile and Performance Mode can no longer trip over each other. They change the same two
settings — the power plan and the visual-effects switch — and each writes down "how you had it" before
changing anything. Start one while the other is mid-change and the note it takes down is the other one's
change, so restoring later put your PC somewhere you had never been.

### Fixed
- **Gaming Profile now waits for Performance Mode, and vice versa.** Starting a game profile while a
  Performance Mode change is running says plainly which one is busy instead of starting anyway. It takes
  that check **before** reading your current settings, not just before changing them — reading them at
  the wrong moment is what recorded the wrong "original" and could leave your PC on a gaming power plan
  after everything was supposedly restored.
- **Undoing is never refused.** If a game exits while another change happens to be running, the
  optimizations are still undone. Leaving your PC tweaked because something else was busy would be worse
  than the clash being avoided, so the undo goes ahead and says so in the log. The same applies to the
  leftover-session cleanup that runs after a crash.
- **The README no longer overstates the protection.** It said the lock covers every tab that changes
  system state and then omitted Gaming Profile, which was the one tab it did not cover. A test now keeps
  that list honest in both directions.

## [1.66.2] - 2026-08-24

Turning notifications back on while a Gaming Profile is running now sticks. Gaming Profile and the
Notifications page are two switches wired to the same setting, and when the game exited the profile put
its own version back — silencing notifications you had just deliberately switched on.

### Fixed
- **Gaming Profile no longer overrules a notification change you made yourself.** When a profile ends it
  restores the notification setting only if it is still the one the profile applied. If you changed it
  meanwhile — from Privacy & Security → Notifications, which is the very same switch — your choice is
  kept and the profile leaves it alone. Everything else about the restore is unchanged, including putting
  an explicitly-on setting back exactly as it was rather than merely "not off".
- **The two features now share one definition of that setting.** The registry location was written out
  twice, word for word, in two different services that both wrote to it, with neither aware of the other
  — so a correction to one would silently not apply to the other. There is now a single owner, and a test
  refuses any registry location that is spelled out in more than one place, which is the class of mistake
  rather than this one instance of it.

## [1.66.1] - 2026-08-24

Two tabs list the same scheduled tasks, and now each one says so. Startup Manager deliberately leaves out
Windows' own tasks to keep its list short and safe to touch, which is the right call — but it never told
you, so a task you could not find looked like a task that was not there.

### Fixed
- **Startup Manager and Task Scheduler now explain how they overlap.** Startup Manager says that it lists
  scheduled tasks belonging to programs you installed and that Windows' own are left out, and points at
  Task Scheduler for the complete list. Task Scheduler says the reverse: a third-party task also appears
  in Startup Manager, it is the same task in both places, and switching it in one tab shows up in the
  other once that tab refreshes. Neither claims the two lists stay in step live, because they do not.
- **Corrected a documented claim that was never true.** The README said Startup Manager lists
  "logon-triggered scheduled tasks". The scan only requires that a task have some trigger — it never reads
  which kind — so a task that runs daily or when the machine goes idle was always listed too. The README
  now describes what the scan really does, and a test pins the wording to the code so the claim cannot
  drift back: it becomes allowed only if the scan is one day taught to read the trigger type.

## [1.66.0] - 2026-08-24

Startup Manager now shows a hiding place it was missing. Windows has a "policy" startup list that Task
Manager does not display at all, which is exactly why unwanted software likes it — you turn off
everything you can see, restart, and the program starts anyway.

### Added
- **Startup Manager reads the policy startup list.** Programs registered under Windows'
  `Policies\Explorer\Run` key, for your account and for the whole machine, now appear in the list like
  any other startup item, with the folder they came from shown as usual. Windows gives no way to switch
  these off from an app, so each one says "Set by a system policy — managed elsewhere" instead of
  offering a switch that would do nothing: if you try anyway, it tells you plainly rather than reporting
  success and leaving the program to start again next time. On a PC with no such entries — most PCs —
  nothing changes.

## [1.65.23] - 2026-08-24

Six more indicators now say what they are measuring. The Dashboard's CPU, memory and GPU bars, its
per-drive space bars and its quick-action bar carried no label at all, and neither did the level meter
beside each app in Volume Control — a screen reader announced a number with nothing to attach it to.

### Fixed
- **Six progress indicators were announced with no name.** The Dashboard's three headline bars now read
  "CPU usage", "Memory usage" and "GPU usage", matching the CPU / MEMORY / GPU headings printed above
  them, and its quick-action bar reads "Quick action progress". The two that are drawn once per item
  name that item: each STORAGE bar says which drive it measures, and Volume Control's meter says which
  app it is listening to — previously a whole column of bars would have announced the same words. A test
  now fails the build if an indicator that repeats per item announces a fixed label.

## [1.65.22] - 2026-08-24

Progress bars now say what they are reporting. On four pages several appeared at once and all of them
were announced as just "Progress", and two were not progress at all — a screen reader read out
"Progress 78" for a battery that was 78% charged.

### Fixed
- **Ten progress bars were announced identically, or described the wrong thing.** Deep Cleanup shows
  separate bars for the scan, the cleanup and the large-file search, and all three announced
  "Progress", so there was no way to hear which one was moving. Disk Analyzer's drive-usage bar and
  Battery Health's charge bar are not progress at all but gauges, and announcing them as progress said
  something untrue — they now read "Drive space used" and "Battery charge level". Disk Analyzer's
  per-folder bar names its folder, and Speed Test's two bars name their engine, matching the Cancel
  buttons beside them. A test now fails the build if two bars on one page are announced the same way.

## [1.65.21] - 2026-08-24

If you drive Windows by voice, 41 buttons and tick boxes could not be pressed by saying what is
printed on them. They now answer to their own labels.

### Fixed
- **41 controls could not be activated by voice.** Windows Voice Access and similar tools listen for
  the words a control is announced by and match them against what you say. On these 41 the announced
  name had been reworded rather than extended — the button printed "Export CSV" but announced "Export
  logs to CSV", "Clear History" announced "Clear alert history", "Empty Recycle Bin" announced "Empty
  the Recycle Bin". Saying the printed label matched nothing, and the failure was silent: a screen
  reader read the announced wording out perfectly well, so nothing looked wrong unless you tried to
  speak to it. Each name now begins with the words printed on the control and adds any extra detail
  after them, which is what the other 209 controls already did.
- **The check meant to prevent this had been accepting them.** It asked only that the label's words
  appear in order somewhere in the announced name, which "Clear alert history" does. It now asks for
  them as one unbroken phrase, so a name may still add detail but can no longer reword the label.

## [1.65.20] - 2026-08-24

Two fixes for anyone using SysManager with a screen reader. In Startup Manager, every row's "Open"
button announced just the word "Open", so there was no way to hear which program you were about to
open the folder for. In App Blocker, the tick box on each row announced nothing at all.

### Fixed
- **Startup Manager's per-row "Open" button now says which program it opens.** The button carries the
  same label on every row, so a screen reader read out "Open" and nothing else — with dozens of
  startup entries listed, that is a button you cannot safely press. It now announces "Open file
  location for <program>", matching the on/off switch in the same row, which already named its entry.
- **App Blocker's row tick boxes were announced as unlabelled.** The name was set on the column rather
  than on the tick box itself. A column is a definition rather than something on screen, so the label
  never reached the tick box that gets created for each row, and a screen reader had nothing to read.
  Each one now announces "Select <program>". Six other tabs with the same kind of tick-box column were
  already doing this correctly; App Blocker was the one that had been missed, and a test now fails the
  build if a row control is unlabelled, or is labelled identically on every row.

## [1.65.19] - 2026-08-18

A fix for the Duplicate Files finder: a stray minus sign or an enormous number typed into the
"minimum size" box could quietly turn the filter inside out and make it scan every file instead of
only the large ones.

### Fixed
- **The "minimum size (KB)" box in Duplicate Files could invert its own filter.** The scan keeps a
  file when its size is at least the number you type. That number is multiplied by 1024 to get bytes,
  and the box accepted anything — so a leading minus, or a value large enough to overflow when
  multiplied, produced a NEGATIVE threshold. Every file is larger than a negative size, so "only files
  above X" silently became "scan and hash every file in the folder" — the opposite of what you asked,
  and much slower. The typed value is now bounded before it is used, so the filter can only ever mean
  what it says.

## [1.65.18] - 2026-08-18

Volume Control now notices audio devices you plug in while it is open. Before, the "play on" list was read
once when you first opened the tab and never again, so a headset connected afterwards simply never showed up.

### Fixed
- **A headset or speaker plugged in after opening Volume Control never appeared.** The list of output
  devices was read a single time, when the tab first loaded, and each app's "play on" dropdown got its own
  private copy of that list — so even a refresh would not have reached a dropdown that already existed. The
  tab stays loaded for as long as the app runs, which meant the only way to see a new device was to restart
  SysManager. The list is now re-read every ten seconds and every dropdown shares it, so a device appears on
  its own. Ten seconds rather than every second because asking Windows for the device list is expensive, and
  plugging something in is not something you do several times a second.
- **Changing your default output device could make each app's dropdown forget where you sent it.** Refreshing
  the list replaces its contents, and Windows reports the default flag as part of each device — so switching
  the default made every entry count as different, and the dropdown lost the app you had routed. Each
  selection is now re-matched by device identity after a refresh.

## [1.65.17] - 2026-08-18

A safety fix for how SysManager saves its own small settings and history files. Under a specific bit of
bad timing, a setting you changed could quietly fail to save — no error, it just would not stick. Most
people would never have hit it, but "most" is not "none", and a save that loses your data without saying
so is exactly the kind of thing this app must never do.

### Fixed
- **A setting or history entry could silently fail to save if two saves of the same file overlapped.**
  SysManager writes these files safely — to a temporary file first, then swaps it into place, so a crash
  mid-write can never leave a half-written file. But every save of a given file used the same fixed name
  for that temporary file, and cleaned it up afterwards. If two saves of the same file ran at once — for
  example the About screen recording its update-check time in the background while you toggled the
  "check for updates" box — one could delete the other's temporary file mid-swap, and both saves were
  lost with nothing written and nothing reported. Each save now uses a temporary name only it knows, so
  overlapping saves can no longer interfere; the last one simply wins. Applied to the shared save helper
  and to the two Hosts-file writers, which had the same pattern.

## [1.65.16] - 2026-08-18

Volume Control now tells you when a change did not take. Before, if Windows refused a volume, mute or
output-device change, the slider still moved to where you put it while the app kept playing at the old
setting, with nothing on screen to explain it.

### Fixed
- **A volume, mute or "play on" change could silently do nothing.** All three of those controls ask
  Windows to apply the change and get back a yes or no, and the app was ignoring the answer. When the
  answer was no — which happens for a second or so after an app stops playing, while the list of audio
  sessions is being rebuilt — the slider or switch stayed where you left it and the sound did not change.
  Dragging Chrome to 20% could leave it playing at 80% with the label reading 20%. All three now say so
  in the status line under the list, naming the app, and the app keeps the control where you put it
  rather than yanking it back under your cursor. A change that succeeds stays silent, and the once-a-second
  background refresh never reports anything, so the message only ever appears for something you did.

## [1.65.15] - 2026-08-17

The "time remaining" estimate now tells the truth when an operation stalls or when Windows reports its
progress in a burst — before, it could sit on "a few seconds" for the rest of a long repair. Speed Test also
stops leaving "done" printed under both of its cards after a finished test.

### Fixed
- **"Time remaining" got stuck on "a few seconds" for the rest of a scan.** `sfc` and DISM do not report
  progress on a timer — Windows flushes several lines at once, so two different percentages can arrive
  microseconds apart. The estimate divided the progress by that gap, concluded the repair was running
  thousands of times faster than it was, and needed roughly thirty more updates to recover — more than a
  scan produces. A System File Checker run with a quarter of an hour left announced "a few seconds" and
  never corrected itself. Progress reported faster than four times a second is now measured over a
  slightly longer window instead of being taken at face value, and nothing is thrown away in the process.
- **An operation that stopped for ten minutes was still described as nearly finished.** When progress
  resumed after a long pause, the new — and only honest — measurement was outweighed by the pace from
  before the pause, so a job with hours left reported about two minutes. Each measurement now counts for
  as much time as it actually covers, which makes the one spanning the pause the one that decides.
- **Past its own estimate, the app kept promising "a few seconds".** The countdown stopped at zero and
  stayed there, so an operation that overran and then hung looked identical to one about to finish: an
  estimate of a minute and forty seconds read "a few seconds" for the following twenty-five minutes. Well
  past its estimate the app now says "taking longer than expected", which is what it actually knows.
- **Speed Test left "done" under both cards after a test finished.** The estimate line was only cleared
  when the next test started, and both the Ookla and HTTP cards share it — so a finished HTTP test labelled
  the Ookla card too, and a cancelled one stranded "a few seconds" there until the next run.
- **Precise bandwidth mode could reset an app's session total to a few kilobytes.** A program's counter
  became visible a fraction before it was stamped with the time, and a reading taken in that window treated
  it as ten minutes idle and dropped it. The counter restarted from zero on the app's next packet, so the
  running total for something downloading continuously could fall back to almost nothing.

## [1.65.14] - 2026-08-17

Turning off a startup program could fail on a PC where nothing had ever been turned off before — which is
most PCs. That is fixed, along with two internal checks that were quietly passing when they should have
been failing.

### Fixed
- **"Disable" failed with a confusing error on a clean machine.** Windows keeps the on/off state of startup
  programs in a list it only creates the first time something is disabled — through this app, Task Manager,
  or Settings. On a PC where that had never happened the list does not exist, and SysManager reported
  "StartupApproved key not found" and refused, rather than creating the list the way Windows itself does.
  So the feature failed on exactly the machines most likely to need it, for every kind of startup entry and
  not only the 32-bit ones added in 1.65.10. The list is now created when it is missing. If the write is
  genuinely refused — the all-users entries need administrator — the message now says that instead of
  blaming a missing key.

## [1.65.13] - 2026-08-17

Three things an audit of the last few releases turned up. Clearing your speed-test history could say it
worked when it had not, the precise bandwidth readings could show a nonsense spike if your PC corrected
its clock, and the Task Scheduler's two "run time" columns looked broken because they only fill in when
you click a row. The version history in this file was also missing three releases' notes.

### Fixed
- **"Clear history" could report success after failing.** Deleting your saved speed-test results rewrites
  a file, and if that write failed the app logged it quietly and emptied the list on screen anyway — after
  a dialog that told you the data was gone for good. It came back on the next launch, with nothing to say
  which was real. This is the same fault fixed for *saving* a result in 1.65.10; clearing was missed at the
  time, and it is the worse half, because you have just confirmed the data should be destroyed. The rows now
  stay on screen with an explanation when the file cannot be written.
- **Precise bandwidth could show an absurd spike after a clock correction.** The per-app readings are
  worked out by dividing bytes by elapsed time, and elapsed time was measured with the wall clock. When
  Windows corrects the clock backwards — a routine time sync — that measurement went negative, got clamped
  to a single millisecond, and every rate on that reading was multiplied by roughly a thousand. A large
  forward correction had the opposite effect: every program was treated as idle and dropped from the list,
  losing its session totals. Both are gone: the timing now uses a clock that cannot run backwards.
- **Task Scheduler's "Last run" and "Next run" showed a dash on every row.** Both times come from a
  separate lookup that only runs for the row you select, so the columns sat empty until clicked and read as
  missing data. They now say "(on select)" in the heading, which is what actually happens — fetching them
  for every task up front would mean one lookup per scheduled task, hundreds on a normal PC. The publisher
  tooltip on "What it does" is also reachable now on tasks that have no description, which is exactly where
  it was the only information available.
- **Three releases had no entry in this file.** The notes for 1.65.7, 1.65.8 and 1.65.9 were all welded
  under the 1.65.10 heading, so this file jumped from 1.65.10 straight to 1.65.6 while carrying five
  separate sets of fixes under one version. Since the release workflow copies each entry into the download
  page and the announcement, three releases published nothing describing themselves. Each now has its own
  entry, 1.56.7 (tagged but never published) is recorded too, and a check now fails the build if a version
  between two documented ones is ever missing again.

## [1.65.12] - 2026-08-17

The Bandwidth Monitor's precise mode — the one that needs administrator — was doing its measuring on the
same thread that draws the window, and the amount of work grew the longer you left the tab open. Both are
fixed, so it now behaves like the ordinary mode already did.

### Fixed
- **Precise bandwidth mode stuttered, and got worse the longer it ran.** The 1.61.9 release fixed this
  stutter for the ordinary mode and stated that precise mode was never affected. That was wrong: precise
  mode did all of its per-second work on the thread that draws the window. Worse, that work kept growing —
  it re-sorted every program that had used the network since you opened the tab, so an installer that ran
  once in the morning was still being sorted at five o'clock. Programs that have sent nothing for ten
  minutes are now dropped from that list, and the measuring happens on a background thread for both modes,
  so the cost no longer depends on how long the tab has been open.

## [1.65.11] - 2026-08-17

The Volume Control tab is smoother. The moving bars beside each app were being measured in a way that made
the whole window do extra work twenty times a second, which is exactly why the sliders felt sticky while
you dragged them.

### Fixed
- **Volume Control's level bars made the window stutter.** The bars beside each app refresh twenty times a
  second, and each refresh asked Windows for one app's level at a time — a separate request per app, all of
  them on the same thread that draws the window. With ten apps playing that was over two hundred requests a
  second competing with the drawing itself, and once a second one of them had to queue behind the tab's own
  refresh of the app list. The result was bars that jittered instead of moving smoothly, and sliders that
  lagged behind the mouse. The levels are now read for every app in a single request, and that request runs
  on a background thread — the window only receives the finished numbers, and the tab goes completely idle
  the moment you switch away from it. Present since the tab shipped in 1.52.38; the same defect was fixed
  for the Bandwidth Monitor in 1.61.9, at a twentieth of this cadence.

## [1.65.10] - 2026-08-14

Two things that were quietly telling you the wrong thing. A speed test result could fail to save without
saying so, and the "time remaining" on long jobs could climb upward while the bar stood completely still.

Note on versions: the 1.65.9 tag exists but no download was ever published for it — its release build
stopped on the very save bug fixed below. Its own entry is kept for the record; everything that entry
describes reached users here.

### Fixed
- **A speed test result could vanish without telling you.** Saving a result to history writes a file, and
  if that write failed — a moment of disk contention, a permissions problem — the app noted it in the
  diagnostic log and then carried on exactly as if it had worked. The reading stayed on screen, so
  nothing looked wrong until you came back later and found a gap in your history. Now a failed save says
  so on the Speed Test tab, and the reading still stays visible for the session so you do not lose the
  number you just waited for.
- **"Time remaining" grew while progress stood still.** The estimate was worked out from the average
  speed since the start, and recalculated only when a job reported new progress. Long steps report the
  same percentage repeatedly, and each of those reports made the estimate *bigger*: measured on a job
  stuck at 12%, it climbed from about one minute to about thirty-seven while nothing moved. Between
  reports it froze instead, so a stalled job showed a confident "10 seconds left" for minutes. And a slow
  first step could show "about 1 hour 39 minutes" for something that finished in ninety seconds.
  The estimate now follows how fast progress is *currently* moving, counts down on its own while a job is
  quiet, and says "calculating…" rather than guessing from the first few percent.
## [1.65.9] - 2026-08-14

Startup Manager was missing a whole class of programs that start with Windows — anything installed by
a 32-bit installer, which on a typical PC means a VPN client, a printer helper or an older updater.
Those now appear alongside everything else, and can be turned off from here like the rest. Task
Scheduler also shows what each task is for and when it will run next, instead of only when it last ran.

### Fixed
- **Startup Manager did not list programs installed by 32-bit installers.** 64-bit Windows keeps the
  startup list of a 32-bit program in a separate place, and the app only ever read the main one. So an
  application that genuinely runs at every boot simply was not in the list — and since the point of the
  tab is to tell you what starts with Windows, its answer was incomplete without saying so. Windows'
  own Task Manager shows these, so anyone comparing the two would have seen the gap. They are now
  listed, and disabling one writes to the place Windows actually reads for that kind of entry, so
  "Disabled" means disabled rather than looking disabled while the program keeps starting.

### Added
- **Task Scheduler now shows what a task is for, and when it runs next.** The tab was already asking
  Windows for each task's description, who created it, and its next scheduled run — on every single
  scan — and then displaying none of it. "Next run" is now a column beside "Last run", there is a
  "What it does" column with Windows' own description, and hovering it shows which publisher created
  the task, which is also what decides whether it is labelled System, Telemetry or Third-party. No
  extra work is done to collect any of this; it was being fetched and thrown away.


## [1.65.8] - 2026-08-14

If you use one of the six light colour themes, the lines on the Ping, Bandwidth Monitor and Resource
History graphs were washed out to the point of being invisible — a pale mint or pale yellow line on a
white card. The graphs read the same way on light themes as they always have on dark ones now.

### Fixed
- **Graph lines were nearly invisible on the light themes.** The colours the three charts draw with
  were picked for the dark themes, where a bright mint or sky blue stands out against a near-black
  card. On the six light themes the same colours sit on a near-white card, and several of them all
  but disappeared: the Ping graph's mint line measured a contrast of 1.03:1 against its own card,
  where 1.00 means "the same colour as the background". Across the three charts, 80 of the 180
  colour-and-theme combinations were below the 3:1 that an accessibility guideline asks of any line
  a reader has to follow (WCAG 2.2, Non-text Contrast). Each line is now darkened just enough to
  clear that bar when the theme it is drawn on needs it. Only the brightness changes, never the hue,
  so blue is still CPU and purple is still memory; the dark themes already passed and come through
  untouched, byte for byte.

## [1.65.7] - 2026-08-14

The diagnostic log no longer fills up with the same repeated line. If you ever need to send a log to
report a problem, what is in it is now actually about your problem — before, roughly two lines every
second were the app re-listing the same temperature sensors, which pushed everything else out.

### Fixed
- **The log recorded the same sensor list every two seconds, forever.** Each time the Dashboard
  refreshed temperatures — every two seconds while the app is open — it wrote one line per piece of
  hardware describing how many temperature sensors that hardware has. That list never changes while
  the app is running, so on a typical machine it was four identical lines a second time, and again,
  and again. The log keeps a bounded amount of history, so this steadily evicted the entries that
  would actually explain a fault: when a release check dumped the last 40 lines, all 40 were this one
  message. The sensor list is now recorded once per run, where it is genuinely useful, and the
  repetition is gone.

## [1.65.6] - 2026-08-14

If you use the keyboard instead of the mouse, you can now see where you are. Pressing Tab moves a
visible outline from control to control; before this, the outline was drawn in the app's purple on
top of purple buttons, so it was completely invisible on the most important buttons — and checkboxes,
filter chips and table cells had no outline at all.

### Fixed
- **The keyboard outline was invisible on every colour theme.** The outline was drawn in the theme's
  own accent colour, which is also what fills the main action buttons — purple on purple, measured at
  1.00:1 contrast, i.e. nothing to see. The Delete buttons were the same story in red. It also fell
  below the readable minimum on the Sky Breeze, Warm Sand and Mint Fresh themes even for ordinary
  grey buttons. The outline is now two thin lines of opposite shade, a light one and a dark one, so
  one of them always stands out whatever it is drawn on — measured across all 12 themes, the worst
  case is now 4.5 times the required contrast instead of none.
- **Checkboxes, filter chips, mode buttons and table cells had no outline at all.** Space still
  ticked a checkbox and the arrow keys still moved through a table, so a keyboard user could change a
  setting or select a row without ever seeing which one they were on — worst on the pages that are a
  full column of checkboxes (Privacy, Debloater, Tweaks). All of them now show the same outline as
  every other control.

## [1.65.5] - 2026-08-14

Buttons and checkboxes are now read out with the words actually printed on them. If you use Windows Speech Recognition or a screen reader, 36 controls used to be announced with different wording than what you could see — so saying the label out loud did not press the button.

### Fixed
- **36 controls said one thing and were announced as another.** A button reading "Clean TEMP" was announced as "Clean temporary files", "Ask a question" as "Open GitHub Discussions", "Enable 0.5 ms timer" as "Enable high-resolution timer". Voice control matches what is written on screen, so none of those could be activated by saying the visible label, and a screen reader described them differently than a sighted person would. Each name now begins with the words on the control and keeps the longer explanation after it, so both work.

## [1.65.4] - 2026-08-13

The shield next to "Running as administrator" is now a proper icon instead of a colourful emoji, so it matches the rest of the app on all 27 pages that show it.

### Fixed
- **The admin shield was drawn as an emoji.** On every page that can run something as administrator, the banner you see once elevated used an emoji character with no icon font — so Windows substituted its own multi-colour emoji. The banner directly above it, before you elevate, already used the app's icon font, which meant the same banner drew its icon two different ways depending on state. All 27 pages now use the same shield icon.

## [1.65.3] - 2026-08-13

The app does a little less work before its window appears. Four of the Network tabs were being built at launch even if you never opened them, and one of them read a file from disk every single time.

### Fixed
- **Ping, Traceroute, Speed Test and Network Repair were prepared at every launch.** Every other tab waits until you open it — that change was made precisely so starting the app does not run forty tabs' worth of setup first. These four had been left out, and Speed Test's setup reads your saved speed-test history from disk, so that read happened on every start whether or not you ever visited the tab. They now load on first open like the rest.
- **Each of those four tabs was being created twice.** The app builds its parts through a shared registry, but these four were also constructed by hand next to it — so two copies of each existed and the registered ones were never used. There is now one of each.

## [1.65.2] - 2026-08-13

Sizes and counts now use the same decimal point everywhere in the app. If your Windows is set to a language that writes numbers with a comma — Romanian, German, French, Finnish and most of Europe — the same screen could show "1.5 GB" in one place and "1,5 GB" right next to it.

### Fixed
- **Two different decimal marks on one screen.** A previous release made the shared size and speed formatter always use a point, but the numbers written directly into sentences still followed the Windows regional setting. On a comma locale that produced a visible mismatch — a size from one code path and a boot time from another, side by side, punctuated differently. Boot times, disk sizes, memory figures, download speeds, VRAM, drive temperatures, the tray tooltip and the exported system report all now match.
- **Thousands were grouped differently depending on the language.** A file count of 1610 was shown as "1,610", "1.610" or "1 610" depending on the regional setting, and the space variant is a non-breaking space that does not survive a copy-paste. Counts in Deep Cleanup, Disk Analyzer, Duplicate Finder, Browser Cleaner, the Shortcut Cleaner, the Uninstaller and the File Shredder are now consistent, including in the activity log they write.

## [1.65.1] - 2026-08-13

Dates and times now look the same on every PC. If your Windows is set to a language whose calendar is not the Western one — Thai or Arabic, for example — the app was printing the wrong year, and in a few places the wrong month, in lists, exported reports, and even in the names of the files it saved.

### Fixed
- **Dates were wrong, not just differently formatted, on some regional settings.** Where the app shows a date in a fixed layout — the Boot Analyzer, Privacy Monitor, Restore Points, Disk Analyzer, the bandwidth and resource charts, the crash notice, exported reports — it was letting Windows' regional setting pick the calendar. On a Thai setting, 4 August 2026 was shown as 2569-08-04; on an Arabic (Saudi) setting, as 1448-02-21, with the month changed too. On a Finnish setting the time lost its colon (13.45), which also broke sorting a column of timestamps. Every one of these now prints the same on every machine.
- **Saved files could be named with the wrong date.** Exported reports, log CSVs, resource-history CSVs, saved settings profiles, and the automatic registry backup taken before a context-menu change all put a timestamp in the filename. On the regional settings above, that timestamp was a different year, so the files sorted wrongly and did not match the date inside them.
- **The Dashboard could report no critical events on a PC that had them.** Its seven-day event-log scan builds a date filter that Windows parses literally. Under a non-Western calendar that filter asked for a year that has not happened, so the query came back empty and the Dashboard said nothing was wrong.
- **"Updates paused until …" could state a date that was not the pause date.** The Windows Update panel formatted that promise the same way.

## [1.65.0] - 2026-08-13

System Health now shows which slot each memory module is in, and whether your RAM is actually
running at the speed it is rated for.

### Added
- **A "Running at (MHz)" column for your memory modules.** Windows reports both the speed a module is rated for and the speed it is really running at. If the second is lower — which usually means XMP/EXPO is switched off in the BIOS, so RAM you paid extra for is running slow — the figure is highlighted and hovering it explains why. When the two match, nothing draws attention to itself. The exported system report mentions the running speed only when it differs from the rating.

### Fixed
- **The "Slot" column was not showing the slot.** It showed the memory bank instead, which Windows often reports as just "BANK 0" or leaves blank. It now shows the slot name printed on the motherboard — "DIMM0", "ChannelA-DIMM1" — which is what you need if you are opening the case to find a specific module. The app was already reading the better name; it just went to a copy of the memory-module record that nothing on screen could reach.

---

## [1.64.18] - 2026-08-13

The Performance tab now names your power plan correctly, including on non-English Windows and when
you have renamed a plan yourself.

### Fixed
- **The Performance tab could claim the wrong power plan was active.** It decided which plan you were on by looking at the plan’s NAME before its identity. Two consequences: if you had copied a plan and given it a name containing "Ultimate" — say "Ultimate Battery Saver", copied from Balanced — the tab announced "Active plan: Ultimate Performance" even though a normal balanced plan was running; and because Windows translates plan names, the genuine Ultimate Performance plan was never recognised on a Romanian, German, Spanish or French system, where it showed as "Performanță maximă" instead. The plan is now identified by the ID Windows assigns it, which is the same in every language and does not change when you rename a plan. The name is still used as a last resort, which is what correctly catches a copy of the Ultimate plan.
- **The same wrong check also decided which plan appeared selected** in the three plan options, so the selection could disagree with the plan actually in use.

---

## [1.64.17] - 2026-08-13

If the PC loses power or crashes while SysManager is saving, your settings and history survive
instead of coming back empty.

### Fixed
- **A crash or power cut while saving could silently wipe your history and presets.** Seventeen places wrote your data by emptying the file first and then filling it back in. Interrupt that — a power cut, a crash, a full disk — and the file on disk is neither the old version nor the new one. Worse, the app treated a file it could not read as "no data", quietly starting from scratch: you would open the app and find your activity history, speed-test history, volume presets, gaming profiles or saved settings simply gone, with nothing explaining why. Saves now write to a second file and swap it in as one step, so an interrupted save leaves the previous version completely intact. Affects the Activity log, Speed Test history, Volume presets, Gaming profiles, theme and appearance settings, the close-behaviour preference, the standby and update preferences, the Settings Watchdog baseline, the service-startup ledger, the performance snapshot used to undo tweaks, restored configuration backups, and the app-icon cache.

---

## [1.64.16] - 2026-08-13

File sizes now use the same decimal point as network speeds, everywhere in the app, whatever
language your Windows is set to.

### Fixed
- **Sizes and speeds disagreed about the decimal separator outside English.** File sizes were formatted using your Windows language settings while network speeds always used a dot, so on a Romanian, German, French or most other European system the same screen showed "1,5 GB" next to "12.4 Mbps". All 32 places that show a size now use a dot, matching the speeds. On an English system nothing changes.

### Changed
- **Internal: removed notification-suppression code that never did anything.** The shared list used by every refreshing table carried a flag meant to silence change notifications during a bulk refresh. It could never have fired — the refresh writes to the underlying list, which raises no notifications in the first place — so it read as protection while providing none. The list now also refuses, instead of quietly rewriting itself, if something tries to replace its contents from inside a change handler. No visible change; the refresh behaviour is identical.

---

## [1.64.15] - 2026-08-13

The File Shredder now tells you when a file you picked could not be added to the list, instead of
quietly leaving it out.

### Fixed
- **A file you picked could disappear from the shred list with nothing said.** If a file or folder could not be read — locked by another program, on a disconnected drive, or protected by permissions — it was written to a log file you never see and then left out of the list. Pick ten files, see eight, and nothing on screen explained the other two: it looked like the app had simply finished. The footer now names what could not be added and says plainly that it is **not** in the list, so you can retry or remove it rather than assuming it is queued for destruction.
- **A long message in the File Shredder footer was cut off instead of wrapping.** The footer laid its text out on a single unbounded line, so anything longer than the window ran off the right edge invisibly. It now wraps, which is what makes the message above readable when it names several files.

---

## [1.64.14] - 2026-08-13

The "Run as administrator" button now tells screen readers and voice control exactly what it says on
screen, on all 30 pages that offer it.

### Fixed
- **The "Run as administrator" button announced a different name than the one printed on it.** Thirty pages offer that button, and only fifteen of them announced it as "Run as administrator". Five said "Restart SysManager as administrator", four said "Relaunch as administrator", one said "Restart as administrator", and five said nothing at all. Anyone using a screen reader heard a verb that was not on the screen, and anyone using Windows Speech Recognition or Voice Access could say the words they could read and have nothing happen — on the one button that unlocks everything else. All thirty now say "Run as administrator", and a test fails the build if a new page drifts again.

---

## [1.64.13] - 2026-08-13

Pressing Enter on a confirmation prompt no longer approves it. Every "are you sure?" now starts on
the safe answer, so an accidental keypress cancels instead of going ahead.

### Fixed
- **A stray Enter could approve a confirmation you hadn't read.** Every prompt that asks you to confirm something — shredding files, ending a process, removing built-in apps, uninstalling, applying a preset — opened with **Yes** already selected. So pressing Enter, or pressing it a second time after the key that opened the prompt, went straight ahead with the action. All 76 of those prompts now open with **No** selected: Enter cancels, and choosing Yes still takes a single click or keypress. The same applies to the prompt you get when closing the window, which now starts on Cancel — its behaviour finally matches what it already promised, since Esc and the window's X have always meant "leave things alone".

---

## [1.64.12] - 2026-08-13

Internal tidy-up: removed three pieces of information the app tracked but could never truthfully
report. Nothing you see or do changes.

### Fixed
- **Removed a "blocked on" date that recorded when you opened the tab.** The App Blocker kept a timestamp for each blocked app, and it was set at the moment the list was read rather than when the block was applied — so it would have shown "just now" for a block made last month. Windows records no date for these blocks, so there is nothing to read; rather than display a number that looks meaningful and isn't, the field is gone. It was never shown on screen, so nothing is missing from the tab.
- **Removed two fields that could only ever have been blank.** The App Blocker tracked a full file path for each blocked app, but blocks work by program name — Windows never records where the file was, which is also why a block keeps working if you move the file. The Process Manager tracked a process owner that nothing ever filled in. Both had a passing test asserting they were empty, which is how they survived. Neither reached the screen.

---

## [1.64.11] - 2026-08-13

A duplicate scan now shows which file it is reading, so a long scan no longer looks like it might
have frozen.

### Fixed
- **The Duplicate Finder never showed which file it was working on.** During a scan the status line counted files found and hashed, but never named the file being read — even though the scan reported it on every single update and SysManager stored it. On a big folder that made a slow scan indistinguishable from a stuck one: the numbers would crawl while one enormous file, or one file on a slow network drive, was being hashed, and there was no way to tell which. The file name now appears at the end of the status line, and hovering over it shows the full folder path.

---

## [1.64.10] - 2026-08-13

Process Manager now tells you in plain English what each process is, using the description database
it already shipped with but never showed you.

### Fixed
- **The Process Manager knew what each process was and didn't tell you.** SysManager ships a database of 108 common Windows processes and popular applications, each with a plain-English one-liner ("Windows service host — runs background services grouped by function") and a category. It was being looked up for every process, and the search box even matched against it — you could type "browser" and get results — but the list never displayed any of it. What you saw under each process name was the technical description the program file carries, which is the same wording Task Manager already gives you. Worse, Windows refuses to hand over that technical description for its own system processes unless SysManager is running as administrator, so for exactly the processes people are most unsure about — `svchost`, `csrss`, `lsass` — the line was simply blank, while the plain-English explanation sat unused. The description now appears under each process name, falling back to the technical one for anything the database doesn't know, and the category has its own sortable column. A new automatic check makes sure a field the search can match can never again be invisible.

---

## [1.64.9] - 2026-08-12

Closing the Performance Mode tab in the middle of applying a tweak can no longer throw away the
saved settings that "Restore all" needs. Closing the Bandwidth Monitor mid-load no longer risks an
error on the way out.

### Fixed
- **Closing Performance Mode while a change was being applied could leave that change with nothing to undo it.** Before touching any Windows setting, SysManager saves a snapshot of how things were, so "Restore all" can put them back. Closing the tab clears that snapshot — but it did so without waiting for a change that was still in progress, so in the moment between "saving your original settings" and "applying the new one", closing the tab could take the snapshot away from the very operation that was relying on it. The result would be a setting changed with no recorded way back. The tab now waits for any in-progress change before clearing anything, and an attempt to apply a change to a tab that is already closing is refused outright instead of going ahead unprotected.
- **Closing a tab mid-operation could log an error on an otherwise clean exit.** Eleven parts of the app take an internal turn-taking lock so two operations cannot run over each other. Six of them — the Bandwidth Monitor, Performance Mode, and the DNS, network-repair, speed-test-history and system-fix helpers — released that lock on shutdown without checking whether it had already been packed away, which reports an error during what is otherwise a normal exit. The other five already did this correctly; all eleven now behave the same way, and a new automatic check stops a twelfth from being added with the old pattern.

---

## [1.64.8] - 2026-08-12

"Ask a question" now takes you to the questions area instead of the release-announcements wall.

### Fixed
- **Every "ask a question" link dropped you into a page of release notes.** The Ask a question button on the About tab, the link in the README, the one in SUPPORT.md, and the "Ask a question / general discussion" entry you see when you click New issue on GitHub all opened the top of Discussions. SysManager posts an announcement there for every release, and there have been hundreds — so the page you arrived at was a wall of changelogs, with the actual Q&A area a separate category you had to spot for yourself. All four links now open Q&A directly, so you land on the box where you type your question. The GitHub one mattered most: SysManager deliberately has no blank-issue option, so that chooser is what everyone sees first.

---

## [1.64.7] - 2026-08-12

Internal hardening in the "go back to the previous version" check. Nothing you do with SysManager changes.

### Fixed
- **The rollback check could keep a saved version locked if reading it failed unexpectedly.** Before starting your previous version, SysManager opens the saved copy, locks it so nothing can swap it, and checks it against a recorded checksum. Every ordinary outcome — checksum missing, unreadable, or not matching — released that lock correctly. But if reading the file failed in an unusual way, the lock could be left in place until SysManager closed, and while it was held, no future update could refresh your saved copy — so you could quietly end up with an out-of-date version to go back to. The lock is now released the same way on every possible outcome, including ones that have not happened. Found by the project's own automated security scan flagging code added in 1.64.2; nobody reported it, and no user is known to have hit it.

---

## [1.64.6] - 2026-08-12

Closing a tab, or closing SysManager, now actually stops what that tab was doing. Scans, cleanups and
file shredding used to keep running in the background after you closed them.

### Fixed
- **Work carried on after you closed the tab or the app.** Every tab that runs something you can wait on — a scan, a cleanup, a speed test, shredding files, the one-click Tune-Up — is supposed to stop when you close it. Behind the scenes, SysManager was releasing the "stop" signal without ever giving it, so 28 of those operations across 23 tabs simply carried on: file shredding kept overwriting files after you closed the window, Deep Cleanup and the browser cleaner kept deleting, a speed test kept using your connection, and the Tune-Up kept changing settings. Nothing was corrupted by this — the work was real work you had started — but it continued when you had every reason to think it had stopped, and it kept your PC busy after SysManager appeared to be gone. All of them now stop when you close the tab or quit, exactly as the Cancel button already did. A new automatic check makes sure a future tab cannot be added with the same mistake.

---

## [1.64.5] - 2026-08-12

A right-click menu entry with an unusual name can no longer make SysManager write a file where it should
not. Nothing about using the Context Menu tab changes.

### Security
- **A crafted right-click menu entry could redirect SysManager's backup file.** Before turning a right-click menu entry on or off, SysManager saves a copy of the relevant registry key so the change can be undone. It built the command for that backup using the entry's own name — and a program running on your PC can create an entry whose name contains a quotation mark, which broke the command apart and let the rest of the name decide where the backup was written. Since SysManager is often started as administrator, that file could have landed somewhere it had no business being. SysManager now checks the name first and skips the backup with a note in its log if the name cannot be used safely, instead of running the command anyway. Ordinary entry names — spaces, dashes, brackets, symbols — are unaffected, and the same check was already being applied elsewhere in SysManager for the same reason; this was the one place it had been missed.

---

## [1.64.4] - 2026-08-12

Choosing "close SysManager" now actually closes it. It used to leave SysManager running invisibly, with
no window and no icon next to the clock to get it back.

### Fixed
- **"Close SysManager" left it running with no way to see it or stop it.** When you press X, SysManager asks once whether to keep running next to the clock or close completely — and remembers your answer. Choosing to close completely made the window disappear, but the program kept running: no window, and no icon next to the clock either, so there was nothing to click. Worse, it then blocked itself from starting again — launching SysManager would hand over to the invisible copy and quit immediately, so it looked like the app simply would not open. Because your answer is remembered, this happened every time, and the only way out was Task Manager. Closing completely now really does end the program. Keeping it next to the clock, and pressing Cancel to go back, both work exactly as before.

---

## [1.64.3] - 2026-08-12

The Standby List Cleaner used to keep checking your memory every two seconds for as long as SysManager
stayed open, even after you left the tab. It now stops when you are not looking at it.

### Fixed
- **The Standby List Cleaner kept working in the background after you left it.** Opening that tab once started a check every two seconds — and it never stopped: not when you switched to another tab, not when you minimised the window, and not when you closed SysManager to the notification area. On a laptop that is wasted battery for a tab nobody is looking at, and if you had turned on automatic purging, that could run unattended from a hidden tab. The check now runs while the tab is open, and stops when it is not. **Automatic purging still works exactly as before** — if you have turned it on, SysManager keeps watching your free memory in the background, because that is the whole point of the setting; the check only stops when there is nothing it could act on. Every other tab that watches something live already worked this way; this one had been left out, and a new automatic check now makes sure a future tab cannot be missed the same way.

---

## [1.64.2] - 2026-08-12

Two ways the built-in updater could be tricked into damaging your PC are now closed off. Nothing about
normal updating changes.

### Security
- **SysManager could be told to overwrite the wrong file.** When SysManager installs an update, it starts the newly downloaded copy with a hidden instruction naming the file to replace. That instruction was only checked for the right shape, not for what it actually pointed at — so anyone who could start SysManager with a crafted command line (a shortcut, a scheduled task, a script someone talked you into running) could name any file on your PC and have SysManager write over it. If you were running SysManager as administrator at the time, that meant almost any file, including ones Windows needs. SysManager now refuses unless the file it is told to replace really is an existing copy of SysManager itself, outside protected system folders, and writes a note in its log saying why it refused. Normal updates are unaffected; they always pointed at the right file.
- **"Go back to the previous version" now checks the saved copy before starting it.** SysManager keeps one copy of your previous version so you can go back after a bad update, and that copy sits in your own user folder where other programs can also write. Starting it was previously based only on the file being there — so anything running on your PC could have swapped that copy for something else and had SysManager launch it, with administrator rights if SysManager was elevated. SysManager now records a checksum when it saves the copy and verifies it right before starting it, keeping the file locked in between so it cannot be swapped in the meantime. If the copy has changed, or was saved by a version before this one and has no checksum, the button is not offered and you are told why instead of it happening silently. Installing an update already worked this way; going back now does too.

---

## [1.64.1] - 2026-08-12

A rare crash that could happen if you closed the window while the Resource History tab was loading
its charts.

### Fixed
- **Closing the window while Resource History was loading could crash the app.** The tab reads your recorded history from disk in the background and then redraws five chart lines. If you closed SysManager during that read — which is easy to hit, because the read starts on its own when you open the tab, when you change the time range, and when you press Refresh — the finished read tried to redraw charts whose drawing resources had already been released during shutdown. There were three separate ways it could fail, including one that turned a clean shutdown into an error. The loading path now checks whether the tab is still there before touching anything and stops quietly if it is not. Same kind of problem as the Bandwidth Monitor crash fixed in 1.63.1: after that fix every other tab was checked for the same pattern, and this was the one that had it. Only the Resource History tab was affected, and only at the moment of closing.

---

## [1.64.0] - 2026-08-12

There is now a way to report a problem from inside the app, instead of being told to "report it on
GitHub" with no link to get there.

### Added
- **"Report a problem" and "Ask a question" in the About tab.** Preview-feature banners asked you to report anything unexpected on GitHub, but nothing in the app took you there — so a problem usually went unreported. The About tab now has a "Report a problem" button that opens the bug-report form on GitHub with your SysManager version and whether you were running as administrator already filled in (the two details reports most often leave out), and an "Ask a question" button that opens Discussions for anything that is not a bug. The Preview banner now points at that button instead of a dead end. A browser tab opens only when you press one of the buttons — nothing is sent automatically.

---

## [1.63.1] - 2026-08-12

A rare crash that could happen if you closed the window at the exact moment the Bandwidth Monitor was
taking a reading.

### Fixed
- **Closing the window while the Bandwidth Monitor was mid-measurement could crash the app.** In 1.61.9 the network measurement moved onto a background thread so the window stops stuttering. That introduced a small window: if you closed SysManager (or it minimised to tray during shutdown) in the fraction of a second while a measurement was in flight, the finished measurement tried to update a tab that had already been torn down — including its chart, whose drawing resources were already released — which could throw. The measurement now checks whether the tab is still alive before touching anything, and quietly stops if it is not. Only the Bandwidth Monitor tab was affected, and only at the moment of closing.

---

## [1.63.0] - 2026-08-12

The Startup Manager now tells you what each program is and whether it is safe to turn off, instead of
showing a raw file path you would have to decode.

### Added
- **Plain-language descriptions and a safety label in the Startup Manager.** The tab listed each startup program by name and its raw command line — a full file path with switches, like `"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe"` — and left you to work out whether turning it off was safe. It now shows a short description of what the program actually is (drawn from the same built-in database the Process Manager already uses) and a Safety chip: **Windows** for parts of the operating system, **Known app** for recognised software, or **Not recognised** for everything else. So you can tell "nvcontainer" (part of your graphics driver, leave it) from "Spotify" (safe to turn off) at a glance. Programs the database does not recognise keep showing their file path and get no safety label — the app never guesses that something is safe to disable. The full path is always available on hover.

---

## [1.62.1] - 2026-08-12

Several on-screen warnings and admin notices that were being cut off mid-sentence on a narrow window now
wrap and show in full.

### Fixed
- **14 warning and information messages could be truncated instead of wrapping.** Across 13 tabs, notices like the File Shredder's "SSD users: overwrite-based shredding is unreliable…" caveat, Windows Defender's Tamper Protection notice, and the "requires administrator" banners were laid out in a way that told them to wrap but never gave them a width to wrap into — so on a narrow or default-width window the text was cut off at the right edge rather than continuing on a second line. The three longest were clipped even at the default window size. They now wrap and show in full. This is a layout fix only; no wording changed.

---

## [1.62.0] - 2026-08-12

The Speed Test tab now tells you whether your speed is any good, instead of leaving you to work it
out from a number in Mbps.

### Added
- **A plain-English verdict on every speed test.** The tab measured your connection and then showed you "43.2 Mbps", which answers "what is my speed" and not the question most people open the tab with — "is that actually good?". Each result now comes with a short verdict saying what that speed is enough for in terms you can check against your own household: whether video calls and HD video will struggle, whether 4K on several devices at once is comfortable. The Ping tab next door has explained itself in plain English for a long time; this brings Speed Test in line with it, using the same layout so the two read as one app.
- **A comparison with your last test.** When there is an earlier result for the same test engine, the verdict adds one line putting the two side by side — "About the same as your last test (45 Mbps)" or "Noticeably slower than your last test (was 92 Mbps)". That is the difference between a number and evidence: it is what you can point at when calling your provider. Results have always been stored, so nothing new is saved to do this, and small run-to-run differences are treated as noise rather than reported as news — a speed test varies on an unchanged line.
- A slow result is never coloured as a fault. If you are on a modest plan, the app says the speed will limit some things; it does not tell you something is broken when nothing is.

---

## [1.61.11] - 2026-08-12

The Uninstaller tab no longer looks broken when you open it after granting administrator access
somewhere else — it now explains, in the neutral colour it should always have used, why uninstalling
waits for a normal window.

### Fixed
- **The Uninstaller told administrators to "reopen SysManager normally" with no way to do it, in the colour that everywhere else means "you can now do more".** Uninstalling is deliberately turned off while SysManager runs as administrator, so each app can raise its own permission prompt rather than inheriting ours. But this tab announced that in the same gold banner the other 30 tabs use to say "Running as administrator — you can now …", and the message was "Uninstall is disabled in administrator sessions. Reopen SysManager normally to continue." So after pressing the gold "Run as administrator" button on Quick Cleanup, you arrived here to a reassuring gold bar, a greyed-out Uninstall button, no explanation of why, and an instruction the app offers no button for. The banner is now the same neutral grey as the one directly above it, and says what is happening and why: uninstalling is off while running as administrator so each app can show you its own permission prompt, then close and reopen normally to uninstall. The restriction itself is unchanged — it is a safety property, not a bug.

---

## [1.61.10] - 2026-08-11

The Services tab can now actually filter by the things it always claimed to — including the gaming
recommendations, which are the reason the list is more useful than the one in Windows.

### Fixed
- **Five of the Services tab's nine filters had no button.** Filtering by status (Running / Stopped) and by gaming recommendation (Safe to disable / Keep enabled / Advanced) was fully written and working, but nothing on screen could select it — the only chips were Safe, Caution, Critical and All, so those five could be reached only from a debugger. The README promised both anyway: "filter by status (Running/Stopped) … gaming recommendation". All nine are now chips, grouped under Safety / Status / For gaming so the two different meanings do not get confused: safety answers "will turning this off break Windows", the gaming recommendation answers "is it worth turning off for games". Each chip shows how many services it matches, so you can tell before pressing it, and the row reflows on a narrow window instead of clipping.
- **The "N services (M running)" line could disagree with the list under it.** Those two numbers were recalculated only by a full refresh, while everything else on the tab recalculated on every search keystroke and every filter press. So after typing in the search box, the header still described the previous scan. Every count on the tab is now worked out in one place, at the same moment.

---

## [1.61.9] - 2026-08-11

The Bandwidth Monitor no longer makes the window stutter while it is open.

### Fixed
- **The Bandwidth Monitor made the whole window hitch about once a second.** The tab measures network activity every second, and all of that measuring was happening on the same thread that draws the window — listing every network adapter and reading its counters, asking Windows for the full table of open TCP and UDP connections, and looking up the name of each program holding one. On a PC with a few adapters (Wi-Fi plus Ethernet, or a VPN or virtual-machine adapter) or a browser holding dozens of connections, that is enough work to be visible: scrolling, dragging the window and switching the chart range all stuttered in step with the once-a-second refresh. The measurement now runs on a background thread, so only the finished numbers touch the window. Nothing about what the tab shows has changed, and the precise (administrator) mode was never affected — its work was already just arithmetic on counters collected in the background.
- **A related inefficiency in the same code.** The lookup that turns a process ID into a program name was cached, but the cache was emptied halfway through each refresh, so any program holding both a TCP and a UDP connection was looked up twice per second instead of once. The cache now lives for the whole refresh.

---

## [1.61.8] - 2026-08-11

Six places where SysManager either did not ask before doing something irreversible, or asked a
question it did not mean.

### Fixed
- **Deep Cleanup's confirmation said the Recycle Bin was not involved while it was about to empty it.** The prompt ended with "These files are removed directly, not sent to the Recycle Bin." That was meant to say the deletion is permanent, but it reads as a promise that your Recycle Bin is left alone — and "Recycle Bin (all drives)" is one of the categories, ticked automatically whenever the bin has anything in it. So the single dialog standing between you and an emptied Recycle Bin implied the opposite of what the Clean button would do. It now says plainly that the Recycle Bin will be emptied when that category is selected, and keeps the "cannot be recovered" warning when it is not.
- **"Upgrade selected" upgraded every ticked app without asking.** The same action on the Dashboard has always asked first. Now both do, and the App Updates prompt names the apps when there are five or fewer, since "3 apps" tells you less than which three. One prompt covers the whole batch, not one per app.
- **"Restore default" wiped your Windows Update settings without asking.** Of the three buttons in the update-timing panel, the two that *apply* a setting both asked for confirmation; the one that *discards* everything you had configured did not. It now asks, and the prompt shows the current deferral or pause so you can see what you are giving up.
- **"Create restore point" silently turned System Protection back on.** Windows cannot create a restore point while System Protection is off for the Windows drive, so SysManager quietly switched it on first. That is the right thing to do — but it is a lasting change that starts reserving disk space, and it happened with no mention anywhere. Both places that create a restore point (Restore Points and Performance Mode) now say so before doing it.
- **Saving a volume preset silently replaced an existing one.** Deleting a preset asked for confirmation because the change is written to disk immediately with no undo; saving over one destroys exactly the same data and did not ask. Now it does — matching names case-insensitively, since "gaming" and "Gaming" are the same preset — while saving under a new name is never interrupted.
- **A "cannot end this process" message was shown as a Yes/No question.** Trying to end a critical system process from the File Lock Detector opened a dialog with two buttons that did the same thing, and the answer was thrown away. It is now a plain notice with a single button. Dialogs that ask nothing should not look like questions; that habit is what teaches people to dismiss the ones that matter.

---

## [1.61.7] - 2026-08-11

The Settings Watchdog now shows what it is watching, instead of only telling you once something
has already changed.

### Fixed
- **Settings Watchdog never showed what it watches.** The tab monitors eight Windows settings that feature updates tend to reset — telemetry, the advertising ID, activity history, web search in Start, Widgets, Start-menu suggestions and two more — and it loaded that list every time you opened it, but nothing on screen ever displayed it. Until something actually drifted, all you saw was the intro sentence naming four of them as examples, then an empty panel. Being asked to trust a monitor without being told what it monitors is a fair thing to hesitate over, so the list is now on the page: every watched setting with its category, its value right now in the same plain English the change list uses ("Off", "Full", "Not set"), and the reason it is watched. Hovering a row also shows the exact registry location, so anyone who wants to check the claim can. The values refresh with the rest of the tab rather than being read once at startup, and a setting that has drifted is tinted here too — so this list and the changed-settings list above it can never appear to disagree about the same setting.

---

## [1.61.6] - 2026-08-11

Five buttons that were missing: the "row highlight" for System Logs and Services announced back in
May, a "What's new" link on About, a way to select a whole category in the App Installer, and a
"Check now" for the Windows Update module. System Logs also gets back the row hover every other
table in the app has.

### Fixed
- **"Row highlight" for System Logs and Services was announced but could not be used.** Both tabs were given the ability to mark a row in 0.40.0 (12 May 2026), and the release notes said so — but the buttons to do it were never added, and nothing on screen ever showed a mark, so there was no way to reach the feature at all. Both tabs now have a flag button on every row: click it to mark an event or a service, and the row keeps a soft amber tint so you can find it again after scrolling, searching, filtering or sorting. A "Clear marks" button appears next to the search box once anything is marked, showing how many there are, and it clears every one — including marks on rows the current filter is hiding, which would otherwise be impossible to find and unmark.
- **System Logs was the only table with no hover feedback.** Pointing at a row in System Logs did nothing, while every other table in the app tints the row under the mouse. The tab defined its own row styling that accidentally replaced the app-wide styling rather than building on it, and the hover was part of what it replaced. The hover is back, selection still looks the same, and both tables are now checked automatically so a future style edit cannot quietly drop it again.
- **Three more buttons that were written but never added to a screen.** Checking for the row highlight turned up the same omission elsewhere, so all of them are now reachable: **About** gets a **"What's new"** button beside "View license" that opens the changelog — previously the release notes could only be found by going to the repository by hand. The **App Installer** gets a **"Select …"** button, named after the chosen category, that ticks every app in it; before, installing a whole category meant filtering to it and pressing Select All, which also picked up anything the search box was narrowing to. **Windows Update** gets **"Check now"** next to the PSWindowsUpdate status, so you can confirm whether the module is installed instead of having to run Update History and read a failure message.
- **A check now prevents this from happening again.** The build fails if any button action exists in the code without something on screen able to trigger it. That is what let a feature be announced in the release notes while remaining impossible to use: nothing about an unused action looks broken to a compiler or to a test that calls it directly.

---

## [1.61.5] - 2026-08-11

Two places where SysManager told you something that was not true: the tray warning that a
perfectly healthy drive might be failing, and Cleanup reporting that it emptied a Recycle Bin
Windows had refused to empty.

### Fixed
- **The tray could warn that a healthy drive was failing.** SysManager reads disk health from two different places in Windows depending on what the machine offers, and the second one describes a good drive as "OK" rather than "Healthy". The background check only accepted the exact word "Healthy", so on those machines it treated every drive as a problem and showed "Disk Health Warning — reports status: OK. Consider backing up important data." every four hours: a warning that contradicts itself in its own sentence, over nothing. It now recognises what each source actually says, warns for the values that genuinely mean trouble, and stays quiet when it cannot read the status at all — inventing urgency from a value it could not read would be worse than saying nothing. Drives that really are failing still warn, including the abbreviated wording ("Pred Fail", "NonRecover") that Windows uses in the second source.
- **Emptying the Recycle Bin reported success even when Windows refused.** Windows reports a refused empty by returning a failure code rather than by raising an error, and Cleanup ignored that code — so if the Recycle Bin was open in Explorer or a file inside it was still in use, the status read "Done" and a "Operation finished successfully" notification appeared while the bin was still full. It now says plainly that Windows would not empty it, suggests why, and records it in the log. Deep Cleanup and the One-Click Tune-Up already handled this correctly; Cleanup was the one place that did not.

---

## [1.61.4] - 2026-08-11

The Tune-Up card no longer shows a drive as plainly "Healthy" while the headline above it warns
you about that same drive.

### Fixed
- **Quick Tune-Up showed the wrong wording for each drive.** The per-drive line in the Tune-Up result showed a bare status word — "Healthy", "Warning" — instead of the full plain-English summary the rest of the app shows, such as "Healthy — 38 °C · wear 2% · 4210 h on". On a drive that genuinely needed attention that produced an amber "1 recommendation" heading sitting directly above a row reading simply "Healthy", so the card contradicted itself. It now shows the same sentence everywhere, which is also the text the colour beside it was always derived from.
- **Running the project's own tests rewrote your "check for updates at startup" setting.** This affects only people who build and test SysManager themselves, not normal use. The About tab remembers whether you want the startup version check, and the tests had no way to point that at a scratch folder — so every test that built the About tab wrote to your real setting file. That is the same problem fixed for four other files in 1.60.1, 1.61.1 and 1.61.2, arriving through the one route those fixes did not cover. Fixed the same way, plus a test that fails if it ever comes back.

## [1.61.3] - 2026-08-11

Quick Tune-Up says "All good" when your PC actually is, ending a Windows security or update
process now warns you about what really happens, and turning a service back on asks first and
tells you what it is about to set.

### Fixed
- **Quick Tune-Up counted every healthy disk as a problem.** If you have two drives and nothing wrong with your PC — no broken shortcuts, plenty of free memory, recently restarted — the Tune-Up result still opened with **"2 recommendations"** in amber, showed the "what to look at" section, and then listed those same two drives with their own verdict reading *"Healthy — 38 °C"*. The headline contradicted the detail directly underneath it. The check was comparing the disk's verdict against the word "Healthy" on its own, but the app never writes that exact wording — it always adds the temperature and wear, or a full stop. So every healthy drive matched "not healthy". It now decides from the same green/amber/red signal the disk row itself uses, so a healthy PC reads "All good" and a drive that genuinely needs attention still counts.
- **"Enable" changed a Windows service without asking.** On the Services tab, Start, Stop and Disable each ask before doing anything. Enable did not: one click and the service's startup type changed. It also was not always putting things back the way they were — if SysManager had no record of how the service was set before, which is the case for anything you disabled yourself outside the app, it quietly set it to **Manual** instead. It now asks first and says exactly what it will do: either the original setting by name, or plainly that it will use Manual because the original is not known, so you can decide rather than find out afterwards.
- **The "End task" warning was too reassuring for some Windows processes.** Version 1.59.0 fixed the opposite problem — the app used to refuse to end Notepad and claimed doing so would blue-screen your PC — but the message that replaced the refusal said the same thing for every Windows process: that it "will not crash Windows" and at worst "a feature may stop working until you sign out or restart". That is true for something like Explorer, which comes back on its own. It is not true for Windows Defender's engine, where closing it is a step towards switching off your protection, and it is not true for Windows Installer and the servicing processes, where interrupting one part-way through can leave an update half-applied — and restarting does not put that right. Those now get their own prompt that says so plainly. The processes Windows genuinely cannot survive losing are still refused outright, ordinary Windows components keep the existing warning, and normal programs keep the plain one, so nothing you could end before has become harder to end.

### Changed
- **The build now refuses a version number that does not match what the release will be tagged.** Internal only — it does not change anything you see in the app. Two versions had ended up written into the changelog without ever being published, because several fixes were in flight at once and each picked the next free number; the release step then looked for a version that was not there and stopped. The check that catches this now runs while the change is still being reviewed, so it costs one correction instead of a stalled release.

## [1.61.2] - 2026-08-11

Nothing changes in the app itself. This closes three ways the project's own tests could reach
into your real SysManager data — including one that could delete the copy the new
"go back to the previous version" button depends on.

### Fixed
- **The tests could destroy your rollback copy.** Version 1.61.0 added the ability to go back to the previous version by keeping one copy of the build you were running. Running the project's own test suite overwrote that copy with a 9-byte scratch file — so the About tab would still offer "Go back to the previous version", and pressing it could not work. This only affects people who build and test SysManager themselves, not normal use. The retention step now takes a folder from whoever calls it and every test passes a temporary one; a new test asserts the real location is untouched after the applier runs, and it fails against the old code.
- **The tests wrote into your activity history.** The Dashboard's list of recent actions is, as its own code says, the only record of what the app changed on your PC. Any test that exercised a real action — changing DNS, running Deep Cleanup, removing a shortcut — appended to *your* list instead of a scratch one, because the shared logger had no way to be pointed elsewhere. It is now redirectable and the test run redirects it once, before any test starts, so no future test can forget to.
- **The tests consumed your crash report.** When SysManager closes unexpectedly it leaves a note so the next start can tell you. Reading that note deletes it, and every test that constructed the Dashboard read it from the real location — silently throwing away a genuine crash report before you were ever shown it. The crash-note store is now a required argument rather than one that quietly defaults to your own profile.
- **Two test classes could corrupt each other's confirmation prompts.** Two classes swapped the shared dialog service without joining the group that runs them one at a time, so in parallel one could restore a stand-in another was still using — a test about a "are you sure?" prompt silently answering with a different test's answer. Both now join it, and a new check fails the build if a future test class forgets.

## [1.61.1] - 2026-08-11

Two more of the project's own tests can no longer touch your real settings: the Settings
Watchdog baseline and the dark-mode schedule.

### Fixed
- **Two more places where running the tests could overwrite your own files.** This only affects people who build and test SysManager themselves, not normal use — but it is the same problem that was fixed for app icons in 1.60.1, and it is worth closing properly. The Settings Watchdog keeps a snapshot of your chosen Windows settings, and the Dark Mode scheduler keeps your on/off times; both were stored at a location the tests had no way to redirect, so a test that saved a baseline or a schedule wrote over yours. Both now accept a scratch folder, and every test uses one. The dark-mode schedule stays exactly where it has always been for real users, so nothing of yours moves.

## [1.61.0] - 2026-08-11

If an update ever leaves SysManager not working, you can now go back to the version you had
before with one click.

### Added
- **A way back from a bad update.** Until now, installing an update replaced the old SysManager permanently. If a new version turned out to have a problem — and this app has shipped two releases that wouldn't start — your only option was to work out on your own that you needed to find an older release on GitHub and download it by hand. The app now keeps the version you were running before the last update, and the About tab shows a **"Go back to the previous version"** button when there's something to go back to. It asks first, explains that anything the newer version fixed will come back too, and leaves your settings alone. Only one older version is kept, so this doesn't quietly fill your disk with copies of the app — and if there's no room to keep one, the update still installs as normal rather than failing.

## [1.60.3] - 2026-08-10

Closes a hole in the update check before it can ever matter: when SysManager starts being
code-signed, an update signed by someone else will be refused instead of accepted.

### Fixed
- **The update check would have trusted any signature, not just ours.** When you install an update from inside the app, the download is verified against the published checksum — that part was and remains the real protection, and it is what catches a tampered file. The app also looks at the file's digital signature. That second check only confirmed *a* signature existed; it never asked **whose**. SysManager isn't signed yet, so nothing was exposed — but the moment a signing certificate arrives, the reasonable assumption would be "we sign now, so that check protects us", and it would not have: a file signed by anyone at all, including someone who made their own certificate, would have passed exactly like a genuine build. Now a signature has to belong to the expected publisher *and* trace back to a trusted authority, or the update is refused. Unsigned builds keep installing normally, so nothing changes for you today.

## [1.60.2] - 2026-08-10

Disk Analyzer now tells you that its total leaves some Windows folders out, so the number no
longer looks wrong when you compare it against the free space Windows shows.

### Fixed
- **Disk Analyzer's total was quietly incomplete.** To stay fast, it skips four Windows areas it either cannot read or would take a long time to walk — the Recycle Bin, System Volume Information (where restore points live), and the `WinSxS` and `CSC` folders inside Windows. It also never follows shortcut-links between folders, because that would either count the same files twice or wander outside the folder you asked about. All of that is deliberate and stays. What was missing is that nothing told you: you would add up what the tab reported, compare it against the free space Windows shows, find a gap of several gigabytes — `WinSxS` alone is often that big on its own — and reasonably conclude the app was wrong. The tab now says so in one line under the summary, and hovering it names the exact folders.

## [1.60.1] - 2026-08-10

Running the project's own test suite no longer overwrites your choice about whether app
icons may be fetched from the web.

### Fixed
- **The test suite quietly changed one of your settings.** In the Bulk Installer you can allow app icons to be loaded from the web — off unless you turn it on, because the app is local-first. That choice lives in a small file in your profile, and the project's automated tests were writing to *that* file rather than a scratch copy: five of them flip the setting while checking the download behaves correctly. So running the tests left the setting at whatever the last one happened to set, and the value you had picked was gone. Nothing about the app itself was wrong — but if you build and test SysManager yourself, this was silently editing your own configuration, and the same tests could also write into your real icon cache. Both now use a temporary folder.

## [1.60.0] - 2026-08-10

Duplicate Finder now tells you which of several identical copies to keep, and why, instead
of listing them as equal rows and leaving the decision entirely to you.

### Added
- **Duplicate Finder now tells you which copy to keep.** It would find five identical photos, tell you they were wasting 4 GB, and then show them as five identical rows — leaving you to work out which one is the original, which is exactly the judgement the tab exists to help with. One file per group is now badged **Keep**: the oldest, because a copy is normally made after the file it came from. The rule is written on screen instead of applied quietly, every row shows its date so you can check the reasoning yourself, and **"Keep this one"** moves the badge when you know better — a copy that kept its original timestamp, or a file rewritten by cloud sync, will fool the guess. Groups with identical dates fall back to the copy nearest the top of the folder tree, so the badge never jumps around between scans.
- **Still nothing is deleted.** The tab remains read-only on purpose: getting this wrong costs you your own photos and documents with no way back. It suggests a decision and points you at the file; the removing is yours to do, in Explorer.

## [1.59.0] - 2026-08-10

Process Manager stops misjudging what is safe to close: it no longer refuses to end
Notepad, and every row now says whether a process is part of Windows, a known app, or
something it does not recognise.

### Fixed
- **The app refused to end Notepad, and told you it would crash your PC.** Process Manager would not let you end 49 ordinary programs — Notepad, Calculator, Paint, Task Manager, Registry Editor, Command Prompt, PowerShell, Windows Terminal, Snipping Tool, Media Player, Explorer and more — and said each one "would cause a system crash (BSOD)". That was simply untrue. The check was reading the wrong thing: the built-in database records whether a program *ships with Windows*, and that was being used to mean "ending it will crash the machine". The processes Windows genuinely cannot survive losing (`winlogon`, `csrss`, `smss`, `services`, `lsass`, `wininit`, …) are still refused exactly as before. Everything else is now your decision.
- **Windows components warn you honestly instead of blocking you.** Ending Explorer to fix a frozen taskbar, or Print Spooler when printing is stuck, are normal things to do. They now ask first and say what to actually expect — "will not crash Windows, but a feature may stop working until you sign out or restart" — rather than pretending it is impossible.

### Added
- **A Safety column in Process Manager, answering the question the tab exists for.** The app has always carried a 108-entry database that knows whether a process ships with Windows, is a well-known application, or is not recognised at all — and it never showed you. Every row now carries a label: **Windows**, **Known app** or **Not recognised**, each explaining on hover what that means for ending it. "Not recognised" is deliberately plain grey rather than a warning colour: most of what runs on any PC is not in a 108-entry list, and that alone says nothing bad about it. The column sorts, so everything unfamiliar can be grouped together and reviewed in one go.

## [1.58.8] - 2026-08-10

Quick Tune-Up now shows you what it found, and lets you watch it work and stop it.

### Fixed
- **Quick Tune-Up used to throw away everything it found.** You pressed the button, it cleaned temp files, emptied the Recycle Bin and checked your shortcuts, memory, uptime and disks — and then showed you nothing but a toast. The results card it was building all along (how much was freed, how many broken shortcuts, which disk needs attention, whether the PC wants a restart) now actually appears, with a plain-language line for each finding. Where something can be acted on, there's a button that takes you straight to the right tab: broken shortcuts to Shortcut Cleaner, high memory to Process Manager, more to reclaim to Deep Cleanup. If nothing needs attention it says so, rather than leaving you guessing.
- **You can now see the Tune-Up working, and stop it.** It runs six steps and showed no progress at all while doing it. There's now a progress bar with the current step, and a Cancel button — the ability to cancel was already built, it just had no button.

### Changed
- **Removed five internal shortcuts that led nowhere.** Five "jump to this tab" commands existed in the code with nothing connected to them. They've been removed rather than left looking like features; the one that *is* used (View details on the update notice) is untouched.

## [1.58.7] - 2026-08-10

The Dashboard's "Recent activity" card now answers the question it was always meant to:
what did this app change on my PC?

### Fixed
- **"Recent activity" on the Dashboard now shows what the app actually changed.** It listed which tabs you had opened, while leaving out the things you would genuinely want a record of: a permanent Deep Cleanup delete, a browser clean that signed you out of sites, privacy settings written to the registry, an app uninstall, files erased with the shredder, and broken shortcuts deleted. None of those left a trace. All six are now recorded, and the card refreshes when you return to the Dashboard so a new entry shows up straight away instead of looking like it never registered.
- **Only counts and sizes are recorded — never file names.** That list is a plain text file on your PC, so writing down the name of a file you chose to erase for good would leave behind exactly the trace the shredder exists to remove, and it would outlive the file. So it says "Securely erased 3 items (7-pass overwrite)" and nothing more. The same applies to the other five: how much was cleaned, not what.

### Changed
- **Opening a tab no longer counts as "activity".** Every tab you opened used to add an entry, and only the last 20 were kept — so a few minutes of clicking around pushed out any real action. Tab visits are still recorded in the app's diagnostic log, just not on this card, and the card now keeps 60 entries so a busy session cannot bury a cleanup.

## [1.58.6] - 2026-08-10

Jobs that can run for minutes now tell you how long is left, instead of leaving you
watching a bar.

### Fixed
- **"How long is left?" now actually appears.** Speed Test and Deep Cleanup both worked out a time estimate on every tick of a job that can run for minutes — and then had nowhere to show it, so you watched a bar with no idea whether to wait or walk away. Both now show it, the same way App Updates, Bulk Installer, Uninstaller and Quick Cleanup already did.
- **Ping and Traceroute now confirm what they did.** These were the only two tabs in the app with no status line at all. Pressing "Clear History" on Ping emptied the chart and the whole history and said nothing, so there was no way to tell it had worked. Starting or stopping the automatic traceroute was equally silent. Both tabs now report what happened.
- **The plain-language explanation of every service was written and never shown.** SysManager carries 25 hand-written descriptions of what a Windows service actually does and whether it is safe to turn off — "Superfetch — preloads apps into RAM. Disabling frees RAM for games and reduces disk I/O." — while the Services tab showed only Microsoft's own dense wording. The tab's own subtitle and this README both promised those explanations. Hover any row's description to read it. You can also now filter the list by recommendation ("Safe to disable", "Keep enabled", "Advanced"), which the README claimed you could but you could not — that is a separate thing from the Safe/Caution/Critical safety mark, which answers "will this break Windows" rather than "is it worth turning off".

## [1.58.5] - 2026-08-10

Browser Cleaner now finds every browser profile, not just the first one — so traces you
asked it to clear are actually cleared.

### Fixed
- **Browser Cleaner was only ever cleaning your first browser profile.** If you have more than one profile in Chrome, Edge or Brave — a personal one and a work one, or one per person in the house — only the first was scanned. The others' cache, history and cookies were never found, never counted in the total, and never cleaned. So someone clearing their browsing traces kept every trace in the other profile, with nothing on screen to hint at it. Every profile is now included, and each row says which one it belongs to ("Google Chrome — Profile 1"), so you can see exactly what you are about to clean. Cookies stay unticked by default in every profile, just as before, and cleaning one profile cannot touch another. Firefox was already handled correctly and is unchanged.
- **Disk Analyzer now tells you when a folder was only partly readable.** Windows blocks access to parts of some folders even for you, and when that happened the folder's size was quietly reported too small with no hint anything was missing — so you could go hunting for space in the wrong place. Those folders now carry a small amber warning mark, and hovering it explains that the folder may be using more space than shown. The app already knew this; it just never said so.

## [1.58.4] - 2026-08-10

The Windows Update progress bar now really fills, which the previous version claimed to
fix and only half did.

### Fixed
- **The Windows Update progress bar now actually fills.** Version 1.58.2 claimed to fix this and only half did: the bar was told what percentage to show, but it was still stuck in "busy" mode, and in that mode Windows ignores the percentage completely and just sweeps back and forth. So installing updates — one of the longest things the app does — still gave no sense of how far along it was. It now switches to a real filling bar the moment Windows reports a percentage. The Cleanup and Debloater bars fixed in 1.58.2 were genuinely fixed; this was the one that was not.

### Changed
- **The app's own tests no longer touch your saved speed-test results.** Three tests were reading, writing and — in two cases — deleting the real speed-test history file, because that part of the app had no way to be pointed at a scratch location. It does now, so the tests run entirely on throwaway files. Nothing about how the app itself saves your results has changed. This only ever affected anyone running the test suite from source, not the released app, but it also meant those tests could barely check anything; they now verify the saved data properly, including that clearing one engine's results leaves the other's alone.

## [1.58.3] - 2026-08-10

The Ping and Traceroute tabs no longer contradict each other about whether a trace is
running.

### Fixed
- **The Traceroute tab no longer lies about whether it is running.** Ping and Traceroute look like two separate tabs with their own Start and Stop buttons, but behind them sits one shared monitor. So pressing Stop on Ping silently killed a running auto-trace while Traceroute still showed "Stop auto-trace" and claimed it was running — and pressing Start on Ping quietly began tracing every target while Traceroute still offered to start it. Both tabs now read the same state, so whichever one you use, the other tells you the truth.

## [1.58.2] - 2026-08-10

Three buttons that permanently deleted something without asking now ask first.

### Fixed
- **Three ways to permanently delete something now ask first.** "Clear History" on App Alerts is a red button sitting right next to the harmless "Acknowledge All" — and it wiped the entire list of what installed itself on your PC the instant you touched it, with no question asked and no way to get it back (that list is never saved to a file, so what is on screen is the only copy). Deleting a saved preset on Volume Control and clearing your saved speed-test results behaved the same way: the file was rewritten immediately, no undo. All three now ask you to confirm and say what will be lost. If the list is already empty there is nothing to confirm, so no pointless dialog appears.
- **The Boot Analyzer's Cancel button was missing.** Reading your boot history means walking a Windows event log, which on a large or damaged log can take a while — and the tab starts that read by itself the first time you open it. There was no way to stop it short of closing SysManager. A Cancel button now appears next to Refresh while the read is running, exactly like the other tabs in that group.
- **Progress bars that never moved now move.** On Cleanup, Debloater and Windows Update the app worked out exactly how far along it was — 1 of 15, 2 of 15 — and then drew an empty bar that never filled. Removing fifteen preinstalled apps looked identical to the app having frozen. Those bars now fill as the work happens.
- **Bars that could not animate at all now animate.** Defender Tweaks, Context Menu, Profile Export/Import and Startup Manager each showed a small grey rectangle during a long operation that neither filled nor moved, then vanished. Context Menu's preset apply is the worst of them: it closes every File Explorer window while showing a motionless bar, which reads as a crash. They now show activity for as long as the work lasts.
- **Disk Analyzer no longer tells you to do what you just did.** After analysing a folder that genuinely contains no subfolders, the middle of the screen said "No results yet — pick a folder and analyze", while the summary right next to it correctly said "No subfolders found." The two now agree: before a scan it asks you to pick a folder, and after one that found nothing it says so.

## [1.58.1] - 2026-08-10

### Fixed
- **A file SysManager is not allowed to read no longer breaks the job it was part of.** Six places treated "the disk had a problem" and "Windows would not let me read this" as different things and only handled the first — so a single locked-down file could stop the whole operation instead of being skipped. The clearest case: exporting your settings gave up entirely if one of the files could not be read, losing the ones that could. Now the unreadable file is skipped and the rest still export. The same applies to checking a downloaded update, saving the Settings Watchdog baseline, reading the gaming-profile store, loading an app icon, and adding a file to the shredder list.

## [1.58.0] - 2026-08-10

### Added
- **You can now turn off the version check that runs when SysManager starts.** There is a new checkbox on the About tab — "Check GitHub for a new version when SysManager starts". It is on by default, because that check is how you hear about a fix, but it is now yours to switch off. When it is off, nothing goes out until you press "Check for updates" yourself. Either way, nothing about you or your PC is ever sent: SysManager only asks GitHub's public releases page which version is newest.

### Changed
- **That check now runs at most once a day instead of on every launch.** Opening and closing SysManager a few times used to ask GitHub twice each time, which could hit the limit GitHub allows for anonymous requests — after which the About tab reported an error for no real reason. Pressing "Check for updates" or "Retry" still asks immediately, every time.

## [1.57.8] - 2026-08-06

### Fixed
- **The log file can no longer grow big enough to be unusable.** SysManager keeps a diary of what it does, and that file is what you attach when reporting a problem. It had no size limit — one day's file was allowed to reach a gigabyte, which is far past what you could upload anywhere. Each file is now capped at 10 MB and a fresh one is started when it fills, keeping at most two weeks of them, so the diary stays small enough to send and takes a predictable amount of disk.
- **The Volume Control tab no longer floods that log.** If an app's audio could not be read, the tab wrote the same complaint twenty times a second for as long as it stayed open — filling the file with one repeated line and pushing out everything actually worth reading. It is now noted once, and again only if something genuinely changes.

## [1.57.7] - 2026-08-06

### Fixed
- **Light themes no longer come with a black title bar.** The bar across the top of the window — the one Windows draws, with the close button — was always dark, whichever theme you picked. On any of the six light themes the app was a near-white window wearing a black strip, the one part of it that never matched. It now follows your theme, and changes straight away when you switch rather than waiting for a restart.

## [1.57.6] - 2026-08-06

### Fixed
- **Health verdicts are readable again on the light themes.** The coloured answers to "is my PC OK?" — the System Health verdicts, disk health and wear percentages, drive temperatures, the Dashboard health score, Cleanup's repair results and the network-health headline — were painted in colours picked for the dark theme and never repainted when you chose a light one. On a light background they came out as a pale smear: measured against the lightest cards, every one of the six fell below the readable-contrast standard, the worst at about a third of the required difference. They now follow whichever theme you have, so green still reads as green and red as red on all of them. The colours on the dark themes are unchanged.
- **The colour-coded severity in System Logs had the same problem.** Critical, Error, Warning, Info and Verbose entries were marked with their own set of fixed colours, separate from everywhere else in the app and equally unable to follow a light theme. They now use the same theme-aware colours as the rest.
- **The same colours could also get stuck after switching theme.** Once a colour had been drawn it was cached and reused, so changing theme could leave the old one behind until the app restarted. Only genuinely fixed colours — like the per-target lines you pick yourself on the Ping chart — are cached now.

## [1.57.5] - 2026-08-06

### Fixed
- **Closing SysManager to the tray now actually stops it working in the background.** Closing the window hides it rather than quitting — that is the default — but whichever tab was open kept checking your PC once a second for as long as it stayed on: re-listing every running process, or re-measuring network traffic and writing it to disk. Nothing was on screen to show it. Those checks now stop when the window is hidden or minimised and pick up again when you bring it back, so a PC left on all day is no longer doing invisible work. The background recording that is *meant* to keep running while hidden — the resource history graph, and the dark-mode schedule — is untouched.

## [1.57.4] - 2026-08-06

### Fixed
- **Switching the Resource History chart between ranges no longer risks an error.** Picking a different period while another one was still loading could have both of them rebuild the five graphs at the same time, which could fail instead of drawing. Only one rebuild runs at a time now. This is the same problem that was fixed on the Bandwidth Monitor in 1.57.3 — Resource History had it too, and it was missed then.
- **An unreadable history file no longer crashes the recorder.** Both the Resource History and Bandwidth Monitor recorders handled a disk error while reading or writing their history, but not a permission error — and a locked-down or read-only file produces the second, not the first. Because the recording runs quietly in the background, that surfaced as the app failing over something the user never started. Both now log it and carry on with an empty chart, exactly as they already did for a disk error.

## [1.57.3] - 2026-08-06

### Fixed
- **Bulk Installer now explains why an install failed.** It wrote things like "Failed (exit 1618)" into the row, or the raw Windows error text — neither of which tells you what to do. The same numbers were already being translated into plain sentences on the Uninstaller tab, so that translation is now shared: "Another installation is already in progress — wait for it to finish and try again", "Access denied — retry and accept the installer's Windows UAC prompt", and so on. If a PC does not have App Installer at all, all three app tabs now say so in the same words instead of one of them showing an internal error.
- **Switching the Bandwidth Monitor chart between ranges no longer risks an error.** Changing the range while another change was still loading could have both of them redraw the graph at the same time, which could fail instead of drawing. Only one redraw runs at a time now.

## [1.57.2] - 2026-08-06

### Fixed
- **When something goes wrong, SysManager now tells you where the details are.** The error message showed only the raw technical text — most often "Object reference not set to an instance of an object.", which tells you nothing you can act on. It never mentioned that the app had already written the full details to a log, or where that log is. The message now says what happened, that the app is still running, and the exact folder holding the details, so a report can actually include something useful.
- **A crash that closed the whole app is no longer silent.** If SysManager died outright, the window simply vanished: no message, and nothing on the next start to say the previous session had ended badly — so the only thing left to report was "it just closed". The app now records that it went down and, the next time you open it, tells you once when it happened and where to find the details. The note appears once per crash, never blocks the window, and is ignored if it is more than a week old.

### Removed
- Deleted a leftover screenshot of the Bandwidth Monitor from when that tab was still a placeholder. It was no longer shown anywhere, but it stayed in the repository claiming a finished feature was "Work in Progress".

## [1.57.1] - 2026-08-05

### Fixed
- **Fixed a crash in the new Bandwidth Monitor history view.** Switching the chart to a stored range while the tab was live could make the app read the graph's data while it was still being rebuilt, which threw an error instead of drawing the chart. The live reading and the history load no longer touch the graph at the same time, and cancelling a load part-way (by leaving the tab) no longer leaves the live chart frozen the next time you open it.

## [1.57.0] - 2026-08-05

### Added
- **Bandwidth Monitor can now show you the last hour, day, or week.** The tab had been recording your throughput to disk every few seconds and keeping a week of it — but nothing ever read it back, so the file grew for seven days and you could never see any of it. Pick a range and the chart now draws that period, with how much you actually downloaded and uploaded over it and the fastest speed you reached. That answers "where did my data cap go?" without an account or a cloud service. The live view is still the default and still one click away. Totals are worked out from the time between readings, and stretches where the tab was closed are left out rather than guessed at, so the figure never claims traffic that was not measured.
- **Drivers can hide the drivers that came with Windows.** A filter for exactly this had been written into the tab — including the code that adjusts the count — but it had no checkbox, menu item or any other control in the window, so no one could ever switch it on. It is now a checkbox in the toolbar, and the count shows both the total found and the number shown, so nothing looks like it disappeared. If your PC has no third-party drivers at all, the tab now says the filter is what emptied the list, instead of telling you to run a scan you already ran.

### Fixed
- **Re-enabling a service now restores the startup type it actually had.** Disabling a service recorded its previous setting in memory only, and that memory was discarded the next time the list refreshed — which happens on every scan and on every restart. So disabling an "Automatic" service, then re-enabling it later, quietly set it to "Manual" instead while reporting success: a change to your PC's configuration that nobody asked for and nothing showed. The previous setting is now saved to disk and used on re-enable. If you re-enable a service outside SysManager, what Windows reports wins — a stale record can never override your machine.
- **Three tabs sat silent while they worked.** CPU Core Affinity, Display Profiles and Standby List Cleaner each drew a progress bar wired to a signal their code never sent, so the bar could not appear under any circumstance, and the sidebar spinner stayed dark too. Meanwhile the work behind them is genuinely slow: listing every running process and reading its core assignment, or switching a display mode, which blocks while the monitor re-trains and can take seconds on a screen that has gone black. Clicking produced no bar, no spinner and no busy cursor — nothing to distinguish "working" from "the click did not register". All three now report progress while they work. Timer Resolution is deliberately left alone: its operations finish in well under a millisecond, so a bar there would only flicker.

## [1.56.14] - 2026-08-05

### Fixed
- **Tidied up the log writer added in 1.56.13.** It created a small text buffer for every log line without releasing it, and rebuilt its formatter on each call. Both are now handled once, so logging allocates less and leaves nothing behind. No change to what ends up in the log.

## [1.56.13] - 2026-08-04

### Fixed
- **Your Windows user name is now removed from every line of the diagnostic log.** The app already had a helper for this, but only 15 of the 75 places that log a file path actually called it — so whether your account name ended up in the log depended on which part of the app happened to run into trouble. The removal now happens where the log is written, so it covers every existing message, every future one, and paths that appear inside error text (which no per-message fix could reach). Log files stay local and are never uploaded; this matters for a log you choose to share yourself, for example when reporting a problem.

## [1.56.12] - 2026-08-04

### Fixed
- **App Alerts now notifies you when something installs itself.** The tab detected new installations and added them to its list, but nothing surfaced them — so unless you happened to be sitting on that tab at the moment it happened, you never found out. The entire point of the feature is learning about an install you did not start. A notification now appears when one is detected.
- **The auto-purge setting in Standby List Cleaner is remembered.** The switch and its RAM threshold lived only in memory, so arming auto-purge and closing the app silently reverted it to off at the default threshold — unusable for a set-and-forget feature. Both are now saved as soon as you change them and restored on startup. A damaged settings file falls back to auto-purge off rather than arming an automatic action you did not choose.
- **App Blocker no longer blames your permissions for its own safety refusals.** Every failed block reported "check admin privileges", including the four cases where SysManager deliberately refused: a Windows boot-critical program, SysManager itself, an invalid name, or an executable another program already has a debugger registered for. Someone trying to block `winlogon.exe` was told to restart as administrator, where the same refusal would happen again for the same unstated reason. Each case now explains what it is and why, and the genuine permissions case says so specifically.

## [1.56.11] - 2026-08-04

### Fixed
- **The event-log list now names each severity instead of only colouring it.** The severity column was a coloured dot under a blank header, with no text, no tooltip and nothing for a screen reader to read, so the difference between an error and an informational entry was carried by colour alone. The column is now labelled "Severity", shows the word next to the dot, is sortable, and reports the severity to assistive technology.

## [1.56.10] - 2026-08-04

### Fixed
- **Battery health and wear no longer show "-1%".** When Windows refuses the capacity query — it only reports design and full-charge capacity to an elevated process — the app used an internal `-1` marker meaning "unknown", and the page printed it straight out with a percent sign after it. Both figures now read "Not available", and the page explains that these two values need administrator rights while everything else on it works without.
- **The Security event log now says it needs administrator rights.** Selecting Security without elevation produced an empty grid and "Loaded 0 events", indistinguishable from a log that genuinely had nothing in it, because the underlying read swallowed Windows' refusal. The reader now reports why a log came back empty — refused, missing, or unavailable — and the page shows that reason instead of a blank list.
- **Renamed the log-folder button on the System Logs tab.** "Open log folder" sat beside "Open Event Viewer" on a tab named after the Windows Event Log, but opened SysManager's own diagnostic folder. It now reads "SysManager's own logs", and its tooltip shows the exact path before you click.
- **Report and environment actions no longer overwrite the update message.** Exporting a report or copying environment info wrote its result into the update card's status line, replacing "Update available: vX.Y.Z" with "Report saved" — discarding the more useful message and leaving the update card describing something unrelated to updates. These results now appear next to the buttons that produce them.
- **Old update downloads are deleted once a newer one arrives.** Each cached build is roughly 85 MB and nothing ever removed the previous ones, so `%LocalAppData%\SysManager\updates` grew by that much with every update — in an app whose Cleanup tab exists to reclaim disk space. The current build and its checksum are kept; superseded binaries, stale checksums, and interrupted partial downloads are removed. A file still in use by a running copy is left alone and cleaned up next time.

## [1.56.9] - 2026-08-04

### Fixed
- **Closing the window no longer hides the app without telling you.** Pressing the window's X minimized SysManager to the notification area unconditionally, because the switch controlling that defaulted to on and was never exposed anywhere in the UI. The app looked closed while it kept running, with no notice and no way to change it. Closing now asks once whether to keep running in the notification area or exit, remembers the answer, and honours it silently afterwards. Choosing the notification area also shows a one-time note saying where the window went, so it no longer reads as a crash. The stored choice lives in `%LocalAppData%\SysManager\close-preference.json`; an unreadable or hand-edited file falls back to asking again rather than to an action the user never picked.
- **"Export full report" now asks where to save.** The About tab wrote the report straight to the Desktop, unlike every other export in the app (System Report, Logs, Resource History, and Profile all prompt). That silently failed where the Desktop is redirected to OneDrive or restricted by policy, with no way to pick another location. It now uses the same save dialog as its siblings and reports the chosen file name.

## [1.56.8] - 2026-08-04

Version 1.56.7 was tagged but never published: its release run failed at the unit-test
gate, so no binary, notes, or announcement for it exist. The tag is retained rather than
moved, and the work below ships here instead.

### Fixed
- **Restored Performance Mode's persisted `Restore All` after an app restart.** Snapshot persistence added in #431 loaded the saved baseline only when the user applied another change. Reopening SysManager therefore left `Restore All` disabled even though `%LocalAppData%\SysManager\performance-snapshot.json` still held the original settings. Performance Mode now rehydrates that snapshot first during initialization under the existing snapshot gate, so the recovery action becomes available without waiting for live `powercfg` probes and without racing a concurrent Apply.
- **Made old recovery points clear and testable.** New snapshots record their UTC capture time and show it in local time in the `Restore All` confirmation, while snapshots created by older releases remain loadable with an explicit unknown-time label. Snapshot storage now has an injected test directory, and regression coverage recreates an app restart with a second service instance, verifies the real command reaches confirmation, and keeps legacy JSON compatible without touching the user's profile.
- **Hardened the persisted recovery boundary and failure semantics.** Snapshot files are size-bounded, require a complete non-duplicated schema, and reject malformed GUIDs, spoofable plan names, out-of-range processor values, and unsafe GPU subkeys before enabling restore. Power-plan, processor, and GPU restore failures now remain failures instead of reporting success and deleting the only recovery point; failed snapshot saves prevent any setting change, and failed cleanup keeps `Restore All` available for a retry.
- **Made the Performance Mode lock-guard test deterministic.** The guard test seeded the recovery snapshot while initialization was still loading the persisted one, and that load's deferred assignment overwrote the seeded value. On a fast machine the load finished first and the test passed; on a slower one it did not, so `Restore All` reported "nothing to restore" and never reached the guard under test. The test now waits for initialization before seeding. Test-only change, no effect on shipped behavior.

## [1.56.7] - 2026-08-04

Tagged but never published: the release run failed at the unit-test gate, so no download, notes or
announcement exist for this version. The tag is kept rather than moved, and the work it carried
shipped in 1.56.8 below. Recorded here so the version history has no unexplained gap.

## [1.56.6] - 2026-08-04

### Fixed
- **Restored the UI automation safety net for action buttons.** Accessible-name improvements had silently orphaned 17 visible-text lookups across Cleanup, System Health, Logs, Services, and Ping, so 15 tests never reached the controls they were meant to verify. All positive button lookups now use unique, current-tab-scoped automation IDs while descriptive screen-reader names remain unchanged. A blocking unit contract rejects missing or duplicate asserted IDs before the warning-only UI job can mask another regression.
- **Made the Ping interaction test exercise the real Start/Stop cycle.** The previous nullable Start lookup skipped the invocation, then failed while searching for a Stop button that could never appear. The test now normalizes its initial state, proves both UI transitions without fixed delays, invokes both commands, and restores the shared fixture to stopped state even after a failure.
- **Corrected the two stale assertions outside the selector failures.** Windows Update now describes its deferred History-time module check instead of claiming PSWindowsUpdate is available before any probe, while keeping installation hidden until a missing module is confirmed. The `App Blocker` smoke check now validates the rendered view header inside the current content host, so the matching sidebar label cannot make a blank or wrong page pass.

## [1.56.5] - 2026-08-03

### Fixed
- **Restored persistent selected-tab feedback throughout the sidebar.** The grouped-sidebar migration in v0.18.0 replaced the selecting `ListBox` with non-selecting `ItemsControl` rows, leaving the live accent mark permanently collapsed and making all 58 tabs look unselected once the pointer moved away. Normal navigation now keeps exactly one `NavItem` selected, and both the flat Dashboard row and every grouped leaf share one selected-state treatment: an accent bar, selected background, stronger label, and primary foreground.
- **Exposed the active tab to assistive technology.** Each tab entry now has one stable automation peer that reports the selected item's status. Every tab entry is now an invokable keyboard button with a visible focus cue, group headers can be focused and toggled, and collapsed groups disable their visually hidden leaves so keyboard focus cannot disappear into them. Regression coverage pins peer uniqueness, selection handoff, both live XAML templates, collapsed-group behavior, and removal of the obsolete `SideNavTabItem` style that held an unused second copy of the intended visuals.

## [1.56.4] - 2026-08-03

### Fixed
- **Kept DNS changes and Undo bound to the same captured network adapter.** Since snapshot-based Undo was introduced in v1.20.5, capture and apply selected the active adapter in separate PowerShell calls. A VPN connection, cable change, or adapter transition between those calls could capture interface 12, modify interface 27, and later restore only interface 12. Snapshots now include both the interface index and stable adapter GUID. Preset apply and DHCP reset verify that identity plus the confirmed IPv4/IPv6 addresses and automatic/static sources in the same PowerShell script that performs the first mutation; each later command rechecks adapter identity, and preset IPv6 revalidates the post-IPv4 DNS state before its second mutation. Undo resolves the stable GUID again before every restore command, so the same adapter remains targeted even if Windows assigns it a new interface index.
- **Made DHCP reset confirmable and reversible, and rejected incomplete snapshots.** Reset to automatic now uses the same pre-change snapshot flow as preset apply, and each confirmation identifies the captured interface before mutation. After consent, the adapter and DNS state are captured again; if either changed while the dialog was open or before the guarded mutation began, the operation stops without changing DNS or leaving a stale Undo entry. Snapshots separately record whether IPv4 and IPv6 were automatic or static, so effective DNS addresses supplied by DHCP are never restored as persistent static overrides. If no active adapter, no valid stable identity, or no complete read of both address families and their configuration sources can be captured, the operation stops before changing DNS. Ambiguous partial failures retain a safety snapshot while proven pre-mutation failures expose the prior successful rollback point instead of hiding it. A successful retry now keeps the last trustworthy pre-failure snapshot instead of replacing it with partially changed state, and an IPv6 command failure is reported as a partial failure while Undo remains available.
- **Preserved recovery for every adapter after interleaved partial failures.** If ambiguous failures occur on more than one adapter before a retry succeeds, Undo now collapses only untrusted snapshots for the retried adapter. Recovery points for every other possibly changed adapter remain available in newest-first order through later successful changes, until each is restored.
- **Kept DNS recovery status and consent accurate.** A failed Undo now refreshes the displayed current DNS before reporting the error, because reset or reapply steps may already have changed one address family. Restore confirmation refers to the previously changed adapter rather than showing its stale captured interface index after Windows renumbers that adapter.

## [1.56.3] - 2026-07-30

### Security
- **Prevented per-user backup data from authorizing elevated machine-wide registry changes.** Environment restore, introduced in v1.33.3, kept both User and Machine snapshots in a LocalAppData JSON file. An unprivileged process could edit that file and, when SysManager was later restored as administrator, choose valid Machine variable values and which Machine variables were deleted. New User snapshots now live under HKCU, while System snapshots are stored under an owner- and ACL-validated `HKLM\SOFTWARE\SysManagerEnvironmentBackup` key. Only that protected snapshot can drive HKLM restore; legacy LocalAppData data remains read-only compatibility input for User restore, and its `Machine` section is never migrated or trusted.

### Fixed
- **Rejected malformed environment backups before any restore write.** User and protected Machine snapshots are now parsed with bounded file, entry-count, name, value-length, and registry-kind validation. Null or missing sections, wrong JSON types, null values, duplicate names, unsupported registry kinds, and oversized content produce a safe invalid-backup result instead of a `NullReferenceException` or a partial restore.
- **Preserved pristine backups across partial and scope-specific operations.** Present-but-invalid snapshots are never mistaken for missing data or overwritten, restore validates every present scope before its first write, and a System-only change no longer creates a User snapshot. Existing valid User files remain compatible, while new registry values publish atomically without an elevated write into a user-controlled filesystem path.

## [1.56.2] - 2026-07-29

### Security
- **Blocked per-user PowerShell module hijacking in administrator sessions.** Since the initial v0.3.0 runner, both in-process runspaces and child `powershell.exe` processes inherited the caller's `PSModulePath`, whose first entry normally points to the current user's writable Documents folder. A planted module could therefore be auto-loaded with SysManager's administrator token when a built-in command such as `Get-NetAdapter` was resolved. Elevated in-process work now moves into an isolated Windows PowerShell 5.1 child whose environment contains only canonical machine-owned module roots under Program Files and System32; existing child-process execution receives the same restriction. Normal unelevated sessions retain per-user module support, and the parent process environment is never rewritten.

### Fixed
- **Kept optional Windows Update history installation on the correct privilege boundary.** SysManager now installs PSWindowsUpdate only from a normal, non-administrator session into the current user's module directory. The installer verifies the canonical PowerShell Gallery endpoint and explicitly selects `PSGallery`; an elevated session refuses the install instead of consuming user-writable PowerShellGet repository state with an administrator token. Failed module imports now expose installation guidance instead of reporting a successful empty history.
- **Handled locked-down systems without a working Windows PowerShell 5.1 host.** If policy, installation damage, or a disabled executable prevents the isolated administrator runspace from starting, the runner now maps that failure to the existing unavailable/failed service states. External PowerShell launches remain pinned to the canonical system path even when the host is missing, preventing executable search-order fallback. The runner deliberately does not fall back to an elevated in-process runspace, which would weaken the module-discovery boundary.
- **Preserved PowerShell parameter and task-result semantics.** Defender mutation scripts now declare every bound parameter instead of silently receiving null variables, and Scheduled Maintenance correctly decodes unsigned high-bit Task Scheduler result codes after out-of-process serialization.
- **Reported partial elevated operations honestly.** Edge disable/restore now reports failure when its registry policy changes but scheduled-task updates cannot run, and restore-point creation returns a clean failure state when the isolated PowerShell host is unavailable.

## [1.56.1] - 2026-07-28

### Security
- **Prevented the Uninstaller from acting as a privileged execution broker.** Scan remains available in an administrator session, but uninstall actions are disabled until SysManager is reopened normally. This applies to both direct registry commands and the WinGet route, so user-controlled package metadata can no longer inherit SysManager's elevated token.
- **Hardened registry-command validation.** Trusted shared-data paths now come from the canonical Windows known folder instead of the mutable `ProgramData` environment variable. Local commands must identify the canonical absolute executable that is actually launched, bare `rundll32` payloads resolve only from System32, and Windows Installer product-code commands must match the complete supported argument grammar; appended packages, transforms, and other payloads are rejected.

### Fixed
- **Let each local uninstaller own its UAC request.** In an unelevated SysManager session, validated registry uninstallers now launch through the Windows shell. An uninstaller whose manifest requires administrator rights can therefore show its own Windows UAC prompt without requiring SysManager itself to remain elevated.
- **Made cancellation and batch outcomes truthful.** Cancelling while WinGet or a local uninstaller is queued now prevents an unobserved process start, completed processes win a late cancellation race, cancelled or failed batches retain partial progress, and successful removals that require a restart are reported as success with restart guidance.

## [1.56.0] - 2026-07-23

### Added
- **Notification Blocker — the last work-in-progress tab is now implemented** (Privacy & Security). Mute the apps that nag you with pop-up notifications — update reminders, trial offers, "rate us" prompts — without digging through Windows Settings:
  - **Per-app mute switches** for every app Windows has recorded as a notification sender, sorted most-recently-active first and showing each app's recent notification count so the noisy ones stand out. Flipping a switch writes the same documented per-user setting as Windows Settings > System > Notifications — nothing is hooked, injected, or hacked, and Windows itself enforces the mute.
  - **Master switch** to silence all notifications at once, with an explicit warning that it also mutes calendar and reminder alerts until turned back on.
  - **Pending-changes flow** (like the Privacy & Telemetry tab): switch flips stay local until you press Apply, a confirmation summarises what changes, Discard backs out, and a failed write stays visibly pending instead of silently vanishing.
  - Everything is per-user (no administrator), fully reversible by flipping the switch back, and searchable. App names resolve from Windows' own AppUserModelId registrations, falling back to a readable form of the sender ID.
  - Scope note: the original idea (#340) included intercepting arbitrary pop-up windows; that requires manipulating other processes' windows — invasive, fragile, and malware-adjacent — so this tab deliberately sticks to the supported notification channel. Closes #340.

## [1.55.1] - 2026-07-23

### Fixed
- **Cleared the last two CodeQL code-quality warnings, bringing the scanner to zero open findings.** Neither was a field-visible bug — both paths behaved correctly — but each code shape earned its warning and reads better fixed:
  - *Temperature service (`cs/constant-condition`):* the one-time disk-name resolution used a double-checked lock (re-testing the cached field inside the semaphore), whose inner re-test CodeQL flags as a constant condition because it doesn't model the cross-thread race the re-test guards. Replaced with a plain "always take the gate, `??=` the cache" shape — an uncontended `SemaphoreSlim` acquire is trivial next to the SMART walk it guards, the race-safety is identical, and the flagged pattern is gone.
  - *File shredder (`cs/local-not-disposed`):* the exclusive shred stream WAS disposed (via `DisposeAsync` in a `finally`), but the manual form isn't recognized by the analyzer. Converted to a scoped `await using` block — same semantics, including disposing the exclusive `FileShare.None` handle *before* the final `File.Delete` (which would otherwise fail while the handle is held) — a shape both humans and the analyzer read as safe.

## [1.55.0] - 2026-07-22

### Added
- **Volume Control gained per-app output-device routing, saved presets, and a tray shortcut** — completing the per-app mixer (these were previously noted as planned).
  - **Per-app output routing** — route one app to your headset and another to your speakers. Each app row shows an output-device picker where Windows exposes the routing interface; on builds where it doesn't, the row shows a "Choose output device…" button that opens Windows' per-app sound settings, so there's always a path. Routing is applied for the Multimedia and Console roles (Communications is left to the system so a device switch doesn't hijack call audio).
  - **Volume presets** — save the current per-app volumes and mutes as a named preset (e.g. "Gaming", "Focus") and re-apply it in one click. Presets are keyed by executable name so they re-apply to whatever instance of an app is running across restarts, and are stored locally in `%LocalAppData%\SysManager\volume-presets.json`.
  - **Tray shortcut** — a "Volume mixer" item in the system-tray menu opens SysManager straight to the Volume Control tab.
  - Output-device enumeration uses the documented Core Audio device API. Per-app routing uses the undocumented `IAudioPolicyConfig` interface (the mechanism EarTrumpet uses); it is feature-detected and fully guarded — if it can't bind on this Windows build, the tab silently falls back to the guided path rather than failing. Preset logic and the device/endpoint string handling are unit-tested. Closes #332.

## [1.54.0] - 2026-07-22

### Added
- **Bandwidth Monitor — a new Monitor tab that shows how much data is moving through your network and which apps are using it** (replaces the previous work-in-progress placeholder). It has two measurement modes so it's useful to everyone without forcing elevation:
  - **Default (no administrator):** accurate machine-wide download/upload speed with a live rolling throughput chart (the last ~2 minutes), plus a per-app list attributed by active TCP/UDP connections — which programs are talking, how many connections each holds, and the remote ports involved. This reads the same connection tables Windows exposes to any user (`GetExtendedTcpTable`/`GetExtendedUdpTable`), so it needs no elevation and no setup.
  - **Precise per-app rates (optional, administrator):** exact per-process upload/download speeds and per-session data totals (like Task Manager's Network column), captured from a Windows kernel ETW session. It's offered only when the app is already running as administrator, and if the kernel session can't start (privilege, a locked-down host, a blocked native helper) the tab logs it and falls back to the no-admin view — it never crashes because precise mode was unavailable.
  - **Threshold alert:** set a Mbps limit and the tab warns when total download or upload exceeds it (useful for catching a runaway background upload); 0 turns the alert off.
  - Strictly local and read-only — SysManager only observes network activity, never throttles or blocks it, and nothing about your traffic leaves the machine. Total-throughput history is stored as NDJSON under `%LocalAppData%\SysManager`. Closes #337.

### Changed
- Added the `Microsoft.Diagnostics.Tracing.TraceEvent` dependency (used only by the Bandwidth Monitor's optional precise mode). Its native helpers embed into the single-file build, so the shipped `SysManager-vX.Y.Z.exe` remains one self-contained file.

## [1.53.0] - 2026-07-22

### Added
- **Edge/OneDrive Remover — a new Privacy & Security tab that takes Microsoft Edge and OneDrive out of your way, reversibly** (replaces the previous work-in-progress placeholder). Both of these are frequently-unwanted but awkward to deal with by hand, so the tab does it safely and offers a matching restore for every action:
  - **OneDrive: full removal for your account, no admin needed.** Stops the running client, runs the official `OneDriveSetup.exe /uninstall`, and clears its File Explorer navigation-pane entry. Files already synced to the PC stay on disk; cloud-only files simply aren't downloaded. **Restore OneDrive** reinstalls it and re-pins the sidebar entry.
  - **Edge: disable & de-integrate, never uninstall.** Windows relies on Edge (WebView2) and reinstalls it if forced out — and forcing it breaks other apps irreversibly — so the tab never uninstalls it. Instead it turns off Edge's background mode and startup boost (the documented `BackgroundModeEnabled` / `StartupBoostEnabled` Group-Policy values) and disables its two machine auto-update scheduled tasks, so Edge stops launching and running on its own; you can still open it normally. **Restore Edge** clears those policies and re-enables the update tasks. These changes are machine-scope and need administrator (the tab shows the standard golden admin banner explaining why); removing OneDrive does not.
  - **Honest about the default browser.** Windows hash-protects the default-browser association, so no app can switch it programmatically; the tab opens Windows' default-apps settings and guides the user rather than pretending to change it.
  - Every action confirms first with a plain-language impact summary and reports its honest outcome (done / needs administrator / not installed). The service routes all PowerShell and process launches through the shared runner seam with hard-coded scripts (no user input is ever interpolated — the Edge update-task names are a fixed, injection-safe allowlist), and its registry logic is unit-tested against a redirected hive. Closes #339.

## [1.52.105] - 2026-07-22

### Fixed
- **A backslash in the Bulk Installer search box corrupted the winget search command.** `SanitizeQuery` stripped double quotes and control characters (so a search term couldn't inject extra arguments) but left backslashes untouched. Because the query is passed as `search "{term}"`, a term ending in a backslash turned the closing quote into an escaped quote (`search "foo\"`), collapsing the quoted-argument boundary and producing a malformed winget invocation (a broken or empty search). The sanitizer now also strips backslashes — a winget search term has no legitimate use for one — so the quoted argument can no longer be broken. Hardening at the input trust boundary; not remotely exploitable (winget arguments are passed as data, not through a shell), but the search now behaves correctly for any input.

## [1.52.104] - 2026-07-21

### Fixed
- **The app built every one of its ~55 tab view-models at startup, even the tabs you never open.** `MainWindowViewModel` eagerly constructed all tab VMs up front, and most of them kick off real work in their constructor — a background scan, a poll timer, a WMI/registry query — so launching the app fired ~40 of those at once regardless of which tab you actually used (e.g. opening the app to the Dashboard still spun up Services, Drivers, Debloater, Bulk Installer and dozens more). Each tab's view-model is now built lazily, the first time its tab is opened (`NavItem` carries a `ContentFactory` that resolves the VM from DI on first access), so a fresh launch constructs only what the initial view needs. Startup tab-VM construction drops from ~55 to a handful. A deliberately small set stays eager because its constructor drives always-on, app-wide behavior that must run whether or not you open its tab: the Dashboard (the tab shown at launch), the Dark Mode scheduler (owns the theme-schedule poll), and About (its startup update-check feeds the title-bar version label and the "update available" banner). The four Network tabs also stay eager — they share a single network-state object and their constructors do no work. Behavior is otherwise identical; nothing is disabled, only deferred.

## [1.52.103] - 2026-07-11

### Accessibility
- **Six admin-capable tabs showed no "Running as administrator" confirmation when the app was elevated.** Boot Analyzer, Gaming Profile, Settings Watchdog, Standby List Cleaner, Tweaks Hub and the Dashboard each had only the *not-elevated* banner (a grey "Run as administrator" prompt); when the app actually ran elevated the banner row simply collapsed, unlike the other 23 admin views which show a golden "Running as administrator — <what's unlocked>" confirmation. This broke the app's golden admin-control contract (grey when elevation is unavailable, golden when active) and the cross-tab uniformity the other views establish — most visibly on the hard-admin tabs (Boot Analyzer, Standby List Cleaner) where the core action requires elevation. Each of the six now shows the same golden elevated banner (matching `AppBlockerView`), with view-specific text describing what elevation unlocks, bound to `IsElevated` in the same grid row as the existing not-elevated banner.

## [1.52.102] - 2026-07-11

### Fixed
- **Keyboard focus was invisible on every toggle switch.** The shared `ToggleSwitch` style set `FocusVisualStyle` to null but — unlike every other interactive style (buttons, TextBox) — had no `IsKeyboardFocused` trigger, so a keyboard user tabbing onto a switch (severity filters, performance tweaks, startup entries, Windows features, …) got no focus cue while Space still flipped it (WCAG 2.4.7). The switch's Track now shows an accent focus ring when focused, matching the button styles; a reserved transparent 1.5px border keeps the layout from shifting when it lights up.
- **Windows Update category badges were unreadable on the light themes.** Seven of the eight category badge colors (Security, Defender, Driver, Servicing, .NET, Feature upgrade, default) were hardcoded pale dark-theme tints (~1.8:1 on the near-white light presets), while only "Cumulative" had been migrated to a theme brush. All eight now use per-preset status/badge brushes that `ThemeService` recomputes for light-theme legibility.
- **The Logs tab's Critical severity card and two filter dots bypassed the per-preset color palette.** The Critical card's background/stripe/dot and the Error/Verbose filter dots were hardcoded (the Critical card's 8%-alpha red was near-invisible on light themes — the most severe category was the least visible), while the sibling Error/Warning/Info cards re-themed. Critical now uses new per-preset `CriticalText`/`CriticalBgSubtle` brushes, and the Error/Verbose dots use `Danger`/`TextMuted` — so all severity colors flow through one palette. The colorblind-safe ▲ glyph is unchanged.

### Accessibility
- **The DNS/Hosts "On" checkboxes and the Windows Update per-row select checkboxes had no accessible name**, so a screen reader announced an unnamed checkbox with no way to tell which host entry or update it toggled. Each now names its row (e.g. "Enable hosts entry example.com", "Select <update title>"), matching the App Updates / Shortcut Cleaner / Uninstaller grids.

### Changed
- **App Blocker tab's bottom gutter now matches every other tab** (root margin `28,24,28,16` instead of `28,24,28,0`), so its footer no longer sits flush against the window edge when switching tabs.

## [1.52.101] - 2026-07-11

### Fixed
- **Reopening the theme popup on a saved custom theme showed the preset list instead of the color editors, and editing any one color silently reset the other three.** `SyncUiToService` checked the "Custom" mode radio for a persisted custom theme but never made the custom panel visible (the `Mode_Changed` handler that toggles panel visibility is wired up only afterward, and re-checking an already-checked radio raises no event), so the popup opened on the Presets list; the only way to reach the editors was to click Dark/Light — which immediately applied a preset over the custom theme — then Custom again. Worse, the four hex fields kept their XAML-default literals (`#6366F1`/`#070A0F`/`#0E1218`/`#F1F3F7`) and were never seeded from the saved theme, and `ApplyCustomFromInputs` reads all four on any field's `LostFocus` — so editing just the accent wrote the defaults for background/surface/text, destroying the user's saved custom colors on the next save. `SyncUiToService` now sets panel visibility (via a shared `UpdatePanels` helper `Mode_Changed` also uses) and, for a custom theme, seeds the four hex boxes and preview swatches from `ThemeService.CurrentTheme` so a single-field edit preserves the rest.

## [1.52.100] - 2026-07-11

### Fixed
- **App Blocker could block SysManager's own executable, making the app impossible to relaunch and impossible to unblock from within the app.** `BlockApp` writes an IFEO `Debugger` redirection for any executable name that passes validation, and it already refuses a fixed set of boot-critical processes (`winlogon.exe`, `lsass.exe`, …) precisely because an IFEO block on those is unrecoverable — but SysManager's own executable was not protected. A user could block it trivially (the Browse button fills in the real file name of any picked `.exe`, or they could just type it), after which the next launch is redirected to the non-existent `System32\SysManager_Blocked.exe` and fails; because `UnblockApp` requires the app to be running, recovery then needs `regedit` — beyond the non-technical target user. `BlockApp` now refuses to block SysManager's own executable (resolved once from `Environment.ProcessPath`, matching both the dev name `SysManager.exe` and the released `SysManager-vX.Y.Z.exe`), enforced at the service trust boundary alongside the existing boot-critical guard.

## [1.52.99] - 2026-07-11

### Fixed
- **The Gaming Profile crash-recovery sweep reverted the leftover session and rewrote its store without holding the service lock, so a quick Start click could race it.** On launch, if a previous run closed with game-mode tweaks still applied, the tab offers to revert them via `RecoverPendingAsync`. Unlike `ApplyAsync` and `RevertAsync` — which both serialize on the service's `SemaphoreSlim` (`_gate`) — `RecoverPendingAsync` ran its `RunRevertAsync` **and** its `LoadStore`→`SaveStore` completely ungated. After the user answered the "restore?" dialog the UI was live again, so clicking **Start** launched `ApplyAsync` concurrently with the still-running recovery revert: two paths reverting/applying the same machine-wide tweaks (power plan, visual effects, search indexing, notifications) and doing an unsynchronized read-modify-write of the on-disk store — which could lose the `ActiveSession = null` clear (resurrecting the leftover marker so it re-prompts forever) or interleave conflicting tweak steps. `RecoverPendingAsync` now acquires `_gate` for its whole body (mirroring `RevertAsync`, including the `ConfigureAwait(false)` that keeps the gate-release continuation off the UI thread so `Dispose`'s shutdown `_gate.Wait()` can't deadlock) and re-reads the store **inside** the gate so the read-modify-write is atomic against a concurrent `SaveStore`.

## [1.52.98] - 2026-07-11

### Fixed
- **`winget` was launched by its bare name, which — despite the System32 path-pinning added for other tools — could still let an attacker-planted `winget.exe` run with the app's privileges (local privilege escalation).** Every winget operation (Bulk Installer install/list/search, App Updates upgrade, Uninstaller list) routes through `PowerShellRunner.RunProcessAsync("winget", …)`, which hardens the target via `SystemPaths.ResolveSystemTool`. But winget is not a System32 tool — it is an MSIX app whose real binary lives under `%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe\winget.exe`, and whose only PATH entry is a per-user execution alias in the **user-writable** `%LOCALAPPDATA%\Microsoft\WindowsApps`. `ResolveSystemTool` probes only System32, so both probes missed and it returned the **unrooted** bare string `"winget"`. Launched with `UseShellExecute=false`, Win32 `CreateProcess` searches the calling process's **own load directory first** — so a `winget.exe` planted next to SysManager's portable .exe (commonly run from a user-writable Downloads folder, sometimes elevated via the prominent "Run as administrator" buttons on the winget tabs) would execute with the app's rights. The previously-relied-on `WorkingDirectory=System32` pin does **not** cover this: the working directory is search item #2, the app-load directory is #1. Added `SystemPaths.ResolveWinget`, which resolves winget to the highest-versioned App Installer package under the admin-only-writable `WindowsApps` folder (verified by publisher hash and by actually containing `winget.exe`) and **fails closed** — if no trusted install is found it returns a rooted `System32\winget.exe` path (which normally doesn't exist, surfacing the same `Win32Exception` the callers already handle) rather than the plantable bare name. `ResolveSystemTool` now delegates winget/winget.exe to it. The per-user `%LOCALAPPDATA%` alias is deliberately never used for the (potentially elevated) launch. This closes the last winget binary-planting vector left open after the v1.52.91 (`powershell`) and v1.52.92 (Bulk Installer CWD) hardening.

## [1.52.97] - 2026-07-11

### Fixed
- **The Performance tab's system-mutating actions weren't serialized against each other, so "Restore All" could run while an "Apply" was still in flight and corrupt the reversible snapshot.** Every Apply command (power plan, visual effects, Game Mode, Xbox Game Bar, GPU, processor state) plus Restore All, Trim RAM, Create Restore Point and Toggle Hibernation is `async` and awaits real system work (registry writes, `powercfg`, `EmptyWorkingSet` across all processes). Nothing prevented a second command from starting mid-flight: the snapshot's own `SemaphoreSlim` only guards the load-modify of the `_snapshot` field, not the full apply→revert sequence. In the worst case, pressing **Restore All** while an Apply's registry write was still running let Restore All null `_snapshot` and delete the persisted baseline, leaving a tweak applied with nothing left to revert it (and other tabs that mutate the same system state — Tweaks, Cleanup's SFC/DISM — could interleave too). Every one of these ten commands now acquires the app-wide `OperationLockService` `SystemModification` lock (the same lock SFC/DISM and the Environment Variables editor already use) right after its confirmation dialog; if another system-modification operation is running, the command reports "Cannot start — <op> is already running." and bails before touching the system. Toggle-based commands re-sync their toggle from the live profile on that early return so the UI doesn't show a change that wasn't applied.

## [1.52.96] - 2026-07-11

### Fixed
- **Disabling an all-users (Common) startup-folder item did nothing, and such items could show the wrong enabled/disabled state.** Both the per-user Startup folder and the all-users Common Startup folder were scanned and tagged with the same `StartupSource.StartupFolder`, so `ApplyApprovedState` read the enabled/disabled state for *all* folder items only from HKCU, and `SetEnabledAsync` wrote the toggle blob for *all* folder items only to HKCU. But Windows stores the `StartupApproved\StartupFolder` state for all-users shortcuts under **HKLM**, not HKCU. Consequences for a shortcut in `%ProgramData%\...\Startup`: an item disabled via Task Manager (HKLM blob) was shown as "Enabled", and disabling it in SysManager wrote the blob to HKCU where Windows never looks — so it returned success yet the program still launched at logon. Added a distinct `StartupSource.CommonStartupFolder` (set in `ReadStartupFolder` based on which special folder produced the entry); `ApplyApprovedState` now reads HKLM for common-folder entries, and `SetEnabledAsync` routes them to HKLM (which needs administrator — a non-elevated attempt surfaces the same "requires elevation" message the HKLM Run path already produces). Per-user folder items are unchanged.

## [1.52.95] - 2026-07-11

### Fixed
- **Disabling a "run once" startup item in the Startup Manager reported success but did nothing — the item still ran at the next boot.** The scan lists both the `Run` and `RunOnce` registry keys, and `SetEnabledAsync` keyed purely off the entry's scope, so disabling a `RunOnce` entry wrote the "disabled" blob to `…\Explorer\StartupApproved\Run\{name}` and returned success. But Windows has no `StartupApproved\RunOnce` subkey and never consults `StartupApproved` for `RunOnce` keys, so the command still executed on the next boot while the UI showed "Disabled" — a system-mutation toggle that silently lied to the (non-technical) user. `SetEnabledAsync` now detects a `RunOnce` entry and returns a truthful non-success with a plain-language message ("Run-once item — runs next boot, then removes itself; cannot be disabled here.") instead of writing an ineffective blob. The item stays visible so the user still knows it's scheduled to run once.

## [1.52.94] - 2026-07-11

### Fixed
- **The Dashboard's 2-second temperature poll re-ran a Win32_DiskDrive WMI query and a per-disk SMART association walk on every tick, purely to relabel storage sensors with (unchanging) disk names.** `RefreshTemperaturesAsync` calls `TemperatureService.ReadAllAsync()` with `includeStorage` defaulting to `true`; on an elevated session that ran `GetDiskNamesFromWmi()` (a `Win32_DiskDrive` query) **and** `EnrichStorageNamesAsync` → `DiskHealthService.CollectAsync()` (an `MSFT_PhysicalDisk` enumeration plus a per-disk `MSFT_StorageReliabilityCounter` walk — by the code's own comments "by far the heaviest part of a read") every 2 seconds while the Dashboard (the app's default tab) was open. Disk friendly-names are static hardware identity, so both resolutions are now memoized once (mirroring the existing `_nvApiInitTried` "resolve static hardware once" pattern), with a `SemaphoreSlim` gate making the first resolution race-safe across the 2s poll, the user's Refresh, and the 10s resource sampler. After the first read the hot poll only calls LibreHardwareMonitor's `Update()` for live temperatures.

## [1.52.93] - 2026-07-11

### Fixed
- **The App Alerts tab froze the window while "Start Monitoring" took its baseline snapshot.** `StartMonitoring` was a synchronous command that called `AppAlertService.TakeBaseline()` directly on the UI thread — a walk of Program Files / Program Files (x86) / LocalAppData\Programs plus a full enumeration of both HKLM `Uninstall` registry trees (hundreds of subkeys), the same heavy scan the tab's own `RefreshInstalledAppsAsync` already offloads with `Task.Run`. On a machine with many installed apps or a slow disk the window hung for the whole scan. `StartMonitoring` is now an async command that runs `TakeBaseline()` + `Start()` on a background thread and resumes on the UI thread to set the monitoring state. This is safe: `FileSystemWatcher` creation is thread-agnostic and the service's `NewAppDetected` event is already marshaled via the `SynchronizationContext` captured at construction.

## [1.52.92] - 2026-07-11

### Fixed
- **The Bulk Installer launched `winget` by bare name from a hand-built process with no pinned working directory, enabling binary-planting privilege escalation, and interpolated the raw search box text into the winget arguments (argument injection).** `MarkInstalledAppsAsync` (runs automatically on tab open) and the package search both built their own `ProcessStartInfo { FileName = "winget", UseShellExecute = false }` with **no `WorkingDirectory`**. With `UseShellExecute=false`, Win32 `CreateProcess` searches the calling process's own directory before resolving the real winget App Execution Alias — so an attacker-planted `winget.exe` beside SysManager's portable .exe (often run from a user-writable folder, sometimes elevated) would run with the app's privileges. Separately, the search argument was built as `$"search \"{query}\" …"` from the unvalidated search box — the one process-launch trust boundary in the app without input validation — so a query containing a double-quote could break out and inject extra winget arguments. Both winget calls now route through `BulkInstallerService` (the `IPowerShellRunner` seam), which launches winget with `WorkingDirectory` pinned to System32 (removing the CWD from the search order, matching every other winget call in the app), and the search query is sanitized (double-quotes and control characters stripped) before interpolation. This also brings the two calls into Gate-ARCH conformance — external process launches route through the single runner seam instead of a bespoke `ProcessStartInfo`.

## [1.52.91] - 2026-07-11

### Fixed
- **`WindowsFeaturesService` launched PowerShell by the extension-less name `"powershell"`, defeating the System32 path-pinning that blocks binary-planting privilege escalation on an elevated code path.** All three call sites (`ListFeaturesAsync`, `EnableFeatureAsync`, `DisableFeatureAsync` — the enable/disable paths run elevated) called `RunProcessAsync("powershell", …)`. `PowerShellRunner` hardens the target via `SystemPaths.ResolveSystemTool`, but that helper resolved by `File.Exists` and only ever probed the bare name — `File.Exists(@"…\System32\WindowsPowerShell\v1.0\powershell")` is `false` (the file is `powershell.exe`), so both probes missed and it returned the **unrooted** `"powershell"`. With `UseShellExecute=false`, Win32 `CreateProcess` then searches the calling process's own directory first — so an attacker-planted `powershell.exe` next to SysManager's portable .exe (often run from a user-writable folder, sometimes elevated) would execute with the app's privileges. Changed all three sites to `"powershell.exe"` (matching `PowerShellRunner.RunScriptViaPwshAsync` and every other system-tool caller, which all use the `.exe` suffix). **Fix-the-class:** `SystemPaths.ResolveSystemTool` now also probes `"<name>.exe"` when given an extension-less bare name, so a future caller that forgets the suffix can't silently reopen the same hole.

## [1.52.90] - 2026-07-10

### Fixed
- **A single transient WMI hiccup on startup could permanently show "Unknown CPU" / "Windows" / no disks / no RAM modules for the whole session.** `SystemInfoService` caches static hardware info with `??=`, but every `Query*` helper returned a **non-null** fallback on a WMI fault (`CpuInfo("Unknown", …)`, `OsInfo("Windows", …)`, an empty disk/module list). Because the fallback was non-null, `??=` stored it and never re-queried — so if the first `CaptureAsync` raced a transient WMI fault (plausible under the ~36-service startup load → `RPC server too busy`), the fallback defaults were cached process-wide (the service is a singleton) even after WMI recovered milliseconds later. The Dashboard, System Info tab, System Report and tray tooltip stayed stuck on the fallbacks until restart. Each `Query*` now returns `null` on a WMI **fault** (distinguished from a genuinely empty-but-successful result, which is still cached), so `??=` caches only a successful query and retries on the next poll; `Capture` uses a transient, non-cached default for the current snapshot only.

## [1.52.89] - 2026-07-10

### Fixed
- **An environment variable added and applied, then deleted in the same session, could not actually be removed — it stayed orphaned in the registry while the pending-change counter got stuck.** `ApplyChanges` maintains two parallel maps: `_baseline` (drives the pending-change count) and `_originals` (the sole source for the deletion list). The delete loop removed the variable from both maps, but the add/edit loop updated only `_baseline` — so a freshly-added variable was never recorded in `_originals`. If the user then deleted that variable and pressed Apply again, `RecomputePending` (reads `_baseline`) counted it as a pending deletion and the UI showed "1 pending change", but `DeletedEntries` (reads `_originals`) didn't include it, so it was never deleted from the registry — leaving `PendingChangeCount` stuck at 1 and the variable orphaned. (A manual Refresh silently self-healed it by reloading `_originals`.) The apply loop now writes `_originals[Key(v)]` alongside `_baseline[Key(v)]` on a successful `SetVariable`, keeping the two maps in lock-step.

## [1.52.88] - 2026-07-10

### Fixed
- **Deep Cleanup's "Clean selected" button stayed greyed-out after the first scan even though categories arrived pre-selected.** Scanned categories are pre-ticked (non-empty, non-destructive), but `ScanCoreAsync` repopulates the list with `Categories.ReplaceWith(...)` — a collection Reset that raises no per-item `PropertyChanged`. CommunityToolkit's `RelayCommand` re-raises `CanExecuteChanged` only via `NotifyCanExecuteChanged()`, so `CanClean` (`Categories.Any(c => c.IsSelected)`) was never re-evaluated and the button kept its initial disabled state — the user had to untick/retick a category to enable it. `ScanCoreAsync` now calls `CleanCommand.NotifyCanExecuteChanged()` right after `ReplaceWith`, mirroring how the per-item change handler already re-notifies.

## [1.52.87] - 2026-07-10

### Fixed
- **Removing preinstalled apps in the Debloater could abort the whole batch and pop a raw error dialog if one app's uninstall faulted at the PowerShell-runspace level.** `RemoveSelectedAsync` awaited `DebloaterService.RemoveAsync` per app inside a loop guarded by a single `catch (OperationCanceledException)`. `RemoveAsync` catches only `System.Management.Automation.RuntimeException`, so a runspace-level fault (a failed runspace open → `PSInvalidOperationException`, which derives from `InvalidOperationException`, or a `Win32Exception` launching the host) escaped the loop, hit the global dispatcher `MessageBox` with a technical message, and left the remaining rows frozen at "Removing…" (the grid never refreshed). Each app's removal is now wrapped in its own `try/catch` for `InvalidOperationException`/`Win32Exception` — that one row is marked "Failed" and the batch continues — mirroring the guard `DefenderViewModel` already uses.
- **The Dark Mode scheduler could flip the real Windows system theme on startup against a saved "app-only" preference.** `LoadFromSchedule` applies the saved settings through the `[ObservableProperty]` setters one at a time under the `_suppressSave` flag, but that flag only gated `SaveSchedule` — not `EvaluateSchedule`, which the `ScheduleEnabled`/`DarkStart`/`LightStart` change handlers also call. So during load the `ScheduleEnabled` setter ran `EvaluateSchedule` while `DarkStart`/`LightStart`/`ApplyToSystem` still held their defaults (`19:00`/`07:00`/`ApplyToSystem = true`), and `SetTheme` could write the wrong theme system-wide even for a user who chose app-only with a different schedule. `EvaluateSchedule` now returns early while `_suppressSave` is set; the constructor still runs one authoritative evaluation after load completes, so nothing is lost.

## [1.52.86] - 2026-07-10

### Fixed
- **On a PC without winget (App Installer), the App Updates and Uninstaller tabs popped a raw OS-error dialog on their first action instead of a friendly message.** All winget calls launch `winget.exe` via `Process.Start`; when App Installer isn't present or its execution alias is off (common on older/LTSC/Server machines), that throws `Win32Exception` ("The system cannot find the file specified"). `AppUpdatesViewModel.ScanAsync`, `AppUpdatesViewModel.UpgradeSelectedAsync`, and `UninstallerViewModel.ScanAsync` caught only `OperationCanceledException`/`InvalidOperationException`, so the `Win32Exception` escaped the generated `AsyncRelayCommand` to the global dispatcher handler and surfaced as a bare OS-error dialog on the tab's very first action. (The sibling `UninstallerViewModel.UninstallSelectedAsync` already handled this — a missed migration of that fix.) All three now catch `Win32Exception` and show a shared plain-language message ("winget (App Installer) isn't available on this PC — install \"App Installer\" from the Microsoft Store to use this tab."). The upgrade batch stops on the first `Win32Exception` (winget won't reappear mid-run) and keeps the friendly message instead of a misleading "Updated 0 of N" summary.

## [1.52.85] - 2026-07-10

### Fixed
- **Importing a config profile that omitted its `Sections` list crashed with an unhandled `NullReferenceException`.** `ConfigProfile.Sections` was a non-nullable positional record parameter, but System.Text.Json does not enforce non-null on positional params — so any syntactically-valid profile JSON lacking a `"Sections"` property (an empty `{}`, a truncated export, or foreign/hand-edited JSON chosen in the Import dialog) deserialized `Sections` to null. `ProfileViewModel.Import` then called `profile.Sections.Count`, throwing an NRE that escaped the surrounding try/catch (which only caught `IOException`/`UnauthorizedAccessException`/`NotSupportedException`) unhandled on the UI thread. `Sections` is now a defaulted `init` property (`= []`, matching the `CleanupResult.Errors` / `HealthScoreResult.Recommendations` idiom), and `ProfileService.Deserialize` also normalizes a null list to empty (mirroring `SettingsWatchdogService.LoadBaseline`) so the import path can always enumerate it safely.
- **The PATH editor flagged every `PATHEXT` entry as a "missing directory", rendering the whole list in red.** `PATHEXT` is a `;`-separated list of file *extensions* (`.COM;.EXE;.BAT;…`), not directories, but `EnvVariable.IsPathLike` returned true for it and that single flag gated the editor's `Directory.Exists()` annotation — so each extension row was checked as a path (`Directory.Exists(".COM")` is false) and marked missing. Added a distinct `IsDirectoryList` (every path-like variable *except* `PATHEXT`) and gated the missing-directory annotation on it. `PATHEXT` still opens the reorderable list editor (`IsPathLike` is unchanged); its entries are simply no longer checked for directory existence.

## [1.52.84] - 2026-07-10

### Fixed
- **Traceroute re-resolved the destination hostname on every probe, so a round-robin / CDN host could produce a garbled route toward different servers.** `TracerouteService.RunAsync` passed the raw hostname string into `Ping.SendPingAsync` inside the per-hop/per-probe loop, re-running DNS for each of up to `MaxHops * ProbesPerHop` (30×2 = 60) probes. For a load-balanced hostname (e.g. `google.com`), consecutive TTLs could resolve to *different* destination IPs, interleaving hops toward different servers into one nonsensical route — plus ~60× redundant DNS lookups. The destination is now resolved exactly once, up front, via a new `ResolveDestinationAsync` helper (IP literals are used verbatim with no DNS; hostnames pin the first `Dns.GetHostAddressesAsync` result), and every probe targets that fixed `IPAddress`. A resolution failure now surfaces as `InvalidOperationException` (the type the ViewModel already handles) instead of silently looping `MaxHops` times with per-probe DNS failures. Single-IP hosts and IP literals were never affected; the reverse-DNS lookup of each *responding* hop is unchanged.

## [1.52.83] - 2026-07-10

### Fixed
- **Cancelling an Ookla speed test during the first-run CLI download was misreported as an error instead of "Cancelled".** `RunOoklaAsync` wrapped the prepare phase (`EnsureOoklaAsync` — download, extract, signature-verify) in a blanket `catch (Exception)` that re-threw everything as `InvalidOperationException("Could not prepare Ookla CLI: …")`. Because `OperationCanceledException` derives from `Exception`, a user cancel during the download/extract was converted into that error type, bypassing the ViewModel's dedicated `catch (OperationCanceledException) { OoklaStatus = "Cancelled"; }` handler — the user saw `Error: Could not prepare Ookla CLI: A task was canceled.` for a clean cancel. Cancellation now propagates untouched when the caller's token is signalled (an `OperationCanceledException` *without* the token signalled — HttpClient's internal 2-minute timeout — is reported as a truthful "download timed out" error), and the wrapping catch carries a `when (ex is not OperationCanceledException)` filter so cancellation can never be swallowed again.
- **A failed cancel-kill of `speedtest.exe` could mask the cancellation and escape as an unhandled error.** The cancel path killed the child with `proc.Kill(entireProcessTree: true)` but only caught `InvalidOperationException`. `Kill(entireProcessTree: true)` also throws `Win32Exception` (process access-denied/terminating) and `AggregateException` (a descendant couldn't be terminated) — either one propagated *instead of* the intended `throw;`, so the original `OperationCanceledException` was lost and an exception type the caller doesn't handle escaped `RunOoklaAsync` entirely. Now uses the same three-exception filter as `PowerShellRunner`'s cancel-kill (`InvalidOperationException or Win32Exception or AggregateException`). The analogous `schtasks` timeout-kill in `StartupService.SetTaskSchedulerEnabledAsync` had the same gap — a `Win32Exception` from `Kill()` escaped to the outer catch, which mislabeled the timeout as "schtasks not available"; it now uses the same filter and reports the truthful "timed out" status.

## [1.52.82] - 2026-07-10

### Fixed
- **Drive enumeration aborted entirely when a single volume's `DriveFormat` property threw (e.g. BitLocker-locked or transiently busy).** `FixedDriveService.Enumerate()` used a LINQ `.Where(di => ... di.DriveFormat ...)` predicate to filter drives to NTFS/ReFS. Because LINQ predicates evaluate lazily during `MoveNext()`, the `DriveFormat` property access ran OUTSIDE the per-drive try/catch block (which only covered the loop body). An `IOException` from one BitLocker-locked or transiently-busy volume caused the `IEnumerator.MoveNext()` call to throw, aborting enumeration of ALL remaining drives — even healthy ones. Callers (Deep Cleanup, System Health) saw zero drives instead of all-minus-one. Moved the `DriveFormat` filter inside the per-drive try block so one flaky volume is skipped (logged at Debug) while all remaining drives enumerate normally. The `DriveType` and `IsReady` checks remain in the predicate — those are backed by cached kernel data and do not hit the volume handle.

## [1.52.81] - 2026-07-10

### Fixed
- **Event Logs tab's "Info" severity filter missed Level 0 (LogAlways) events.** Windows Event Log providers can emit events at Level 0 ("LogAlways" — informational events from legacy/classic providers). `MapLevel` correctly classifies Level 0 as `EventSeverity.Info` when reading records, but the XPath filter in `BuildXPath` used `SeverityToLevel(Info)` which returned only `Level=4`. This asymmetry meant the OS-side query for "Info" events excluded all Level-0 records from the result set — the user saw fewer informational entries than the system actually logged (commonly from providers like `Microsoft-Windows-Kernel-General`, `Service Control Manager`, and other classic providers in the System log). Added `SeverityToLevels` — the exact inverse of `MapLevel` — which maps Info to `{0, 4}`, and switched `BuildXPath` from `Select(SeverityToLevel)` to `SelectMany(SeverityToLevels)` so the generated XPath clause reads `(Level=0 or Level=4)` when the user selects Info.

## [1.52.80] - 2026-07-10

### Fixed
- **Environment Variables restore/apply aborted partway through when encountering a variable name that passes the registry but fails the application's user-input validation regex.** Windows registries can legally contain environment variable names with spaces, `#`, `@`, `+`, `!`, leading digits, or the `=C:` pseudo-variable prefix — all of which fail `ValidateName`'s strict regex (`\A[A-Za-z_][A-Za-z0-9_.()\-]*\z`). `SetVariable` called the throwing `ValidateName` outside any try/catch, so `RestoreFromBackup` (which iterates all backed-up names) and the ViewModel's `ApplyChanges` loop both threw `ArgumentException` on the first non-conforming name — aborting mid-iteration and leaving the environment HALF-RESTORED (some variables written, others not, no rollback). Added a non-throwing `TryValidateName` that returns `false` for non-conforming names, and `SetVariable` now uses it to skip/count failures instead of throwing — the restore loop processes every valid variable and reports a failure count rather than aborting. Additionally, `EnvBackup` now records each variable's `RegistryValueKind` so that restores round-trip REG_EXPAND_SZ fidelity exactly instead of re-deriving it from a `%`-contains heuristic (which misclassifies expandable variables whose current value happens not to contain a percent sign).

## [1.52.79] - 2026-07-10

### Fixed
- **Self-update installer had a TOCTOU (time-of-check/time-of-use) local privilege escalation window.** `InstallUpdateAsync` verified the downloaded binary's SHA-256 hash via `VerifyHashAsync` (which opened and closed its own file handle) and `VerifyAuthenticode` (likewise), then launched the same path with `Process.Start(UseShellExecute=true)` — reopening the file a third time with no handle held across the verify-to-execute gap. Because the download directory is user-writable (`%LOCALAPPDATA%\SysManager\updates\`), a same-user process could swap the verified binary for a malicious payload in the microseconds between the final verification close and the `CreateProcess` open. When SysManager ran elevated (Run as Administrator), `UseShellExecute=true` inherited the caller's high-integrity token, giving the swapped binary admin privileges — a local elevation-of-privilege. The fix opens the downloaded file once with `FileShare.Read` (deny-write) before any verification begins, hashes directly from that held stream via a new `VerifyHashAsync(ReleaseInfo, Stream)` overload, and keeps the handle open through `Process.Start`. The OS allows `CreateProcess` to read-open the image (compatible with `FileShare.Read`) but blocks any write attempt while the lock is held, closing the TOCTOU window completely.

## [1.52.78] - 2026-07-10

### Fixed
- **Hosts file manager silently deleted standalone comments starting with a digit or colon (e.g. "# 5G adapter notes", "# 1. Block ads") and failed to recognize disabled IPv6 entries whose address starts with a hex letter (e.g. "# fe80::1 myserver").** The internal `LooksLikeIpStart` heuristic only checked whether the first character after `#` was a digit or colon — a crude approximation that disagreed with the real acceptance test (`IPAddress.TryParse` + two-token minimum) in both directions. (1) False negatives: IPv6 addresses starting with `a`–`f` (like `fe80::`, `fd00::`) failed the heuristic, so those disabled entries were invisible in the grid (no data loss — they survived as comments — but could not be re-enabled through the UI). (2) False positives: digit-or-colon-leading comments passed the heuristic (line 71) and were treated as candidate entries, but then failed `IPAddress.TryParse` (line 86) and fell through both code paths — they were neither parsed as entries nor preserved as standalone comments, causing silent data loss on the next save. Replaced with `IsDisabledEntryLine` that mirrors the parser's own logic: strip inline comment, split on whitespace, require `tokens.Length >= 2 && IPAddress.TryParse(tokens[0], out _)`.

## [1.52.77] - 2026-07-10

### Fixed
- **DNS "Undo" could silently apply the previous configuration to the wrong network adapter if network topology changed between Apply and Undo (e.g. VPN connected, USB NIC plugged in, Wi-Fi toggled).** The `DnsSnapshot` record captured the DNS addresses but not the identity of the adapter they belonged to. At restore time, `RestoreSnapshotAsync` re-queried for the "currently active" adapter using the same lowest-ifIndex heuristic — which could resolve to a completely different NIC than the one originally configured. The snapshot now pins the adapter's interface index at capture time (`IfIndex` field); restore targets that specific adapter directly and verifies it still exists before applying, throwing a clear user-facing message if it was removed rather than silently reconfiguring the wrong interface. Legacy snapshots (pre-existing code paths that don't carry an index) fall back to the dynamic lookup so backward compatibility is preserved.

## [1.52.76] - 2026-07-10

### Fixed
- **App icon cache loading crashed on corrupt PNG/JPEG files that trigger WIC codec errors.** `AppIconService.LoadFromFile` caught `NotSupportedException`, `IOException`, and `UriFormatException`, but `BitmapImage.EndInit()` throws `FileFormatException` (derives from `FormatException`, not `IOException`) for malformed image headers and `ExternalException` for native WIC decoder failures. Both are now caught, matching the established pattern in `IconExtractorService` (lines 203/234). Without this fix, a single corrupt cached icon file caused the entire app-icon load path to throw unhandled to the caller.
- **File Shredder failed on read-only files when invoked for a single file (not via folder shred).** `ShredFileAsync` opened a write stream (`FileAccess.Write`) without first clearing the `ReadOnly` attribute, throwing `UnauthorizedAccessException`. `ShredFolderAsync` already stripped ReadOnly (lines 213-215) before calling `ShredFileAsync`, but the ViewModel's single-file path (`FileShredderViewModel:170`) called `ShredFileAsync` directly — the file survived with its data intact while the UI reported "Failed". The ReadOnly strip now lives inside `ShredFileAsync` itself so both entry points are covered.
- **Memory health scan returned partial or empty results when any single RAM module reported a `DBNull` property.** `MemoryTestService` used `Convert.ToDouble(mo["Capacity"] ?? 0)` and `Convert.ToUInt32(mo["Speed"] ?? 0u)` — the `??` operator does not catch `DBNull.Value` (which is non-null), so `Convert.ToUInt32(DBNull.Value)` throws `InvalidCastException`. The catch block for that exception sat OUTSIDE the `foreach` loop, so one problematic module aborted the entire scan. Now uses `FixedDriveService.ToDoubleSafe`/`ToUInt32Safe` (DBNull-aware, already tested) and the defensive catch wraps each individual module so remaining modules are still reported.
- **Flaky test (`ContextMenuViewModelTests.Constructor_TotalCount_DefaultsToZero`) that raced the ViewModel's constructor-initiated async registry scan.** On CI, the scan could complete before the assertion checked, producing `Expected: 0, Actual: 6`. Replaced all four timing-dependent baseline tests with post-scan invariant assertions that await `InitializationComplete` — the same deterministic pattern used by 16 other VM test classes.

## [1.52.75] - 2026-07-10

### Fixed
- **Disk Analyzer over-counted drive usage by descending into WinSxS and other heavyweight system directories during recursion.** The `SkipSegments` filter (`\windows\winsxs`, `\windows\csc`, `\$recycle.bin`, `\system volume information`) was only applied to the top-level children of the analyzed root (line 71). The recursive `MeasureFolder` walk pushed every non-reparse subdirectory onto the stack without re-checking against `SkipSegments`, so scanning a drive root like `C:\` would enter `C:\Windows` (not skipped — it's not in the list) and then descend into `C:\Windows\WinSxS` (a hardlink farm, not a reparse point) and `C:\Windows\CSC` (offline files cache), double-counting hundreds of thousands of hardlinked files at full `FileInfo.Length`. The recursive loop now guards each subdirectory with `ShouldSkip(d)` before pushing it, matching the top-level behavior.

## [1.52.74] - 2026-07-10

### Fixed
- **RestartExplorer could leave the user without a taskbar/desktop if any explorer.exe process was unkillable (e.g. elevated or another user's session).** The kill loop and the shell relaunch lived in a single try block, so one `Win32Exception` from `Kill()` on a higher-integrity explorer aborted the entire method — explorer was dead but never relaunched. Each process is now killed in its own guarded block (matching `FileLockService.KillProcess`'s per-process pattern), and the `Process.Start("explorer.exe")` relaunch runs unconditionally after the loop so the shell is always restored.
- **The Context Menu tab's "Enable" action on HKLM-disabled entries reported success but actually did nothing.** The HKCU fallback path used `CreateSubKey` even when enabling (removing `LegacyDisable`), which created an empty orphan key and deleted a value that never existed there, then returned `true`. The HKLM `LegacyDisable` was never touched, so Explorer still hid the entry and the next scan snapped the toggle back. The enable path now uses `OpenSubKey` and returns `false` if no user-authored override exists to clear — surfacing the "needs admin" message instead of silently lying.
- **Every Context Menu toggle froze the UI thread for up to 5 seconds.** `ToggleEntry` was synchronous and called `BackupRegistry` which spawns `reg.exe` with a 5-second `WaitForExit` — all on the dispatcher thread. Converted to an async command with `Task.Run`, matching the existing `ApplyPresetAsync` pattern that already offloads the same service calls for exactly this reason.

## [1.52.73] - 2026-07-10

### Fixed
- **The Process Manager can no longer force-kill kernel-critical processes (csrss, lsass, smss, services, wininit, etc.) that would cause an immediate BSOD.** The Kill command was the only destructive surface in the app that did not guard against acting on a known-critical target — unlike the Services tab (which refuses to stop/disable critical services) and the File Lock tab (which refuses to end critical lockers). It now refuses outright when the process is classified as "System" in the process database, and as defense-in-depth also checks against a hardcoded boot-critical denylist so protection holds even for processes not yet in the database.

## [1.52.72] - 2026-07-10

### Fixed
- **Several services and ViewModels depended on the concrete PowerShellRunner class instead of the IPowerShellRunner interface, making them impossible to unit-test in isolation.** UninstallerService, WindowsFeaturesService, PerformanceService, ServiceManagerService, CleanupViewModel, DriversViewModel, SystemHealthViewModel, WindowsUpdateViewModel, and ServicesViewModel all accepted a concrete PowerShellRunner in their constructors. This prevented substituting a mock in tests (NSubstitute cannot proxy sealed concrete classes) and violated the dependency-inversion principle the rest of the codebase already follows. All ten consumers now depend on IPowerShellRunner, and the redundant concrete-only DI registration was removed — every resolution goes through the interface mapping.

## [1.52.71] - 2026-07-10

### Fixed
- **The Dashboard and App Updates tabs could cross-talk when both ran winget at the same time.** Both tabs shared a single winget service instance (and therefore a single underlying process runner). If the Dashboard's "Update All Apps" action ran while the App Updates tab was scanning, the output lines from one operation could bleed into the other tab's console, and the interleaved event subscriptions could produce garbled results or swallowed lines. Each tab now receives its own independent winget service with its own process runner, so concurrent winget operations are fully isolated.

## [1.52.70] - 2026-07-10

### Fixed
- **The Startup tab could throw an error when toggling items while the "Hide Windows entries" filter was changed at the same time.** After enabling or disabling a startup item (or using "Enable all"), the tab finished its work on a background thread and then recounted the list from there. If the list was being re-filtered on the screen at that exact moment (for example, by ticking "Hide Windows entries"), the background recount could read the list while it was changing and fail. The work now finishes back on the screen's own thread — matching how the Services tab already does it — so the two can no longer collide.

## [1.52.69] - 2026-07-10

### Fixed
- **GPU temperature could stop being reported for the rest of a session on some NVIDIA machines.** The Dashboard reads temperatures from several places at once (the 2-second temperature tile, the manual Refresh button, and the always-on resource-history sampler). On a non-administrator session the NVIDIA read path set up its GPU connection without the guard the other sensor path already used, so two of those reads starting at the same moment could collide during that one-time setup — and if the colliding attempt failed, the app remembered "no NVIDIA GPU" permanently and quietly dropped GPU temperature until the app was restarted. That setup is now serialized behind the same lock the rest of the temperature reading already uses, so simultaneous reads no longer collide and a transient collision can't disable GPU temperature for the session.

## [1.52.68] - 2026-07-10

### Security
- **Built-in Windows repair tools are now launched by their full trusted system path instead of by name.** Several maintenance actions started Windows' own tools by name only (the SFC / DISM / CHKDSK / network helpers behind the repair tabs, the scheduled-task and registry edits behind the Context Menu and Startup tabs, the memory-diagnostic scheduler, and the auto-logon dialog on the System Fixes tab). When a program is started by name with a direct (non-shell) launch, Windows searches the application's own folder before the system folder — so if SysManager were run as a portable copy from a folder other programs can write to (for example, a Downloads folder) with administrator rights, a look-alike file planted there under the same name could have run in place of the real Windows tool, inheriting those rights. SysManager now resolves these tools to their protected `System32` location before launching them, so a planted look-alike can no longer take their place.

## [1.52.67] - 2026-07-09

### Changed
- **The sidebar "administrator" badge now uses the amber/gold admin colour instead of purple.** When SysManager runs elevated, the shield in the sidebar footer turned purple — but purple is the app's primary-action colour, while the admin/elevation cue is amber everywhere else (the "Run as administrator" buttons and banners). The shield now matches that amber, so the elevation cue is consistent across the app.
- **The Logs cards now use the shared large corner-radius token** instead of a slightly different hard-coded value, so their rounding matches the other cards.

### Added
- **The "Run as administrator" buttons now show a keyboard-focus outline.** They were missing the focus cue the other buttons already have, so keyboard users couldn't tell when one was focused; they now show an amber focus ring that matches the button's colour.
- **Completion notifications (toasts) are now announced by screen readers and can be dismissed with the keyboard.** The toast is now a live region — assistive technologies read it aloud when it appears — and its close control is a real button (keyboard-focusable, Enter/Space to dismiss) rather than a mouse-only target.

## [1.52.66] - 2026-07-09

### Fixed
- **The Windows Update tab no longer lets a module action run at the same time as another update operation.** Its "Check module" and "Install PSWindowsUpdate module" actions share the tab's single background engine and console with the Check-for-updates / History / Install-updates operations, but — unlike those — their buttons stayed enabled while another operation was running, so starting one could interleave its output on the shared console. They are now disabled while any update operation is in progress, matching the rest of the tab.

## [1.52.65] - 2026-07-09

### Fixed
- **The Cleanup tab no longer lets two repairs use its console at the same time.** Temp Cleanup and the SFC / DISM system repairs all stream their output into the tab's single console, but only SFC and DISM were stopped from running together — Temp Cleanup could still be started while SFC or DISM was running (they use different internal locks), so their output could interleave in the console. Starting any of the three while another is using the console is now declined with a clear message until the first finishes. (Emptying the Recycle Bin is unaffected — it doesn't use the console.)
- **Dragging the theme shade (background-brightness) slider no longer rewrites the theme file on every tick.** Each tiny movement saved the theme settings to disk; the shade still updates instantly, but the save now happens once you settle on a position instead of many times during a single drag.

### Added
- **Dashboard buttons now expose their names to screen readers.** The Quick Tune-Up button, the four Quick Actions (Run Quick Cleanup, Update All Apps, Check Windows Updates, Run Speed Test), and the temperature "Run as administrator" button are icon-and-text buttons that didn't carry an accessible name; they now do, so assistive technologies announce what each button does.

## [1.52.64] - 2026-07-09

### Fixed
- **The System Fixes output now uses the same live console as the rest of the app.** Its repair output was rendered with a slower approach that rebuilt the entire text on every new line and updated the UI thread line-by-line. It now uses the shared, capped, timestamped console control that the App Updates, Windows Update, and Cleanup tabs already use — smoother during long repairs, and consistent with those tabs (same Clear / Copy / Auto-scroll controls).

## [1.52.63] - 2026-07-08

### Fixed
- **The App Updates console no longer shows winget output from other tabs.** App Updates listened for winget output for as long as the app was open, and it shares one winget engine with the rest of the app — so running "Update All Apps" from the Dashboard (or any other winget action) quietly appended that output to the App Updates console. It now listens only while its own Check-for-updates or Update runs, so the console shows only what you started from that tab.

## [1.52.62] - 2026-07-08

### Fixed
- **The Performance tab no longer briefly freezes when you apply Visual Effects, Game Mode, Xbox Game Bar, or GPU settings.** These four toggles wrote to the registry (and, for visual effects, broadcast a system-wide settings change) directly on the UI thread, so the window could hang for a moment while the change was applied. They now run that work off the UI thread and show the progress/busy indicator while they do — matching how the Power Plan and Processor State toggles on the same tab already behave. What each toggle does is unchanged.

## [1.52.61] - 2026-07-08

### Fixed
- **The Process Manager can no longer show — or end — the wrong process when Windows reuses a process ID.** The list refreshes every second and matched rows by process ID (PID) alone, but Windows recycles PIDs, so between two refreshes the same PID can belong to a different program. When that happened, the row kept the old program's name and icon while showing the new program's activity — and because Kill targets the PID, confirming "End task" on that row would have ended the new program instead. Rows are now matched by PID together with the process start time, so a recycled PID is treated as a new process (old row removed, new one shown) and End task always acts on the process you see.

## [1.52.60] - 2026-07-08

### Fixed
- **The live resource-history charts no longer leak a small amount of memory each time they're rebuilt.** Each chart axis uses a "Segoe UI" font handle; when the chart was torn down and re-created (for example on a theme change), those font handles weren't released — only the paint objects were. They're now freed alongside the paints, the same way the chart legend's font already was.
- **Selecting a task in Task Scheduler no longer runs its "last run / next run" lookup twice.** Picking a task looked up its run details, and updating the row with those details re-triggered the selection handler, firing the same lookup a second time. The selection update is now guarded so the lookup runs once per selection.

## [1.52.59] - 2026-07-08

### Fixed
- **Cancelling a PowerShell-backed operation now ends cleanly instead of risking a stray error.** Two cases: cancelling an in-process PowerShell run reported the stop as a low-level pipeline error rather than a normal "cancelled", so callers watching for cancellation could treat it as a failure; and when cancelling an external PowerShell process, the attempt to stop its process tree only handled one of the errors it can raise, so an access-denied or partially-failed stop could surface as an unhandled error. Both now resolve to the standard "operation cancelled" outcome and swallow the expected stop-time errors.

## [1.52.58] - 2026-07-08

### Fixed
- **The Tweaks Hub no longer reads its settings on the UI thread while the app is starting.** The tab builds its Essential/Advanced tweak lists from registry-backed values; that read ran synchronously as the app started (the tab is created eagerly), adding to startup delay. It now runs off the UI thread and fills the list when ready — matching the Privacy Toggles tab — so there's no visible change, just a smoother start. Refresh runs off-thread too.
- **The Battery Health tab can no longer start a second read on top of its own startup scan.** The tab reads battery data automatically when it opens, but the Refresh button stayed enabled during that first read, so pressing it could run two reads at once. Refresh is now disabled while a read is in progress, matching the other tabs.

## [1.52.57] - 2026-07-08

### Fixed
- **Three background operations now fail safely instead of risking an unhandled error.** Ending a process could throw an unhandled error when part of its child-process tree refused to close — the Process Manager now reports that as a normal "couldn't end it" result. And loading the saved Speed Test history or the stored performance-tweak baseline could throw if the file was momentarily unreadable (locked or permission-denied) rather than access-denied being treated like any other read error; both now fall back cleanly (empty history / fresh baseline), matching how saving already behaves.

## [1.52.56] - 2026-07-08

### Fixed
- **Applying and restoring Environment Variables can no longer run at the same time and leave a mixed result.** Since 1.52.51 the slow parts of Apply and Restore run off the UI thread, which keeps the window responsive — but it also meant that starting a Restore and then pressing Apply (or the reverse) could rewrite the user and system variables from both operations at once, leaving the environment in an unpredictable mix of the two. Apply and Restore now take the same app-wide system-modification lock the SFC/DISM tools use, so one waits with a clear message until the other finishes. Introduced in 1.52.51.

## [1.52.55] - 2026-07-08

### Fixed
- **Shredding a folder that contains a junction or symlink can no longer delete an empty directory outside the folder you selected.** When removing the emptied directories after a folder shred, SysManager listed sub-directories with a recursive scan that follows junctions and symbolic links — so a link inside the selected folder that pointed elsewhere was followed, and an empty directory at its target (outside your selection) could be removed. The cleanup now skips junctions and symlinks exactly like the file-shredding pass already does: it never descends through a link and only ever removes empty directories inside the folder you chose. Introduced in 1.52.47 with the folder-shred cleanup rewrite; no file contents were ever at risk — only empty directories, and only through a link located inside the selected folder.

## [1.52.54] - 2026-07-08

### Fixed
- **The Process Manager stops re-reading each program's static details every second.** On every 1-second refresh the process list read each process's description, executable path and file-existence check — even for the (often hundreds of) processes it was already showing and whose details it keeps unchanged. It now reads those static details only for processes that have just appeared and refreshes only the live metrics (CPU, memory, threads, status) for the rest, cutting the per-tick work substantially on machines with many processes.

## [1.52.53] - 2026-07-08

### Fixed
- **System-info refreshes now make one Windows query instead of two.** Each refresh read the current uptime and the memory totals with two separate WMI queries against the same OS object; they are now fetched in a single query, one fewer round-trip per poll. No visible change — the same uptime and memory figures.

## [1.52.52] - 2026-07-08

### Fixed
- **The File Shredder queue can no longer be changed while a shred is running.** During a shred, the Add Files, Add Folder and Remove buttons stayed active, and the shred walked the live queue by index across each file's overwrite — so adding or removing an item mid-shred could skip an item, act on the wrong one, or error. The shred now works on a snapshot of the queue taken when it starts, and the add/remove buttons are disabled until it finishes.

## [1.52.51] - 2026-07-08

### Fixed
- **The Environment Variables tab no longer freezes the app while applying, restoring, or refreshing.** Applying changes broadcasts a system-wide "settings changed" message that waits up to 5 seconds for other windows to respond, and restoring or refreshing re-reads every user and system variable — all of which ran on the UI thread, freezing the whole window until they finished. The slow parts (the broadcast, the restore, and the full re-read) now run off the UI thread, so the app stays responsive; the actual variable writes are unchanged.

## [1.52.50] - 2026-07-08

### Fixed
- **The Dashboard does less redundant work at startup and while polling.** Two efficiency fixes, both leaving what you see unchanged: (1) the live GPU tile re-enumerated all physical GPUs through the NVIDIA API on every 300 ms poll — it now resolves the GPU once and reads usage/memory from that cached handle each tick; (2) the health score's heavy disk-SMART/memory/battery computation ran three times at startup (the Dashboard's own load plus the SMART and memory alert scans, two of them at once) — the alert scans now reuse the score the Dashboard already computed, falling back to a fresh computation only if that load failed.

## [1.52.49] - 2026-07-08

### Fixed
- **GPU temperature polling no longer re-initializes the NVIDIA API every couple of seconds — and no longer throws once per poll on PCs without an NVIDIA GPU.** The temperature reader called NVIDIA's `Initialize()` and re-enumerated GPUs on every poll (about every 2 seconds). On a machine with no NVIDIA GPU that raised and swallowed an exception on every single poll; on NVIDIA machines it redid the one-time setup each time. SysManager now initializes the NVIDIA API at most once, remembers whether an NVIDIA GPU is present, and skips the read entirely afterwards when there isn't one — so there's no per-poll exception and no repeated setup. GPU temperatures on NVIDIA machines are unchanged.

## [1.52.48] - 2026-07-08

### Fixed
- **Starting or stopping a Windows service from the Services tab no longer risks a cross-thread error.** After a start/stop completed, SysManager refreshed the service row and the on-screen status from a background thread — unlike the Enable/Disable actions in the same tab, which correctly resume on the UI thread. Updating UI-bound state off the UI thread can raise a cross-thread exception. Start and Stop now resume on the UI thread just like Enable and Disable, so the row and status update safely.

## [1.52.47] - 2026-07-08

### Fixed
- **Shredding a folder no longer plain-deletes (leaving recoverable) any file it could not securely overwrite.** When shredding a folder, a file that could not be securely overwritten — for example a locked or permission-denied file, or a hard-linked file the shredder now refuses — was still removed by the final recursive folder delete. That plain delete leaves the data recoverable while the operation reported success, so you could believe such a file had been securely erased. SysManager now leaves any file it could not securely overwrite in place (removing only the emptied folders around the files it did shred) and reports how many were left, so the outcome is honest.

## [1.52.46] - 2026-07-08

### Fixed
- **The File Shredder now refuses to shred a file that has more than one hard link, instead of destroying data shared with other locations.** A file can have several names (hard links) that all point at the same data — including names outside the folder you selected, or a link that shares a protected system file's data. Overwriting the file's bytes to "shred" it would have destroyed that shared data everywhere, and the safety check that blocks system paths only sees the name you picked (it cannot tell the data is shared). The shredder now detects multiple hard links up front and refuses with a clear message, leaving every copy intact; remove the extra links first, or delete the file normally.

## [1.52.45] - 2026-07-08

### Fixed
- **The System Info snapshot no longer risks an unhandled error on systems where the modern disk-info source is unavailable.** When the Windows Storage management interface cannot be reached (some older or minimal Windows installations), SysManager falls back to the classic disk query — but that fallback ran without its own error handling, so a second failure there (or a malformed size value from one drive) could surface as an unhandled error instead of simply showing the disk list it managed to read. The fallback is now guarded like every other hardware query: a fault degrades to the partial disk list, and a single unreadable drive is skipped rather than aborting the whole snapshot.

## [1.52.44] - 2026-07-08

### Fixed
- **Cancelling a duplicate-file scan now reports it as cancelled instead of showing partial results as "Complete."** If you stopped a duplicate scan between files (rather than while a file was being hashed), the scan quietly finished and presented whatever it had found so far as a completed result. It now treats cancellation consistently — the scan reports as cancelled and no partial list is shown as if it were the full result — matching how the Large Files scan already behaves.

## [1.52.43] - 2026-07-08

### Fixed
- **Disabling a Startup-folder program from the Startup Manager now actually stops it from launching.** For items that live in the Windows Startup *folder* (as opposed to the registry Run keys), SysManager identified the item by its name without the file extension, but Windows tracks the enabled/disabled state under the item's full filename (for example `Spotify.lnk`). Because of that mismatch, a Startup-folder item that was already disabled showed as enabled, and turning one off wrote the "disabled" flag under a name Windows ignores — so the program kept starting. SysManager now uses the full filename for these items, so their on/off state reads correctly and disabling one takes effect. (Registry Run entries and scheduled-task entries were unaffected.)

## [1.52.42] - 2026-07-08

### Fixed
- **On non-English Windows, the processor "minimum state" could be saved as 0% and later restored as 0%.** Before applying a performance tweak, SysManager takes a safety snapshot that records your current processor *minimum state* (read from Windows' `powercfg`). On English Windows it read the right value, but on a translated Windows the label it searched for was in another language, so it fell back to the first number in `powercfg`'s output — which is the fixed "minimum possible" value (0%), not your actual setting. That wrong 0% went into the snapshot, so a later "Restore All" wrote 0% back as if it were your original minimum. It now reads the correct value in any display language (the real setting is always the second-to-last value `powercfg` prints), so snapshots and restores are accurate on every Windows locale.

## [1.52.41] - 2026-07-08

### Fixed
- **Four background operations no longer risk an unhandled error when Windows denies file or system access.** Saving and loading the activity log, enriching the drive list with disk media/bus details, downloading an update, and caching an app icon each already handled ordinary I/O errors but not an "access denied" (UnauthorizedAccess) error from a locked-down or permission-restricted system — which could surface as an unhandled exception (and, for the activity log, on the UI thread). Each now treats access-denied like the I/O errors it already handled: the activity log and icon cache degrade quietly, the drive list is still returned (just without the extra media/bus detail rather than coming back empty), and a denied update download reports failure cleanly so the About tab falls back to opening the download in your browser.

## [1.52.40] - 2026-07-08

### Fixed
- **Shredding a folder that is a junction or symlink no longer destroys data outside it.** If the folder you picked for "Shred Folder" was itself a junction or symbolic link (for example, one that points at another drive or folder), the shredder followed it and securely erased the files at the *link's target* — data outside the folder you actually selected. It now refuses to shred a folder that is a junction/symlink and asks you to pick the real target folder instead; the existing protection that skips links found *inside* the folder is unchanged.
- **A folder shred no longer reports failure after it has actually finished.** Removing the emptied folder structure at the end of a shred handled only one kind of error; if a leftover read-only or locked entry denied that final cleanup, the whole operation reported failure even though every file had already been securely overwritten. That cleanup denial is now logged and the shred is reported as completed.

## [1.52.39] - 2026-07-07

### Fixed
- **Context-menu entry names capitalize correctly on Turkish Windows.** The first letter of a cleaned-up context-menu entry name was upper-cased using the current culture, so on a Turkish (tr-TR) system a leading "i" became "İ" (dotted capital I) rather than "I", subtly corrupting names. It now upper-cases invariantly, since these are program and shell-verb identifiers rather than locale-sensitive prose.
- **The resource-history file is now rewritten atomically.** When trimming samples past the retention window, the usage-history file was rewritten in place with a single full-file write; a crash or power loss mid-write could leave it truncated or corrupt. It is now written to a temporary file and swapped in atomically, so an interrupted prune can never damage the existing history.
- **The Standby List Cleaner's background auto-refresh can no longer crash the app.** Its two-second timer tick ran without a top-level guard, so an unexpected fault while reading memory status or auto-purging would surface as an unhandled background exception and take the whole app down. The tick now catches and logs any such fault, matching the guard the tray-icon timer already uses.

## [1.52.38] - 2026-07-07

### Fixed
- **The Dashboard's "Update All Apps" quick action now uses the app's shared, reliable updater.** The one-click "Update All Apps" button on the Dashboard ran its own hand-written winget command that had drifted from the one the App Updates tab uses — it was missing the flags that suppress winget's interactive prompts and its garbled progress animation and that include packages whose installed version winget can't read, and it always reported "All apps updated" even when the update actually failed. It now calls the same shared updater the App Updates tab uses, so a one-click update behaves identically to the full tab and reports the real outcome.

## [1.52.37] - 2026-07-04

### Fixed
- **The "Run as administrator" button is now legible on the Light themes.** Its golden-amber styling used fixed dark-theme colors (pale yellow text), so on a light preset the label washed out against the near-white button. It now uses the same theme-aware warning colors as the rest of the app, which switch to a dark amber on light themes — matching the elevation banners that were already fixed.

## [1.52.36] - 2026-07-04

### Fixed
- **Copying a command from the CLI Interface tab no longer risks an unhandled error if the clipboard is briefly unavailable.** The copy handler only caught one specific clipboard failure type; a different (but related) clipboard error would have gone unhandled. It now catches the documented base error type — matching every other copy-to-clipboard action in the app — so a locked or unavailable clipboard always shows the friendly "try again" message instead.

## [1.52.35] - 2026-07-04

### Fixed
- **Installing Windows updates no longer aborts the whole batch when one update fails to download.** In the install loop, a failed download released its update collection early and then `continue`d — but the loop's cleanup block always releases that same collection too, so it was released twice. The second release threw an error that stopped the entire install run on the first failed download. The redundant early release is removed; the collection is now released exactly once by the cleanup block, so the batch continues to the next update.

## [1.52.34] - 2026-07-04

### Fixed
- **A blank or malformed update checksum file no longer crashes the update check.** When verifying a downloaded update, SysManager reads the release's `.sha256` file; if that file came back empty or whitespace-only, parsing it threw an unhandled error instead of simply reporting the checksum as unverified. It now degrades cleanly to a verification failure (the update is treated as unverified rather than crashing), matching how a missing checksum file is already handled.

## [1.52.33] - 2026-07-04

### Fixed
- **Long names in the Boot Analyzer and Shortcut Cleaner tables now truncate with an ellipsis.** Their free-text columns clipped long values hard at the column edge with no "…", unlike the other data tables in the app. Both now trim consistently. Also aligned the Work-in-Progress placeholder title to the app's shared "Display" heading style instead of a one-off inline font, so all headings stay uniform.

## [1.52.32] - 2026-07-04

### Fixed
- **System Logs severity cards and the "missing folder" tag stay legible on the Light themes.** The System Logs Critical/Errors/Warnings/Info cards used hardcoded translucent backgrounds, slate-gray labels and a fixed white card border; the Environment Variables "missing folder" tag used a fixed light-red. These now use the app's theme-aware brushes, so they render correctly on every theme. (The Critical card keeps its distinct brighter-red hue so it stays visually separable from Error, and the glow effects are unchanged.)

## [1.52.31] - 2026-07-04

### Fixed
- **Screen readers now announce a clear label for the action buttons on System Health, Network Repair and Windows Features.** Several buttons (Scan, Check memory errors, Run SMART check, chkdsk scan/cancel, Flush DNS, Reset Winsock, Reset TCP/IP, Run as administrator) had no accessibility name, so assistive tech only had the raw content to read. They now carry a descriptive `AutomationProperties.Name`, matching the pattern already used elsewhere in those views.

## [1.52.30] - 2026-07-04

### Fixed
- **Refreshing the Privacy & Telemetry tab no longer briefly freezes the window.** The "Refresh" button read every privacy registry key synchronously on the UI thread, so the app hitched while the toggles reloaded. The refresh now reads the registry on a background thread (matching how the tab loads initially) and updates the UI when done, keeping the window responsive.

## [1.52.29] - 2026-07-04

### Fixed
- **Toggle switches and safety filter chips now follow the theme on the Light presets.** The "off" state of every toggle switch (Privacy toggles, feature switches, etc.) used a hardcoded dark-slate track that looked nearly black on the light themes; the safety filter chips (Safe / Caution / Critical) used hardcoded translucent status colors. Both now use the app's theme-aware brushes, so the off-toggle reads as a soft muted track and the chips stay legible on every theme. On the dark themes the look is unchanged.

## [1.52.28] - 2026-07-04

### Fixed
- **Ping target cards and Context Menu preview tooltips are now theme-aware on the Light themes.** The per-target cards on the Ping tab used a hardcoded translucent-white border and fixed slate-gray metric labels (LATENCY/AVG/JITTER/LOSS) that were nearly invisible on the light presets; the Context Menu style-preview tooltips used fixed `Gray`/`DimGray` text. Both now use the app's theme text and border brushes, so they stay legible on every theme. On the dark themes the look is unchanged.

## [1.52.27] - 2026-07-04

### Fixed
- **Dashboard "Run as admin" pill now follows the theme.** When SysManager isn't elevated, the small amber "Run as admin for all sensors" pill on the Dashboard used hardcoded amber tints that didn't adapt to the theme (and were tuned for dark). It now uses the app's theme-aware warning colors, so it stays legible on the light presets too.

### Changed
- **Dashboard metric colors are now named theme brushes.** The MEMORY (blue) and GPU (purple) accent colors were copy-pasted hex values repeated across the metric cards and quick-action list. They're now defined once as named `MetricBlue`/`MetricPurple` brushes and referenced everywhere — no visual change, just a single source of truth (matching how the CPU card already used a named brush).

## [1.52.26] - 2026-07-04

### Fixed
- **Long task names and paths on the Task Scheduler tab now truncate with an ellipsis.** The Task and Path columns rendered long text clipped hard at the column edge with no "…" indicator, unlike the other data tables in the app (Services, Drivers, Uninstaller, etc.) which trim cleanly. Both columns now use the same character-ellipsis trimming, so overflowing text ends in "…" and the layout stays tidy.

## [1.52.25] - 2026-07-04

### Fixed
- **Status badges (Startup, Windows Features, Uninstaller, Process Manager) are now readable on the Light themes.** The colored status pills — "Enabled"/"Disabled" on Startup Manager, "Done"/"Failed"/"Reboot required" on Windows Features, the "winget"/"Local" source tag and status on Uninstaller, and the process-state pill — used hardcoded colors tuned for the dark themes (pale green/amber text, translucent-white borders). On the six light presets they washed out to near-invisible (green-on-green text, no visible border). They now use the app's theme-aware status colors, so they stay legible on every theme. The "Irreversible" badge on Deep Cleanup was migrated the same way. On the dark themes the look is unchanged.

## [1.52.24] - 2026-07-04

### Fixed
- **Dropdowns (combo boxes) now match the theme and stay readable on the Light themes.** The folder/path pickers on Duplicate Finder and Disk Analyzer, and other dropdowns across the app, used the default Windows combo-box look — a fixed white popup with near-black text that ignored the selected theme. On the dark themes it clashed; on the light themes the editable text and dropdown items were low-contrast. Combo boxes now use a theme-aware style (matching the app's text fields — themed surface, border, rounded corners, accent focus, and a properly styled dropdown list), so they look consistent and stay legible on every theme.

## [1.52.23] - 2026-07-03

### Fixed
- **Warning, success and info banners are now readable on the Light themes.** Semantic status text (the amber "a finer timer wakes the CPU…" note on Timer Resolution, the "Running as administrator" banners, and similar warning/success/info messages across the app) used pale colors tuned for the dark themes. On the six light presets those washed out to near-invisible against the light banner — the amber warning text measured only ~1.4:1 contrast (WCAG AA needs 4.5:1). The theme engine now recomputes all status colors per theme, using darker, saturated tones on the light presets, so every banner stays legible on every theme. On the dark themes nothing changes.

## [1.52.22] - 2026-07-03

### Fixed
- **Outline buttons are now visible on the light themes.** "Ghost" buttons (e.g. Export text / Copy on the System Report tab, and similar secondary actions elsewhere) drew their border in a translucent white that was invisible against the light presets' pale backgrounds — the buttons looked borderless. They now use the theme's own border color, so they're clearly outlined on every theme.
- **Startup Manager asks before re-enabling everything.** The "Enable All" button immediately re-enabled every disabled startup item — a bulk system change that adds boot time — with no confirmation. It now asks first (showing how many items will be affected), matching the confirm-before-bulk-change behavior used elsewhere in the app.

## [1.52.21] - 2026-07-03

### Fixed
- **Editing the hosts file no longer erases your own comments.** When you added, removed, or toggled an entry on the DNS & Hosts tab, SysManager rewrote the file from just the address mappings — silently dropping any standalone comment lines or blank spacing you'd written (section notes, documentation). Those comment and blank lines are now preserved through an edit, kept above the entries. Repeated saves stay stable (no duplicated headers), and a file with no comments is written exactly as before.

## [1.52.20] - 2026-07-03

### Fixed
- **The theme picker now opens on your saved theme's presets.** If you'd set a Light or Custom theme, the theme popup initially built its preset list from the default Dark mode and only corrected itself once you clicked something — so it briefly showed the wrong set of presets. It now reads your saved mode first, so the correct presets appear immediately.
- **Ping and System Logs numbers are now readable on the Light themes.** The average-ping / jitter / latency figures on the Network Ping tab and the severity counts (Critical, Errors, Warnings, Info) on the System Logs tab used fixed pale colors that nearly disappeared against the light preset backgrounds. They now use the app's semantic status colors, so they stay legible on every theme.

## [1.52.19] - 2026-07-03

### Fixed
- **Recycle Bin size estimates now match what emptying actually frees.** Both Quick Cleanup and Deep Cleanup summed the whole hidden `$Recycle.Bin` folder on every drive — which, on a shared PC (especially when running as administrator), also counts *other users'* deleted files. But emptying the bin only ever clears the current user's items, so the "X MB in Recycle Bin" figure could be far larger than what actually gets freed. The estimate now measures only the current user's Recycle Bin, so the number is honest.
- **Installed-app detection no longer mislabels similarly-named apps.** In the Bulk Installer, an app was marked "Installed" if its winget Id appeared anywhere in a `winget list` row — so an app whose Id is a prefix of another (e.g. `Microsoft.Teams` vs. an installed `Microsoft.Teams.Classic`) was wrongly shown as already installed. Detection now compares exact winget Ids.
- **System Report and About page now show the correct VRAM for GPUs over 4 GB.** Video memory was read from a 32-bit WMI field that caps at ~4 GiB, so an 8 GB or 12 GB card was reported as ~4 GB. The true size is now read from the graphics driver's 64-bit registry value, falling back to the old field only when that isn't available.

## [1.52.18] - 2026-07-03

### Fixed
- **A broken WMI service no longer throws an error dialog when reading system info.** The OS, CPU, and memory queries weren't guarded, so on a machine with a damaged or stopped WMI service the Dashboard/System Report could surface a raw error. Each query now degrades to safe defaults (matching how the disk query already behaved), so the app keeps working with whatever information is available instead of failing.

## [1.52.17] - 2026-07-03

### Fixed
- **System Logs no longer come back empty on some non-English regional settings.** The Event Log query built its time filter with the OS's regional time separator, so on a region that uses `.` instead of `:` in times (e.g. Finnish) the timestamp became invalid and the query silently failed — the Logs tab showed nothing. The timestamp is now always formatted in the culture-independent ISO form, so log filtering works regardless of regional settings.

## [1.52.16] - 2026-07-03

### Fixed
- **Cancelling a Deep Cleanup scan or clean now reports "cancelled", not "complete".** A scan or clean stopped partway through cancellation still reported success (and fired a "complete" notification) with partial results. Cancelling now correctly ends the operation as cancelled.
- **Driver list can't be started twice at once.** The "List drivers" button had no busy-guard (unlike the other scan tabs), so a second click during a scan could corrupt the collected output. It's now disabled while a scan is running, matching the App Updates / Uninstaller tabs.
- **Hardened Performance-tab power queries against a threading race.** The four `powercfg` reads collected output into a plain list from a callback that fires on two reader threads at once, which could corrupt the captured lines. They now use a thread-safe queue, matching the winget service.

## [1.52.15] - 2026-07-03

### Fixed
- **One invalid entry no longer aborts a whole batch upgrade or uninstall.** In App Updates and the Uninstaller, if a single package had an Id that failed validation (e.g. an Add/Remove-Programs GUID), the error thrown before that item even started would abort the entire remaining batch. Each item's error is now recorded on its own row and the batch continues with the rest.
- **Single-instance activation is more robust.** The background listener that focuses the existing window when you launch a second copy wrapped its whole loop in one try/catch, so a single unexpected error could stop it permanently for the rest of the session. Each iteration now handles its own errors and keeps listening.

## [1.52.14] - 2026-07-03

### Security
- **File Shredder now overwrites through a single locked handle, closing a path-swap race.** The shredder validated the target path, then reopened the file by name for each overwrite pass and the final truncate. Between the validation and those reopens, a reparse point (junction/symlink) swapped in at the path could have redirected the overwrite to a different file — a time-of-check/time-of-use race. The shredder now opens the file once with an exclusive (no-sharing) handle, re-verifies *that handle's* real resolved path against the protected-folder denylist before writing anything, and reuses the same handle for every pass and the truncate — so nothing can redirect the operation once it starts. The existing symlink/junction protections are unchanged.

## [1.52.13] - 2026-07-03

### Fixed
- **App Updates now shows upgrades on Windows display languages we don't have column titles for.** The winget table parser identifies the "Available" and "Source" columns by matching their header word against a list of known translations. On a language not in that list (e.g. Russian, Korean), those two columns weren't found, so every upgrade row came back with a blank "Available" version and was silently dropped — App Updates showed **no upgrades at all**. For the standard five-column upgrade table the parser now falls back to the fixed column order when a title isn't recognized, so upgrades appear regardless of display language. Four-column tables stay ambiguous by design and are left untouched (they can't be disambiguated by position).

## [1.52.12] - 2026-07-03

### Fixed
- **Turning a privacy toggle off now removes the setting instead of forcing the opposite.** When you switched a privacy protection back off (or used Undo in the Tweaks hub), SysManager wrote the "off" value into the registry — for the policy-backed toggles this **created an enforced Group Policy the machine may never have had**. The worst case was "Disable diagnostic data": reverting it wrote `AllowTelemetry = 3`, which is *enforced Full telemetry* — strictly worse than the value simply being absent. Reverting a toggle now **deletes** our registry value so Windows falls back to its own default, which is the correct meaning of "undo". This applies to both the Privacy & Telemetry tab and the Tweaks hub's Undo.

## [1.52.11] - 2026-07-03

### Fixed
- **Performance tab now reverts correctly on non-English Windows.** Two power settings were read by matching English text that Windows translates in other display languages, so on a non-English system the "Restore" path misbehaved:
  - The **active power plan** was located by the English "GUID:" label. On a localized Windows that label is translated, so the plan couldn't be read and Restore silently skipped restoring it. The plan is now identified by its GUID (identical in every language), so it restores correctly regardless of display language.
  - The **processor minimum state** was read by an English label and fell back to a fabricated 5% when the label didn't match — which then got **written back** as the "restored" value on non-English machines. It now returns "unknown" when it genuinely can't be read, and Restore leaves the setting untouched rather than forcing a wrong value. The English fast-path is unchanged.

## [1.52.10] - 2026-07-03

### Fixed
- **Browser Cleaner now actually cleans Opera.** Opera was listed as a supported browser but its cleanup silently did nothing: the paths were built for the Chromium `\Default\` profile layout that Chrome/Edge/Brave use, while Opera Stable stores its profile directly under `Opera Software\Opera Stable` (no `\Default\`) and keeps cookies/history/sessions under Roaming AppData rather than Local. Every Opera path missed, so scan found nothing and clean freed nothing. Opera now uses its real on-disk layout; Chrome, Edge, Brave, and Firefox are unchanged.

## [1.52.9] - 2026-07-03

### Fixed
- **Disk Analyzer no longer crashes when "Top files" is set to 0.** The large-file scanner takes a "keep the top N" count from the Deep Cleanup input. If that value was 0 (or negative), the scan threw an internal error the moment it found its first file and the operation faulted. The scanner now treats a non-positive count as "nothing to keep" and returns an empty result instead of crashing.

## [1.52.8] - 2026-07-01

### Fixed
- **App Updates, Uninstaller, and Bulk Installer now work on non-English Windows.** These tabs read winget's table output by matching the English column headers ("Name / Id / Version / Available / Source"). On a localized Windows, winget translates those titles (e.g. German "Name / Kennung / Version / Verfügbar / Quelle"), so the match failed and every list came back **empty** — App Updates showed no updates, the Uninstaller showed no apps, and Bulk Installer search returned nothing. The parser now locates the table via the dashes separator row that winget prints in every language and maps the columns by position instead of by the English words, so it works regardless of the Windows display language. The English fast-path is unchanged.

## [1.52.7] - 2026-07-01

### Fixed
- **Chart text is now readable on the light themes.** The Ping/Traceroute latency chart and the Resource History usage/temperature charts painted their axis labels, legend, and tooltip in a fixed near-white color that was set once and never updated when you switched themes. On any of the six light presets that meant white-on-white — the axis values, time labels, and legend were effectively invisible. The chart text now follows the active theme (dark-on-light on light presets, light-on-dark on dark presets) and repaints instantly when you change the theme. The previously-unused theme-change signal is now wired up to drive this.
- **Update banner and toast notification follow the theme.** Both used a fixed dark background, so on a light preset the update banner was a dark box clashing with the light UI and the toast's title could render dark-on-dark. They now use the theme's elevated surface color and stay legible on every preset.

## [1.52.6] - 2026-07-01

### Fixed
- **`--json` now stays machine-readable on CLI errors.** When an unknown flag or a bare usage error was hit together with `--json`, the CLI printed the human help text instead of JSON — so piping the output to a JSON parser (`SysManager.exe --bogus --json | ConvertFrom-Json`) broke. Both error paths now emit valid JSON: an unknown flag returns `{"error": "..."}` and a bare usage error returns the machine-readable command catalog.
- **Headless CLI reports a runtime fault as an error (exit 1), not a usage error (exit 2).** If a CLI command threw unexpectedly the process exited with code 2, which conventionally means "you typed the command wrong." An unexpected fault is now logged and exits with code 1 (general error), so scripts can distinguish a bad invocation from a genuine failure.
- **Startup crashes are now logged instead of vanishing.** The unhandled-exception handlers were registered *after* the dependency container, tray icon, and resource-history sampler were built — so a failure during that early startup surfaced as a bare Windows crash with no log entry. The handlers are now wired first, so any startup fault is captured in the log.
- **Resource-history retention config load no longer risks an unhandled exception at startup.** Reading the retention setting caught malformed JSON and I/O errors but not an access-denied error; since it runs during construction, that gap could throw in the unprotected startup window. It now degrades to the 7-day default on access-denied too.

## [1.52.5] - 2026-07-01

### Fixed
- **Dashboard vitals polling no longer re-scans the RAM hardware inventory every 300 ms.** The live CPU/RAM/GPU snapshot the Dashboard refreshes ~3× a second was re-enumerating the physical memory modules (bank, manufacturer, capacity, speed, part number) via WMI on every tick, even though that inventory is fixed hardware that never changes while the app runs. The DIMM list is now read once and cached — matching how OS, CPU, and disk info were already cached — so only the dynamic RAM totals are refreshed per poll. Lower background CPU/WMI overhead with no change to what's displayed.

## [1.52.4] - 2026-07-01

### Fixed
- **In-app updater no longer aborts every update as "possible tampering."** The Authenticode check treated an *unsigned* download as an invalid signature and cancelled the install — but SysManager ships unsigned builds, so the About-tab "Download → Install" flow was blocked for every release. (Most people update through winget, so this went unnoticed.) An unsigned binary is now correctly accepted; file integrity is still enforced by the SHA256 verification that runs first, and the check now only rejects a file whose signature data is genuinely unreadable.

## [1.52.3] - 2026-07-01

### Fixed
- **Performance tab "Restore" no longer mis-reverts Xbox Game Bar.** Restore captured the Game Bar overlay (`AppCaptureEnabled`) and per-game DVR (`GameDVR_Enabled`) as two independent settings but then wrote a single combined value to both keys — so if you had one on and the other off, restoring forced both off and silently lost the on state. Each setting is now restored to exactly what the snapshot captured.

## [1.52.2] - 2026-07-01

### Fixed
- **App-update count on the Dashboard is now accurate.** The dashboard alert used a fragile "count non-blank winget lines minus two" heuristic that mis-counted whenever winget's header/footer layout shifted. It now reuses the same parsed upgrade list the App Updates tab shows (rows that actually have an available version), so the two surfaces always agree.
- **Bulk Installer search results parse reliably for wide or non-Latin app names.** The search parser previously used fixed character offsets that mis-sliced rows containing wide/CJK characters. It now routes through the shared winget table parser (the same one the upgrade list uses), so column detection is handled in one place.

## [1.52.1] - 2026-06-30

### Added
- **Safety note in the About tab.** The legal section now explicitly states that some tools change system settings, the registry, or delete files — use them at your own risk, review each action before confirming, and back up important data first — alongside the existing reminder that SysManager creates a System Restore point where it can and keeps changes reversible.

## [1.52.0] - 2026-06-30

### Changed
- **App icons in the Bulk Installer are now opt-in.** SysManager no longer contacts the web for app icons by default — this keeps the "no cloud, no telemetry" promise intact out of the box. A new "Load app icons from the web" checkbox in the Bulk Installer toolbar turns the feature on; only then are icons fetched from Google's favicon service (the choice is remembered). Already-cached icons still load offline. The README now documents exactly when the app uses the network.

## [1.51.14] - 2026-06-30

### Security
- **Input validators that feed a command/registry boundary now reject a trailing newline.** Nine allowlist patterns (winget package IDs, service names, blocked-executable names, Appx package names, environment-variable names, Windows-feature names, event-log provider names, and hostnames) used `^…$` anchors, which in .NET match before a trailing newline — so a value like `pkg\n` slipped through and could smuggle a second line into the command. They now use absolute `\A…\z` anchors. The winget package-ID pattern additionally dropped `\s` (which allowed tabs/newlines mid-string) in favour of a literal space.
- Added injection-rejection negative tests for the winget package-ID validators (`UpgradeAsync`, `UninstallAsync`) and the service-name/start-type validator (`SetStartupTypeAsync`), covering command separators, chaining, substitution, quotes, newlines, and over-length input.

## [1.51.13] - 2026-06-30

### Security
- **Settings Watchdog restore is now allowlisted to its own catalog.** The restore path verified the hive but not the full setting; it now only ever writes a setting present in the watchdog's curated catalog (matched by exact path and value name), so it can never be repurposed to write an arbitrary registry key.
- **Defender exclusion paths are validated at the service boundary.** Adding a scan exclusion now rejects empty, non-rooted, and wildcard (`*`/`?`) paths at the service itself (not just the UI), so an over-broad exclusion can't weaken Defender via any caller.
- **The downloaded Ookla speed-test CLI now has its full certificate chain validated.** Verification previously checked only that the Authenticode subject contained "Ookla"; it now also builds and validates the certificate chain to a trusted root with online revocation, failing closed (and deleting the binary) if the chain is not valid.
- **Event-log provider filtering uses an allowlist instead of character stripping.** A provider name with unexpected characters is now rejected outright rather than silently stripped (which could mangle a legitimate name into a different, wrong filter); injection remains blocked.
- **The single-instance named pipe is restricted to the current user.** The activation pipe is now created with an explicit ACL granting only the current user connect rights, instead of relying on the default permissions.

## [1.51.12] - 2026-06-30

### Fixed
- **File shredder no longer follows symlinked files out of the selected folder.** When shredding a folder, the file walk skipped reparse-point directories but still included reparse-point files, so a symlink/hardlink file could cause its link target — possibly outside the folder — to be overwritten. Symlinked files are now skipped, matching the existing directory behaviour.
- **Restoring the hosts file backup preserves the file's security descriptor.** Restore used a plain overwrite-copy, which relinks a new file that inherits only the folder's default permissions; it now replaces the file in place (like Save does), keeping the hardened hosts-file ACL.
- **Hostname validation for new hosts entries is stricter.** The validator accepted malformed names such as consecutive dots (`a..b`), a leading/trailing dot, and over-long labels. It now enforces proper DNS label rules (1–63 chars per label, no consecutive or edge dots).

## [1.51.11] - 2026-06-30

### Fixed
- **A native string buffer was leaked on every Known-Folder lookup.** The Downloads/Documents/Desktop/Pictures/Music/Videos path resolver marshalled the Win32 result as a managed string but never freed the underlying COM-allocated buffer; it now takes the raw pointer and frees it, so repeated lookups (cleanup scans, etc.) no longer leak memory.
- **Two power-plan changes could corrupt a concurrent power-query's output.** The power-plan/processor-state/hibernation writers shared the same process runner as the readers that parse `powercfg` output, but ran without the reader's serialization gate; a write running during a read could interleave the output stream. The writers now take the same gate.
- **Listing installed apps could drop or corrupt a line under a thread race.** The winget-list output collector used a plain list that both the stdout and stderr reader threads wrote to concurrently; it now uses a thread-safe queue, matching the upgrade-list path.

## [1.51.10] - 2026-06-30

### Fixed
- **Cancelling an update download no longer deletes a previously-downloaded copy.** If a fresh download was cancelled, the cleanup step also removed any already-cached, still-valid installer, forcing an avoidable re-download on the next launch. Cancellation now only removes the partial in-progress file.
- **A safe-to-disable service description is no longer silently dropped.** The service safety database held two entries for Windows Audio that differed only in capitalisation; in the case-insensitive lookup the second silently overwrote the first, losing the "only disable on headless servers" guidance. The duplicate was removed so the fuller description is always shown.
- **The default-gateway detection prefers the real physical route.** Ping/monitoring picked the first active adapter's gateway, which on a machine with a VPN or virtual adapter could be the wrong one. Tunnel adapters are now skipped and physical adapters (Ethernet/Wi-Fi, fastest first) are preferred.

### Changed
- Removed an unreachable `catch` in the Recycle Bin helper: `SHEmptyRecycleBin` reports failure through its return code, not an exception, so the failure path now reads and logs the HRESULT directly.

## [1.51.9] - 2026-06-30

### Fixed
- **A failed uninstaller launch no longer aborts the whole batch.** When uninstalling several apps at once, one app whose uninstaller executable could not be launched (missing, blocked, or corrupt) threw an error that stopped the entire run and surfaced a raw error dialog. The failure is now recorded on that app's row and the batch continues with the remaining apps.
- **Defender toggles report failures cleanly instead of crashing to an error dialog.** Enabling/disabling PUA protection or Controlled Folder Access, and adding/removing scan exclusions, now catch a PowerShell runspace-level fault and show it as a status message — matching how the status refresh already behaved.
- **System Restore actions report failures cleanly.** Creating or starting a restore now catches a runspace/WMI-level fault and shows it as a status message instead of letting it surface as a global error dialog.

## [1.51.8] - 2026-06-30

### Fixed
- **Work-in-progress tabs no longer show a doubled hash in their issue reference.** The placeholder tabs (Bandwidth Monitor, Gaming Profile, Edge/OneDrive Remover, Notification Blocker, Volume Control) rendered "Tracked in issue ##337" because the template prepended a `#` to a value that already started with one. The duplicate is removed, so they now read "Tracked in issue #337".
- **A drive with no SMART data is no longer painted red as if it were failing.** The disk-health percentage swatch fell through to the "failing" red when SMART health data was unavailable; it now shows the neutral grey used elsewhere for unknown readings, matching the temperature swatch.
- **The App Alerts busy indicator no longer switches off while monitoring is active.** Running a manual "refresh installed apps" while monitoring was on forced the busy/monitoring affordance off in its cleanup step; the indicator now stays in sync with the monitoring state.
- **The Context Menu search box now shows its placeholder text.** The "Search entries…" hint was wired through a `Tag` that the default text-box template never renders, so the field appeared blank; it now uses the same in-box placeholder pattern as the Bulk Installer search.
- **One invalid custom-theme colour no longer discards the other three.** Entering a malformed hex value in the Appearance → Custom editor silently dropped all four colour edits; each field is now parsed independently, valid values still apply, and an invalid field is flagged with red text and a tooltip.

## [1.51.7] - 2026-06-29

### Fixed
- **Unknown command-line flags now report a usage error instead of silently opening the app.** Running `SysManager.exe --bogus` (or any unrecognized `--flag`) used to fall through to launching the GUI and exit 0, contradicting the documented contract (`--help` states unknown options exit 2). Unrecognized flags are now treated as a headless usage error: they print "Unknown option" with the help text and exit 2, while the internal startup sentinels (the elevation relaunch and the in-process update applier) are explicitly excluded so they still route to their own startup paths. Bare non-flag arguments are still ignored.

## [1.51.6] - 2026-06-29

### Added
- **Progress indicator while a tab is working.** Resource History, Scheduled Maintenance, and Tweaks Hub now show a small progress bar next to the status line during loads, applies, and schedule changes — so you can tell the app is busy instead of wondering if a click registered.

### Changed
- **Internal cleanup:** removed eight unused placeholder objects that were allocated on every launch but no longer shown anywhere (left over after those features graduated to real tabs). No user-visible change.

## [1.51.5] - 2026-06-29

### Fixed
- **Scheduled Maintenance buttons no longer double-fire.** Save / Remove / Refresh are now disabled while one of them is running, so a fast double-click can't start overlapping operations.

### Changed
- **Scheduled Maintenance task now pins an explicit user principal.** The recurring task is registered to run as the current interactive user at the standard (non-elevated) level via an explicit principal, rather than relying on the scheduler's default — making its "current user, no admin, only when logged on" behavior deliberate.

## [1.51.4] - 2026-06-29

### Fixed
- **`SysManager.exe --version` now reports the real version.** The command-line interface printed a hardcoded version that had fallen behind the actual build; it now reads the version from the running app, so `--version` and `--help` always match the installed release.

## [1.51.3] - 2026-06-29

### Changed
- **Resource History is lighter on the system.** The background sampler no longer runs a disk health/SMART query every 10 seconds just to label storage sensors it doesn't record — it now reads only the CPU/GPU temperatures it actually stores, cutting continuous background WMI work over a long session.
- **Faster history loading.** Opening the tab or changing the range now reads only the samples in the selected window (reading the file from the newest end and stopping at the cutoff) instead of parsing the entire history file every time, which matters once weeks of history have accrued.

### Fixed
- **Cleaner shutdown for Resource History.** The sampler now stops fully before its file lock is released on exit, avoiding a harmless-but-noisy background error during shutdown.
- **Temperature chart now shows a clear "no data" message** on machines without supported temperature sensors, instead of a blank chart.

## [1.51.2] - 2026-06-29

### Fixed
- **Tweaks Hub now reports the restore point honestly.** It previously stated a System Restore point "is created before the first change" as a fact; creating one needs administrator and Windows only allows one per 24h, so it often silently didn't happen. The wording now says it's attempted (when running as administrator) and the status line confirms only when one was actually created — every tweak remains individually reversible regardless.
- **Fixed a threading issue when applying tweaks.** The first apply of a session updated the on-screen state from a background thread; it now stays on the UI thread, avoiding a potential intermittent error.

## [1.51.1] - 2026-06-29

### Fixed
- **Settings Watchdog no longer crashes on a malformed baseline file.** A `settings-baseline.json` that was valid JSON but missing its data could throw when checking for changes; it's now treated as "no baseline saved" so the tab stays usable.
- **Settings Watchdog now shows a "Run as administrator" banner** when not elevated, since restoring machine-wide settings needs admin — previously you only found out after a restore silently failed.

## [1.51.0] - 2026-06-29

### Added
- **Tweaks Hub tab (Preview).** A single place to review and apply the safe, reversible optimizations that are otherwise spread across tabs. Tweaks are grouped into **Essential** (low-risk, per-user, apply without admin) and **Advanced** (higher-impact, machine-wide, need administrator). Tick the ones you want and **Apply Selected** or **Undo Selected** in bulk — a live counter shows pending changes, an automatic System Restore point is created before the first change, and every tweak is individually reversible. Each row shows whether it's currently Applied or at the Windows Default. It's a front-end over the same reversible operations as the Privacy & Telemetry tab — no tweak is reimplemented. Closes #907.

## [1.50.0] - 2026-06-29

### Added
- **Scheduled Maintenance tab (Preview).** Automate maintenance on a schedule: register one Windows scheduled task that runs SysManager in the background (via its CLI) to clean temporary files or purge standby memory, daily or weekly at a time you pick. The tab shows the task's last run, next run, and last result, and lets you update or remove the schedule (each confirmed first). It runs in your user context — no admin required — and only ever touches its own task at `\SysManager\Scheduled Maintenance`, never any other scheduled task. Built on the same safe CLI verbs, so nothing destructive is automated. Closes #10.

## [1.49.0] - 2026-06-29

### Added
- **Command-line interface (Preview).** SysManager now accepts command-line flags so you can automate the safe maintenance actions from scripts, Task Scheduler, or deployment tools — it runs headless (no window) and writes to the launching console. Commands: `--health` (read-only health score), `--cleanup` (temp-file cleanup), `--trim-ram` (purge the standby list), plus `--version`, `--help`, and `--list`; add `--json` for machine-readable output or `--silent` for scripting, with conventional exit codes (0 success · 1 error · 2 usage). Only read-only or non-destructive actions are exposed — anything that changes the system irreversibly stays in the GUI behind a confirmation. A new **CLI Interface** tab lists every command with one-click copy. Closes #342.

## [1.48.0] - 2026-06-29

### Added
- **Settings Watchdog tab (Preview).** A new Monitor tab that catches the settings Windows Update silently resets — telemetry level, web search in Start, the Widgets board, lock-screen ads, and Start-menu suggestions. Save a baseline of your current preferences with one click; after an update, "Check now" lists anything that drifted in plain language (e.g. "Diagnostic data: was 'Off', now 'Full'") and "Restore changed" writes them back to your baseline in one step. Strictly local: the baseline lives in your `%LocalAppData%\SysManager` folder and only a fixed list of well-known registry values is ever read or written. Closes #335.

## [1.47.0] - 2026-06-29

### Added
- **Resource History tab (Preview).** A new Monitor tab that records your CPU, RAM and GPU usage plus CPU/GPU temperatures every 10 seconds in the background — including while the app is minimized to the tray — so you can investigate what caused a slowdown hours or days ago instead of only seeing the live moment. Pick a range (last hour through 30 days) to scroll a usage chart and a temperature chart, keep 7/14/30 days of history, and export the visible range to CSV. Strictly local: history lives in your `%LocalAppData%\SysManager` folder and nothing leaves the machine. Closes #13.

## [1.46.0] - 2026-06-29

### Changed
- **Clearer App Updates status.** When updating apps, each row now shows a plain-English result instead of a raw exit code — e.g. "Updated", "No applicable update found", "Update installed — restart required", or "App is running — close it and retry" — and unknown failures show a tidy hex code rather than a giant negative number. The summary line now reports successes and failures honestly ("Updated 3 of 4 · 1 failed") instead of counting every attempt as done. winget's progress bar is also suppressed (`--no-progress`), so the live output no longer fills with garbled block characters. Closes #1130.

## [1.45.3] - 2026-06-29

### Fixed
- **Dashboard "Recent activity" now reflects what you actually do.** Previously it only recorded a handful of Dashboard quick-action buttons, so it stayed empty for normal use. It now records the features you open and the operations you complete across the app (temp cleanup, DNS changes, app removals, restore points, standby purge, …), newest first. Closes #1132.

## [1.45.2] - 2026-06-29

### Fixed
- **Docs now match the app after the eight features left Preview.** The README still showed those eight tabs with a "Preview" marker and ARCHITECTURE still tagged them "Preview" even though they had graduated — both are now corrected so the documentation reflects the shipped state.
- **Update log no longer records the full user folder path** in three places (an invalid-signature warning and two cleanup messages); they now use the same path-scrubbing the rest of the updater already applied, so a shared log can't reveal the account name.

## [1.45.1] - 2026-06-29

### Fixed
- **CPU Core Affinity now scrolls when there are many cores.** On a machine with a high logical-processor count (or a short window), the core tiles overflowed the card and the lower ones were cut off with no way to reach them. The core grid now scrolls, with the header and select buttons pinned, so every core is reachable regardless of core count or window size.

## [1.45.0] - 2026-06-29

### Changed
- **Consistent "empty list" messages across every tab.** Lists and tables that can start empty — App Updates, App Blocker, App Alerts, Shortcut Cleaner, File Shredder, Duplicate Finder, Context Menu, Task Scheduler, Display Profiles, Boot Analyzer, Defender exclusions and the live output console — now show the same centred placeholder (an icon, a short title and a one-line hint on how to populate the list) instead of a blank area or nothing at all. The handful of tabs that already had a placeholder (Debloater, Restore Points, Camera/Mic/Location, Browser Cleaner, System Logs, System Report) were moved onto the same shared component so the look can't drift between tabs again. A 📂 emoji in the Disk Analyzer and a shield emoji in Process Manager were also swapped for the proper icon font. Purely visual — no behaviour changes.

## [1.44.1] - 2026-06-28

### Changed
- **More consistent look across tabs.** A pass over the whole UI brought the remaining tabs in line with the app's design system: every tab that does background work now shows the same slim progress indicator next to its status line; status text, section labels and large readouts use the shared text styles instead of one-off sizes; a few hardcoded colours and an emoji icon were swapped for the shared theme colours and the proper icon font; and a redundant font override was removed. Purely visual consistency — no behaviour changes.

## [1.44.0] - 2026-06-28

### Changed
- **Eight tabs graduated out of Preview.** Task Scheduler, Standby List Cleaner, Timer Resolution, CPU Core Affinity, Display Profiles, File Lock Detector, Defender Tweaks and Dark Mode Scheduler no longer carry the "Preview feature" banner or the PREVIEW tag in the sidebar. They've been verified end to end — functionality, error handling, safety guards, automated tests for the command paths, and a live check that each one's real effect on the system works and reverts cleanly. (The four correctness fixes from 1.43.1 — the Task Scheduler wildcard match, the Standby purge running off the UI thread, the Display Profiles revert check, and the Defender verification — were part of this hardening.)

## [1.43.1] - 2026-06-27

### Fixed
- **Task Scheduler enable/disable now always acts on exactly the task you picked.** Windows task names can contain characters like `* ? [ ]`; the enable/disable used these verbatim against a wildcard-matching command, so a task whose name contained them could have toggled *more than one* task (or silently none) while still reporting success. Names and paths are now matched literally, and the operation reports success only when exactly the selected task changed.
- **The Standby List Cleaner no longer freezes the window while purging.** The memory purge ran on the UI thread; on a large cache that briefly locked up the app. It now runs in the background, both for the manual button and the automatic threshold purge (which also no longer stacks if a purge is still running).
- **Display Profiles tells you if it couldn't undo a change.** If the 15-second auto-revert failed to restore your previous resolution/refresh rate (e.g. the driver rejected it), the app used to claim it had reverted. It now detects the failure and tells you how to recover via Windows display settings.
- **Defender Tweaks no longer reports a change that didn't happen.** Turning a protection off or removing an exclusion could show "updated" even when the change silently failed (e.g. without administrator rights), because the verification matched the unavailable/empty fallback state. It now confirms Defender status was actually readable before reporting success.

## [1.43.0] - 2026-06-27

### Added
- **Six more tabs now tell you when they need administrator rights.** Process Manager, Startup Manager, Task Scheduler, Defender Tweaks, File Lock Detector and Shortcut Cleaner all perform actions that require elevation (ending system processes, toggling startup entries and scheduled tasks, changing Defender settings, unlocking protected files, removing shortcuts in shared locations) — but unlike the rest of the app they gave no upfront hint. Each now shows the same banner the other tabs use: a "Run as administrator" prompt when you're not elevated, and a confirmation strip when you are. Consistent with Services, DNS & Hosts, Uninstaller, and the others.

## [1.42.12] - 2026-06-27

### Fixed
- **The Dashboard is lighter on the system while it's open.** Two background readings were doing far more work than needed on their refresh timers: the GPU widget re-initialised the graphics API (and re-queried the adapter on non-NVIDIA machines) several times a second, and — when running as administrator — the temperature panel opened and closed its hardware-monitoring driver on every two-second poll. Both now initialise once and reuse that handle, so the live Dashboard uses noticeably less CPU and fewer system handles without changing what you see.
- **Log files no longer record your Windows username from the update path.** A couple of update-related log lines wrote the full executable path (which for a per-user install includes `C:\Users\<name>\…`) without the existing path-scrubbing the rest of the app applies. They now scrub the username like everywhere else.
- **A failed update recovers instantly instead of stalling.** If the downloaded file went missing right before applying (e.g. removed by antivirus), the updater treated it as a temporarily-locked file and retried for several seconds before giving up; it now detects the missing file immediately and reports it.

## [1.42.11] - 2026-06-27

### Changed
- **The in-app updater no longer uses an on-disk script.** Applying an update previously wrote a small batch file to a temporary folder and ran it through `cmd.exe` to swap in the new build after the app closed. That left a brief window in which another program running as the same user could tamper with the script before it executed. The update is now applied entirely from within the freshly-downloaded (and already hash- and signature-checked) executable itself: it waits for the old version to close, replaces it using a staged atomic file move — so an interrupted update can never leave a half-written, unstartable executable — and relaunches, keeping your run-as-administrator state if you were elevated. There is no longer any script on disk for another process to interfere with.

## [1.42.10] - 2026-06-27

### Fixed
- **The in-app updater is hardened against a local tampering window.** When applying a downloaded update, SysManager writes a small batch script that swaps in the new executable after the app closes. That script was previously written into the same predictable, user-writable folder as the download and launched via the bare `cmd.exe` name. A malicious program running as the same user could, in theory, replace the script (or plant a fake `cmd.exe` on the search path) during the brief window before it ran, getting its own commands executed by the update step. The script is now written to a fresh, randomly-named private folder, launched via the full system path to `cmd.exe`, refuses any path containing an illegal character, and cleans up its own folder afterwards. Hash and Authenticode verification of the downloaded binary were already in place and are unchanged.

## [1.42.9] - 2026-06-27

### Fixed
- **System Logs detail cards regained their colour coding.** The "What this means" and "What to try" panels are tinted blue and green again to tell them apart at a glance — this time using theme-aware translucent tints, so they stay legible on light and custom themes (the previous fix had flattened them to a neutral surface).

## [1.42.8] - 2026-06-27

### Fixed
- **Empty-list messages are clearer and no longer misleading.** The "nothing to show" text on Services, Startup Manager, Process Manager, Uninstaller, Windows Features and DNS & Hosts previously assumed a specific cause (e.g. "use Refresh" or "no match for the filter") even when a different one applied, and could flash briefly while a tab was still loading. The wording is now neutral and accurate in every state.
- **Preview tabs are now released cleanly on exit.** The newer tabs (Display Profiles, Defender Tweaks, Task Scheduler, CPU Affinity, Timer Resolution, File Lock Detector, Standby List Cleaner, Dark Mode Scheduler) weren't being disposed when the app closed, so their timers and event subscriptions could linger. They're now disposed with the rest, and Defender Tweaks unsubscribes its internal handler.

### Changed
- Documentation accuracy: corrected the System-group tab order in the README and ARCHITECTURE feature tables, fixed the Windows Update description (it uses the Windows Update COM API, user-triggered — not a PSWindowsUpdate auto-check), and aligned the SECURITY supported-versions table with the "latest minor only" policy.

## [1.42.7] - 2026-06-27

### Fixed
- **Process Manager keeps auto-refreshing even if one refresh hits a snag.** Its 1-second live refresh loop only handled cancellation; any other transient fault (a process vanishing mid-read, a brief WMI hiccup) would stop the auto-refresh for the rest of the session. A failed refresh is now logged and the loop simply continues, matching the Dashboard's polling.
- **Disk and battery readings shrug off transient WMI COM faults.** System Health's reliability/SMART read and the Battery Health queries now also catch the COM-level exception WMI can throw under load, so a one-off glitch no longer drops the whole disk list or aborts the battery scan.
- **"Trim RAM" no longer briefly freezes the window.** Emptying every process's working set (a per-process system call across hundreds of processes) now runs on a background thread, keeping the Performance tab responsive while it works.
- **Defender status read reports errors instead of failing silently.** If reading Defender status hit a PowerShell host fault, the tab could end up stuck; it now shows a clear message.

## [1.42.6] - 2026-06-27

### Fixed
- **Temp cleanup can't be tricked into deleting files outside the temp folder.** The cleanup scan already refused to descend into junctions/symlinks it found *inside* the temp tree, but didn't check whether the starting folder itself was a junction — so a redirected temp root could, in theory, lead it to enumerate (and delete) files elsewhere on disk, especially when run as administrator. The scan now treats a junction/symlink root the same way and stops immediately.
- **Speed Test re-verifies the bundled Ookla CLI's signature every run.** The Ookla command-line tool is cached under your local app data (a user-writable folder). Its Authenticode signature was checked right after download but not on later reuse, leaving a window where a swapped binary could be launched. It is now re-verified (Ookla-signed, fail-closed) before every run, not only on first download.

## [1.42.5] - 2026-06-27

### Fixed
- **Display Profiles auto-revert now restores the correct monitor.** If you applied a mode and then switched to a different display in the dropdown during the 15-second "Keep these settings?" countdown, the automatic revert could restore the *previous* display's old mode onto the *newly selected* one. The pending revert now remembers the exact display it belongs to and only ever restores that one. Also hardened against rapid display switching: an in-flight mode list for a display you've already switched away from no longer overwrites the current one.

## [1.42.4] - 2026-06-27

### Fixed
- **Event Viewer detail panel and System Health bars now follow the theme correctly.** A few surfaces used fixed dark colours instead of theme brushes, so under a light or custom theme the "What this means" / "What to try" cards in System Logs, their monospace message/XML boxes, and the small health/usage bars on System Health could lose contrast (light text on a still-dark panel). They now use the shared theme brushes and adapt to any preset or custom theme.

## [1.42.3] - 2026-06-27

### Fixed
- **Lists that are empty now say so, instead of showing a blank area.** Several tabs (Services, Startup Manager, Process Manager, Uninstaller, Windows Features, DNS & Hosts, Disk Analyzer) showed an empty table with no explanation when there was nothing to display or a filter matched nothing — leaving you unsure whether it was still loading, found nothing, or had failed. Each now shows a short, centred message in that case. This also fixes the empty-state messages on the Speed Test history, which previously never appeared: the visibility converter treated a list's item count as "always present", so a count of zero was misread — numeric values are now correctly treated as empty when zero.

## [1.42.2] - 2026-06-27

### Fixed
- **Display Profiles applies and auto-reverts without freezing the window.** Switching resolution/refresh rate, picking a display, and the 15-second auto-revert all called the Windows display APIs (`EnumDisplaySettings` / `ChangeDisplaySettingsEx`) directly on the UI thread, so the app could briefly stop responding while the driver re-trained the panel — and that stall could hold up the very countdown meant to rescue you from a bad mode. These calls now run on a background thread, so the window and the auto-revert timer stay responsive throughout.
- **Defender Tweaks no longer lets two changes overlap.** The PUA, Controlled Folder Access, exclusion-add and exclusion-remove actions could be triggered again while a previous change was still being written and verified, starting overlapping operations whose read-back checks could race and show a misleading "not changed" message. Each action now disables the others until it finishes, matching how the rest of the app serialises long-running operations. No change to what any action does.

## [1.42.1] - 2026-06-27

### Fixed
- **System Health now reads SMART/reliability data on Storage Spaces and similar setups.** On machines where Windows surfaces disks through the Storage provider, the physical-disk identifier embeds characters (`=` and `"`) that the previous safety check rejected, so temperature, wear, power-on hours and read/write error counts were silently dropped — the drive still showed as healthy but with no detail — and a warning was written to the rotating log on every few-second refresh. The reliability counters are now read by following the disk's WMI association directly instead of rebuilding a query from the identifier, which is robust to the identifier format and needs no text parsing. Drives that genuinely expose no counters (non-elevated sessions, virtual disks) are treated as a normal empty result rather than logged as a warning. No visible change on machines that already showed full SMART detail.

## [1.42.0] - 2026-06-26

### Added
- **Standby List Cleaner tab (Gaming & Profiles).** Frees cached "standby" memory to reduce stutter when RAM runs low — the built-in equivalent of ISLC. Shows live total / available / load%, purges the standby list on demand, and can auto-purge when available RAM drops below a threshold you set. Safe and non-destructive: the standby list is clean, disk-backed file cache, so clearing it loses nothing — Windows just reloads from disk on next use. Reading stats needs no admin; purging requires administrator (it enables the same privilege RAMMap/ISLC use and reports cleanly if not elevated). Marked **PREVIEW** while it's verified. Closes #325.

## [1.41.0] - 2026-06-26

### Added
- **Dark Mode Scheduler tab (Customization).** Switch the Windows light/dark theme on demand, or have it follow a fixed-time schedule (e.g. dark at 19:00, light at 07:00). Optionally switches the taskbar/Start too. The theme is applied instantly (no sign-out) by writing the per-user theme setting and notifying Windows; no admin needed and fully reversible. Honest about its limits: the schedule runs while SysManager (or its tray) is open — it's not a background service. Marked **PREVIEW** while it's verified. Closes #329.

## [1.40.0] - 2026-06-26

### Added
- **Task Scheduler tab (System).** Browse all Windows scheduled tasks and turn them on or off. Tasks are color-coded by type — Third-party, well-known Telemetry (CEIP / Compatibility Appraiser / Feedback / Error Reporting), and System — so it's clear what's safe to touch. Filter by name/path, optionally hide system tasks, and see each task's last/next run. Disabling is **fully reversible and never deletes a task**; system tasks show an extra confirmation warning, changes need administrator, and each toggle is verified by reading the task's state back. Marked **PREVIEW** while it's verified. Closes #334.

## [1.39.0] - 2026-06-26

### Added
- **Defender Tweaks tab (Privacy & Security).** See your Microsoft Defender status at a glance (real-time protection, cloud protection, PUA and Controlled Folder Access), toggle PUA protection and Controlled Folder Access, and manage scan-exclusion folders (add/remove). Built to be safe: every change requires administrator and is **verified by reading the value back** — because Tamper Protection can silently ignore changes, the tab detects it and shows a clear warning, and never reports a change as done unless Windows actually applied it. Exclusion folders are validated (rooted, existing, no wildcards) before use, and changes are confirmed first. Marked **PREVIEW** while it's verified. Closes #344.

## [1.38.0] - 2026-06-26

### Added
- **CPU Core Affinity tab (Gaming & Profiles).** Pin a running process to specific CPU cores — useful for games on Intel hybrid CPUs, where **P-cores and E-cores are detected and labelled** (via `GetLogicalProcessorInformationEx`). One-click "P-cores" / "All cores" presets, a per-core checkbox map, Apply and Restore. Affinity is per-running-process and is lost when the process exits, so it's inherently temporary and reversible; no admin needed for your own processes (changing another user's process is surfaced as needing admin, not a crash). An empty core selection is rejected (Windows would treat 0 as "let the OS decide"). Marked **PREVIEW** while it's verified. Closes #327.

## [1.37.0] - 2026-06-26

### Added
- **Display Profiles tab (Gaming & Profiles).** Quickly switch resolution and refresh rate per monitor — e.g. 165 Hz for gaming, 60 Hz for work — from the list of modes your display actually supports, using only the Windows display APIs (no NVIDIA/AMD tool conflict). Safe by design: changes apply for the session (a reboot reverts), and a **15-second auto-revert** restores the previous mode unless you confirm "Keep", so a bad mode can never strand you on a blank screen. Each mode is validated before applying; no admin needed. Marked **PREVIEW** while it's verified. Closes #328.

## [1.36.0] - 2026-06-26

### Added
- **File Lock Detector tab (Monitor).** When you hit a "file is in use" error, enter or browse to the file/folder and see exactly which process is holding it — name, PID, type and start time — via the Windows Restart Manager (the same mechanism Explorer's own dialog uses). You can end a selected locking process (with confirmation) to release the file; critical system processes are protected. Detecting works as a standard user; ending a process owned by SYSTEM or another user needs admin (surfaced cleanly, never crashes). Marked **PREVIEW** while it's verified. Closes #333.

## [1.35.0] - 2026-06-26

### Added
- **Timer Resolution tab (Gaming & Profiles).** Request the finest Windows timer resolution (≈0.5 ms instead of the ~15.6 ms default) to reduce input latency in games, and restore it with one click. Shows the live current/finest/default values — it re-queries the *effective* resolution rather than echoing the request, so the number is honest even when Windows stops honoring it (e.g. while minimized on Windows 11). Fully reversible and no admin needed; includes a clear power-consumption warning. Marked **PREVIEW** while it's verified. Closes #326.

## [1.34.0] - 2026-06-26

### Added
- **"Preview" marking for newly added features.** Features that are implemented but not yet fully verified now show a small **PREVIEW** pill next to their name in the sidebar and a short banner at the top of the page, so it's clear which tabs are brand-new. This lets new features ship and be tried out while they're still being polished.

## [1.33.16] - 2026-06-26

### Changed
- **Moved "Environment Variables" from the Customization group to Advanced**, next to Profile Export/Import — it's a system/developer tool, not a UI-customization one. The tab itself is unchanged.
- **Moved "App Alerts" from Privacy & Security to the Monitor group.** App Alerts passively watches for newly installed apps and keeps a timestamped history — it observes rather than enforces, so it belongs alongside the other monitoring tabs. The tab itself is unchanged.
- **Moved "Legacy Panels" from the System group to Info.** It's a read-only launcher for classic Windows applets (Device Manager, Disk Management, etc.) with no system modification, so it sits better among the other read-only Info tabs. The tab itself is unchanged.
- **Quick Cleanup now separates "Clean up" from "Repair Windows".** Clean TEMP / Empty Recycle Bin / Rescan and the SFC / DISM repairs were previously one mixed row of buttons; they're now two labelled sections so it's clear which actions free space and which repair Windows. Also added accessible names to all the action buttons for screen readers.

## [1.33.15] - 2026-06-26

### Fixed
- **Faster, smoother startup — several tabs no longer read the registry or probe drives on the UI thread while the app launches.** The Privacy & Telemetry, Environment Variables, App Blocker, Duplicate Finder, Disk Analyzer, and Profile Export/Import tabs loaded their initial data synchronously as the window was being built, which could briefly freeze launch (worst case: a stalled or disconnected drive). Each now loads in the background like the other tabs, so the window appears promptly and the tab fills in a moment later.

## [1.33.14] - 2026-06-26

### Fixed
- **Quick Cleanup empties the Recycle Bin the same reliable way as the rest of the app.** It used a PowerShell `Clear-RecycleBin` call that can leave "ghost" entries behind; it now uses the shared shell-API helper (the same one Deep Cleanup and the One-Click Tune-Up use), which clears every drive's bin cleanly. Keeps the three entry points from drifting apart.

## [1.33.13] - 2026-06-26

### Fixed
- **Traceroute monitor no longer risks a background error when stopped mid-cycle.** Stopping the live traceroute monitor disposed its cancellation source unconditionally, even if a route was still being traced; the still-running cycle could then throw on the disposed token in the background. Disposal is now deferred until the cycle actually finishes, matching the ping monitor. No visible change in normal use.

## [1.33.12] - 2026-06-26

### Fixed
- **Sidebar navigation items are now announced correctly by screen readers.** Each tab in the sidebar exposes its name (e.g. "Dashboard", "System Health") to assistive technology and UI automation; previously the items had no accessible name and were announced only as an internal type name. No visual change.

## [1.33.11] - 2026-06-26

### Fixed
- **Recycle Bin size estimate on the Cleanup tab now counts every fixed drive, not just C:.** It previously looked only at `C:\$Recycle.Bin`, so deleted files on other drives weren't reflected in the estimate. It now sums the hidden `$Recycle.Bin` on each ready fixed drive.
- **App Blocker no longer assumes Windows is installed on C:.** The sentinel path it writes to block an app is now derived from the real system directory instead of a hardcoded `C:\Windows\System32`, so blocking works on systems where Windows lives on another drive. Existing blocks are unaffected (the value is compared case-insensitively).

## [1.33.10] - 2026-06-25

### Fixed
- **The Dashboard's Quick Cleanup now uses the same safe temp cleaner as the One-Click Tune-Up.** It previously had its own inline cleaner that only looked at the top level of the user TEMP folder, ignored the Windows TEMP folder, and silently swallowed every error. It now cleans both temp folders and never follows a junction or symbolic link out of the temp tree, so it can't be redirected into unrelated files — matching the protection already used elsewhere.

## [1.33.9] - 2026-06-25

### Fixed
- **The Camera/Mic/Location tab no longer reads the registry on the UI thread at startup.** Its access history was loaded synchronously while the main window was being built, so the registry walk ran on every launch — even though most people never open the tab — and an unreadable or corrupt consent-store entry could surface as a startup failure. It now loads in the background like the other tabs (with a Cancel-able refresh) and a damaged entry is skipped instead of bubbling up.

## [1.33.8] - 2026-06-25

### Changed
- **Removed the duplicate "Reset Network Stack" button from System Fixes.** The same Winsock + TCP/IP reset and DNS flush already lives on the Network → Network Repair tab (as individual one-click tools), so having it in two places was confusing and risked the two copies drifting apart. System Fixes now links to Network Repair for network resets instead of duplicating them.
- **Renamed the "Privacy Monitor" tab to "Camera/Mic/Location"** so it is no longer confused with the separate "Privacy & Telemetry" tab. The feature is unchanged — it still shows which apps recently used your camera, microphone, or location.

### Fixed
- **Performance Mode's "Create restore point" now uses the same code as the Restore Points tab.** The two had separate implementations of the same operation that had begun to diverge; they now share one, which also enables System Restore on the system drive first if it was turned off. No visible change beyond that improvement.

## [1.33.7] - 2026-06-25

### Changed
- **Boot Analyzer: hardened the event-XML reader to tolerate alternate payload shapes.** The boot-performance reader already parses the standard `<EventData><Data Name="BootTime">` form that Windows emits, and that path is unchanged. This adds a defensive fallback that also resolves directly-named child elements (matched by local name, namespace-agnostic) for any event variant that nests its fields differently, so the reader is robust across builds. No behavior change on current Windows — the boot history is read exactly as before.

## [1.33.6] - 2026-06-25

### Fixed
- **The Debloater now correctly protects the Photos app from removal.** The system-critical denylist listed `Microsoft.Windows.Photos.Settings`, but the real package name is `Microsoft.Windows.Photos` — and because the match is a prefix check, the shorter real name never matched, so Photos was offered as removable. The entry is now the correct family name. A redundant, never-matching Windows Security entry was also removed (the correct one was already present). Added tests covering each critical package by its real name, plus tests confirming removal refuses a protected package and rejects an injection-shaped package name without ever invoking PowerShell.

## [1.33.5] - 2026-06-25

### Fixed
- **Profile Export/Import now correctly handles the theme/appearance section.** The exporter looked for every config file under one folder (Local AppData), but the theme is saved under Roaming AppData — so exporting never picked up your theme, and importing a profile wrote the theme to a folder the app doesn't read on startup, making it a silent no-op. Each config file is now read from and written to the same folder its owning feature uses (theme under Roaming, speed-test history under Local), so theme export and import actually take effect.

## [1.33.4] - 2026-06-25

### Fixed
- **"Undo" on the DNS & Hosts tab now fully restores the previous setting, including IPv6.** Applying a filtering preset configures both IPv4 and IPv6 resolvers, but Undo only captured and restored IPv4 — so reverting an ad/family-blocking preset left its IPv6 resolvers active, and on a dual-stack network the "undone" filtering was often still in effect. Undo now snapshots both families before a change and, on restore, clears the adapter's DNS first (removing anything applied since) before re-applying exactly what was captured. Undo is also offered now even if a change only partially applied. Additionally, programming the IPv6 resolvers is treated as best-effort: on a machine with IPv6 disabled the IPv4 change still succeeds (and stays undoable) instead of the whole apply failing.

## [1.33.3] - 2026-06-25

### Fixed
- **Editing an environment variable no longer breaks `%VAR%` expansion in PATH and similar variables.** SysManager wrote every variable as a plain string (REG_SZ), which silently converted variables Windows stores as expandable (REG_EXPAND_SZ) — most importantly the system `Path` — and froze references like `%SystemRoot%` or `%JAVA_HOME%` to whatever they happened to expand to at edit time, so they stopped tracking their source variable. Variables are now written directly to the registry with their original type preserved (a variable that was expandable stays expandable, and a new value containing a `%token%` is created as expandable), and the editor now shows and round-trips the raw `%VAR%` tokens instead of their expanded form. A single `WM_SETTINGCHANGE` broadcast still notifies running programs after a batch of changes.

### Added
- **Restore button on the Environment Variables tab.** The one-time backup taken before your first change can now be restored from inside the app: every variable is rewritten to its original value (type preserved) and any variable added since is removed. The Apply confirmation already promised this was possible — now there's a button for it. (System-scope restore needs administrator rights; if the safety backup can't be written, Apply now stops without making any change instead of risking an unbacked edit.)

## [1.33.2] - 2026-06-25

### Fixed
- **The Privacy Monitor no longer risks failing to open if the Windows access-history store contains an unusual entry.** Decoding an app's name from a desktop-app entry whose key was made up only of path separators could throw and, because the tab is built at start-up, prevent the window from loading. The decoder now falls back to the raw key name for such entries, and a single unreadable or malformed entry is skipped instead of aborting the whole scan. Also refined "in use now" detection so an app whose most recent start is newer than its last stop (e.g. after a force-close) is correctly shown as currently using the device, and the "last used" time now reflects the most recent of the two timestamps.

## [1.33.1] - 2026-06-25

### Fixed
- **Browser Cleaner now refuses to follow junctions or symbolic links out of a browser's own folders.** The reparse-point safety check failed *open* — if a folder's attributes couldn't be read it was treated as a normal folder and traversed — and the file-deletion path skipped the check entirely, so a junction or link placed inside a browser profile directory (something a standard user can create without administrator rights) could redirect a clean to measure or delete files outside the browser tree. The check now fails *safe* (an unreadable entry is treated as a link and skipped), runs before every measure and delete on both files and folders, and matches the behavior already used by Deep Cleanup and the File Shredder. The Firefox "Cache" entry now targets each profile's `cache2` cache folder specifically, instead of the whole profile directory — so a Firefox clean can never touch saved logins, bookmarks, or preferences.

## [1.33.0] - 2026-06-25

### Added
- **Boot Analyzer tab** (System group). The placeholder is now a working tab that reads the Windows boot-performance history (the Diagnostics-Performance log) and shows how long your PC takes to boot — total, core (main path), and desktop ready-up time — across recent boots, with a trend versus your recent average. A second list shows the apps, drivers, services, and devices Windows flagged as slowing boot, with the delay attributed to each. Read-only; reading the log requires administrator (the tab shows the standard elevation banner).

## [1.32.0] - 2026-06-25

### Added
- **Privacy Monitor tab** (Monitor group). The placeholder is now a working tab that shows which applications recently used your **camera, microphone, or location**, and when — read from the Windows access history (the CapabilityAccessManager consent store). Devices currently in use are flagged and sorted to the top. Read-only: to grant or revoke a permission, an **Open privacy settings** button hands off to Windows — SysManager never changes capability permissions itself.

## [1.31.0] - 2026-06-24

### Added
- **Browser Cleaner tab** (Privacy & Security). The placeholder is now a working tab that scans installed browsers — Chrome, Edge, Brave, Opera, and Firefox — and shows the on-disk size of each cleanable category (cache, history, cookies, sessions). Tick what to remove and clean it after a confirmation. Cookies and sessions are flagged "signs you out" and left **unticked by default**, so a clean never logs you out by accident. Cache and history are pre-selected. Cleaning is per-user (no admin); locked files (browser open) are skipped rather than forced, and reparse points are never followed.

## [1.30.0] - 2026-06-24

### Added
- **Windows Update timing & deferral controls** (Windows Update tab). A new "Update timing & deferral" section lets you **defer feature updates** by a configurable number of days while security and quality updates keep installing, **pause all updates** for a bounded window (up to 35 days, after which Windows auto-resumes), and **Restore default** to return to standard behavior. It uses the documented Windows Update policy registry keys and is fully reversible. There is deliberately no "disable updates forever" option — the strongest action is a clearly-bounded pause, so a machine is never left permanently unpatched. Requires administrator.

## [1.29.0] - 2026-06-24

### Added
- **BIOS & firmware section in System Health.** A scan now also reports your BIOS version, release date, and vendor, the motherboard model, the boot mode (UEFI / Legacy), and Secure Boot status — all read-only. A **Find BIOS update** button opens the right manufacturer support page (ASUS, MSI, Gigabyte, ASRock, Dell, HP, Lenovo, Acer, Biostar, or a web search as a fallback) based on the detected motherboard, and **Copy info** copies the board model + BIOS version for support searches. SysManager never flashes firmware itself; the section includes a clear reminder that BIOS updates carry risk if interrupted.

## [1.28.0] - 2026-06-24

### Added
- **Profile Export/Import tab** (Advanced group). The placeholder is now a working tab that exports SysManager's own configuration — theme/appearance and speed-test history — to a single portable JSON file, and imports it on another PC. Export is selective (tick which sections to include); import shows what the profile contains and asks for confirmation before overwriting, supports selective per-section apply, and refuses profiles made by a newer, incompatible version. Only SysManager's own config is ever touched — never system settings — so importing is fully reversible.

## [1.27.0] - 2026-06-24

### Added
- **DNS filtering presets and IPv6** (DNS & Hosts tab). The DNS preset switcher now includes ad/malware/family-blocking variants — Cloudflare Malware-blocking (1.1.1.2) and Family (1.1.1.3), AdGuard DNS (ad/tracker blocking, plus a Family variant), and OpenDNS FamilyShield — each with a short description of what it blocks. Every preset now also configures IPv6 resolvers automatically alongside IPv4. The existing "Reset to automatic (DHCP)" undo continues to work for all variants.

## [1.26.0] - 2026-06-24

### Added
- **System Fixes tab** (System group). A consolidated panel for common one-click Windows repairs, each with a plain-English description and a confirmation before it runs: **Reset Windows Update** (stop services, clear the SoftwareDistribution/catroot2 caches, restart services), **Reset Network Stack** (Winsock + TCP/IP reset and DNS flush), and **Reinstall WinGet** (re-register the App Installer when app installs/uninstalls fail). A **Set up Auto Sign-in** shortcut opens the built-in User Accounts dialog so Windows stores the credential securely — SysManager never handles your password. Repairs run with live output and report success or failure honestly. They modify system state and require administrator rights (standard elevation banner).

## [1.25.0] - 2026-06-24

### Added
- **Legacy Panels tab** (System group). A one-click launcher for the classic Windows applets that newer releases keep burying — Control Panel, Sound, Power Options, Network Connections, Region, System Properties, User Accounts, Device Manager, Computer Management, Programs and Features, Mouse, and Date and Time. Each is a pure launcher that just opens the built-in panel; nothing is modified, so no elevation or confirmation is needed. The applet list is hard-coded, so no input ever reaches the process launcher.

## [1.24.0] - 2026-06-24

### Added
- **Debloater & Ads tab.** The Privacy & Security group's "Debloater & Ads" placeholder is now a working tab. Scan installed Windows Store apps and remove the ones you don't use — with a curated "common bloat" preset that pre-selects safe, frequently-removed apps (Bing News/Weather, Clipchamp, Solitaire, Xbox apps, Teams consumer, and more). System-critical packages (the Store itself, frameworks, security and shell components) are denylisted: they're shown but can never be selected or removed. Removal runs per-user with an impact summary and confirmation first, and is reversible — any removed app can be reinstalled from the Microsoft Store. Search and per-app descriptions help you decide what each app is before removing it.

## [1.23.0] - 2026-06-24

### Added
- **Restore Points tab.** The System group's "Restore Points" placeholder is now a working tab. List every Windows System Restore point (sequence number, date, description, and type, newest first), create a new restore point with an optional description, and restore the PC to a selected point. Creating and restoring require administrator rights (the tab shows the standard elevation banner); viewing the list does not. Restoring warns clearly that Windows will restart and asks for confirmation first. Creating also enables System Restore on the system drive if it was off, and notes Windows' once-per-24-hour limit.

## [1.22.0] - 2026-06-24

### Added
- **Environment Variables tab.** The Customization group's "Environment Variables" placeholder is now a working tab. View and edit both User and System (machine-wide) variables in one grid, filter by scope, and search by name or value. A dedicated PATH editor opens for PATH-like variables: reorder directories, remove entries, strip duplicates in one click, and see missing folders highlighted. Add or remove variables too. Edits stay local until you press *Apply*; a one-time JSON backup of every variable is written first so the original environment can be restored. System-scope changes require administrator rights (the tab shows the standard elevation banner); User-scope changes do not. Changes broadcast to Windows so new terminals pick them up without a reboot.

## [1.21.0] - 2026-06-24

### Added
- **System Report tab.** The Info group's "System Report" placeholder is now a working tab. Click *Generate* to gather a full, read-only snapshot of this PC — operating system, CPU, memory (with per-slot detail), GPU, motherboard, storage health (including SMART temperature, wear, and power-on time when available), and active network adapters. Export the report as plain text, a styled self-contained HTML page, or structured JSON, or copy it to the clipboard. The report is read-only: nothing on the system is changed, and the file is written only where you choose — nothing leaves the machine.

## [1.20.67] - 2026-06-24

### Changed
- **Removed the temporary administrator-state startup logging.** The diagnostic added while investigating the "tabs still ask for admin after elevating" report has done its job and is no longer written. The underlying fix (the elevated relaunch now reliably lands in the elevated window) stays in place.

## [1.20.66] - 2026-06-24

### Fixed
- **The System Logs tab is no longer blank.** One of the severity summary cards (Warnings) set a glow effect's colour from a brush resource instead of a colour value, which threw when the tab's view was first built and left the whole tab empty. The glow now uses the colour directly, matching the other cards, so the tab renders normally.
- **The Privacy and Windows Features tabs no longer show two "running as administrator" notices at once.** When elevated, each of those tabs displayed both the standard full-width administrator banner and a small redundant "Administrator" chip in the toolbar. The redundant chip has been removed; the standard banner remains (and the Windows Features "Reboot pending" chip is unaffected).

## [1.20.65] - 2026-06-24

### Fixed
- **"Run as administrator" now reliably lands you in the elevated window instead of leaving the tabs still asking for admin.** The app allows only one instance at a time, enforced by a system-wide lock. When you clicked "Run as administrator", the elevated copy started while the original (non-elevated) window was still closing and had not yet released that lock — so the elevated copy saw "another instance is already running", brought the *old* non-elevated window back to the foreground, and exited. You were left on the non-elevated instance, where the tabs that need admin still showed the "needs administrator" notice. The elevated relaunch now hands the single-instance lock over to the new instance (it waits briefly for the old one to release it), so you end up in the actually-elevated window.

### Changed
- **Added administrator-state logging at startup** to confirm the fix above and catch any residual case. On launch the app records, to its local log file only, the process elevation state (Windows token elevation type + process ID) and each affected tab's administrator status, under `%LocalAppData%\SysManager\logs` with usernames scrubbed — nothing is sent anywhere.

## [1.20.64] - 2026-06-24

### Fixed
- **The broken-shortcut scan no longer stops early at the first unreadable folder, and no longer mislabels long-target shortcuts as broken.** The scan used a recursive enumerator that threw the moment it reached a folder it couldn't read (e.g. a protected Start Menu subfolder), which aborted the rest of that location and silently skipped every shortcut after it. It now walks folder-by-folder and skips only the unreadable ones (and skips junction/symlink folders so it can't be redirected out of the tree). Separately, shortcut targets were read into a 260-character buffer, so a target longer than that was truncated and the shortcut wrongly reported as broken — the buffer is now large enough to hold extended-length paths.

## [1.20.63] - 2026-06-24

### Fixed
- **The Process Manager no longer loses your selected row every second.** The list auto-refreshes once a second, and each refresh rebuilt the whole collection — which cleared the row you had selected (and your scroll position) and re-extracted every process icon each time. The refresh now updates the existing rows in place: surviving processes keep their row (and your selection), new processes are added, exited ones are removed, and icons are only fetched for newly-appeared processes.

## [1.20.62] - 2026-06-24

### Fixed
- **"Enable All" on the Startup tab now reports honestly when some items can't be enabled.** It used to always say "All items enabled" even if a registry write failed (for example, an item that needs administrator rights), hiding the failure. It now counts the results and reports how many were enabled and how many could not be — and says so when everything was already enabled.
- **Startup actions can no longer overlap.** Scan, Enable All and the per-item toggle all read or write the same startup registry/task state; running two at once could interleave their writes and produce inconsistent counts. They are now disabled while one of them is running.

## [1.20.61] - 2026-06-24

### Fixed
- **Starting a scan or uninstall on the Uninstaller tab while another is running no longer risks a crash.** Both the Scan and Uninstall actions re-create one shared cancellation source, so triggering a second action while the first was still running could dispose the cancellation source the first was using and throw. Those two actions are now disabled while one of them runs — matching the App Updates, Windows Update and Bulk Installer tabs — while Cancel stays available.

## [1.20.60] - 2026-06-24

### Fixed
- **Scanning Windows optional features while a feature toggle is running no longer mixes up their output.** The Scan and enable/disable actions share one PowerShell runner, and each subscribes to its output independently. Toggling was already blocked while busy, but Scan was not — so starting a scan mid-toggle let both read the same output stream and cross-contaminate results. Scan is now disabled while a toggle runs (and vice versa), making the two mutually exclusive.

## [1.20.59] - 2026-06-24

### Fixed
- **The App Updates and Context Menu tabs no longer risk a crash when their actions overlap.** On both tabs the long-running actions shared one cancellation source that each action re-created. Starting a second action while the first was still running (e.g. clicking Scan again mid-upgrade, or applying a context-menu preset while a scan ran) could dispose the cancellation source the first was still using and throw. Those actions are now disabled while one of them is running — matching how the Windows Update and Bulk Installer tabs already behave — while Cancel stays available. On the Context Menu tab this also prevents two overlapping runs from corrupting the entry list or triggering two Explorer restarts at once.

## [1.20.58] - 2026-06-24

### Security
- **The file shredder can no longer be tricked into destroying a system file through a junction in the middle of the path.** Its safety check resolved a link only at the end of the path (and expanded short `8.3` names), but a junction or symlink in a *parent* folder — which a standard user can create without admin — was not followed during validation. A path such as `C:\Temp\link\notepad.exe`, where `C:\Temp\link` pointed into `System32`, slipped past the system-folder denylist and the real protected file behind it was overwritten and deleted. The shredder now asks Windows for the fully-resolved canonical path (collapsing every junction/symlink anywhere in the chain) and re-checks that against the protected-folder list before touching anything.

## [1.20.57] - 2026-06-24

### Security
- **The Uninstaller no longer runs a per-user app's uninstaller from a user-writable folder while elevated.** When SysManager ran as Administrator, uninstalling a local app executed the path from its registry `UninstallString` (and any DLL a `rundll32` command would load). That path was trusted even when it pointed inside `%LocalAppData%`, which a standard user can write to — and the uninstall key in `HKCU` is also user-writable. An unprivileged attacker could therefore plant a binary there plus a fake uninstall entry, then have an elevated SysManager run it with admin rights (local privilege escalation). Admin-protected locations (Program Files, Windows, ProgramData) stay trusted as before; the per-user location is now trusted only when SysManager is *not* elevated, so normal per-user uninstalls (e.g. VS Code, Discord) are unaffected.

## [1.20.56] - 2026-06-23

### Fixed
- **SFC and DISM repairs can no longer run at the same time and corrupt each other's output.** Both share a single PowerShell runner whose output event was subscribed by each command independently, so starting one while the other ran cross-contaminated their captured results and progress. They now acquire a shared system-modification lock, making them mutually exclusive (and blocked against other system-repair operations), with a clear "already running" message instead.

## [1.20.55] - 2026-06-23

### Security
- **Deep Cleanup can no longer delete data outside a cache folder via a junction at its root.** The reparse-point guard that stops cleanup from following junctions/symlinks only covered sub-folders, not the cleanup root itself. A junction planted at a cleanup-root path (which a normal user can create without admin) was traversed directly, so the linked target's files could be deleted. The traversal now checks the root for being a reparse point first, and the per-file delete catches only the expected I/O/access exceptions instead of all exceptions.

## [1.20.54] - 2026-06-23

### Fixed
- **Cancelling a disk-usage or large-files scan no longer shows partial results as if complete.** Both scanners stopped mid-traversal on cancel but still returned what they had gathered, so a cancelled scan looked like a finished one with incomplete data. They now stop cleanly and report the scan as cancelled.
- **Traceroute now stops correctly when the destination replies on any probe.** The hop status and the stop-at-destination check used only the last probe's result, so if an earlier probe reached the destination but a later one timed out, the trace could mislabel the hop and keep probing past the target. It now tracks the destination-reached result across all probes of each hop.

## [1.20.53] - 2026-06-23

### Fixed
- **Dashboard quick actions now ask before changing your system.** "Quick Cleanup" deleted temporary files and "Update All Apps" ran a winget upgrade of every installed app with no confirmation — unlike the equivalent actions on their dedicated tabs. Both now show a confirmation dialog first, matching the rest of the app.
- **Installing Windows updates now asks for confirmation.** Selecting updates and clicking install applied them (including drivers and feature updates that can force a restart) without a prompt. A confirmation dialog now precedes the install.
- **Deleting broken shortcuts no longer races other disk operations.** The shortcut cleaner's delete now holds the shared disk operation lock for its duration, so it can't run at the same time as a cleanup or tune-up.

## [1.20.52] - 2026-06-22

### Fixed
- **Cancelling a speed test now stops the ping phase immediately.** The initial latency measurement ran four 2-second probes without honoring the cancellation token, so pressing Cancel during the ping phase could wait up to 8 seconds before stopping. The probes now observe cancellation and stop right away.

## [1.20.51] - 2026-06-22

### Security
- **The file shredder now expands 8.3 short paths before its system-folder safety check.** The guard that blocks shredding inside Windows/System32/Program Files compared full paths, but a short-name alias (e.g. `C:\PROGRA~1`) isn't expanded by the framework, so it could slip past the check. Short paths are now expanded to their long form first, closing that bypass.

## [1.20.50] - 2026-06-22

### Fixed
- **Reverse-DNS lookups during a traceroute no longer keep running after they time out.** Each hop's host-name lookup had an 800 ms budget, but the timeout only abandoned the wait — the lookup itself kept running in the background. It is now actually cancelled when the budget elapses, freeing the resource immediately.

## [1.20.49] - 2026-06-22

### Fixed
- **Removed a rare crash risk in the network ping chart.** The per-host line offset used `Math.Abs` on a hash code, which throws if the hash happens to be the most-negative integer. The calculation now masks the sign bit instead, so it can never overflow.

## [1.20.48] - 2026-06-22

### Fixed
- **Hardened the Zip Slip guard on the speed-test CLI download.** The containment check that keeps extracted archive entries inside the tools directory used a plain prefix test, which a sibling folder whose name merely started with the target's name could slip past. The check now requires a directory-separator boundary, closing that edge case.

## [1.20.47] - 2026-06-22

### Fixed
- **Disk health no longer drops every disk when one reports a bad value.** If a single physical disk returned an unexpected value for its media type, bus type, size, or health status, the conversion threw and aborted the whole scan — so no disks were shown. A disk with an unreadable field is now skipped individually and the rest still appear.

## [1.20.46] - 2026-06-22

### Fixed
- **Windows Update scans and installs no longer leak COM objects on error paths.** Reading an update's KB article list left its underlying COM collection unreleased on every scanned update, and a failed install (one that threw a COM error) leaked the update-installer object. Both are now released deterministically even when the operation throws.

## [1.20.45] - 2026-06-22

### Fixed
- **Saving or restoring the hosts file no longer freezes the window.** The Hosts editor wrote the system hosts file (and restored it from backup) synchronously on the UI thread, so the app could hang briefly while the file was written to or copied from System32. Both operations now run in the background.

## [1.20.44] - 2026-06-22

### Fixed
- **Better screen-reader support for tables and per-row toggles.** Every data table announced itself generically as "Data table"; each now has a content-specific name (Installed applications, Running processes, Windows services, Event log entries, etc.). The per-row enable/disable toggles in Startup Manager and Windows Features, the Startup "hide system entries" toggle, and the Shortcut Cleaner selection checkboxes also gained clear accessible names. No visual change.

## [1.20.43] - 2026-06-22

### Fixed
- **App-install monitoring can no longer start twice and leak a timer.** Starting the App Alerts monitor a second time without stopping it first orphaned the previous background timer and added duplicate folder watchers. Starting now does nothing if monitoring is already running.
- **Listing fixed drives no longer aborts if one drive becomes unavailable mid-scan.** Reading a volume's label/size could throw if the drive dropped out or was locked (e.g. BitLocker) right after it was checked as ready; that one drive is now skipped instead of failing the whole list.
- **Process icons resolve correctly for apps installed after launch.** A failed icon-path lookup was cached permanently, so a program installed later never got its icon until restart. Only successful lookups are cached now.
- **A corrupt cached app icon no longer sticks forever.** If a downloaded icon was truncated/corrupt, the bad cache file was kept and reused; it's now deleted and re-downloaded on next use.
- **Shortcut Cleaner reports an accurate deletion count.** Moving a broken shortcut to the Recycle Bin could silently fail (the shell reports failure without throwing) yet still be counted as deleted; only genuinely recycled items are counted now.

## [1.20.42] - 2026-06-17

### Fixed
- **Deleting broken shortcuts no longer freezes the window.** The Shortcut Cleaner ran the shell delete on the UI thread, so removing many shortcuts could hang the app until it finished; the delete now runs in the background.
- **Loading the full installed-apps list no longer freezes the App Alerts tab.** "Show all installed apps" walked the entire registry uninstall tree on the UI thread; that scan now runs in the background.
- **Refreshing the System Logs twice in a row no longer mixes results.** Starting a second refresh while one was running could let the cancelled run's leftover batches land in the new list. The Refresh button is now disabled while a scan is in progress.
- **Applying a Context Menu preset no longer freezes the window.** Applying a preset ran the registry changes and the Explorer restart on the UI thread, briefly hanging the app; that work now runs in the background.

## [1.20.41] - 2026-06-17

### Fixed
- **"Restore All" in Performance Mode now fully clears the saved baseline.** After restoring everything to the original state, the on-disk snapshot was left behind, so the next change reloaded the now-reverted values as its baseline — a later "Restore All" could then re-apply stale settings. The saved snapshot is now deleted when you restore all.
- **Applying several performance tweaks at once can no longer race the baseline capture.** The independent Apply buttons (power plan, visual effects, Game Mode, Xbox Game Bar, GPU, processor state) each captured the "before" snapshot without coordination, so two run together could both think no baseline existed. Baseline capture is now serialized so the original state is recorded exactly once.

## [1.20.40] - 2026-06-17

### Fixed
- **The Health Score no longer fails outright when a system query hits a transient WMI error.** A repository or RPC fault while reading system info, disk health, or battery could throw an error the score didn't handle, failing the whole calculation; each source now degrades gracefully and the score is still produced from the rest.
- **The memory-error check no longer counts a *passing* memory test as an error.** It counted every Windows Memory Diagnostic result, including the "no errors found" result (event 1101), as a problem; it now counts only the actual error result (event 1201).
- **Known-folder lookup (Downloads, Documents, etc.) now falls back correctly when the system call fails.** The call's failure code was being ignored, so a failed lookup could return an empty path instead of using the standard fallback location; the result is now checked and the fallback applies.

## [1.20.39] - 2026-06-17

### Fixed
- **A transient system-info failure can no longer crash the app from the tray.** The system tray refreshes a CPU/RAM/uptime tooltip on a background timer; if the underlying WMI query failed transiently (for example "RPC server unavailable"), the error could go unhandled and bring the whole app down. The tray refresh now handles those failures and simply skips that tick.

## [1.20.38] - 2026-06-17

### Fixed
- **External command output is no longer occasionally truncated.** Results from tools like the network repair commands, chkdsk, and winget are read on background threads; the app could snapshot the captured text a moment before the last lines arrived, dropping them. The runner now waits for the output streams to fully drain before returning.
- **The Speed Test no longer leaves a stray `speedtest.exe` running if it is cancelled or times out mid-transfer.** Cancellation during the result read could skip the cleanup that kills the CLI process; the process is now always terminated on cancellation.
- **Captured output from chkdsk and the winget upgrade scan is now collected safely.** Both gathered command output into a list that two background reader threads wrote to at once, which could drop or corrupt lines; they now use a thread-safe collector.

## [1.20.37] - 2026-06-17

### Fixed
- **Windows Update and Bulk Installer buttons no longer cause an error when clicked during a running operation.** The toolbar actions (List updates / History / Pending reboot / Install selected, and Bulk Installer's Install Selected) stayed clickable while one was already running; a second click could cancel the first operation's work and crash it. These actions are now disabled while one is in progress and re-enable when it finishes.

## [1.20.36] - 2026-06-17

### Fixed
- **The Dashboard's live temperature readings now resume after you leave and return to the tab.** Temperature polling was started only once at launch; navigating away stopped it permanently, so on returning to the Dashboard the temperatures stopped updating until the app was restarted. Temperature polling now restarts together with the rest of the live vitals whenever the Dashboard is shown again.
- **The Dashboard Event Log check no longer leaks system handles.** Counting recent critical events read each event record without releasing it, leaking a Windows event-log handle per event on every scan. Each record is now disposed as it is read.

## [1.20.35] - 2026-06-17

### Fixed
- **Several panels are now legible in light themes.** A number of cards, banners, and badges (Deep Cleanup safety note and action row, Privacy toggle rows, the File Shredder SSD warning, Quick Cleanup SFC/DISM result cards, the Speed Test info banner, the Windows Features "reboot pending" badge, the Bulk Installer status/installed badges, and the work-in-progress badge) used fixed dark colours that did not change with the theme, so their text became hard to read on the light presets. They now use theme-aware colours and adapt to the active theme.
- **The File Shredder "Shred All" button is readable again.** It showed red text on the indigo primary-button background (very low contrast); it now uses the standard red danger-button style with white text.

## [1.20.34] - 2026-06-17

### Fixed
- **Re-enabling a service now restores its original startup type instead of always setting it to Manual.** When you disabled a service, SysManager forgot what its startup type had been, and "Enable" always set it to Manual — so a service that was originally Automatic (for example) would not start on the next boot as it used to. Disabling now remembers the previous startup type and Enable restores it exactly.

## [1.20.33] - 2026-06-17

### Fixed
- **Temp cleanup can no longer delete files outside the temp folders by following a junction or symbolic link.** Both the Quick Cleanup temp sweep and the background tune-up temp cleanup recursed into reparse points (directory junctions / symbolic links) inside `%TEMP%`, so a link pointing elsewhere could cause real user data outside the temp tree to be deleted. Both now skip reparse points during the walk and only ever remove the link itself, never its target.

## [1.20.32] - 2026-06-17

### Fixed
- **Windows Update no longer leaks system resources during scan and install.** Each scanned and installed update holds several underlying Windows Update COM objects (the update's identity, category list, downloader, and child collections). Some of these were only released on the success path, so a cancelled scan, a failed download, or a category match leaked a handle every time. All of them are now released on every path, including errors and cancellation.

## [1.20.31] - 2026-06-17

### Fixed
- **DNS changes now report failure honestly instead of a false success.** Applying, resetting, or restoring DNS ran the underlying command without treating its errors as fatal, so a change that was actually rejected (for example without administrator rights, or when the adapter dropped) could still be reported as applied. These operations now surface the real error.
- **DNS now targets the same network adapter for reading and changing.** The current-DNS display, the saved snapshot, and the apply/reset/restore actions used slightly different rules to pick the active adapter, so on a PC with several adapters (Wi-Fi + Ethernet + VPN) a change could land on a different adapter than the one shown — breaking the undo. All paths now select the adapter by one shared rule.
- **Disabling a service now reports failure honestly.** Changing a service's startup type ignored the result of the underlying `sc.exe` call, so a change blocked by Windows (for example on a protected service) was still reported as "set to Disabled". The real exit code is now surfaced as a clear error.
- **Refreshing a service's status after a change can no longer crash the app.** Reading a service's status right after stopping or disabling it could throw if the handle had just become invalid; that error is now handled and the status shows as "Unknown" instead.
- **Enabling or disabling a Windows optional feature now reports failure honestly.** A failed feature change could still be reported as successful because the command's error was not propagated; failures now surface correctly.
- **Privacy toggles that fail to apply no longer look like they succeeded.** Toggles backed by machine-wide (HKLM) settings need administrator rights; without elevation the write was silently swallowed and the toggle appeared applied. The app now reports how many changes need administrator rights and keeps the unapplied ones marked as pending.

## [1.20.30] - 2026-06-17

### Fixed
- **App Blocker can no longer block a startup-critical Windows process.** The blocker accepted any executable name, so a user could block boot/logon components such as `winlogon.exe` or `lsass.exe` — which would prevent Windows from starting and leave no way to launch the app to undo it. It now refuses a built-in list of boot- and logon-critical executables with a clear message.
- **Services can no longer disable a boot-critical service behind a generic prompt.** Disabling a service classified as Critical (for example Remote Procedure Call or DCOM Server Process Launcher) could stop Windows from booting or signing in, yet the confirmation read the same as for any safe-to-disable service. Critical services are now refused outright with an explanation of why.
- **Uninstaller no longer runs unvalidated arguments through `rundll32`/`MsiExec`.** A per-user uninstall entry (which can be written without administrator rights) could point these trusted system binaries at an arbitrary DLL or package, which they would then execute with the app's elevation. The uninstaller now requires the `rundll32` DLL to live under a trusted directory and restricts `MsiExec` to a product-code uninstall.
- **File Shredder no longer crashes after a successful shred.** Once at least one item was shredded, the cleanup step that removes finished items ran off the UI thread and threw, aborting the operation on its normal success path. The shredding flow now resumes on the UI thread so completed items are removed cleanly.

## [1.20.29] - 2026-06-17

### Fixed
- **Speed Test no longer reports an inflated upload speed when the server cuts the upload short.** The upload test always credited the full 50 MB payload against the elapsed time, even when the server rejected the request early and only part of the data was sent — producing an unrealistically high number. It now reports a failed measurement if the server rejects the upload, and otherwise measures the bytes actually sent, so the upload speed reflects reality.

## [1.20.28] - 2026-06-17

### Fixed
- **Dashboard alert checks are more robust.** The "estimated time remaining" hint that appears while a dashboard check is still running updated its on-screen state from a background thread, which could occasionally fail to display or glitch; it now updates on the UI thread like every other dashboard check. Separately, two error paths (a failed dashboard check and a failed quick action) now record the underlying error in the log so failures can be diagnosed instead of vanishing silently. No visible change in normal use.

## [1.20.27] - 2026-06-17

### Fixed
- **Deep Cleanup now empties the Recycle Bin through the Windows shell instead of deleting its files directly.** The "Recycle Bin (all drives)" category used the same raw file-delete path as ordinary caches, removing the internal `$Recycle.Bin` index/data files and per-account folders directly. That could leave the Recycle Bin in an inconsistent state (ghost or undeletable items in Explorer) until the next sign-in. It now empties the bin via the documented shell API, the same safe method used elsewhere in the app.

## [1.20.26] - 2026-06-16

### Fixed
- **The About → System Info diagnostics no longer leak system handles.** Building the environment summary (CPU, RAM, GPU, display, and OS lines) queried Windows for hardware details but never released the query result sets, leaking a small COM handle on each refresh. Every query now disposes its results, matching how the rest of the app handles these reads. No change to the information shown.

## [1.20.25] - 2026-06-16

### Fixed
- **Saving the hosts file now preserves its original permissions.** The atomic save introduced in 1.20.24 replaced the file by moving a freshly written temporary file over it, which left the new file with the folder's default permissions instead of the hosts file's own (more restrictive) access-control settings. Saving now replaces the file in place, keeping its existing permissions and attributes intact.

## [1.20.24] - 2026-06-12

### Fixed
- **Hosts entries with multiple hostnames per IP are no longer silently lost.** A line like `127.0.0.1 a b c` was read keeping only the first hostname, so editing and saving dropped the rest. Each hostname is now read as its own entry and survives a round trip.
- **The hosts file is now written atomically.** Saving wrote directly over the file, so a crash mid-write could leave it empty or truncated. It now writes to a temporary file and atomically replaces the target, cleaning up the temp file afterward.

## [1.20.23] - 2026-06-12

### Fixed
- **No more leaked process handles when opening Explorer / links.** Ten places that launch Explorer ("show in folder"/"open file location"), Event Viewer, the browser, or the updater left the returned process handle undisposed. Each now releases it. The launched program is unaffected; only the orphaned handle is cleaned up. Covers Deep Cleanup, Disk Analyzer, Duplicate Finder, Startup Manager, Logs, About, and the Context Menu refresh.

## [1.20.22] - 2026-06-12

### Fixed
- **Drive/disk health reads survive systems without the Storage WMI namespace.** Disk Health and System Info could throw an unhandled COM error on older or headless Windows where the `root\Microsoft\Windows\Storage` namespace isn't present; both now handle that case and fall back gracefully (mirrors the earlier Fixed Drives fix).
- **No leaked process handle when opening a file location.** "Open file location" in Process Manager left the launched Explorer process handle undisposed; it's now released.
- **Swallowed errors are now diagnosable.** Several silent `catch` blocks now log at Debug level with the full exception (update-file cleanup, Deep Cleanup directory deletion, Windows Update size extraction, and the Dashboard polling loop), so failures leave a trace in the log instead of vanishing.

## [1.20.21] - 2026-06-12

### Changed
- **Some status/accent colors now follow the theme instead of being hardcoded.** Replaced hardcoded color values that exactly matched a theme color (success green, warning amber, danger red, accent, hover border) with theme references on the Dashboard, Network Repair, Privacy, and Uninstaller tabs. The colors render identically today but will track the theme going forward.

## [1.20.20] - 2026-06-12

### Fixed
- **Much better screen-reader support across the app.** Many buttons, toggles, drop-downs, search/filter boxes, data grids, and per-row actions had no accessible name, so screen readers announced them generically (or not at all). Added clear, specific accessible names to interactive controls across 21 tabs — including destructive actions (Delete, Shred, Clear History), per-row buttons (now named after the item they act on), and unlabeled inputs. No visual change.

## [1.20.19] - 2026-06-12

### Fixed
- **Activity history can no longer be corrupted by concurrent updates.** The recent-actions log saved its data outside the lock that protects it, so two actions logged at the same time could clash and produce a "collection modified" error or a truncated file. It now writes a consistent snapshot taken under the lock.
- **The in-app updater fails gracefully when the download location can't be determined.** Installing an update now checks the downloaded file's folder up front and shows a clear message instead of risking a crash.

## [1.20.18] - 2026-06-12

### Fixed
- **More resilient reading of battery, memory, and app-list data.** Unexpected values from Windows (battery stats, memory-module details) could throw a conversion error and interrupt a scan; those conversions now fall back safely. The winget output parser also no longer throws when the tool reports columns in an unusual order.

## [1.20.17] - 2026-06-12

### Fixed
- **Plugged several resource leaks.** The system-tray icon's underlying graphics handle was never released on shutdown; the elevated-relaunch helper left a process handle open; and a memory-module query left a WMI result set undisposed. All are now released properly. Also added handling for a WMI namespace being unavailable when reading drive media/bus details, so that case fails quietly instead of surfacing an error.

## [1.20.16] - 2026-06-12

### Fixed
- **Starting or stopping a Windows service no longer crashes the app on failure.** If a service couldn't be started or stopped (access denied, a dependency problem, or the service state changing mid-operation), the underlying error escaped unhandled. Those failures are now caught and shown as a clear status message.
- **Privacy toggles no longer crash on a registry write error.** Applying a privacy toggle only handled permission errors; a registry I/O error or an invalid key/value now logs a warning instead of bringing down the app.
- **Disabling Xbox Game Bar no longer crashes on a registry I/O error.** The Performance tab's Xbox Game Bar action now handles I/O failures the same way it already handled permission and state errors.
- **A misbehaving UI subscriber can no longer permanently lock an operation category.** If a property-change notification threw while acquiring an operation lock, the category could stay locked forever (blocking all future disk/network/system operations of that kind). The lock is now rolled back cleanly if that happens.

## [1.20.15] - 2026-06-12

### Fixed
- **File Shredder can no longer be tricked into destroying a protected system file.** The safety check that blocks shredding inside Windows, System32, and Program Files compared the path you gave it without first following symbolic links — so a link placed in an allowed folder but pointing at a protected system file slipped past the check, and the real file was overwritten. The check now resolves link targets before validating and matches protected folders on an exact directory boundary (so an unrelated folder that merely starts with the same name is no longer falsely blocked). If a file's contents are securely overwritten but the entry can't be removed, the app now reports that clearly instead of surfacing a raw error.

## [1.20.14] - 2026-06-11

### Fixed
- **No more hidden shutdown errors when closing the app.** Cleanup ran more than once on exit (it is triggered by both the window-close and the application-exit events), which double-released the network charts' underlying graphics resources — an error that was caught and hidden but still occurred on every exit. Cleanup is now guarded so it runs exactly once, and the shared network monitors are stopped rather than disposed twice, so the app shuts down cleanly.

## [1.20.13] - 2026-06-11

### Fixed
- **The live-output console now matches the app's card styling.** Its container used a one-off border with a smaller corner radius than every other card; it now uses the shared `Card` style (same surface, border, and 10px radius) while keeping its zero inner padding, so the console looks consistent on the App Updates, Cleanup, System Health, and Windows Update tabs.

## [1.20.12] - 2026-06-10

### Fixed
- **Windows Update list has a real select-all checkbox.** The updates grid's checkbox column used a decorative `✓` header that did nothing and had no accessible name. It is now a working select-all checkbox ("Select all updates") that toggles every row, and it stays in sync with the existing Select all / Deselect all buttons.

## [1.20.11] - 2026-06-10

### Fixed
- **System Logs severity tiles are now screen-reader friendly and colorblind-safe.** The Critical / Errors / Warnings / Info count tiles conveyed their value only through color and an unlabeled number. Each tile now exposes an accessible name with its count (e.g. "Critical events: 3"), and the Critical and Errors tiles — both previously red and indistinguishable to colorblind users — are now told apart by a leading glyph (▲ vs ●) and weight, not hue alone.

## [1.20.10] - 2026-06-10

### Fixed
- **Console output toolbar is now consistent and screen-reader friendly.** The Clear and Copy buttons on the live-output console (shown on App Updates, Cleanup, System Health, and Windows Update) used the implicit default button style; they now use the standard `SecondaryButton` style like the rest of the app. The output list also gained an accessible name ("Live output").

## [1.20.9] - 2026-06-10

### Fixed
- **System Logs time-range chips now show which range is active and are keyboard-navigable.** The 1h / 24h / 7d / 30d / All chips were unlabeled buttons with no selected state, so you couldn't tell which range was applied. They are now a proper radio-button group (matching the Services tab filter chips): the active range is highlighted, the group is arrow-key navigable, and each chip is named for screen readers.

## [1.20.8] - 2026-06-10

### Fixed
- **Theme presets are now keyboard-accessible.** The preset cards in the appearance popup were mouse-only — not reachable by Tab, not activatable from the keyboard, and unnamed to screen readers. They are now focusable, activate with Enter or Space, and announce their preset name. The custom-color hex inputs (accent, background, surface, text) also gained accessible names.

## [1.20.7] - 2026-06-10

### Fixed
- **Search and filter boxes now have accessible names.** The category/filter/search inputs on Apps (Bulk Installer), Uninstaller, Process Manager, Services, and Windows Features had no `AutomationProperties.Name`, so screen readers announced them as anonymous edit fields. Each now states what it filters (e.g. "Filter installed apps", "Search winget packages", "Filter Windows features").

## [1.20.6] - 2026-06-10

### Fixed
- **Screen readers now announce row actions and the App Blocker input by name.** Several icon-only and unlabeled controls had no accessible name, so assistive technology announced them generically (e.g. "X button"). Added `AutomationProperties.Name` to the Process Manager Kill/Open buttons (with the process name), the Ping remove-target button (with the target name), the File Shredder row Remove button (with the file name), and the App Blocker executable-name input.

## [1.20.5] - 2026-06-08

### Added
- **Undo a DNS change.** Applying a DNS preset now snapshots the servers in effect beforehand, and a new "Undo" button on the DNS & Hosts tab restores that exact previous configuration (re-applying the prior static servers, or resetting to DHCP if that was the prior state). Previously the only way back was "Reset to DHCP", which silently discarded any manually-configured DNS.

## [1.20.4] - 2026-06-08

### Fixed
- **Ping monitor no longer leaks its CancellationTokenSource.** When `Stop()` was called while the ping loop was still winding down (the 1.5s wait timed out), the CTS reference was dropped and never disposed. It is now disposed once the loop actually finishes — immediately if already complete, otherwise via a continuation.
- **System Logs no longer block the UI thread while reading the Event Log.** `EventLogService.ReadAsync` ran the blocking `EventLogReader.ReadEvent()` COM call on the caller's (UI) thread, freezing the app while large logs were enumerated. Each read now runs on the thread pool via `Task.Run`.

### Changed
- **Dashboard GPU name now works for AMD/Intel, not just NVIDIA.** When no NVIDIA GPU is present, the Dashboard falls back to `Win32_VideoController` (WMI) to show the adapter name. Live usage % remains NVIDIA-only (it requires vendor-specific APIs).

## [1.20.3] - 2026-06-08

### Fixed
- **Drive enumeration no longer crashes on missing WMI properties.** `FixedDriveService` read `MediaType`/`BusType` with `Convert.ToUInt32(value ?? 0u)`, but WMI returns `DBNull.Value` (not null) for absent properties, so `Convert.ToUInt32(DBNull.Value)` threw and aborted the whole scan on some hardware. Reads now go through a `ToUInt32Safe` helper that treats null and `DBNull` as 0.
- **Uninstaller trusted-directory check no longer accepts sibling folders.** `IsUnderTrustedDirectory` used a bare `StartsWith`, so `C:\Program Files Evil\…` passed the `C:\Program Files` check. It now compares on a normalized directory boundary (trailing separator) so only true sub-paths of a trusted directory are accepted.

## [1.20.2] - 2026-06-08

### Fixed
- **Restore point creation no longer reports false success.** `CreateRestorePointAsync` returned `true` whenever the PowerShell call didn't throw, but `Checkpoint-Computer` fails *non-terminating* in common cases (notably the once-per-24h rate limit), so failures were reported as success — undermining the "everything is reversible" guarantee. It now forces the error to terminate and only returns `true` when an explicit success sentinel is emitted.
- **In-app updater can now find its download asset.** The release-asset matcher looked for a fixed `SysManager.exe`, but releases publish `SysManager-v<version>.exe`, so `AssetUrl`/`AssetSize` were always null. Replaced with `IsMainExeAsset`, which matches the versioned executable and excludes the `.sha256` companion.
- **Windows Update scan no longer leaks COM objects on failure.** `WindowsUpdateService.ScanAsync` released its COM objects only on the success path, so a cancellation or mapping error mid-scan leaked them. The releases now run in a `finally` block.

## [1.20.1] - 2026-06-08

### Fixed
- **App Blocker no longer clobbers a pre-existing debugger.** `BlockApp` wrote its IFEO `Debugger` value unconditionally, overwriting any value already present — which could break a legitimately-debugged application and was unrecoverable (Unblock only removes SysManager's own value). It now refuses to block an executable that already has an external `Debugger` set, leaving the existing configuration intact.
- **Privacy changes now require confirmation.** Applying pending privacy toggles to the registry now prompts with `DialogService.Confirm` first, stating how many changes will be written and how to revert. Declining keeps the changes pending and writes nothing.

## [1.20.0] - 2026-06-08

### Added
- **Restore original hosts file.** A new "Restore original" button on the DNS & Hosts tab reverts the system hosts file to the pristine backup taken before SysManager first modified it.

### Fixed
- **Hosts file backup no longer destroys the pristine original.** `SaveHosts` previously copied the current hosts file over `hosts.bak` on **every** save with `overwrite: true`, so after the first save the backup already held SysManager's own output — the real original was lost and restore was impossible. The backup is now written only once (when none exists), preserving the true pre-SysManager file.
- **DNS and hosts changes now require confirmation.** Applying a DNS preset and overwriting the system hosts file each prompt with `DialogService.Confirm` first, stating exactly what will change and how to revert. Declining makes no system change.

### Changed
- `HostsFileService` gained a path-injection constructor (used only for testing) and `HasBackup` / `RestoreBackup` members backing the new restore flow.

## [1.19.4] - 2026-06-08

### Fixed
- **Destructive cleanup actions now require confirmation.** Three deletion paths ran immediately with no prompt: Deep Cleanup (`CleanAsync`), Temp Cleanup, and Empty Recycle Bin. Each now shows a `DialogService.Confirm` dialog first — Deep Cleanup states the file count and total size and warns the files bypass the Recycle Bin. Declining cancels with no changes. Adds regression tests covering both the decline (nothing deleted) and confirm (files deleted) paths.

## [1.19.3] - 2026-06-08

### Fixed
- **Deep Cleanup no longer follows junctions / symbolic links (data-loss fix).** The cleanup traversal descended into reparse points, so a junction inside a cache folder could lead `File.Delete` to remove files **outside** the target tree — for example real user data behind a junction. Traversal now detects reparse points (`FileAttributes.ReparsePoint`) and skips them entirely, never entering or deleting through a link.
- **Dashboard alerts no longer always show "unavailable".** The App Updates, Event Log, and Pending Reboot scanners each had a free code block after their `catch` that ran unconditionally and overwrote the real scan result with an "unavailable / green" status. The Dashboard therefore reported false-OK for these three checks regardless of the actual system state. The decision logic is now extracted into pure, unit-tested methods and the overwrite is gone, so real results surface.

## [1.19.2] - 2026-06-08

### Fixed
- **UI test harness could never locate the app executable.** `AppFixture` hardcoded a `net8.0-windows` output path while the project targets `net10.0-windows`, so `FindExecutable()` always threw `FileNotFoundException` when running the UI automation tests locally. The path is now resolved dynamically from the `net*-windows` build folder, so it survives future framework bumps.

## [1.19.1] - 2026-06-05

### Fixed
- **Code-scanning cleanup (mechanical, no behavior change).** Resolved a batch of low-risk CodeQL quality alerts in hand-written code:
  - Empty `catch` blocks now log at Debug level (`ContextMenuService` registry-write fallbacks and command-path parse, `App.OnExit` service-provider disposal) or carry an explanatory comment where the caught exception is expected (`DnsHostsViewModel` cancellation on teardown).
  - `Path.Combine` → `Path.Join` in `ActivityLogService` to avoid silently dropping earlier path segments.
  - Object `==`/`!=` comparisons made explicit with `ReferenceEquals` where reference identity is intended (`MainWindowViewModel` tab activation, `TemperatureService` core-vs-package sensor check).
  - Implicit `foreach` filtering/mapping replaced with explicit LINQ (`Where`/`Select`/`FirstOrDefault`) in `TemperatureService`, `FileShredderService`, `HostsFileService`, and `ContextMenuViewModel`.
  - Removed useless local assignments in `DashboardViewModel` (an unused `Stopwatch`, an unread temp-scan size) while preserving the scans' side effects.

## [1.19.0] - 2026-06-05

### Changed
- **Uniform outer margins across 13 views.** BatteryHealth, BulkInstaller, ContextMenu, DiskAnalyzer, Drivers, DuplicateFile, Performance, Privacy, ProcessManager, Services, Startup, Uninstaller, WindowsFeatures all migrated from `Margin="32,24"` to the canonical `Margin="28,24,28,16"` already used by Dashboard and AppAlerts. Layout is now consistent across the whole nav.
- **Page background — theme-aware.** 15 views were defining a hardcoded `LinearGradientBrush PageBg` (`#070A0F`/`#0B1220`/`#090D16`) and using `{StaticResource PageBg}` for their root `Grid.Background`. Replaced with `{DynamicResource Surface0}`. The gradient resource definitions are gone, the views are smaller, and a future light-theme switch will work without per-view edits.
- **Admin elevation banner colors — theme-aware.** Replaced 4 hardcoded amber hex values (`#1AFBBF24`, `#40FBBF24`, `#FBBF24`, `#FCD34D`) used by elevation banners and warning pills across 17 views with new theme brushes: `WarningBgSubtle`, `WarningBg`, `WarningStripe`, `WarningText`. Defined once in `App.xaml`, used everywhere.

## [1.18.3] - 2026-06-03

### Fixed
- **Async-safety follow-up.** Dropped the remaining `async void` pipe-listener path and the sync-over-async wrappers flagged during review, completing the threading cleanup started in 1.18.2.

## [1.18.2] - 2026-06-03

### Fixed
- **Pipe listener no longer fire-and-forgets via `async void`.** `App.StartPipeListener` was an async-void method, meaning any exception escaping the loop would crash the process via the AppDomain handler. Renamed to `StartPipeListenerAsync` returning `Task`; `OnStartup` calls it as `_ = StartPipeListenerAsync()` so a stray exception flows through `TaskScheduler.UnobservedTaskException` (logged) instead of terminating the app.
- **StartupService — removed sync wrapper over async.** `SetEnabled` (sync) was a thin wrapper around `SetEnabledAsync` using `.GetAwaiter().GetResult()`. The wrapper is gone; tests now call `SetEnabledAsync` directly via xUnit `Task` test methods.
- **Schtasks stderr read** — replaced `stderrTask.Wait(timeout) ? .GetAwaiter().GetResult() : ""` with `await stderrTask.WaitAsync(timeout)` so the read is fully async with a clean timeout fallback.
- **Privacy Toggles no longer write to the registry on every click.** Toggling a switch now updates local state only; the user must press **Apply** to write pending changes. A live counter shows how many changes are pending, and **Discard** reverts them without touching the registry. Prevents accidental system changes when scrolling through the toggle list.
- **Dashboard no longer freezes on first load.** Static system info (CPU, OS, RAM modules) is now loaded asynchronously instead of blocking the UI thread on a synchronous WMI capture. The Dashboard tab is responsive immediately on startup.
- **DNS / Hosts tab loads asynchronously.** Reading the hosts file is now async (`File.ReadAllLinesAsync`); Refresh no longer freezes the UI on slow disks.
- **Icon cache eviction is now true FIFO.** The icon cache previously evicted random entries because `ConcurrentDictionary.Keys` has no insertion-order guarantee. Frequently-used icons could be dropped while stale ones survived. The cache now tracks insertion order via a queue and evicts the oldest entries first when the size limit is reached.
- **SpeedTest output read** — replaced `Task.Result` access after `Task.WhenAll` with proper `await` to remove the deadlock-prone pattern (the awaited tasks were already complete, but the style is now safe under all call paths).
- **Silent exception swallowing** — empty `catch { }` blocks now log at Debug level so failures are diagnosable: ThemePopup custom-color parser, TemperatureService LibreHardwareMonitor close, WindowsUpdateService COM/RuntimeBinder catches in `ExtractKbIds` and `ClassifyCategory`.
- **Deep Cleanup** — file/directory cleanup errors are now logged in addition to being added to the per-run error list, so unexpected I/O issues surface in the SysManager log.
- **Admin relaunch** — `RelaunchAsAdmin` now distinguishes the user's UAC decline (Win32 error 1223 → Information) from real Win32 failures (Warning) and logs `InvalidOperationException` instead of swallowing it silently.
- **`SHGetFileInfo` P/Invoke** — added `SetLastError = true` so callers can inspect the Win32 error code on failure.

### Changed
- **PrivacyView toolbar** — the **Apply All** button is replaced by **Apply** (writes only pending changes) and **Discard** (reverts to last-applied state). Both are disabled when no changes are pending. The Apply button uses the primary style to highlight the action.
- **PerformanceService.TakeSnapshotAsync** XML doc now warns callers that the method must run before any state-modifying call; the recommended lazy-initialization pattern is documented inline.
- **PerformanceService.CreateRestorePointAsync** comment reworded — the previous `// BUG-003:` marker was a design note, not an open bug; replaced with an explanation of why PowerShell `AddParameter` cannot be used here.
- **App.xaml.cs unhandled-exception dialog** — added inline note explaining why `MessageBox.Show` is used instead of `DialogService` (the dispatcher exception may originate from DialogService itself).
- **`.gitignore`** — added entries for local developer notes (`.session-notes/`, `notes-local.md`, `scratch.md`) so scratch files can never be tracked accidentally.

## [1.18.1] - 2026-06-03

### Fixed
- **Critical and high-priority audit fixes (P0 + P1).** Resolved the top-severity findings from the code audit ahead of the 1.18 line — crash-safety, resource, and correctness fixes across the service layer.

## [1.18.0] - 2026-06-03

### Fixed
- **Windows Update install actually works** — replaced PSWindowsUpdate's `Install-WindowsUpdate` with direct calls to the Windows Update Agent COM API (`Microsoft.Update.Session`). PSWindowsUpdate filters out optional driver updates client-side even when the COM API can install them; the new code installs everything WUA reports as available, including drivers, firmware, Defender Definitions, cumulative updates, and feature upgrades.
- **Honest per-update progress** — live console now streams real per-update events (Connecting → Downloading → Installing → ✓ Installed) instead of a 16-times-repeated PSWindowsUpdate pre/post search noise that resulted in "Installed 0".
- **Per-row Status reflects reality** — each row's Status column is updated as the install progresses (`Pending…` → `Downloading…` → `Installing…` → `Installed` / `Installed (reboot required)` / `Failed (download)` / `Failed (install code N)` / `Not applied`).
- **Status column wider with tooltip** — fits longer messages like "Installed (reboot required)" without truncation; full text always visible on hover.
- **Live output panel no longer auto-resizes** — fixed height of 240px so the DataGrid above keeps its space when many log lines arrive.

### Changed
- **Removed live output from Ping and Traceroute** — those tabs already display their data graphically (latency chart, hops grid). The console panel was redundant and stole vertical space.
- **Single-header live output panel** — removed the redundant "Live output" Card+Expander wrapper; the ConsoleView toolbar (Live output / Clear / Copy / Auto-scroll) now sits directly on the panel border, matching CleanupView and AppUpdatesView.
- **KB column header tooltip** — explains "KB = Microsoft Knowledge Base article ID" since not all updates have one (drivers, firmware, Defender).

### Added
- **WindowsUpdateService** — new service wrapping `Microsoft.Update.Session` COM API directly. Supports scan (`IsInstalled=0`), download, EULA acceptance, install, and reboot detection. Exposes a `Log` event for live console streaming.
- **Title-based category classifier** — Defender / Driver / Cumulative / Security / Servicing / .NET / Feature upgrade / Update, with COM `Categories` collection lookup as the primary signal and title heuristics as fallback. Unit-tested.

## [1.17.4] - 2026-06-03

### Fixed
- **Windows Update install never applied updates** — install command sent KB numbers prefixed with `KB` (e.g. `KB5034441`) to PSWindowsUpdate's `-KBArticleID` parameter, which expects bare digits; the cmdlet matched zero updates and exited silently. Updates without a KB (Defender Definitions, drivers) and updates with multiple KBs were also excluded by the selection filter. The status bar reported a fabricated "Installed N update(s)" message based on the selection count rather than the cmdlet's actual result.
- **Honest install reporting** — Install-WindowsUpdate output is now captured and parsed; the status bar shows real counts (`Installed X/Y. Failed: Z. Not applied: W.`) and each row's Status column reflects per-update outcome (`Installed`, `Failed`, `Not applied`).

### Changed
- **Unified update list** — "List updates" now returns Standard, Feature upgrades, and Hidden updates in a single grouped table; the separate "Feature upgrades" button has been removed. Category column distinguishes Security, Cumulative, Defender, Driver, Servicing, .NET, Feature upgrade, and Hidden entries.
- **Title-based install pipeline** — selected updates are matched against the live PSWindowsUpdate feed by Title rather than KB, so updates without a KB (Defender, drivers) and updates with multiple KBs install correctly.

## [1.17.3] - 2026-05-29

### Fixed
- **Performance** — NetworkSharedState TracerouteHops converted to BulkObservableCollection with ReplaceWith() (eliminates per-hop UI notifications during route updates).
- **Performance** — ServicesViewModel safety level counts now computed in a single pass instead of 3 separate LINQ queries.
- **Consistency** — DnsHostsView removed stale `HorizontalGridLinesBrush` property (no visual impact, code cleanliness).
- **Consistency** — AppBlockerView DataGrid BorderThickness set to 0 matching all other views.

## [1.17.2] - 2026-05-29

### Fixed
- **Memory leaks** — AboutViewModel now properly disposes ManagementObject instances in all 5 WMI foreach loops (CPU, RAM, GPU, Display, OS detection).
- **Silent failures** — ThemeService Save/Load empty catch blocks now log errors via Serilog instead of swallowing silently.
- **Dashboard error handling** — replaced 4 bare `catch (Exception)` in alert scanners with logged exceptions for diagnostics.
- **UI flicker** — BulkInstallerViewModel.FilteredApps converted from ObservableCollection to BulkObservableCollection with ReplaceWith().
- **Visual consistency** — ShortcutCleanerView DataGrid now has `Background="Transparent"` and `BorderThickness="0"` matching all other views.

## [1.17.1] - 2026-05-29

### Fixed
- **Documentation** — ARCHITECTURE.md updated with new TemperatureService, ActivityLogService, and rewritten DashboardViewModel description to reflect v1.17.0 redesign.

## [1.17.0] - 2026-05-29

### Added
- **Dashboard redesign** — complete overhaul of the landing page:
  - **Real-time vitals** — CPU%, RAM%, GPU% with 300ms polling (smoother than Task Manager), live indicator dots, detailed hardware info (cores/threads, DDR speed, VRAM usage)
  - **Temperatures** — real-time sensor readings via LibreHardwareMonitor (admin) or NvAPIWrapper (non-admin NVIDIA). Shows CPU Package, GPU Core, GPU Hot Spot, all storage drives. Color-coded (green/blue/yellow/red). "Run as admin for all sensors" button when elevated data unavailable.
  - **Storage overview** — per-drive usage bars with color coding (<50% green, 50-75% blue, 75-90% yellow, >90% red)
  - **System Alerts** — auto-scans at boot with loading spinners: SMART health, app updates count, memory errors (30d), Event Log critical events (7d), pending reboots. Each with ETA if scan takes >5s.
  - **Quick Actions** — Run Quick Cleanup, Update All Apps, Check Windows Updates, Run Speed Test. Each runs inline with progress bar, result summary, and "Go to [tab] for more details" navigation button. Buttons unlock after action completes.
  - **Recent Activity** — last 5 user actions with timestamps (persisted to JSON)
  - **Health Score** hero card with recommendations (existing, repositioned)
  - **IsActive pattern** — polling pauses when user leaves Dashboard tab (saves CPU)
- **TemperatureService** — new service aggregating temps from LibreHardwareMonitor + NvAPIWrapper + SMART
- **ActivityLogService** — new service persisting user action history to `%LOCALAPPDATA%\SysManager\activity.json`
- **NvAPIWrapper.Net** — new dependency for NVIDIA GPU temps without admin
- **LibreHardwareMonitorLib** — new dependency for full sensor access with admin

## [1.16.3] - 2026-05-29

### Fixed
- **Code quality** — ContextMenuService uses `[GeneratedRegex]` for compile-time regex (performance + AOT-ready).
- **Naming standardization** — all admin relaunch methods now consistently named `RelaunchAsAdmin` across all 12 ViewModels (was mixed: `RelaunchElevated`, `RequestElevation`).
- **Naming standardization** — filter properties unified to `FilterText` everywhere (LogsViewModel was `SearchText`, ServicesViewModel was `Filter`).
- **ConsoleViewModel** — removed dead optimization branch (clear-and-rebuild path was unreachable).
- **Missing toasts** — added completion notifications to System Health scan and App Alerts "Show Installed".

## [1.16.2] - 2026-05-28

### Fixed
- **UI uniformity** — AppAlertsView fully reworked: proper Card wrappers, styled buttons (Primary/Secondary/Danger), removed DataGrid gridlines, standardized header using Display style, consistent column styling.
- **DashboardView consistency** — standardized margins (28px), replaced inline admin button template with app-wide AdminButton/elevation banner pattern, added proper button styles to all actions.
- **Performance** — replaced `ObservableCollection` with `BulkObservableCollection` in UninstallerViewModel, WindowsFeaturesViewModel, WindowsUpdateViewModel, and LogsViewModel (eliminates UI flicker from Clear+Add loops).

## [1.16.1] - 2026-05-28

### Fixed
- **Security hardening** — version bump for security and performance fixes.

## [1.16.0] - 2026-05-28

### Added
- **Context Menu redesign** — complete overhaul of the Context Menu Manager tab:
  - **Presets:** Win10 Default (classic full menu), Win11 Default (modern compact), Custom
    (manual toggles). Selecting Win10/Win11 resets to clean default by disabling all
    third-party entries (Git, NVIDIA, etc.) — user can re-enable individually.
  - **Win10/Win11 style toggle** — switch between classic full context menu and modern
    Win11 "Show more options" via registry. Automatically restarts Explorer.
  - **Visual preview on hover** — real screenshots showing exactly what each menu style
    looks like (default + custom + "Show more options" expanded).
  - **Entry explanations** — human-readable descriptions for ~40 common entries shown
    inline (e.g. "Opens a Git Bash terminal in the current directory").
  - **"Applies to" column** — clearly shows whether an entry affects Files, Folders,
    Directory Background, or Desktop.
  - **HKCU fallback** — system-protected entries (TrustedInstaller) can now be toggled
    via HKCU registry override instead of failing with "access denied".

### Fixed
- **Crash on admin relaunch** — `CancellationTokenSource disposed` error no longer shown
  when restarting the app with elevated privileges.
- **Admin elevation banner** — Context Menu tab now shows the standard admin banner
  (matching all other tabs) with "Run as administrator" button.

## [1.15.0] - 2026-05-27

### Added
- **ETA on long operations** — estimated time remaining now shown on:
  SFC scan, DISM restore, Bulk Installer, Uninstaller, and App Updates.
  Uses linear extrapolation from elapsed time and current progress percentage.

## [1.14.0] - 2026-05-27

### Added
- **Sidebar smooth animation** — expand/collapse groups now slide with a 150ms animation
  instead of instant jump.
- **Chevron indicator** — rotating arrow on sidebar group headers showing expand/collapse state.
- **Full-width hitbox** — entire sidebar group header row is clickable, not just the text.

### Changed
- **Theme button relocated** — moved from content area top-right (caused overlaps) to sidebar
  footer bottom-right, next to version info. Always accessible, no overlapping.

## [1.13.3] - 2026-05-27

### Fixed
- **Theme compliance** — replaced hardcoded hex colors with DynamicResource tokens so all
  UI elements follow the active theme. ConsoleView, nav hover, DataGrid hover, and semantic
  status colors (Success, Warning, Danger, Info) now update live on theme switch.
- **AccentSoft opacity** — unified hover/selected background opacity to 9.4% across all
  views (was inconsistent between 6%–9.4%).

## [1.13.2] - 2026-05-27

### Fixed
- **DataGrid column resize** — all 19 DataGrids across 16 views now have `MinWidth` on every
  column preventing content from being compressed to invisible on resize.
- **Startup Manager "Open" button clipped** — column widened from 60→80px.
- **Toggle switch clipping** — toggle columns (Startup, Context Menu, Windows Features) widened
  to 62px to prevent pill shape from being cut off.
- **Action columns no longer resizable** — buttons/toggles/checkboxes columns locked with
  `CanUserResize="False"` so users cannot accidentally shrink them.

## [1.13.1] - 2026-05-27

### Fixed
- **Theme performance** — freeze all runtime-created brushes for reduced GC pressure
  and improved WPF rendering throughput.
- **Theme popup duplicate handlers** — prevent event subscriptions from stacking on
  repeated popup opens, which caused redundant theme re-applies.

## [1.13.0] - 2026-05-27

### Added
- **Theme customization** — persistent appearance settings with Dark/Light/Custom modes.
  Choose from 12 curated presets (6 dark, 6 light) or fully customize accent, background,
  surface, and text colors. Settings saved between sessions.
- **Theme button** — palette icon in top-right corner, accessible from every page.
- **Background shade slider** — fine-tune lightness/darkness within any preset.
- **Auto companion preset** — switching Dark↔Light automatically selects the matching
  color family (e.g. Midnight Indigo ↔ Clean Indigo).

### Changed
- All color resources converted from `StaticResource` to `DynamicResource` for live
  theme switching without restart.

## [1.12.1] - 2026-05-27

### Fixed
- **Startup crash** — duplicate implicit CheckBox style in App.xaml caused
  `XamlParseException` ("Item has already been added") preventing the app from launching.

## [1.12.0] - 2026-05-26

### Added
- **SpeedTest server selection** — dropdown to choose Ookla test server (Auto/Bucharest/
  London/Frankfurt/Amsterdam/Paris/New York) instead of always using nearest.

## [1.11.0] - 2026-05-26

### Added
- **Bulk Installer "Installed" badge** — apps already on the system show a green "Installed"
  badge. Detection via `winget list` at startup.
- **SpeedTest explanation** — info banner explaining Ookla vs HTTP test differences.
- **Network Repair explanations** — detailed descriptions for each repair action
  (Flush DNS, Reset Winsock, Reset TCP/IP).

### Fixed
- **App update failure messages** — more helpful explanations when downloads fail
  (mentions network issues, firewall, retry suggestions).

## [1.10.3] - 2026-05-26

### Fixed
- **DashboardView** — replaced 30+ hardcoded hex colors with StaticResource tokens
  (Surface1, Surface2, Border1, TextPrimary, TextSecondary, Info).
- **AppBlockerView** — full structural modernization (Display header, Card wrappers,
  button styles, DataGrid accessibility, Background, margins).
- **DnsHostsView** — DataGrid grid-lines removed, accessibility name added, text token.
- **ObservableCollection → BulkObservableCollection** — AppAlerts, AppBlocker,
  ShortcutCleaner now use single-notification ReplaceWith() instead of N Add() events.
- **Missing toast notifications** — added on Drivers, Services, ShortcutCleaner,
  DeepCleanup (3 operations), NetworkRepair.
- **UninstallerView** — hardcoded `#6366F1` replaced with `{StaticResource Accent}`.

## [1.10.2] - 2026-05-26

### Added
- **Process Manager real-time refresh** — 1-second auto-refresh timer matches Task Manager
  update speed. CPU measurement window reduced to 100ms for faster snapshots.
- **SFC progress bar** — parses stdout for completion percentage, shows real progress.
- **DISM progress bar** — parses stdout for percentage (handles decimal formats like 62.3%).
- **Ping live output** — ConsoleView showing real-time replies per target (time, timeout).
- **Traceroute live output** — ConsoleView with hop-by-hop results and explanations
  (gateway detection, ISP backbone, filtered nodes, destination reached).

## [1.10.1] - 2026-05-26

### Fixed
- **UI uniformity audit** — replaced all remaining CheckBoxes with purple ToggleSwitch on:
  Performance (5 toggles), Logs (5 severity filters), Ping targets, Process Manager,
  Deep Cleanup categories.
- **Hover consistency** — all interactive elements now use `#186366F1` purple tint.
  Fixed: LogsView, DiskAnalyzer, NetworkRepair (3 cards), DuplicateFile (added missing hover).
- **Dashboard** — replaced green Tune-Up button with PrimaryButton (purple), green borders
  with Accent.
- **Ping targets** — green tint background replaced with purple.
- **Hardcoded colors → StaticResource** — ~30 instances replaced across 8 views
  (Danger, Success, Warning, Info, Accent tokens).

## [1.10.0] - 2026-05-26

### Added
- **Safety ratings on Services** — each service shows Safe/Caution/Critical badge with
  description tooltip. Filter chips in toolbar to show only safe-to-disable services.
- **Safety ratings on Windows Features** — same badge system as Services.
- **Curated safety database** — 50+ services and 20+ features with researched safety
  levels and human-readable explanations.
- **Startup Manager hide system** — toggle to filter out Windows/Microsoft system entries.
- **Filter chip styles** — reusable green/amber/red radio pill components.

## [1.9.1] - 2026-05-26

### Fixed
- **Startup Manager columns** — reduced fixed widths to prevent last column overflow.
- **Startup Manager icons** — use extracted executable path for more accurate icon resolution.
- **Windows Update live output** — increased MinHeight/MaxHeight for better visibility.

## [1.9.0] - 2026-05-26

### Added
- **Purple toggle switch** — global ToggleButton component replacing all CheckBoxes and
  enable/disable buttons. Consistent on/off/locked states across Startup Manager, Privacy,
  Windows Features, and Context Menu tabs.
- **Glass toast notifications** — bottom-right overlay appears on operation completion
  (scan, install, cleanup, shred, etc). Auto-dismisses after 5 seconds.
- **Inline status bar** — progress state transitions visually from purple (busy) to green (done).

### Changed
- **Startup Manager** — toggle column uses purple ToggleSwitch instead of checkbox.
- **Privacy Toggles** — scaled checkbox replaced with ToggleSwitch.
- **Windows Features** — Enable/Disable button replaced with ToggleSwitch.
- **Context Menu** — checkbox replaced with ToggleSwitch.

## [1.8.0] - 2026-05-26

### Added
- **Dark title bar** — forced immersive dark mode via DWM API, no more white chrome.
- **SSD warning on File Shredder** — info banner explaining wear-leveling limitations.
- **Download button in updater** — users can now click Download when an update is available.
- **Windows Features status column** — shows Enabled/Disabled on initial scan.

### Fixed
- **ProgressBar accent color** — all progress bars now use purple theme globally.
- **RadioButton/CheckBox accent** — power plan selector and all checkboxes match theme.
- **Bulk installer hover** — app rows now highlight with visible purple tint on mouseover.
- **"Install selected" on History tab** — buttons hidden when viewing update history.
- **Startup Manager refresh** — fixed cross-thread collection update crash.
- **Startup Manager open folder** — robust path extraction for apps with arguments (lghub etc).
- **DNS detection** — skips virtual adapters, iterates all active until DNS found.
- **Release history notifications** — single UI update instead of N individual events.

### Changed
- **Complete UI redesign** — glass card components, golden admin system, modern severity
  badges, unified accent color (#6366F1) throughout all views.

## [1.7.20] - 2026-05-25

### Fixed
- **Silent test runs** — AdminHelper.RelaunchAsAdmin, AboutViewModel.OpenUrl, and
  DialogService.Confirm now skip execution in test context, preventing UAC prompts
  and browser tabs during `dotnet test`. All 2281 tests pass silently.

## [1.7.19] - 2026-05-25

### Fixed
- **Task.Delay in WindowsUpdateViewModel** — replaced remaining `Task.Delay(1)`
  with `Task.Yield()` for consistent async startup pattern.

## [1.7.18] - 2026-05-25

### Fixed
- **Atomic update downloads** — UpdateService temp file + SHA-256 verification +
  atomic rename (carried forward from 1.7.17 fix scope).
- **ObservableCollection mutation** — build full list before clearing collection.
- **DeepCleanup skipped-file counts** — track and surface IOException/access errors.
- **Navigation refactor** — data-driven BuildNavGroups() with Group()/Item() helpers.

## [1.7.17] - 2026-05-25

### Fixed
- **Task.Delay anti-patterns** — replaced `Task.Delay(1000)` and `Task.Delay(250)` with
  `Task.Yield()` in AboutViewModel and WindowsUpdateViewModel startup paths.
- **UpdateService atomic download** — downloads now write to a `.tmp` file first, compute
  SHA-256 on the temp file, then atomically `File.Move` to the final target. Prevents
  half-written binaries from being used after interrupted downloads.
- **ObservableCollection mutation** — AboutViewModel `LoadHistoryAsync()` now builds the
  full list with LINQ `.Select().ToList()` before clearing/adding to the collection,
  separating data construction from UI mutation.

### Added
- **DeepCleanup skipped-file counts** — scan now tracks files that threw IOException,
  UnauthorizedAccessException, or SecurityException and reports `SkippedCount` in the
  `CleanupCategory` model. CountDisplay shows "N files - M skipped" when applicable.

### Changed
- **InitNavigation refactored to data-driven** — sidebar tree construction replaced with
  `BuildNavGroups()` returning a `NavGroup[]` via `Group()` and `Item()` helper methods.
  Subtitle and Tooltip are derived from child labels.
- **Version** aligned to 1.7.17.

## [1.7.16] - 2026-05-22

### Fixed
- **Tray icon creation is now forced**, ensuring the system-tray icon appears even when the platform delays its initial creation.

### Changed
- **Upgraded H.NotifyIcon to 2.3.0** for more reliable tray-icon handling.

## [1.7.15] - 2026-05-22

### Fixed
- **Tray icon reliability** — follow-up adjustments to ensure the system-tray icon initializes correctly during startup.

## [1.7.14] - 2026-05-22

### Added
- **Real app icons in Bulk Installer** — app icons are fetched via the Google Favicon service and cached locally, so the catalog shows recognizable icons instead of placeholders.

### Fixed
- **Tray icon always visible** — falls back to a system icon when an app icon fails to load, so the tray icon is never missing.

## [1.7.13] - 2026-05-22

### Fixed
- **Bulk Installer icons** — real application icons (Chrome, Firefox, Steam, etc.)
  downloaded via Google Favicon API with local cache and offline fallback.
- **Elevation banners** — App Updates, Uninstaller, Bulk Installer now uniform.
  Services banner moved above toolbar. All 13 admin pages consistent.
- **File Shredder** — fixed white page (transparent DataGrid background).
- **Column resize** — CanUserResizeColumns on all remaining DataGrids.
- **Tray icon** — shows real app icon from exe (not generic).

## [1.7.12] - 2026-05-22

### Fixed
- **Tray icon loads from the executable**, which is reliable under single-file publishing, with a pack-URI fallback if the embedded icon cannot be read.

## [1.7.11] - 2026-05-22

### Fixed
- **Tray icon visibility** is now set explicitly, so the system-tray icon shows reliably.

## [1.7.10] - 2026-05-22

### Fixed
- **Uniform elevation banners** across App Updates, Uninstaller, and Bulk Installer, so every tab presents the admin-elevation prompt the same way.
- **File Shredder no longer shows a blank page** when the tab is opened.

### Changed
- **Resizable grid columns everywhere** — `CanUserResizeColumns` is now enabled on all data grids for consistent behavior.

## [1.7.9] - 2026-05-22

### Fixed
- **Services tab — consistent banner ordering.** The admin elevation banner now sits above the toolbar, matching the layout used by the other tabs.

## [1.7.8] - 2026-05-22

### Fixed
- **Ping chart flicker** — chart buffers now use BulkObservableCollection with single
  Reset notification instead of per-item Add/Remove, eliminating visual stutter during
  live ping monitoring.

## [1.7.7] - 2026-05-22

### Fixed
- **Uniform elevation banners** — all 10 admin-required pages now show consistent
  elevation UI with page-specific reasons and "Run as administrator" button.

## [1.7.6] - 2026-05-22

### Fixed
- **Uniform elevation banners (first 5)** — Windows Update, Windows Features, Privacy,
  DNS & Hosts, and Services now use identical elevation banner design.

## [1.7.5] - 2026-05-22

### Fixed
- **Ghost checkboxes** — eliminated phantom empty rows in Windows Update and Uninstaller
  DataGrids via `CanUserAddRows="False"`.
- **DNS & Hosts elevation** — added "Run as administrator" banner (was missing).
- **File Shredder empty state** — hides table headers when no files are added.
- **Startup column width** — "Open" button no longer cut off.
- **Resizable columns** — all 18 DataGrid tables now support column resizing.

## [1.7.4] - 2026-05-22

### Fixed
- **DNS & Hosts page empty** — view referenced non-existent converter causing silent
  XAML load failure.
- **Quick Tune-Up ignored No** — now asks explicit confirmation before any action.
- **Design polish** — Bulk Installer redesigned with categories, descriptions, custom
  search. Context Menu Manager with friendly names. Elevation badges restyled.

## [1.7.3] - 2026-05-22

### Fixed
- **Critical: startup crash** — fixed "Entry point DefWindowProc not found in user32.dll"
  that prevented the app from launching. P/Invoke declaration now correctly specifies
  `DefWindowProcW` entry point.
- **Shutdown crash** — fixed ObjectDisposedException when closing the app
  (DnsHostsViewModel CTS disposal race condition).

## [1.7.2] - 2026-05-22

### Fixed
- **Shutdown crash** — prevented `ObjectDisposedException` in `DnsHostsViewModel` when the app is closed while a refresh is in flight.

## [1.7.1] - 2026-05-21

### Fixed
- **Code review findings** — addressed security, thread-safety, and disposal issues surfaced during review (#487).

## [1.7.0] - 2026-05-21

### Added
- **Context Menu Manager** — scan, enable/disable Windows Explorer right-click entries
  via LegacyDisable (non-destructive). Covers files, folders, directory background,
  and desktop with search/filter and registry backup.

## [1.6.0] - 2026-05-21

### Added
- **DNS Changer** — quick-switch between Google, Cloudflare, Quad9, OpenDNS, or DHCP
  with automatic adapter detection and one-click apply/reset.
- **Hosts File Editor** — visual editor for the Windows hosts file with add/remove/toggle
  entries, IP/hostname validation, and automatic backup before saves.

## [1.5.0] - 2026-05-21

### Added
- **Privacy Toggles** — 12 one-click privacy switches (telemetry, advertising ID, Copilot,
  Cortana, web search, widgets, Start suggestions, lock screen tips) with instant apply
  and registry state detection.

## [1.4.0] - 2026-05-21

### Added
- **File Shredder** — secure file deletion with multiple overwrite methods (Quick 1-pass,
  Standard 3-pass, Thorough 7-pass). Protects system paths, uses confirmation dialog,
  supports files and folders.

## [1.3.0] - 2026-05-21

### Added
- **System Info Export** — comprehensive system report (OS, CPU, GPU, RAM, storage,
  network, SMART data) exportable to file or clipboard from the About tab.

## [1.2.0] - 2026-05-21

### Added
- **Bulk App Installer** — install multiple applications via winget with curated list
  of 25 apps across 7 categories, category/text filtering, and per-app progress.

## [1.1.0] - 2026-05-21

### Added
- **Windows Update** — individual update selection via checkboxes. Users can now
  select/deselect specific updates before installing. Added "Select all" and
  "Deselect all" buttons. KB article IDs validated before passing to PowerShell.

## [1.0.0] - 2026-05-20

### Changed
- **BREAKING:** migrated from .NET 9 to .NET 10 — requires .NET 10 Desktop Runtime
  to run. All projects (main, tests, integration tests, UI tests) now target
  `net10.0-windows`. CI workflows updated to use .NET 10 SDK.

## [0.48.39] - 2026-05-20

### Fixed
- **ObservableCollection batch updates** — replaced Clear() + foreach Add() pattern
  (N+1 CollectionChanged events) with BulkObservableCollection.ReplaceWith() (single
  Reset notification) across 10 ViewModels, reducing UI notification overhead during
  data refreshes.

## [0.48.38] - 2026-05-20

### Fixed
- **LogService** — path sanitization regex now dynamically derives the user
  profile directory from `Environment.GetFolderPath` instead of assuming a
  hardcoded `<drive>:\Users\` pattern; falls back to the generic regex if the
  environment variable is unavailable.
- **MarkdownTextBlock** — cached `FontFamily("Consolas")` as a static field to
  eliminate per-render allocation in code span formatting.

## [0.48.37] - 2026-05-19

### Fixed
- **DiskHealthReport** — fixed potential integer overflow in `HealthPercent`
  calculation when `ReadErrors` or `WriteErrors` exceed `int.MaxValue`; arithmetic
  now uses `long` before clamping to the 0–20 deduction cap.
- **SpeedTestService** — documented pinned Ookla CLI version (`1.2.0`) with
  maintenance comment explaining update procedure and Authenticode verification.

## [0.48.36] - 2026-05-19

### Fixed
- **MemoryTestService** — `ManagementObject` instances in `GetModulesAsync` WMI
  query are now properly disposed via `using (mo)` block, preventing native handle
  leaks when enumerating physical memory modules.
- **NetworkSharedState** — `Dispose()` now fully releases all SkiaSharp paint
  resources: series paints (stroke, geometry, fill), axis paints (name, labels,
  separators), and class-level legend/tooltip paints. Previously only typefaces
  were disposed, leaking unmanaged `SKPaint` handles.

### Added
- **ServicesViewModelTests** — 20 unit tests covering ApplyFilter logic: category
  filters (All, Running, Stopped, Safe to disable, Advanced), text search by name/
  display name/description, combined filters, sort order, empty data, and property
  change triggers.

## [0.48.35] - 2026-05-19

### Fixed
- **ProcessManagerViewModel** — resolved CodeQL `cs/complex-condition` alert (#302)
  by replacing chained null-conditional `||` expression with a `ReadOnlySpan` loop
  in `MatchesDescription`.
- **PerformanceView** — eliminated MVVM violation: removed `PropertyChanged`
  subscription and `Checked` event handler from code-behind; radio buttons now use
  two-way `EqualityConverter` binding to `SelectedPlan` (pure XAML, no code-behind
  logic).
- **OperationLockServiceTests** — replaced flaky `Barrier` + `Thread.Sleep`
  thread-safety test with deterministic `CountdownEvent` + `ManualResetEventSlim`
  synchronization; asserts exactly 1 acquisition instead of `>= 1`.

### Added
- **EqualityConverter** — reusable two-way `IValueConverter` that compares a bound
  value to `ConverterParameter`; ideal for radio button groups bound to a string
  property.
- **EqualityConverterTests** — 10 unit tests covering Convert/ConvertBack, null
  handling, and case sensitivity.
- **FormatHelperTests** — 14 unit tests covering `FormatSize` at all boundaries
  (bytes, KB, MB, GB) with exact boundary and mid-range values.

### Changed
- **README.md** — added missing tech stack entries: Microsoft.Extensions.DependencyInjection,
  H.NotifyIcon.Wpf, NSubstitute.

## [0.48.34] - 2026-05-19

### Fixed
- **PerformanceService** — implemented `IDisposable` to properly dispose the
  internal `SemaphoreSlim` gate, preventing resource leaks on app shutdown.

### Changed
- **README.md** — corrected sidebar tab counts (56 total, 25 implemented).
- **ARCHITECTURE.md** — removed false claim that TuneUpService and
  ShortcutCleanerService are instantiated directly (both are registered in DI).
- **ARCHITECTURE.md** — added 9 missing services to the Key services section
  (AppAlertService, AppBlockerService, BatteryService, DialogService,
  IconExtractorService, OperationLockService, ProcessDescriptionService,
  SpeedTestHistoryService, ShortcutCleanerService).

## [0.48.33] - 2026-05-18

### Fixed
- **CodeQL** — resolved 5 remaining source code alerts:
  - Replaced generic `catch (Exception)` in `ViewModelBase.InitializeAsync`
    with specific exception types (`InvalidOperationException`,
    `UnauthorizedAccessException`, `IOException`, `HttpRequestException`,
    `TimeoutException`).
  - Converted `UninstallerService.IsUnderTrustedDirectory` foreach+if to
    LINQ `.Any()` (cs/linq/missed-where).
  - Converted `WindowsFeaturesService.ParseFeatureList` foreach loop to
    LINQ `.Select().Where()` pipeline (cs/linq/missed-select).
  - Extracted `ProcessManagerViewModel.MatchesFilter` complex condition into
    three focused helper methods (cs/complex-condition).
  - Replaced `Path.Combine` with `Path.Join` in `UpdateService.DownloadAsync`
    (cs/path-combine).
- **CodeQL workflow** — added query filter to suppress `cs/call-to-obsolete-method`
  for `UpdateService.VerifyAuthenticode` (intentional use of `CreateFromSignedFile`
  — no modern .NET replacement exists without P/Invoke).

## [0.48.32] - 2026-05-18

### Fixed
- **ConsoleViewModel** — buffer trimming now uses clear-and-rebuild when
  removing more than 25% of lines, reducing worst-case from O(n×excess)
  to O(n) (CQ-LOW: ConsoleViewModel O(n²)).
- **LogsViewModel** — event log entries are now dispatched to the UI thread
  in batches of 50 instead of one-at-a-time, reducing dispatcher overhead
  by ~98% when loading large event logs (CQ-LOW: LogsViewModel batch dispatch).

## [0.48.31] - 2026-05-18

### Fixed
- **FormatSize duplication** — extracted shared `FormatHelper.FormatSize` method;
  `ProcessManagerViewModel`, `DiskAnalyzerViewModel`, and `DuplicateFileViewModel`
  now delegate to the shared helper instead of duplicating the switch expression.
- **OEM encoding duplication** — `CleanupViewModel` (SFC + DISM) and
  `SystemHealthViewModel` (chkdsk) now use `PowerShellRunner.OemEncoding`
  instead of duplicating the encoding resolution logic inline.

### Changed
- **Test parallelism** — enabled `parallelizeTestCollections` in xunit.runner.json
  so pure-logic unit tests run concurrently, reducing CI test time. Tests that
  touch shared OS resources remain serialized via `[Collection("Network")]`
  (TEST-M4).
- **Mocking framework** — added NSubstitute 5.3 to the test project, enabling
  interface-based mocking for future tests that need to isolate OS dependencies
  (TEST-H1).
- **TESTING.md** — documented test infrastructure (frameworks, parallelism
  strategy, conventions for mocking and time-dependent tests).

## [0.48.30] - 2026-05-18

### Fixed
- **ViewModelBase** — added `InitializeAsync` helper method that wraps
  fire-and-forget async calls with structured error handling. Exceptions
  from async initialization are now caught and logged via Serilog instead
  of becoming unobserved task exceptions (CQ-M3).
- **12 ViewModels** — replaced `_ = InitAsync()` fire-and-forget pattern
  with `InitializeAsync(InitAsync)` in: AboutViewModel, BatteryHealthViewModel,
  CleanupViewModel, DashboardViewModel, DeepCleanupViewModel,
  PerformanceViewModel, ProcessManagerViewModel, ServicesViewModel,
  SpeedTestViewModel, StartupViewModel, SystemHealthViewModel,
  WindowsUpdateViewModel.

## [0.48.29] - 2026-05-18

### Changed
- **IconExtractorService** — `FindExecutableByName` results are now cached in a
  `ConcurrentDictionary`, eliminating repeated Program Files directory scans
  (~100+ subdirs) on every process list refresh (PERF-M5).
- **NetworkSharedState** — `TrimBuffer` now uses a clear-and-rebuild strategy
  when removing more than 25% of buffer entries, reducing worst-case complexity
  from O(n×removeCount) to O(n) (PERF-M3).

## [0.48.28] - 2026-05-18

### Fixed
- **CodeQL** — resolved 38 code scanning alerts across 16 source files:
  - Replaced `Path.Combine` with `Path.Join` in 8 locations to prevent
    unexpected path rooting when arguments contain absolute paths.
  - Added descriptive comments to 6 empty catch blocks (intentional
    swallowing of expected exceptions like `FormatException`, `IOException`).
  - Replaced generic `catch (Exception)` in `TrayIconService.OnTimerTick`
    with specific exception types (`OperationCanceledException`,
    `ObjectDisposedException`, `InvalidOperationException`).
  - Converted implicit foreach filters to explicit `.Where()` calls in
    `AppAlertService`, `NetworkSharedState`, `WindowsFeaturesService`,
    `SpeedTestService`, and `TrayIconService`.
  - Extracted complex conditions into helper methods in
    `UninstallerService.IsUnderTrustedDirectory` and
    `ProcessManagerViewModel.MatchesFilter`.
  - Flattened nested if-statements in `UninstallerViewModel.SelectAll`.
  - Replaced `if/else` assignment with ternary in
    `HealthScoreService` weighted average calculation.
  - Converted `ComputeDiskScore` foreach loop to LINQ `.Select().Min()`.
  - Converted `DashboardViewModel` manual `Dispose()` call to `using var`
    declaration for `OperationLockService` lock guard.
  - Removed redundant `(SolidColorBrush)` cast in
    `OutputKindToBrushConverter`.
- **CodeQL workflow** — added `codeql-config.yml` to exclude `obj/` and
  `bin/` directories from analysis (36 alerts in compiler-generated code).

## [0.48.27] - 2026-05-15

### Fixed
- **NetworkSharedState** — SkiaSharp `SolidColorPaint` objects are now disposed
  when a ping target is removed, preventing unmanaged memory leaks (CQ-M1).
- **NetworkSharedState** — latency chart offset now uses a stable hash of the
  target host instead of `Targets.IndexOf`, preventing visual jumps when
  targets are removed mid-session (CQ-M2).
- **PerformanceViewModel** — added `Dispose` override to clean up snapshot
  reference and satisfy the base class disposal contract (CQ-M4).

## [0.48.26] - 2026-05-15

### Changed
- **SystemInfoService** — static WMI data (OS caption, CPU name, disk models)
  is now cached on first query; only dynamic data (CPU load, RAM, uptime) is
  re-queried every 60 seconds, reducing WMI overhead by ~70% (PERF-M1).
- **NetworkSharedState** — `RecomputeStats` rewritten with manual loops instead
  of LINQ `.Where().Select().ToList()`, eliminating heap allocations on the
  hot path that runs 32×/sec per target (PERF-M2).
- **TrayIconService** — added `Interlocked` re-entrancy guard on
  `UpdateTooltipAsync` so overlapping timer ticks skip instead of stacking
  concurrent WMI calls (PERF-M4).

## [0.48.25] - 2026-05-15

### Fixed
- **HealthAnalyzer** — no longer claims "DNS is clean" when DNS IS bad; when
  both DNS and game server show trouble, correctly returns Mixed verdict
  instead of GameServer (FUNC-M2).
- **TuneUpService** — empty directory removal now sorts by path depth (separator
  count) instead of string length, ensuring deepest directories are deleted
  first regardless of path name length (FUNC-M3).
- **SpeedTestHistoryService** — `SaveAsync` and `ClearAsync` now serialize via
  `SemaphoreSlim` to prevent concurrent load-modify-save races that could lose
  history entries (FUNC-M4).
- **FixedDriveService** — multi-disk enrichment now maps drive letters to
  physical disks via `MSFT_Partition.DiskNumber`, correctly annotating media
  type and bus type on systems with multiple drives (FUNC-M5).

## [0.48.24] - 2026-05-15

### Fixed
- **UpdateService** — cached download now validated by SHA-256 hash (stored in
  companion `.sha256` file) instead of file size alone; prevents cache poisoning
  with same-size payloads (SEC-M2).
- **SpeedTestService** — Zip Slip protection: manual extraction validates each
  entry path stays within the target directory; blocks path traversal attacks
  via crafted zip archives (SEC-M3).
- **SpeedTestService** — DLL hijacking mitigation: Ookla CLI process now
  launches with `WorkingDirectory` set to System32 instead of the user-writable
  tools directory, preventing CWD-based DLL search order hijacking (SEC-M4).
- **ServiceManagerService** — defensive validation on service names before
  interpolating into registry paths; rejects names containing path separators
  or null characters (SEC-M6).
- **UninstallerService** — `ParseUninstallCommand` hardened: rejects shell
  metacharacters (`|&;` backtick `$(`) to prevent command injection; improved
  `.exe` boundary detection to avoid misparsing paths with `.exe` substrings;
  removed unsafe fallback that treated unparseable strings as executables
  (SEC-M7).
- **PowerShellRunner** — expanded security contract documentation clarifying
  that `ExecutionPolicy.Bypass` is safe only because all script content is
  hard-coded in source; callers must never interpolate user input (SEC-M8).
- **DiskHealthService** — replaced bare `catch` blocks in WMI conversion
  helpers with specific exception types (`FormatException`, `OverflowException`,
  `InvalidCastException`).
- **DeepCleanupService** — replaced bare `catch` with specific `IOException`,
  `UnauthorizedAccessException`, `SecurityException`.
- **TracerouteMonitorService** — replaced bare `catch` with specific network
  and operation exception types.
- **TracerouteService** — replaced generic `catch (Exception)` in event raiser
  with specific `ObjectDisposedException`, `InvalidOperationException`.
- **PingMonitorService** — replaced bare `catch` in event raiser with specific
  exception types.
- **EventLogService** — replaced bare `catch` blocks in record projection and
  message formatting with specific `EventLogException`,
  `InvalidOperationException`.

## [0.48.23] - 2026-05-15

### Fixed
- **UpdateService** — added Authenticode signature verification on downloaded
  update binaries; rejects files with invalid (tampered) signatures (SEC-H1).
- **AboutViewModel** — update script now uses a random GUID filename to prevent
  TOCTOU race conditions on the updater .cmd file (SEC-M1).
- **UninstallerService** — uninstall executables from registry are now validated
  against trusted directories (Program Files, Windows, ProgramData,
  LocalApplicationData); rejects paths outside these locations (SEC-H2).
- **EventLogService** — XPath sanitization now strips all metacharacters
  including `|()@*<>` in addition to the existing set (SEC-M5).
- **BatteryInfo** — `HealthPercent` returns -1 (unavailable) instead of 0 when
  WMI capacity data is missing (no admin elevation), preventing false-critical
  health scores on every non-elevated laptop (FUNC-H1).
- **HealthScoreService** — `ComputeBatteryScore` treats -1 (unavailable) as
  neutral (100) instead of critical (10).
- **BatteryHealthViewModel** — displays "requires elevation" when health data
  is unavailable instead of showing 0%.
- **StartupService** — registry approved-state blob now uses bitmask
  `(blob[0] & 1) == 0` for enabled detection, fixing Windows 11 which uses
  `07` (not just `03`) for disabled entries (FUNC-M1).

## [0.48.22] - 2026-05-15

### Fixed
- **AppAlertService** — `NewAppDetected` event now marshaled to the UI thread
  via captured `SynchronizationContext`, preventing crashes when
  `FileSystemWatcher`/`Timer` callbacks invoke subscribers directly.
- **NetworkRepairService** — added `SemaphoreSlim` gate to serialize
  subscribe/unsubscribe on the shared `PowerShellRunner`, preventing
  concurrent calls from interleaving output.
- **PerformanceService** — same `SemaphoreSlim` serialization for all methods
  that subscribe to `PowerShellRunner.LineReceived`.
- **PowerShellRunner** — documented that `LineReceived` fires on thread-pool
  threads; subscribers must marshal to the dispatcher for UI updates.
- **StartupService** — added `RuntimeBinderException` catch for dynamic COM
  shortcut resolution (`.lnk` files with broken targets).
- **StartupService** — `GetAwaiter().GetResult()` on stderr task now guarded
  with a 5-second timeout to prevent hangs if the pipe isn't fully drained.
- **AppAlertsViewModel** — use `Application.Current.Dispatcher` instead of
  `Dispatcher.CurrentDispatcher` to avoid capturing the wrong dispatcher.
- **NetworkSharedState** — documented that `FlushPending` direct-call path
  (when `Dispatcher == null`) is intentional for unit tests / headless mode.
- **AboutViewModel** — removed auto-download of updates without user consent;
  user must now explicitly click Download.
- **App.xaml.cs** — single-instance activation now uses a named pipe listener,
  fixing activation when the window is minimized to tray (no `MainWindowHandle`).
- **MainWindow.xaml.cs** — ViewModel disposal now also hooks
  `Application.Current.Exit` as a safety net for when `OnClosed` is not called.

### Changed
- **SysManager.csproj** — version updated from 0.12.1 to 0.48.21 (cosmetic;
  auto-release overrides at build time).
- **SysManager.Tests.csproj** — xunit bumped from 2.5.3 to 2.9.3 (matches
  UITests project).
- **SysManager.IntegrationTests.csproj** — xunit bumped from 2.5.3 to 2.9.3.
- **dependabot.yml** — added `IntegrationTests` directory entry for NuGet
  dependency monitoring.

## [0.48.21] - 2026-05-15

### Fixed
- **AdminHelper** — `Process.GetCurrentProcess()` now properly disposed via
  `using` in `RelaunchAsAdmin()` (prevents brief handle leak).
- **HexToBrushConverter** — frozen brushes now cached by hex value in a
  `ConcurrentDictionary` to eliminate repeated allocations and GC pressure
  on frequently-updating bindings (dashboard, health score, tune-up).
- **App.xaml.cs** — `ReleaseMutex()` wrapped in try-catch for
  `ApplicationException` (thrown if called from wrong thread on shutdown).
- **EtaCalculator** — added thread-safety documentation (single-thread
  requirement via UI dispatcher).

## [0.48.20] - 2026-05-15

### Fixed
- **NetworkSharedState** — replaced obsolete `SkiaPaint.FontFamily` with
  `SKTypeface = SKTypeface.FromFamilyName()` on 4 axis paint objects,
  eliminating all CS0618 build warnings.
- **AboutViewModel** — replaced `Assembly.Location` (returns empty in
  single-file publish) with `AppContext.BaseDirectory` lookup, eliminating
  IL3000 warning.

## [0.48.19] - 2026-05-15

### Fixed
- **DuplicateFileService** — skip reparse points (symlinks, junctions) during
  directory traversal to prevent infinite loops on circular symlinks.
- **LargeFileScanner** — same reparse point check added.
- **DeepCleanupService** — `EnumerateFiles()` now catches `IOException` and
  `UnauthorizedAccessException` during `MoveNext()` iteration, not just at
  enumerator creation. Prevents crashes on files that become inaccessible
  mid-scan.
- **TrayIconService** — `OnTimerTick` (async void) now wraps the entire call
  in try-catch to prevent unhandled exceptions from crashing the application.

## [0.48.18] - 2026-05-15

### Fixed
- **SystemInfoService** — `QueryMemory()` and `QueryDisks()` now properly
  dispose `ManagementObject` and `ManagementObjectCollection` instances via
  `using` statements (4 foreach loops fixed, prevents COM handle leaks).
- **FixedDriveService** — same WMI disposal fix for MSFT_PhysicalDisk query.
- **DeepCleanupViewModel** — post-clean rescan no longer deadlocks on the
  operation lock. Extracted `ScanCoreAsync()` (lock-free) called from
  `CleanAsync` which already holds the disk lock.
- **WindowsFeaturesViewModel** — separated shared `_cts` into `_scanCts` and
  `_toggleCts` so toggling a feature no longer cancels a running scan.

## [0.48.17] - 2026-05-15

### Fixed
- **DeepCleanupViewModel** — dispose previous CancellationTokenSource before
  creating a new one in Scan/Clean/LargeScan (3 locations). Prevents kernel
  handle leak on repeated operations.
- **SpeedTestViewModel** — same CTS disposal fix (2 locations: HTTP + Ookla).
- **TracerouteViewModel** — same CTS disposal fix.
- **ShortcutCleanerViewModel** — same CTS disposal fix.
- **NavItem** — implement `IDisposable` to unsubscribe `PropertyChanged`
  handler from ViewModel on teardown. Previously 51 subscriptions leaked
  permanently. `MainWindowViewModel.Dispose()` now disposes all NavItems.

## [0.48.16] - 2026-05-15

### Fixed
- **SpeedTestService** — stdout and stderr now read in parallel via
  `Task.WhenAll` to prevent classic Windows pipe buffer deadlock when
  Ookla CLI writes enough to stderr while stdout is being consumed.
- **DiskHealthService** — added regex validation (`^[\w{}\-\\.:/]+$`) on
  WMI objectId before WQL interpolation (defense-in-depth against injection).
- **UninstallerService** — tightened `PackageIdPattern` regex: replaced `\s`
  (which allows tabs/newlines) with a literal space character.

### Changed
- **WindowsFeaturesService** — added SECURITY-CRITICAL documentation comment
  on `FeatureNamePattern()` regex explaining it is the sole injection defense.

## [0.48.15] - 2026-05-15

### Fixed
- **AppBlockerView, AppAlertsView, ShortcutCleanerView** — removed XAML
  `<UserControl.DataContext>` that bypassed DI container, causing these views
  to operate on isolated ViewModel instances instead of the shared singletons.
- **DashboardView** — ColorHex string bindings now use `HexToBrushConverter`
  instead of invalid `<SolidColorBrush Color="{Binding}"/>` which produced
  runtime binding errors (health score ring, recommendations, disk verdicts,
  tune-up overall verdict).
- **WindowsFeaturesView** — "Not elevated" warning badge now uses `FlexVis`
  converter (supports `ConverterParameter=Inverse`) instead of `BoolToVis`
  which ignores the parameter, causing the badge to always display.

### Changed
- **AppBlockerView, AppAlertsView** — replaced legacy `SystemControlForeground*`
  brushes with app-standard `TextPrimary`/`TextSecondary`/`Border1` resources
  for consistent dark-theme styling.
- **MainWindowViewModel** — corrected stale comment "non-DI resolved" to
  "resolved from DI at runtime" (all 4 VMs are DI singletons since v0.48.0).

## [0.48.14] - 2026-05-15

### Fixed
- **SystemInfoService (CQ-002)** — ManagementObjectCollection and ManagementObject
  instances now properly disposed via `using` in QueryOs() and QueryCpu().
- **HexToBrushConverter** — SolidColorBrush now frozen after creation to prevent
  cross-thread access crashes; bare `catch` narrowed to `catch (FormatException)`.

### Changed
- **LargeFileScanner, DuplicateFileService, DiskAnalyzerService** — replaced
  remaining `Array.Empty<T>()` with collection expressions `[]` (MODERN-003).

## [0.48.13] - 2026-05-15

### Fixed
- **UninstallerService (SEC-007)** — trusted system binaries (MsiExec, rundll32)
  now resolved to absolute System32 path before execution, preventing PATH
  hijacking attacks.
- **SpeedTestService (SEC-008)** — Ookla CLI process now killed on timeout or
  cancellation, preventing orphan processes consuming resources indefinitely.
- **SpeedTestService (PRIV-001)** — all exception messages in Log.Debug calls
  now sanitized via LogService.SanitizePath to prevent username leakage in logs.

## [0.48.12] - 2026-05-15

### Fixed
- **DiskHealthService (CQ-007)** — WQL ASSOCIATORS OF query now escapes single
  quotes in objectId, preventing potential WQL injection.
- **OperationLockService (CQ-008)** — removed redundant lock object; TryAcquire
  and Release now use ConcurrentDictionary atomic TryAdd/TryRemove directly.
- **PingMonitorService (CQ-015)** — CancellationTokenSource only disposed if the
  background loop actually completed, preventing ObjectDisposedException in
  still-running pump tasks.

## [0.48.11] - 2026-05-15

### Fixed
- **ProcessManagerViewModel (CQ-004)** — replaced sync-over-async
  `GetAwaiter().GetResult()` with proper `await` inside `Task.Run` async
  lambda, preventing thread pool thread blocking.
- **DeepCleanupService (CQ-010)** — replaced `Directory.GetFiles()` and
  `GetDirectories()` (full array allocation) with lazy `EnumerateFiles()`
  and `EnumerateDirectories()` to reduce memory pressure on large directories.
- **TracerouteService (CQ-011)** — bare `catch {}` replaced with specific
  `PingException`, `SocketException`, `InvalidOperationException` catches;
  subscriber error catch narrowed to `catch (Exception)`.

## [0.48.10] - 2026-05-15

### Fixed
- **DiskHealthService (CQ-001)** — ManagementObjectCollection and ManagementObject
  instances now properly disposed via `using` statements, preventing COM resource
  leaks during SMART/reliability queries.
- **SpeedTestService (CQ-003)** — Ookla CLI process now has a 5-minute independent
  timeout via linked CancellationTokenSource, preventing indefinite hangs.

### Changed
- **CONTRIBUTING.md** — corrected .NET SDK reference from 8 to 9; added
  SysManager.IntegrationTests to project layout.
- **SECURITY.md** — updated supported versions table to reflect 0.48.x as latest.

## [0.48.9] - 2026-05-14

### Fixed
- **SpeedTestService** — empty catch blocks replaced with `Log.Debug` calls for
  best-effort file cleanup (resolves 4 CodeQL `cs/empty-catch-block` alerts).
- **WindowsFeaturesViewModel** — if/else replaced with ternary for enable/disable
  dispatch (CodeQL `cs/missed-ternary-operator`).
- **UninstallerViewModel** — if/else replaced with ternary for local vs winget
  uninstall dispatch (CodeQL `cs/missed-ternary-operator`).

## [0.48.8] - 2026-05-14

### Fixed
- **UninstallerService (SEC-005)** — `StartsWith` allowlist replaced with exact
  filename match to prevent bypass via similarly-named executables (e.g.
  "MsiExecEvil.exe"). `/I` → `/X` replacement now uses regex word-boundary
  match to avoid corrupting GUIDs.
- **SpeedTestService (SEC-006)** — Authenticode verification now fail-closed:
  if the Ookla binary is unsigned or subject mismatches, it is deleted and an
  exception is thrown instead of just logging a warning.
- **DialogService** — singleton setter now rejects null to prevent global
  null-swap hazards.
- **Application.Current.Shutdown()** — added null-conditional `?.` operator on
  all 5 shutdown call sites (WindowsUpdateVM ×2, DashboardVM, AppUpdatesVM,
  TrayIconService) to prevent NullReferenceException in tests or non-standard
  hosting.
- **AboutViewModel** — clipboard copy no longer reports success when
  `Clipboard.SetText` throws `ExternalException` (clipboard locked).
- **NetworkSharedState** — TOCTOU race in FlushPending replaced
  `ContainsKey` + indexer with `TryGetValue`; all paint SKTypeface instances
  now disposed on cleanup (LEAK-003 complete).
- **AppAlertService** — replaced `ContainsKey` + set with atomic `TryAdd` to
  prevent duplicate new-app notifications in race conditions.
- **PerformanceService** — `CreateRestorePointAsync` no longer uses always-true
  `results != null` check; relies on exception propagation for failure.
- **ServiceManagerService** — service name regex narrowed from `\s` (any
  whitespace including newlines) to literal space only.
- **WindowsFeaturesViewModel** — CancellationTokenSource now cancelled before
  disposal in all code paths to prevent orphaned in-flight operations.
- **App.xaml.cs** — DI ServiceProvider now disposed on application exit,
  ensuring all DI-owned singletons implementing IDisposable are cleaned up.
- **DashboardView.xaml** — disk verdict and overall tune-up verdict colors now
  bound to model `ColorHex`/`OverallColorHex` instead of hardcoded green.

## [0.48.7] - 2026-05-14

### Fixed
- **UninstallerService (SEC-002)** — UninstallLocalAsync now validates that the
  executable exists and has a .exe extension before running. Prevents execution
  of arbitrary commands from HKCU registry keys (modifiable without admin).
- **EventLogService (SEC-003)** — XPath sanitization now strips quotes, brackets,
  slashes in addition to single quotes to prevent XPath injection.
- **LogService (SEC-004)** — path sanitization regex now covers all drive letters
  (A-Z:\Users\) instead of only C: drive.

### Changed
- **Modern C#** — replaced Array.Empty<T>() with collection expressions []
  across 7 files: DiskAnalyzerService, DuplicateFileService, LargeFileScanner,
  UpdateService, CleanupCategory, TuneUpResult, HealthScoreResult (MODERN-003).

## [0.48.6] - 2026-05-14

### Fixed
- **PingMonitorService** — bare catch replaced with specific AggregateException
  and ObjectDisposedException (CodeQL cs/catch-of-all-exceptions).
- **TracerouteMonitorService** — same bare catch fix.
- **OutputKindToBrushConverter** — simplifiable boolean expression refactored
  to pattern matching (CodeQL cs/simplifiable-boolean-expression).
- **LogsViewModel** — unsafe cast from ICollectionView to CollectionView
  replaced with safe as-cast with fallback (CodeQL cs/cast-from-abstract).

## [0.48.5] - 2026-05-14

### Changed
- **DuplicateFileService** — ShouldSkipDir uses OrdinalIgnoreCase instead of
  ToLowerInvariant allocation on every path (PERF-002).
- **LargeFileScanner** — same OrdinalIgnoreCase fix (PERF-002).
- **SpeedTestService** — SHA-256 hashing uses stream instead of
  File.ReadAllBytes to avoid loading entire zip into memory (PERF-004).
- **ProcessManagerService** — MainModule accessed once per process instead of
  twice, halving P/Invoke overhead (PERF-005).
- **AboutViewModel** — CopyEnvironmentInfo WMI queries now run on background
  thread via Task.Run, preventing UI freeze (PERF-008).

## [0.48.4] - 2026-05-14

### Fixed
- **IconExtractorService** — cache eviction race condition resolved with
  double-checked lock pattern (THR-002).
- **PingMonitorService** — Start/Stop race on _cts resolved with lock around
  state transitions (THR-003).
- **TracerouteMonitorService** — same Start/Stop race fix as PingMonitor
  (THR-003).
- **AppAlertService** — List<FileSystemWatcher> access from concurrent threads
  protected with lock on Start/Stop (THR-004).
- **NetworkRepairService** — List<string> output replaced with ConcurrentQueue
  to prevent corruption from background thread callbacks (THR-005).
- **PerformanceView** — SyncRadioButtons now marshals to UI thread via
  Dispatcher.BeginInvoke when called from background (THR-006).

## [0.48.3] - 2026-05-14

### Fixed
- **DuplicateFileGroup** — WastedBytes now raises PropertyChanged when Count or
  FileSize changes (missing NotifyPropertyChangedFor attributes).
- **UpdateService** — pre-release and draft GitHub releases are now filtered out
  in GetRecentAsync results.
- **ServiceManagerService** — StartService no longer throws when the service is
  already in StartPending state.
- **DiskAnalyzerService** — empty directories are no longer incorrectly flagged
  as access-denied; the flag now tracks actual UnauthorizedAccessException.
- **WindowsUpdateViewModel** — null-conditional on Application.Current before
  calling Shutdown() prevents NullReferenceException during unit tests or
  non-standard hosting.
- **TuneUpService** — SHEmptyRecycleBin HRESULT is now checked; returns false
  on failure instead of always reporting success.

## [0.48.2] - 2026-05-14

> **Note:** Versions 0.49.0–0.53.1 below were released under the previous
> repository (`SysManager`). When the project migrated to `SystemManager`
> (2026-05-14), the auto-release workflow reset to the last tag on the new
> repo (v0.48.1). Subsequent releases continue from 0.48.2 onward.
> The entries below are preserved for historical completeness.

### Fixed
- **Security: SpeedTestService** — remove fabricated placeholder SHA-256 hashes
  that caused perpetual warning logs (alert fatigue). Security now relies on
  Authenticode signature verification of the extracted binary + zip structural
  integrity check (SEC-001).

## [0.53.1] - 2026-05-14

### Fixed
- **Resource leak: NetworkSharedState** — dispose SKTypeface on LegendTextPaint
  in Dispose() to release unmanaged SkiaSharp memory (LEAK-003).
- **Resource leak: TrayIconService** — dispose icon resource stream after
  creating System.Drawing.Icon to prevent stream leak (LEAK-006).
- **Resource leak: MemoryTestService** — dispose Process returned by
  Process.Start when launching mdsched.exe (LEAK-007).

## [0.53.0] - 2026-05-13

### Added
- **Navigation: 4 new groups** — Gaming & Profiles, Privacy & Security,
  Customization, and Advanced groups added to sidebar navigation.
- **Gaming & Profiles (5 WIP tabs)** — Gaming Profile, Standby List Cleaner,
  Timer Resolution, CPU Core Affinity, Display Profiles.
- **Privacy & Security (6 WIP tabs)** — Privacy & Telemetry, Debloater & Ads,
  Browser Cleaner, Edge/OneDrive Remover, Defender Tweaks, Notification Blocker.
- **Customization (4 WIP tabs)** — Context Menu, Dark Mode Scheduler, Volume
  Control, Environment Variables.
- **Advanced (4 WIP tabs)** — Restore Points, Profile Export/Import, CLI
  Interface, System Report.
- **Monitor (3 new WIP tabs)** — File Lock Detector, Settings Watchdog,
  Bandwidth Monitor added to existing Monitor group.
- **System (2 new WIP tabs)** — Task Scheduler, Boot Analyzer added to
  existing System group.
- **Cleanup (1 new WIP tab)** — Scheduled Maintenance moved into Cleanup group.

### Changed
- **Navigation structure** — reorganized from 9 groups to 12 groups for better
  feature categorization as the app grows.
- **Placeholder descriptions** — improved all WIP placeholder descriptions with
  clearer feature explanations and correct issue references.

## [0.52.0] - 2026-05-13

### Fixed
- **Resource leak: BatteryService** — dispose WMI ManagementObject instances
  in foreach loops to prevent COM RCW accumulation (LEAK-001, partial).
- **Resource leak: ShortcutCleanerService** — remove double ReleaseComObject
  on same COM interface to prevent undefined behavior (LEAK-002).
- **Resource leak: UninstallerViewModel** — store LineReceived handler in field
  and unsubscribe in Dispose to prevent memory leak (LEAK-004).
- **Bug: WindowsFeaturesViewModel** — call NotifyCanExecuteChanged on
  ToggleFeatureCommand when IsBusy changes to prevent double-clicks (BUG-001).
- **Thread safety: ProcessStatusToBrushConverter** — freeze static brushes to
  prevent cross-thread InvalidOperationException (THR-001, partial).
- **Performance: BoolToElevationBadgeBrushConverter** — pre-create static frozen
  brush instances instead of allocating per Convert call (PERF-001, partial).

## [0.51.0] - 2026-05-13

### Fixed
- **Security: PowerShellRunner** — document ExecutionPolicy Bypass usage and
  caller restrictions in XML doc comment (SEC-005).
- **Performance: App.xaml** — remove DropShadowEffect from CardElevated style
  to avoid software-rendered shadows (PERF-008).
- **Testing: IntegrationTests** — align dependency versions with Tests project
  (coverlet 10.0.0, Test.Sdk 18.5.1, xunit.runner 3.1.5) (TEST-008).

## [0.50.0] - 2026-05-13

### Fixed
- **Performance: ConsoleViewModel** — fix O(n²) trim by removing from index 0
  forward instead of reverse-order removal (PERF-005).
- **Performance: ProcessManagerViewModel** — move icon extraction and process
  description lookup to background thread to prevent UI freezes (PERF-007).
- **CI: auto-release** — detect breaking change commits (feat!:/fix!:) and
  bump major version instead of treating them as minor/patch (CI-001).
- **CI: ci.yml** — add warning annotation when UI automation tests fail so
  failures are visible on PRs without blocking merge (TEST-005).

## [0.49.0] - 2026-05-13

### Fixed
- **Binding: BatteryInfo** — add NotifyPropertyChangedFor on DesignCapacityMWh,
  FullChargeCapacityMWh, EstimatedRuntimeMinutes for computed properties
  HealthPercent, WearPercent, RuntimeDisplay (BIND-001).
- **Binding: DiskHealthReport** — add NotifyPropertyChangedFor on HealthStatus,
  TemperatureC, WearPercent, PowerOnHours, ReadErrors, WriteErrors for 6+
  computed properties (BIND-002).
- **Binding: FriendlyEventEntry** — add NotifyPropertyChangedFor on Timestamp
  and Severity for RelativeTime, FullTimestamp, SeverityIcon, SeverityColor
  (BIND-003).
- **Binding: PerformanceProfile** — add NotifyPropertyChangedFor on
  ActivePlanName and ActivePlanGuid for ProfileSummary (BIND-004).
- **Binding: ProcessEntry** — add NotifyPropertyChangedFor on MemoryBytes for
  MemoryDisplay (BIND-005).
- **Binding: DiskUsageEntry** — add NotifyPropertyChangedFor on SizeBytes for
  SizeDisplay (BIND-006).
- **Binding: InstalledApp** — add NotifyPropertyChangedFor on SizeBytes for
  SizeDisplay (BIND-007).
- **Memory: DeepCleanupViewModel** — replace anonymous PropertyChanged lambda
  with named handler, unsubscribe on rescan and Dispose (MEM-006).
- **Memory: ShortcutCleanerViewModel** — replace anonymous PropertyChanged
  lambda with named handler, unsubscribe on rescan and Dispose (MEM-007).
- **Bug: MemoryTestService** — set ReverseDirection=true on EventLogQuery so
  the cutoff break works correctly with newest-first ordering (BUG-002).
- **Bug: PerformanceService** — fix CreateRestorePointAsync by embedding
  description directly in script instead of using AddParameter which doesn't
  create script-scope variables (BUG-003).
- **Bug: SpeedTestView/CleanupView/DeepCleanupView/NetworkRepairView/
  SystemHealthView/TracerouteView/AboutView** — replace FlexVis converter
  misuse on IsEnabled with dedicated BoolInverterConverter (BUG-004, BUG-005).
- **Security: ServiceManagerService** — replace weak quote-only validation with
  strict allowlist regex for sc.exe service name arguments (SEC-006).
- **Performance: LogsViewModel** — use CollectionView.Count directly instead of
  iterating entire filtered view via Cast/Count (PERF-002).
- **Performance: NetworkSharedState** — simplify buffer trimming to remove from
  front sequentially (PERF-003).
- **Performance: MarkdownTextBlock** — use static compiled Regex instead of
  creating new state machine on every parse call (PERF-004).
- **Performance: DiskAnalyzerService** — use StringComparison.OrdinalIgnoreCase
  instead of allocating ToLowerInvariant copy on every path (PERF-006).

## [0.48.0] - 2026-05-13

### Fixed
- **Security: UpdateService** — treat missing .sha256 hash file as verification
  failure instead of silently passing (SEC-001).
- **Security: SpeedTestService** — pin expected SHA-256 hashes for Ookla CLI
  download, log warning on mismatch (SEC-002).
- **Security: AppBlockerService** — apply same input validation regex to
  UnblockApp as BlockApp to prevent registry path injection (SEC-004).
- **Memory: AppUpdatesViewModel** — store LineReceived handler in field and
  unsubscribe in Dispose to prevent event subscription leak (MEM-001).
- **Memory: NetworkSharedState** — unsubscribe Pinger.SampleReceived and
  TraceMonitor.RouteCompleted in Dispose, dispose TraceMonitor (MEM-002).
- **Memory: ConsoleView** — unsubscribe from previous DataContext's
  CollectionChanged before subscribing to new one (MEM-003).
- **Memory: PerformanceView** — store PropertyChanged handler and unsubscribe
  from previous VM on DataContext change (MEM-004).
- **Bug: DuplicateFileGroup** — guard WastedBytes with Math.Max to prevent
  negative value when Count is 0 (BUG-001).
- **Performance: ProcessEntry** — cache CanOpenFileLocation on creation instead
  of calling File.Exists on every property evaluation (PERF-001).
- **Bug: WindowsFeaturesViewModel** — add CanExecute guard on ToggleFeature
  command to prevent rapid-click race condition (BUG-006).

## [0.47.1] - 2026-05-13

### Fixed
- **Ten high-priority code-review findings** — a batch of correctness and security fixes from the code review, plus the SECURITY.md supported-versions update to the 0.47.x line.

## [0.47.0] - 2026-05-13

### Changed
- **Migrate to .NET 9** — all projects now target `net9.0-windows`. CI
  workflows updated to use .NET 9 SDK. `Microsoft.Extensions.DependencyInjection`
  bumped to 9.0.4. Closes #257.
- **DI: PowerShellRunner is now Transient** — each ViewModel gets its own
  instance to prevent LineReceived event cross-talk between tabs.

### Fixed
- **Uninstaller** — filter out entries with names shorter than 2 characters
  (eliminates empty rows from winget list parsing edge cases).
- **Process Manager** — explicitly enable column resizing (`CanUserResizeColumns`).
- **Windows Features** — show "Not elevated" warning badge when not running
  as Administrator.
- **SpeedTestService** — suppress SYSLIB0057 obsolete warning for
  `CreateFromSignedFile` (no .NET 9 replacement for Authenticode verification).

## [0.46.0] - 2026-05-13

### Added
- **Windows Features tab** — list, enable, and disable Windows optional
  features (Hyper-V, WSL, .NET 3.5, Telnet, etc.) directly from SysManager.
  Features are categorized (Virtualization, Networking, Development, Media,
  Legacy). Toggle requires admin. Shows reboot-required status. Includes
  search/filter. Closes #5.

## [0.45.0] - 2026-05-13

### Added
- **Dependency Injection container** — introduced
  `Microsoft.Extensions.DependencyInjection` for service and ViewModel
  lifetime management. All services (PowerShellRunner, SystemInfoService,
  WingetService, TrayIconService) are now shared singletons resolved from
  the container. MainWindowViewModel resolves child VMs from DI at runtime,
  falls back to manual creation in tests. Closes #255.

## [0.44.0] - 2026-05-13

### Added
- **Uninstaller — Local app support** — apps not managed by winget (per-user
  installs, legacy software, custom apps) can now be uninstalled directly
  using their registry UninstallString. The service parses quoted paths,
  MsiExec commands, and rundll32 invocations. Prefers QuietUninstallString
  when available. Closes #236.

## [0.43.0] - 2026-05-12

### Added
- **ETA Calculator** — reusable helper that estimates time remaining for
  any progress-based operation. Integrated into Speed Test (HTTP + Ookla)
  and Deep Cleanup (scan + clean). Shows human-friendly estimates like
  "~2 min 15 s" next to progress bars. Closes #241.

## [0.42.0] - 2026-05-12

### Added
- **Drivers — Scrollable view** — wrapped the Drivers tab in a
  ScrollViewer so the full content (toolbar, summary, table) is
  scrollable when the window is small. DataGrid has explicit
  VerticalScrollBarVisibility and MaxHeight for large driver lists.
  Closes #235.

## [0.41.0] - 2026-05-12

### Added
- **Speed Test — History tracking** — each speed test result (HTTP and
  Ookla) is saved to disk and displayed in a history table below the
  test card. Stores up to 20 results per engine with date, download,
  upload, ping, and server. Clear button per engine. Persists between
  sessions. Closes #237.

## [0.40.1] - 2026-05-12

### Fixed
- **Auto-update** — "Install" now performs a true in-place update: verifies
  SHA256 hash of the downloaded build, writes an updater script that waits
  for the current process to exit, copies the new executable over the old
  one, and restarts. Previously it only launched the new exe from a temp
  folder without replacing the original. Closes #240.

## [0.40.0] - 2026-05-12

### Added
- **System Logs — Row highlight** — toggle highlight on any log entry
  for better visibility when reviewing events. Closes #233.
- **Services — Row highlight** — toggle highlight on any service row
  to mark entries of interest while browsing. Closes #239.

## [0.39.0] - 2026-05-12

### Added
- **About — Changelog link** — new "View Changelog" button opens the
  GitHub CHANGELOG.md in the browser. Closes #232.
- **Drivers — Hide system drivers** — toggle to filter out Microsoft /
  Windows drivers from the list, showing only third-party drivers.
  Closes #234.
- **Startup Manager — Hide Windows entries** — toggle to filter out
  Microsoft / Windows startup items that should not be disabled.
  Closes #238.

## [0.38.0] - 2026-05-12

### Added
- **System Tray mode** — minimize-to-tray on window close, background
  health monitoring every 60 seconds, CPU/RAM/uptime tooltip on hover,
  Windows toast notifications when RAM > 90%, uptime > 14 days, or disk
  health degrades. Right-click context menu with Show / Exit. Uses
  H.NotifyIcon.Wpf 2.2.1. Closes #262.

## [0.37.0] - 2026-05-12

### Added
- **Dashboard — Health Score card** — overall system health gauge (0–100)
  combining disk SMART, RAM usage, uptime, and battery wear (on laptops).
  Color-coded circular ring with label (Excellent/Good/Fair/Poor) and up
  to 3 actionable recommendations. Auto-computes on load and refreshes
  with "Scan system". Closes #259.
- **HealthScoreService** — aggregates SystemInfoService, DiskHealthService,
  and BatteryService into a weighted health score.

## [0.36.0] - 2026-05-12

### Added
- **Dashboard — Quick Tune-Up wizard** — one-click button that runs safe
  cleanup (temp files), optionally empties Recycle Bin (with confirmation),
  scans for broken shortcuts (report only), checks disk SMART health,
  flags high uptime (14+ days) and high RAM usage (85%+). Displays a
  dismissible summary card with freed space, disk verdicts, and
  recommendations. Non-destructive, no admin required. Closes #261.
- **IntGreaterThanZeroConverter** — value converter for conditional
  visibility when an integer is greater than zero.
- **IDialogService** — abstraction for user confirmation dialogs, replacing
  direct `MessageBox.Show` calls in ViewModels. Enables unit testing of
  confirmation-gated code paths (CQ-003).

### Fixed
- **Disk Health** — `TemperatureColorHex` returns grey (#9AA0A6) for drives
  without temperature sensors instead of misleading red (QA-004).
- **Battery Health** — `HealthPercent` clamped to 0–100, `WearPercent`
  clamped to ≥0 for new batteries exceeding design capacity (QA-005).
- **Network Monitor** — `TrimBuffer` batch-removes expired points from
  end-to-start, eliminating O(n²) array shifting (CQ-001).
- **Shortcut Cleaner** — COM objects (`IShellLink`, `IPersistFile`) now
  released via `Marshal.ReleaseComObject` in finally block (SEC-006).
- **Models** — deduplicated `FormatSize` from `DiskUsageEntry`, `InstalledApp`,
  and `ProcessEntry`; all now use `CleanupCategory.HumanSize` (CQ-002).
- **Console** — batch-remove excess lines from end-to-start instead of
  repeated `RemoveAt(0)`, reducing O(n) per append to amortized O(1) (CQ-008).

### Security
- **Speed Test** — improved download integrity comment and added
  Authenticode signature verification on extracted speedtest.exe (SEC-001).

## [0.35.12] - 2026-05-12

### Fixed
- **Code-review batch 2** — `IDialogService` extraction plus a set of QA and security fixes from the second code-review pass.

## [0.35.11] - 2026-05-12

### Fixed
- **Process Manager** — null-safe filter: `ApplyFilter` no longer throws
  `NullReferenceException` when `Description`, `PlainDescription`, or
  `Category` are null (QA-002).
- **Network Monitor** — `Buffers`/`TraceBuffers` changed from `Dictionary`
  to `ConcurrentDictionary` to prevent `InvalidOperationException` under
  concurrent timer + UI access (QA-003).
- **Disk Analyzer** — `DrillDown`/`GoUp` now await `AnalyzeAsync()` instead
  of fire-and-forget, preventing race conditions with the operation lock
  (QA-001).
- **Console** — `Dispatcher.Invoke` → `BeginInvoke` to avoid thread-pool
  starvation under heavy output (CQ-005).
- **Integration tests** — `UpdateServiceTests.Constants_AreSet` expects
  `"SystemManager"` matching the renamed repo (TEST-004).

### Security
- **chkdsk** — drive letter validated with `^[A-Z]:$` regex before
  interpolation into process arguments (SEC-003).
- **App Blocker** — `exeName` validated with `^[A-Za-z0-9_\-. ]+\.exe$`
  regex to prevent registry path injection via IFEO (SEC-004).
- **Restore Point** — `CreateRestorePointAsync` uses parameterized
  PowerShell (`$desc` variable) instead of string concatenation (SEC-002).

## [0.35.10] - 2026-05-08

### Fixed
- **Auto-update** — UpdateService now points to the new `SystemManager` repo
  name instead of the old `SysManager`. Without this fix, the in-app update
  checker would fail to find new releases.

## [0.35.9] - 2026-05-08

### Changed
- **Code quality** — refactored implicit `foreach` filters to explicit LINQ
  `.Where()` calls across 7 files (GatewayHelper, FixedDriveService,
  AppAlertService, DeepCleanupService, LargeFileScanner,
  ProcessDescriptionService, ShortcutCleanerViewModel). Resolves CodeQL
  `cs/linq/missed-where` alerts.

## [0.35.8] - 2026-05-08

### Fixed
- **Ping chart** — fixed chart visual collapse that occurred after 2–5 seconds
  of monitoring. Root cause: LiveCharts auto-scaled the X-axis on every buffer
  trim, causing momentary layout thrashing. Fix pins the X-axis to a fixed
  time window (now − windowSeconds → now) during active monitoring, and adds
  MinHeight="200" to prevent layout collapse. Axis limits reset on Stop/Clear
  (#518).

## [0.35.7] - 2026-05-08

### Fixed
- **Encoding** — all native Windows tools (powercfg, ipconfig, netsh, sc.exe)
  now use OEM encoding for output parsing, matching the fix applied to chkdsk,
  sfc, and DISM. Added centralized `PowerShellRunner.OemEncoding` static
  property. Prevents garbled output on non-English Windows systems.

## [0.35.6] - 2026-05-08

### Removed
- **Old green progress panel** — removed the legacy green-bordered background
  task tray from the sidebar footer. Progress is now shown exclusively via the
  blue indeterminate bar under each tab name in the sidebar (#513).

## [0.35.5] - 2026-05-08

### Fixed
- **chkdsk** — register OEM code pages (437, 852, etc.) at application startup
  via `CodePagesEncodingProvider`. On .NET 8, these code pages are not available
  by default, causing chkdsk output parsing to fail with encoding errors on
  non-English systems (#505).

## [0.35.4] - 2026-05-08

### Fixed
- **Traceroute** — reduced per-hop timeout from 3s to 2s and DNS reverse
  lookup timeout from 1.5s to 800ms. Prevents the appearance of freezing
  when intermediate hops don't respond (#519).

## [0.35.3] - 2026-05-08

### Fixed
- **Duplicate Finder** — replaced non-virtualized `ItemsControl` with a
  virtualized `ListView` to prevent UI freezes when displaying thousands of
  duplicate groups (#527).
- **Process Manager** — reduced column widths (PID 55, Mem 70, CPU 50,
  Thr 45) and added `MinWidth="200"` on the Name column to prevent columns
  from crowding on smaller screens (#511).

## [0.35.2] - 2026-05-08

### Fixed
- **Shortcut Cleaner** — tab was showing a black page due to referencing
  undefined `BoolToVisibility` converter. Rewrote the View with correct
  converter names and matching app theme styles (#512).
- **Startup Manager** — blank placeholder row at the bottom of the table
  caused by missing `CanUserAddRows="False"` on the DataGrid (#509).
- **Disk Analyzer** — two confusing "Open" buttons renamed: drill-down is
  now "→" and Show in Explorer is now "📂" with distinct tooltips (#514, #515).

## [0.35.1] - 2026-05-07

### Fixed
- **Deep Cleanup / Duplicate Finder** — use Windows Known Folder API
  (SHGetKnownFolderPath) to resolve Downloads, Documents, Desktop, Pictures,
  Music, and Videos paths. If the user has moved these folders to a different
  drive (e.g. D:\Downloads), the application now detects the actual location
  instead of assuming the default C:\Users path (#483).

## [0.35.0] - 2026-05-07

### Added
- **DataGrid sort arrows** — all sortable DataGrid column headers now display
  an ascending (▲) or descending (▼) arrow indicator on the currently sorted
  column (#488).
- **DataGrid hover highlight** — column headers change background color and
  show a hand cursor on hover to signal interactivity (#489).

## [0.34.2] - 2026-05-07

### Fixed
- **Disk Analyzer** — skip junctions, symbolic links, and mount points during
  folder traversal to prevent double-counting files reachable through multiple
  paths (e.g. `C:\Documents and Settings` → `C:\Users`). Fixes reported total
  exceeding actual disk capacity (#484).

## [0.34.1] - 2026-05-07

### Fixed
- **Sidebar** — all groups now start collapsed on launch instead of expanded,
  reducing visual clutter (#482).
- **Speed Test** — swapped card order: Ookla (primary) now appears first,
  HTTP (backup) second (#485).
- **App Updates** — per-package upgrade now includes `--include-unknown` flag
  so packages with undetermined versions can be upgraded (#486).
- **Uninstaller** — blank entries with empty names are now filtered out of
  the installed applications list (#487).
- **About** — "View license" button no longer appears grayed out; changed
  from GhostButton to SecondaryButton style (#490).

## [0.34.0] - 2026-05-07

### Added
- **App Blocker** — fully implemented tab replacing the WIP placeholder.
  Blocks applications from executing using Image File Execution Options (IFEO)
  registry mechanism. Enter an exe name or browse for a file, confirm, and the
  app is prevented from launching. Fully reversible — unblock restores normal
  execution. Shows list of currently blocked apps with select/deselect.
- `AppBlockerService` — IFEO-based block/unblock with specific exception
  handling, admin privilege detection, and GetBlockedApps enumeration.
- `AppBlockerViewModel` — block, unblock selected, browse, refresh, select all.
- `BlockedApp` model with observable properties.
- `AppBlockerView` XAML with input field, toolbar, and DataGrid.
- Unit tests for ViewModel and Model.

## [0.33.0] - 2026-05-07

### Added
- **App Alerts** — fully implemented tab replacing the WIP placeholder.
  Monitors Program Files, AppData\Programs, and registry uninstall keys for
  new application installations using FileSystemWatcher and periodic registry
  polling. Shows timestamped install history with app name, publisher, path,
  and detection source. Start/stop monitoring, acknowledge alerts, show all
  currently installed apps, clear history.
- `AppAlertService` — FileSystemWatcher on install directories + 30s registry
  poll cycle. Thread-safe with ConcurrentDictionary baseline.
- `AppAlertsViewModel` — full MVVM with start/stop, acknowledge, clear,
  refresh installed apps.
- `AppInstallEntry` model with observable properties.
- `AppAlertsView` XAML with DataGrid and toolbar.
- Unit tests for ViewModel and Model.

## [0.32.0] - 2026-05-06

### Added
- **Shortcut Cleaner** — fully implemented tab replacing the WIP placeholder.
  Scans Desktop, Start Menu, Quick Launch, and Recent Items for broken .lnk
  shortcuts whose targets no longer exist. Lists results with name, location,
  and missing target path. Supports select all/deselect, move to Recycle Bin
  or permanent delete, with confirmation dialog before any deletion.
- `ShortcutCleanerService` — COM-based IShellLink resolution, SHFileOperation
  for Recycle Bin support, scans 6 common shortcut locations.
- `ShortcutCleanerViewModel` — full MVVM implementation with scan, delete,
  select/deselect, cancel, and OperationLockService integration.
- `BrokenShortcut` model with observable properties.
- `ShortcutCleanerView` XAML with DataGrid, toolbar, and status footer.
- Unit tests for ViewModel and Model.

## [0.31.0] - 2026-05-06

### Added
- **Process Description Database** — built-in JSON database with 107 common
  Windows processes and popular applications, each with a plain-language
  description, category (System, Browser, Development, Communication, Media,
  Gaming, Graphics, Productivity, Creative, Cloud, Utility, Network, Security),
  and safety indicator (System, Trusted, Unknown).
- **ProcessDescriptionService** — singleton service that loads the embedded
  JSON database and provides fast case-insensitive lookup by process name.
- **ProcessEntry model** — extended with `PlainDescription`, `Category`, and
  `SafetyLevel` fields populated from the database on each refresh.
- **Enhanced filtering** — Process Manager search now matches against
  plain description and category in addition to name and PID.
- Unit tests for `ProcessDescriptionService` covering lookup, case
  insensitivity, .exe stripping, categories, and safety levels.

## [0.30.0] - 2026-05-06

### Added
- **Operation Lock Service** — new `OperationLockService` singleton that
  prevents conflicting concurrent operations across tabs. Operations are
  grouped by category (Disk, Network, SystemModification). If a user tries
  to start a conflicting operation while another is running, the UI shows
  which operation is blocking and refuses to start the new one.
- Integrated operation locks into: `DeepCleanupViewModel` (scan, clean,
  large file scan), `DiskAnalyzerViewModel` (analyze), `DuplicateFileViewModel`
  (scan), `CleanupViewModel` (temp cleanup), `SpeedTestViewModel` (HTTP and
  Ookla tests), `TracerouteViewModel` (trace), `NetworkRepairViewModel`
  (all repair operations).
- Unit tests for `OperationLockService` covering acquire, release, conflict
  detection, thread safety, and double-dispose safety.

## [0.29.1] - 2026-05-06

### Fixed
- **Code quality** — replaced 8 generic `catch (Exception)` blocks with
  specific exception types in `AppUpdatesViewModel`, `DashboardViewModel`,
  and `LogsViewModel`. No behavior change — same error messages, but now
  CodeQL-clean and explicit about what can fail.

## [0.29.0] - 2026-05-06

### Added
- **Sidebar restructure** — reorganized navigation from 7 groups / 21 tabs to
  9 groups / 36 tabs. New groups: **Monitor** (Process Manager moved here,
  plus Resource History, App Alerts, Privacy Monitor placeholders) and
  **Control** (Privacy Settings, Context Menu, Restore Points, Scheduled
  Maintenance, System Report placeholders). Existing groups expanded: System
  (+Windows Features), Cleanup (+Shortcut Cleaner, File Shredder), Network
  (+DNS Changer, Hosts Editor), Apps (+Bulk Installer, App Blocker).
- **PlaceholderView** — generic WIP view showing feature name, description,
  issue reference, and "Work in Progress" badge for planned tabs.
- **PlaceholderViewModel** — lightweight ViewModel for placeholder tabs,
  stores feature name, description, and issue number.

## [0.28.34] - 2026-05-06

### Removed
- **Dead code** — removed legacy `NetworkViewModel.cs` (superseded by split
  ViewModels: PingViewModel, TracerouteViewModel, SpeedTestViewModel,
  NetworkRepairViewModel + NetworkSharedState). Removed associated integration
  tests that exercised the dead class.

## [0.28.33] - 2026-05-06

### Fixed
- **Code quality** — resolved `cs/missed-using-statement` CodeQL alert in
  `ProcessManagerService`: wrapped `Process.GetProcesses()` array in
  try/finally to guarantee disposal of all process handles, even on early
  cancellation.

## [0.28.32] - 2026-05-06

### Fixed
- **Code quality** — resolved final 5 CodeQL alerts: replaced `foreach`+
  `ContainsKey` guard with `TryAdd` in `UninstallerService`, converted
  `foreach`+immediate-map to LINQ `.Select()` in `NetworkSharedState` and
  `IconExtractorService` (×2), added logging to previously empty catch block
  in `StartupService`.

## [0.28.31] - 2026-05-06

### Fixed
- **Code quality** — resolved 2 additional CodeQL alerts: converted
  `foreach`+immediate-map to `.Select()` in `DriversViewModel`, converted
  `foreach`+type-check to `.Where()` in `StartupService.ReadApprovedKey`.

## [0.28.30] - 2026-05-06

### Fixed
- **Code quality** — resolved 40 CodeQL alerts across 16 files: replaced
  `Path.Combine` with `Path.Join` to prevent silent argument dropping (18),
  converted `foreach`+`if continue` to LINQ `.Where()` (17), replaced
  `foreach`+immediate-map to `.Select()` (3), added comments to intentional
  empty catch blocks (2).

## [0.28.29] - 2026-05-05

### Fixed
- **Logs / Console** — replaced generic `catch (Exception)` with specific
  exception types in `LogsViewModel` and `ConsoleViewModel` (resolves
  CodeQL catch-of-all-exceptions alerts).

## [0.28.28] - 2026-05-05

### Fixed
- **Cleanup** — SFC and DISM scans no longer crash with "No data is available
  for encoding 437" on systems where the OEM code page is not registered;
  falls back to UTF-8 (same fix as #443 applied to remaining callers).
- **App Updates** — winget upgrade now accepts package IDs with spaces (same
  fix as #444 applied to WingetService).
- **Code quality** — replaced bare `catch { }` with specific exception types
  in DiskHealthService, FixedDriveService, MemoryTestService, SystemInfoService,
  and AdminHelper (resolves multiple CodeQL alerts).
- **SECURITY.md** — updated supported version table from 0.5.x to 0.28.x.
- **ARCHITECTURE.md** — removed stale tab counts from group headers.

## [0.28.27] - 2026-05-05

### Fixed
- **System Health** — chkdsk scan no longer crashes with "No data is available
  for encoding 437" on systems where the OEM code page is not registered;
  falls back to UTF-8 gracefully (#443).
- **Uninstaller** — packages with spaces in their winget ID (e.g. "Riot
  Games.League of Legends") can now be uninstalled without "Invalid package
  ID" error (#444).

## [0.28.26] - 2026-05-04

### Fixed
- **CodeQL regressions** — resolved 2 alerts introduced during the bug fix
  session: converted `foreach`+`if` to LINQ `Where()` in
  `DeepCleanupService.RiotLogDirs` (missed-where), wrapped `JsonDocument` in
  `using` block in `SpeedTestService.RunOoklaAsync` (missed-using).

## [0.28.25] - 2026-05-04

### Fixed
- **Accessibility: LogsView** — replaced remaining search emoji (🔍) in the
  no-results overlay with Segoe MDL2 Assets glyph (E721). Missed in the
  initial accessibility pass (#411).

## [0.28.24] - 2026-05-04

### Fixed
- **Accessibility** — replaced emoji characters (📁🔍✕📂📋🗑⟳↺⬆) with text
  equivalents across all 21 XAML views; added `AutomationProperties.Name` to
  all DataGrid and ProgressBar elements for screen reader support (#411).

## [0.28.23] - 2026-05-04

### Fixed
- **Services: timeout handling** — `WaitForStatus` in `ServiceManagerService`
  now catches `TimeoutException` and converts to a descriptive error instead
  of crashing when a service takes longer than 30 seconds (#414).
- **Performance: snapshot persistence** — `OriginalSnapshot` is now saved to
  JSON in `%LOCALAPPDATA%\SysManager` and loaded on startup, so Restore All
  works after app restart (#415).
- **Traceroute: DNS race condition** — reverse DNS lookup is now awaited with
  a 1.5 s timeout before emitting the hop, so hostnames appear immediately
  in the UI instead of showing `*` (#416).

## [0.28.22] - 2026-05-04

### Fixed
- **Update download: SHA256 verification** — added `VerifyHashAsync` to
  `UpdateService` that downloads the `.sha256` file from the GitHub release
  and compares against the local file hash (#408).
- **Speed Test: Ookla integrity check** — Ookla CLI download now computes
  SHA256 (logged for audit), validates the zip is not corrupt, and verifies
  it contains `speedtest.exe` before extraction (#409).

## [0.28.21] - 2026-05-04

### Fixed
- **Performance: audit logging** — all registry modifications in
  `PerformanceService` (Game Mode, Xbox Game Bar, GPU, visual effects) now
  log key path, action, and new value via Serilog (#405).
- **Error messages: operation context** — replaced 38+ generic `Error: …`
  messages in `PerformanceViewModel`, `ServicesViewModel`, and
  `SystemHealthViewModel` with operation-specific context like
  "Power plan change failed:" and "Start service failed:" (#407).

## [0.28.20] - 2026-05-04

### Fixed
- **Deep Cleanup: drive scanning** — Riot Games / League of Legends log
  paths now scan all fixed drives instead of only Program Files (#401).
- **Icon cache: eviction** — `IconExtractorService` cache now has a
  configurable `MaxCacheSize` (default 500) with automatic eviction to
  prevent unbounded memory growth (#402).
- **ConfigureAwait(false)** — added to all async calls in
  `PerformanceService`, `UninstallerService`, and `WingetService` to
  prevent potential UI deadlocks (#403).

## [0.28.19] - 2026-05-04

### Fixed
- **Speed Test: JSON error handling** — `SpeedTestService.RunOoklaAsync`
  now catches `JsonException` and `KeyNotFoundException` when Ookla CLI
  returns malformed output (#400).

## [0.28.18] - 2026-05-04

### Fixed
- **Input validation: allowlist regex** — `UninstallerService` and
  `WingetService` now validate package IDs with an allowlist regex
  (`[a-zA-Z0-9._-/+]`, max 256 chars) instead of a blocklist (#397).
- **Null checks: verified safe** — confirmed all `OpenSubKey` calls and
  Process API access already have proper null checks (#398).

## [0.28.17] - 2026-05-04

### Fixed
- **CTS disposal** — added `Dispose(bool)` override to 8 ViewModels that
  had `CancellationTokenSource` fields but no cleanup: `AppUpdatesVM`,
  `DiskAnalyzerVM`, `DriversVM`, `DuplicateFileVM`, `LogsVM`,
  `SpeedTestVM`, `TracerouteVM`, `UninstallerVM` (#396).
- **UpdateService: bare catch** — replaced bare `catch` blocks in
  `GetRecentAsync` and `DownloadAsync` with specific exception types
  (`HttpRequestException`, `JsonException`, `IOException`) plus Serilog
  logging (#413).

## [0.28.16] - 2026-05-04

### Fixed
- **Dispose lifecycle** — `MainWindow.OnClosed` now disposes
  `MainWindowViewModel`, which chains to all child ViewModels and
  `NetworkSharedState`. `NetworkViewModel` disposes its CTS, unsubscribes
  events, and stops the pinger (#395, #410).

## [0.28.15] - 2026-04-30

### Fixed
- **CodeQL: empty-catch-block** — added Serilog logging or descriptive comments
  to ~50 empty catch blocks across 10 files: `IconExtractorService`,
  `DiskAnalyzerService`, `DuplicateFileService`, `ProcessManagerService`,
  `SpeedTestService`, `StartupService`, `UninstallerService`,
  `CleanupViewModel`, `DiskAnalyzerViewModel`, `DuplicateFileViewModel`.
- **CodeQL: catch-of-all-exceptions** — replaced bare `catch { }` in
  `DiskAnalyzerService` (7 blocks) with specific `UnauthorizedAccessException`
  and `IOException`; replaced `catch (Exception)` in `DiskAnalyzerViewModel`
  and `DuplicateFileViewModel` with specific types.
- **CodeQL: missed-where** — converted `ShouldSkip`/`ShouldSkipDir`/
  `ShouldSkipFile` foreach loops to LINQ `Any()` in `DiskAnalyzerService`
  and `DuplicateFileService`.

## [0.28.14] - 2026-04-30

### Fixed
- **CodeQL: missed-using-statement** — `ServiceController` objects in
  `ServiceManagerService.GetAllServices()` and `Process` objects in
  `PerformanceService.TrimWorkingSets()` now use `using` blocks instead of
  manual `try/finally Dispose()`.

## [0.28.13] - 2026-04-30

### Fixed
- **CodeQL: DuplicateFileService catch blocks** — bare `catch { }` in file
  discovery, partial hash, and full hash loops replaced with specific
  `IOException` + `UnauthorizedAccessException`.
- **CodeQL: App.xaml.cs using statement** — `Process` objects in single-instance
  activation now use `using` block instead of manual try/finally dispose.
- **CodeQL: App.xaml.cs static field** — `_instanceMutex` changed from static
  to instance field (only one App instance exists per process).
- **CodeQL: StartupService unused variables** — removed unused `actions`
  variable; stdout drain changed to discard pattern.

## [0.28.12] - 2026-04-30

### Fixed
- **CodeQL: catch-of-all-exceptions** — replaced all `catch (Exception)` and
  bare `catch { }` with specific exception types across 12 files: AboutVM,
  BatteryHealthVM, CleanupVM, DeepCleanupVM, NetworkVM, PerformanceVM,
  ProcessManagerVM, ServicesVM, StartupVM, SystemHealthVM, WindowsUpdateVM,
  ProcessManagerService. Exception types include `InvalidOperationException`,
  `IOException`, `HttpRequestException`, `ManagementException`,
  `Win32Exception`, `TaskCanceledException`, and others.
- **CodeQL: empty catch blocks** — added Serilog logging to previously silent
  catch blocks so failures are traceable in diagnostics.

## [0.28.11] - 2026-04-30

### Fixed
- **ViewModel lifecycle: IDisposable** — `ViewModelBase` now implements
  `IDisposable` with virtual `Dispose(bool)` pattern. All ViewModels with
  event subscriptions or CancellationTokenSources override Dispose to clean up.
- **Event handler leaks** — lambda event handlers in CleanupVM, SystemHealthVM,
  and WindowsUpdateVM replaced with named methods and unsubscribed in Dispose.
- **Fire-and-forget error handling** — 11 ViewModels with `_ = InitAsync()`
  wrapped in try/catch with `Log.Warning` to prevent unobserved task exceptions.
- **CTS disposal in Dispose** — CleanupVM (4×), DeepCleanupVM (3×),
  SystemHealthVM, WindowsUpdateVM now dispose CancellationTokenSources on
  ViewModel teardown.

## [0.28.10] - 2026-04-30

### Fixed
- **Critical: deadlock in StartupService** — `Process.WaitForExit()` called
  before reading stderr/stdout caused pipe buffer deadlock on schtasks.exe.
  Now reads streams asynchronously before waiting.
- **Critical: COM object leak in StartupService** — `WScript.Shell` and
  shortcut COM objects were not released, leaking COM references. Added
  `Marshal.ReleaseComObject` in finally block.
- **Critical: 50 MB allocation in SpeedTestService** — upload test allocated
  a single 50 MB byte array on the Large Object Heap. Replaced with streaming
  `RandomChunkStream` using 256 KB chunks.
- **Input validation** — schtasks, sc.exe, and winget arguments now validated
  against injection characters (`"`, `\0`) in StartupService,
  ServiceManagerService, UninstallerService, and WingetService.
- **Bare catch blocks** — 7 bare catches in StartupService, SpeedTestService,
  ServiceManagerService, UninstallerService, and WingetService replaced with
  specific exception types and Serilog logging.

## [0.28.9] - 2026-04-30

### Fixed
- **Cleanup: CancellationTokenSource disposal** — `_tempCts`, `_binCts`,
  `_sfcCts`, and `_dismCts` were not disposed before recreation, leaking
  handles on repeated Clean TEMP / Empty Recycle Bin / SFC / DISM operations.
  Now follows the same `_cts?.Dispose()` pattern applied in other ViewModels
  during the #161 memory leak fix.

## [0.28.8] - 2026-04-29

### Fixed
- **Process Manager: Open file location disabled for system processes** — button
  was active but non-functional for processes without an accessible file path.
  Now disabled with a tooltip when the path doesn't exist (#100).

### Added
- **Process Manager: Show only apps toggle** — checkbox in the toolbar filters
  out system processes and shows only applications with a visible window,
  reducing the list from 200+ entries to just user-facing apps (#100).

## [0.28.7] - 2026-04-29

### Fixed
- **Memory leak: CancellationTokenSource disposal** — previous CTS instances
  were not disposed before creating new ones across 8 ViewModels (15 locations),
  causing WaitHandle accumulation during extended use. Affected: Windows Update,
  Uninstaller, System Health, Drivers, App Updates, Logs, Duplicate Finder,
  Disk Analyzer (#161).
- **Memory leak: Process object disposal** — `Process.GetProcessesByName()` and
  `GetCurrentProcess()` results in `App.ActivateExistingInstance` were not
  disposed, leaking OS handles (#161).
- **Memory leak: PropertyChanged event handlers** — anonymous lambdas subscribed
  to `target.PropertyChanged` in the Network tab were never unsubscribed when
  targets were removed, preventing garbage collection of removed targets (#161).

## [0.28.6] - 2026-04-29

### Fixed
- **Startup Manager: crash when scrolling** — WPF DataGrid virtualization
  passed internal placeholder objects to command handlers, crashing the app.
  Commands now accept `object?` with pattern matching (#326).
- **About: What's New raw markdown** — release notes were displayed as plain
  text. Added a lightweight markdown-to-Inlines renderer that formats headings,
  bold, bullets, and inline code (#335).
- **System Health: chkdsk false errors** — verdict relied solely on exit code,
  which is non-zero even on healthy volumes. Now parses chkdsk output text for
  known healthy/error patterns (#323).
- **Quick Cleanup: Rescan not updating** — property changes fired from a
  background thread inside Task.Run. Refactored to set ObservableProperties on
  the UI thread after await (#327).
- **Deep Cleanup: sidebar progress missing** — IsBusy was never set. Added
  forwarding from IsScanning/IsCleaning/IsLargeScanning to IsBusy (#328).
- **Disk Analyzer: duplicate progress indicator** — removed the redundant
  background task tray entry; the NavItem slim bar is sufficient (#329).
- **Ping: unreachable targets** — replaced 5 unreachable CS2 Europe IPs and
  removed 3 unreachable FACEIT IPs. All new IPs verified with ICMP ping
  (#330, #331, #332).
- **Traceroute: chart not rendering** — LiveChartsCore CartesianChart collapsed
  to zero height. Added MinHeight=250 (#333).
- **Speed Test: HTTP values too low** — increased parallel streams from 4 to 8
  and payload from 25 MB to 50 MB to saturate 1 Gbps+ links (#334).

## [0.28.1] - 2026-04-29

### Fixed
- **Startup Manager no longer crashes when scrolling the list** — fixed a DataGrid virtualization crash while scrolling the Startup Manager entries (#337).

## [0.28.0] - 2026-04-28

### Changed
- **Windows Update: structured DataGrid** — the Windows Update tab now displays
  updates in a sortable DataGrid table (Title, KB, Size, Status, Date, Category)
  instead of raw console text. Console output is hidden behind a collapsible
  panel, shown only during Install/Pending Reboot operations (#305, #240).

## [0.27.0] - 2026-04-28

### Changed
- **Drivers: structured DataGrid** — the Drivers tab now displays installed
  drivers in a sortable DataGrid table (Device Name, Manufacturer, Version,
  Date) instead of raw console text. Click column headers to sort (#304).

## [0.26.0] - 2026-04-28

### Added
- **Sidebar busy indicator** — every tab now shows a slim indeterminate progress
  bar under its name in the sidebar when performing a long-running operation.
  Works automatically for all tabs via ViewModelBase.IsBusy (#263).

## [0.25.0] - 2026-04-28

### Added
- **Ping: more targets per region** — CS2 Europe expanded from 4 to 10 targets
  (2 IPs per region + Frankfurt, Spain subnets). FACEIT Europe expanded from 5
  to 8 targets (3× Germany, 2× Netherlands, Sweden, UK, France). A single
  server going down no longer shows the entire region as failed (#285, #259).

## [0.24.0] - 2026-04-28

### Changed
- **Clickable column headers** — all table tabs now use DataGrid with native
  click-to-sort column headers (ascending/descending toggle), replacing
  standalone sort buttons and dropdowns. Consistent with Windows Task Manager
  behavior.
  - **Process Manager**: sortable PID, Name, Memory, CPU%, Threads, Status (#266)
  - **Uninstaller**: sortable Name, Size, Version, Publisher, Source, Status (#254)
  - **Services**: removed redundant Sort ComboBox, column headers handle sorting
  - **Startup Manager**: sortable Name, Publisher, Status (previously had no sort)
  - **App Updates**: sortable Name, Id, Current, Available, Source, Status
    (previously had no sort)

## [0.23.0] - 2026-04-28

### Changed
- **Sidebar readability** — improved font contrast and size for group headers,
  subtitles, and child count badges. TextMuted → TextSecondary, larger font
  sizes, higher opacity (#265).

## [0.22.0] - 2026-04-28

### Changed
- **Removed MemTest86 external reference** — the MemTest86 button, command, and all
  references have been removed from System Health. SysManager no longer references
  external third-party tools. The built-in Windows Memory Diagnostic remains (#271).

## [0.21.9] - 2026-04-27

### Fixed
- **SFC/DISM elevation consent** — SFC and DISM no longer auto-relaunch the
  application with admin privileges. A Yes/No confirmation dialog is now shown
  before any elevation. If the user declines, the operation is cancelled with a
  clear status message (#264).

## [0.21.8] - 2026-04-27

### Fixed
- **chkdsk admin check** — chkdsk /scan now checks for admin privileges before
  running. Without elevation, drives show "Needs admin" status with a clear
  message instead of failing with cryptic exit codes (#270).

## [0.21.7] - 2026-04-27

### Fixed
- **UI freeze on Cleanup scan** — separated PropertyChanged event wiring from
  collection population to reduce per-item UI re-renders (#261).
- **UI freeze on Speed Test** — offloaded synchronous file-system I/O and
  process creation in the Ookla speed test to the thread pool (#258).
- **UI freeze on Drivers** — offloaded Process.Start() and PowerShell runspace
  initialization to the thread pool so the dispatcher is never blocked (#249).

## [0.21.6] - 2026-04-27

### Fixed
- **Speed Test panels independent** — each panel (HTTP / Ookla) now shows its own
  status text, progress bar, and cancel button only while that specific test runs.
  Previously starting one test would display status on both panels (#257).
- **Traceroute auto-trace** — Start Auto-Trace now adds the current host to the
  monitor and runs an initial trace immediately. Previously the monitor had no
  targets when started from the Traceroute tab (#239).

## [0.21.5] - 2026-04-27

### Fixed
- **Startup Manager disable** — entries from the shell Startup folder can now be
  properly disabled. Previously they were incorrectly routed to
  `StartupApproved\Run` instead of `StartupApproved\StartupFolder`, so Windows
  never saw the change (#268).

## [0.21.4] - 2026-04-27

### Fixed
- **Tab name consistency** — all sidebar labels now match their tab headers exactly.
  Adopted descriptive naming throughout: Process Manager, Startup Manager, System
  Logs, Performance Mode, Battery Health, Network Repair, Duplicate Finder, Quick
  Cleanup, Deep Cleanup (#267).
- **System Logs hover highlight** — log entry rows now show a subtle background
  change on mouse hover, consistent with other tabs (#247).

## [0.21.3] - 2026-04-27

### Fixed
- **Buttons grayed out on focus loss** — intercepted `WM_NCACTIVATE` to keep the
  window chrome rendering as active at all times. ModernWPF was dimming controls
  when the window lost focus, making buttons appear disabled across the entire
  application (#252, #251, #248, #245).

## [0.21.2] - 2026-04-26

### Fixed
- **Startup toggle not working** — clicking the checkbox to disable a startup app
  (e.g. MEGAsync) appeared to do nothing. Root cause: WPF CheckBox two-way binding
  flipped `IsEnabled` before the command ran, then the command inverted it back.
  Now uses the already-flipped value as the desired state and reverts on failure.

## [0.21.1] - 2026-04-26

### Fixed
- **Icon extraction quality** — drastically improved icon resolution for all three
  tabs (Startup, Uninstaller, Process Manager):
  - Contextual fallback icons: Windows shield for system processes, gear for services,
    generic app icon for unknown apps (no more blank squares)
  - Deeper path resolution: handles rundll32 (extracts DLL target), msiexec, searches
    PATH, Program Files, and App Paths registry
  - Process Manager: finds exe by process name when FilePath is empty (access denied)
  - Uninstaller: scans HKCU registry for per-user installs (Discord, VS Code, Spotify)
    and searches InstallLocation for exe when DisplayIcon is missing

## [0.21.0] - 2026-04-25

### Added
- **Application icons** — Startup Manager, Uninstaller, and Process Manager now
  show the real application icon (extracted from the exe) next to each app name.
  Uses Shell32 `SHGetFileInfo` with a concurrent cache for performance. Falls back
  to a generic icon when the exe is missing, inaccessible, or a UWP/system process
  (#229).

## [0.20.0] - 2026-04-25

### Added
- **FACEIT Europe ping preset** — 5 EU server locations (Germany, UK, France,
  Netherlands, Sweden) for checking latency to FACEIT CS2 competitive servers.
  Appears in the preset dropdown between CS2 Europe and PUBG Europe (#228).

## [0.19.0] - 2026-04-25

### Added
- **Network split** — the monolithic `NetworkViewModel` (~700 lines) is now split
  into 4 focused ViewModels with separate Views:
  - `PingViewModel` + `PingView` — live ping, targets, presets, latency chart,
    health verdict
  - `TracerouteViewModel` + `TracerouteView` — auto-traceroute + manual trace
    with dedicated Start/Stop buttons (previously only available on Ping)
  - `SpeedTestViewModel` + `SpeedTestView` — HTTP + Ookla speed tests
  - `NetworkRepairViewModel` + `NetworkRepairView` — DNS flush, Winsock reset,
    TCP/IP reset
- **NetworkSharedState** — shared state class for targets, buffers, pinger,
  tracer, and health diagnostic, consumed by all 4 network ViewModels.
- **Sidebar visual hints** on collapsed groups:
  - Child count badge next to label (e.g. "System (6)")
  - Subtitle with abbreviated child labels (auto-hides when expanded)
  - Tooltip with full child labels on hover
- 30+ new unit tests for NetworkSharedState, PingViewModel,
  TracerouteViewModel, SpeedTestViewModel, NetworkRepairViewModel, NavGroup.

### Changed
- **Windows Update** moved from Apps → System group (System now has 6 children).
- **Apps group** reduced to 2 children (App updates + Uninstaller).
- **Network group** expanded from 1 to 4 sidebar children (no longer a
  single-item flat entry).
- Sidebar now shows 21 leaf items across 7 groups (was 18).

## [0.18.0] - 2026-04-25

### Added
- **Sidebar tab reorganization** — the 18 flat sidebar tabs are now grouped into
  7 collapsible categories: Dashboard, System, Cleanup, Storage, Network, Apps,
  and Info. Groups expand/collapse with a click. Single-item groups (Dashboard,
  Network) render as flat top-level entries without expander chrome (#82).
- **NavGroup model** — new `NavGroup` class for collapsible sidebar categories
  containing child `NavItem` entries.

### Changed
- **Large File Finder** — conceptually moved from the Deep Cleanup group to the
  Storage group, alongside Disk Analyzer and Duplicates. This resolves the
  confusion about where to find storage analysis tools (#98).
- **Cleanup tab** renamed to "Quick cleanup" in the sidebar to distinguish it
  from the Cleanup group header.
- **Sidebar rendering** — replaced the flat `ListBox` with a grouped
  `ItemsControl` + `Expander` tree layout. Active-mark accent bar and hover
  states preserved.
- **UI test infrastructure** — `AppFixture.GoToTab` updated to find nav items
  by `AutomationProperties.AutomationId` anywhere in the visual tree instead
  of requiring a `NavList` ListBox.

## [0.17.0] - 2026-04-25

### Added
- **Application logging** — structured Serilog logging across all 16 ViewModels.
  Logs now capture tab navigation, operation completion (cleanup, scan, upgrade,
  speed test, disk analysis, etc.), system state changes (power plan, Game Mode,
  services, startup entries), admin elevation events, and error context. Privacy-safe:
  no PII, IPs, file paths, or hostnames are logged — only operation names, counts,
  and metrics (#95).
- **LogService.SanitizePath** — helper method that strips Windows usernames from
  file paths as a safety net for any future path logging.

## [0.16.1] - 2026-04-25

### Fixed
- **Network / Ping** — latency chart no longer freezes when switching away from the
  Ping sub-tab and returning; LiveCharts2 series are nudged on tab re-entry (#153).
- **Network / Navigation** — switching between Network and Services tabs during
  concurrent background scans no longer throws a cross-thread exception; collection
  updates are now dispatched to the UI thread (#154).
- **Network / Speed test** — HTTP download test now uses 4 parallel connections to
  saturate the link, producing results closer to Ookla/fast.com benchmarks (#152).

## [0.16.0] - 2026-04-25

### Added
- **Logs tab** — relative timestamps ("2h ago", "3d ago") in the event list with
  full timestamp on hover; quick time-range pill buttons (1h / 24h / 7d / 30d / All)
  replacing the dropdown; search placeholder watermark; no-results empty state with
  helpful message when filters match nothing (#83).
- **System Health** — disk health cards now show a computed health percentage
  (0–100%) with colored gauge bar, temperature gauge with color thresholds,
  life-remaining gauge (inverted wear), and friendly power-on time formatting
  (days/years instead of raw hours) (#143).

## [0.15.1] - 2026-04-25

### Fixed
- **Uninstaller** — empty status badges no longer render for apps without a
  status; FlexVis converter now treats empty/whitespace strings as Collapsed (#130).
- **Uninstaller** — ARP-only apps show yellow "Local" tag with tooltip; status
  badge column widened for less truncation (#131).

### Changed
- **Uninstaller / Process Manager** — "Filter:" label renamed to "Search:" with
  placeholder hint text (#130).

## [0.15.0] - 2026-04-25

### Added
- **Sidebar** — SFC /scannow, DISM RestoreHealth, and chkdsk now show progress
  indicators in the left sidebar mini-tray alongside existing background task
  indicators (#146, #149, #156).

## [0.14.0] - 2026-04-25

### Added
- **Cleanup** — SFC /scannow and DISM /RestoreHealth now parse output into
  color-coded verdicts: green (healthy), yellow (repaired), red (failed) (#148).
- **Uninstaller** — application size displayed from registry EstimatedSize;
  sort by Name, Size, or Publisher (#139).
- **Process Manager** — CPU usage percentage measured and displayed; sort by
  CPU added alongside Memory, Name, PID (#78).
- **About** — "Copy environment info" now includes CPU, RAM, GPU, storage,
  and display diagnostics similar to DxDiag (#84).

### Changed
- **Sidebar** — fixed duplicate icons: Processes and Uninstaller now have
  unique Segoe Fluent Icons (#138).

## [0.13.14] - 2026-04-25

### Fixed
- **SFC / DISM / chkdsk** — live output no longer appears corrupted. Added
  optional encoding parameter to `PowerShellRunner.RunProcessAsync`; system
  tools now use the OEM code page instead of UTF-8 (#147, #150, #157).

## [0.13.13] - 2026-04-25

### Fixed
- **Network** — speed test loading indicator now only appears on the panel that
  is actually running (HTTP or Ookla), not both simultaneously (#151).

## [0.13.12] - 2026-04-25

### Fixed
- **Network** — tab content now follows the dark theme. Set transparent
  background on CartesianChart controls and added global TabControl style to
  prevent light-mode bleed-through (#140).

## [0.13.11] - 2026-04-25

### Fixed
- **Drivers** — added sorting options (Name, Manufacturer, Version, Date) via
  ComboBox in the toolbar. Modernized view layout with Card borders and
  consistent typography. Replaced generic catch with specific exceptions (#155).

## [0.13.10] - 2026-04-25

### Fixed
- **DataGrid styling** — added global dark-friendly styles for DataGrid, column
  headers, rows, and cells. Rows now use transparent default with Surface1
  alternating, Surface2 hover, Surface3 selected. Text stays readable in all
  states (#136).
- **Deep Cleanup** — clicking the "Show" button in the large files DataGrid no
  longer highlights the entire cell. Custom DataGridCell template removes the
  default focus/selection highlight (#158).

## [0.13.9] - 2026-04-25

### Fixed
- **Buttons** — buttons across the application no longer become invisible when
  hovered, focused, or navigated via keyboard. Added explicit Foreground binding
  on ContentPresenter and keyboard focus trigger with accent border (#145).
- **About tab** — "View license" button text no longer clips or disappears on
  hover/focus (#162).

## [0.13.8] - 2026-04-25

### Fixed
- **Startup Manager** — toggle now works for Task Scheduler entries via
  `schtasks.exe /Change`. Previously threw `NotSupportedException` silently
  (#160).
- **Startup Manager** — replaced generic "Error — may need admin" message with
  specific error descriptions (`SecurityException`, `UnauthorizedAccessException`,
  `IOException`). Error messages now describe the actual failure (#159).
- **Tests** — fixed flaky `PreScan_EventuallyPopulatesLabels` test by replacing
  fixed 3s delay with polling loop (up to 15s).

## [0.13.7] - 2026-04-25

### Fixed
- **Uninstaller** — error messages are no longer truncated. Added ToolTip on
  status badge for full text on hover, TextTrimming for graceful truncation, and
  widened status column from 90px to 160px (#163).

## [0.13.6] - 2026-04-25

### Fixed
- **Release workflow** — fixed `Rename-Item` in release.yml that was passing a
  full path instead of just the new filename, causing v0.13.3–v0.13.5 releases
  to fail.

## [0.13.5] - 2026-04-25

### Fixed
- **App Updates** — checkbox column alignment corrected; increased width and
  centered the checkbox to prevent clipping on the right side.

## [0.13.4] - 2026-04-25

### Fixed
- **Services tab** — sorting buttons now actually sort the service list. Added
  SortBy property with options (Name, Status, Startup, Recommendation) and a
  sort ComboBox in the toolbar.
- **Cleanup tab** — added auto-rescan after cleaning temp files or emptying the
  Recycle Bin so size labels refresh immediately. Added an explicit Rescan button.

## [0.13.3] - 2026-04-25

### Fixed
- **About tab** — "Copy environment info" now shows a friendly Windows name
  (e.g. "Microsoft Windows 11 Pro (build 26200)") instead of the raw NT version
  string. Uses WMI `Win32_OperatingSystem.Caption` with fallback.

## [0.13.2] - 2026-04-25

### Fixed
- **Single instance** — the application now prevents multiple instances from
  running simultaneously. A named Mutex detects an existing instance; the second
  launch activates the existing window and exits.

### Changed
- **Release assets** — executables are now named `SysManager-vX.Y.Z.exe` instead
  of `SysManager.exe` to avoid filename conflicts when downloading multiple
  releases.

## [0.13.1] - 2026-04-24

### Fixed
- **Services tab** — Rec. column now shows empty for services without a gaming
  recommendation instead of cluttering all 280+ rows with "keep-enabled".

## [0.13.0] - 2026-04-24

### Added
- **Network Repair Tools** — DNS flush, Winsock reset, TCP/IP reset in a new
  Repair sub-tab on the Network tab. Confirmation dialogs and admin checks.
- **Restore Point Creation** — create a Windows System Restore point from the
  Performance tab (requires admin).
- **RAM Working Set Trim** — free physical RAM by trimming all process working
  sets, same as RAMMap's "Empty Working Set" (Performance tab).
- **Hibernation Toggle** — enable/disable hibernation from the Performance tab.
  Disabling deletes hiberfil.sys and frees disk space.
- **Services Management** — new Services tab listing all Windows services with
  gaming recommendations (safe-to-disable / advanced / keep-enabled), filtering,
  and start/stop/disable/enable controls.

## [0.12.5] - 2026-04-24

### Fixed
- **Duplicate File Scanner** — dramatically faster duplicate detection using
  a two-phase hashing approach. Files sharing a size are now pre-filtered by
  a partial hash (first 4 KB) before computing the full SHA-256. Files that
  differ in the first 4 KB are skipped entirely, avoiding gigabytes of
  unnecessary I/O. (Closes #80)

## [0.12.4] - 2026-04-24

### Fixed
- **Performance Mode** — processor state controls are now disabled when the
  active power plan is High Performance or Ultimate Performance (Windows
  forces min state to 100 %). A warning message explains the lock and how
  to unlock by switching to Balanced. (Closes #103)
- **Process Manager** — replaced the plain text status badge with a colored
  dot + text indicator. Green for Running, red for Not responding. New
  `ProcessStatusToBrushConverter`. (Closes #88)
- **Sidebar progress** — added progress indicators in the left navigation
  for Disk Analyzer and Duplicate File scans, matching the existing Deep
  Cleanup mini-tray pattern. Click to navigate to the tab. (Closes #81, #91)

## [0.12.3] - 2026-04-24

### Fixed
- **Cleanup tab** — added explanatory text describing what each operation
  does (Clean TEMP, SFC /scannow, DISM /RestoreHealth) so users understand
  the tools before running them. (Closes #92)
- **System Health** — chkdsk status line now stays visible after the scan
  finishes instead of disappearing. Shows green while running, muted gray
  when done, so the user can see the result. (Closes #94)

## [0.12.2] - 2026-04-24

### Fixed
- **Version display** — updated `.csproj` from `0.5.1` to `0.12.1` so the
  app reports the correct version in the sidebar and About tab. Fixed
  `auto-release.yml` + `release.yml` + `publish.ps1` to inject version at
  build time via `/p:Version=`, so released binaries always match the tag.
  (Closes #90)
- **False update prompt** — the app no longer offers an update when already
  running the latest version. Root cause was the stale assembly version.
  (Closes #74)
- **System Health** — renamed "Rescan" button to "Scan" to match the
  initial prompt text. (Closes #97)
- **System Health scroll** — fixed ConsoleView auto-scroll from
  propagating `BringIntoView` to the parent ScrollViewer, which caused
  the entire page to jump to the bottom during file-system scans. Now
  scrolls the internal ListBox directly via `ScrollToEnd()`. (Closes #93)
- **Startup tab** — now discovers startup items from shell:startup folders
  (user + common) and Task Scheduler logon tasks, not just registry Run
  keys. Resolves `.lnk` shortcuts to their target path. Deduplicates
  entries already found in the registry. Filters out Microsoft/Windows
  system tasks to reduce noise. (Closes #76)
- **Cleanup tab** — auto-scans TEMP folders and Recycle Bin sizes on load,
  showing results in two summary cards so the tab is no longer empty until
  the user runs an action. (Closes #96)
- **Uninstaller** — failed uninstalls now show descriptive error messages
  instead of cryptic exit codes. Covers common winget/MSI codes: access
  denied, cancelled, already removed, reboot required, installer busy.
  (Closes #87)
- **Network chart labels** — increased axis label font sizes and switched
  to Segoe UI with brighter text color (`#E6E9EE`) for better readability
  on the dark background. (Closes #99, #75)
- **Issue templates** — added all missing tabs (Startup, Duplicates, Disk
  Analyzer, Processes, Battery, Uninstaller, Performance) to both bug
  report and feature request templates. Updated version placeholder.
  (Closes #77)

## [0.12.1] - 2026-04-23

### Fixed
- **CodeQL** — replaced bare `catch` blocks with specific exception types
  (`SecurityException`, `UnauthorizedAccessException`) in PerformanceService
  and PerformanceViewModel. No functional changes.

## [0.12.0] - 2026-04-23

### Added
- **Performance Mode tab** — tune system performance settings with per-tweak
  Apply buttons. Every change is non-destructive and reversible.
  - **Power Plan**: switch between Balanced, High Performance, and Ultimate
    Performance via powercfg.
  - **Visual Effects**: reduce animations, fades, and shadows via P/Invoke
    `SystemParametersInfo` (instant, no logout needed).
  - **Game Mode**: enable or disable Windows Game Mode via registry.
  - **Xbox Game Bar**: disable Game Bar overlay and Game DVR via registry.
  - **NVIDIA GPU**: force max performance (DisableDynamicPstate) with
    auto-detected GPU subkey (not hardcoded). Requires reboot.
  - **Processor State**: force CPU min state to 100% via powercfg.
  - **Overlays info**: manual instructions for Discord, Steam, NVIDIA GFE,
    and EA App overlays (not safe to modify externally).
  - **OriginalSnapshot**: captures exact system state before first change;
    Restore All reverts to the snapshot, not hardcoded defaults.
  - Confirmation dialog before every change.
  - GPU changes warn about reboot requirement.
- **38 new unit tests** for `PerformanceService`, `PerformanceViewModel`,
  and `PerformanceProfile`.

## [0.11.1] - 2026-04-23

### Fixed
- **Process Manager** — kill process now shows a Yes/No confirmation dialog
  warning about potential data loss before terminating.
- **Uninstaller** — uninstall shows a confirmation dialog listing all
  selected apps before proceeding. Select All warns when selecting more
  than 20 apps without an active filter.

## [0.11.0] - 2026-04-23

### Added
- **Uninstaller tab** — lists all installed applications via winget and
  allows batch uninstall of selected apps.
  - Scan installed apps with `winget list`.
  - Filter by name or package ID.
  - Select/deselect all, checkbox per app.
  - Uninstall selected apps silently via `winget uninstall`.
  - Cancel support during scan and uninstall.
  - Virtualized ListView for smooth scrolling.
  - Live console output from winget.
- **18 new unit tests** for `UninstallerService` (table parser, edge cases,
  model properties) and `UninstallerViewModel` (commands, state, filter).

## [0.10.0] - 2026-04-23

### Added
- **Battery Health tab** — monitors battery charge, health percentage, wear
  level, cycle count, chemistry, design vs full-charge capacity, and
  estimated runtime via WMI.
  - Charge bar with percentage and status (Charging / Discharging / Full).
  - Health % (full-charge ÷ design capacity) and wear % display.
  - Detail grid: battery name, chemistry, design capacity, full-charge
    capacity, cycle count, estimated runtime, manufacturer/ID.
  - Gracefully shows "No battery detected" on desktops.
  - Specific exception handling for CodeQL compliance.
- **20 new unit tests** for `BatteryService` and `BatteryHealthViewModel` —
  covers status mapping, chemistry mapping, model calculations, property
  notifications, runtime display formatting, and ViewModel state.

## [0.9.0] - 2026-04-23

### Added
- **Process Manager tab** — lists running Windows processes with memory,
  thread count, and status. Supports kill, filter, sort, and open file
  location.
  - Lists all running processes with PID, name, description, memory,
    threads, and responding status.
  - Real-time filter by name, description, or PID.
  - Sort by memory (default), name, or PID.
  - Kill process button (per-process).
  - Open file location in Explorer.
  - Virtualized ListView for smooth scrolling with 200+ processes.
- **24 new unit tests** for `ProcessManagerService` and
  `ProcessManagerViewModel` — covers snapshot, entries, cancellation,
  kill edge cases, model properties, commands, and filter/sort defaults.

## [0.8.0] - 2026-04-23

### Added
- **Disk Analyzer tab** — shows space breakdown by top-level folders with
  drill-down navigation and drive usage overview.
  - Scans top-level subfolders and computes total size recursively.
  - Shows folder name, size, file/folder count, and percentage bar.
  - Drive usage bar with total/used/free at the top.
  - Drill-down into any folder to see its subfolders.
  - Go Up button to navigate back to parent.
  - Preset paths (fixed drives, user profile, Program Files).
  - Browse button for custom folder selection.
  - Show in Explorer for each folder.
  - Cancellation support.
  - Read-only by design — nothing is modified.
  - Skips system paths ($Recycle.Bin, WinSxS, System Volume Information).
- **30 new unit tests** for `DiskAnalyzerService` and
  `DiskAnalyzerViewModel` — covers empty dirs, subfolders, nested files,
  root files, percentages, invalid inputs, cancellation, progress, and
  model properties.

## [0.7.0] - 2026-04-23

### Added
- **Duplicate File Finder tab** — scans a folder tree for files with
  identical content and shows them grouped by SHA-256 hash.
  - Two-pass scan: group by size first, then hash only size-matched files.
  - SHA-256 content hashing with cancellation support.
  - Duplicate groups sorted by wasted space (descending).
  - Preset folders (user profile, documents, downloads, all fixed drives).
  - Browse button for custom folder selection.
  - Configurable minimum file size filter (default 1 KB).
  - "Show in Explorer" and "Copy path" for each file.
  - Read-only by design — no delete functionality.
  - Skips system paths ($Recycle.Bin, WinSxS, System Volume Information)
    and system files (pagefile, hiberfil, swapfile).
- **41 new unit tests** for `DuplicateFileService` and
  `DuplicateFileViewModel` — covers empty dirs, single files, duplicate
  detection, subdirectories, min size filter, wasted bytes calculation,
  cancellation, progress reporting, hash determinism, and model properties.

## [0.6.0] - 2026-04-22

### Added
- **Startup Manager tab** — lists every program that runs at Windows boot
  and lets users toggle them on/off non-destructively.
  - Scans Registry `Run` / `RunOnce` keys (HKCU + HKLM).
  - Reads `StartupApproved` state (same mechanism as Task Manager).
  - Shows name, publisher, command, and enabled/disabled status.
  - Toggle on/off writes to `StartupApproved` — original `Run` values are
    never deleted.
  - "Open file location" button for each entry.
- **170 new unit tests** for services, models, and helpers — brings the
  total past 1 300 tests.
- **Author header** added to all source files (`laurentiu021`).

### Changed
- **Auto-release workflow** now triggers the release pipeline via
  `workflow_dispatch` instead of relying on tag-push events, fixing a
  race condition where the release job could start before the tag was
  fully pushed.

## [0.5.3] - 2026-04-22

### Fixed
- **CodeQL warnings resolved** — constant-condition check and
  floating-point equality comparison cleaned up.
- **Bug report template visibility** — the issue template was not
  showing up correctly in the GitHub "New issue" picker.

### Added
- **Pure unit tests** for `CleanupViewModel`, `DeepCleanupViewModel`,
  `LargeFileScanner`, and Helpers (converters + `AdminHelper`).
- **Codecov configuration** (`.codecov.yml`) for coverage gating.
- **General issue template** (bug / crash / stability) added to
  `.github/ISSUE_TEMPLATE/`.
- **Auto-release workflow** (`auto-release.yml`) — automatically bumps
  the version and creates a GitHub Release when app code changes land
  on `main`.

### Changed
- **CI** — Codecov upload upgraded to v5; explicit file glob removed.
- **Discussions announcement** posted automatically on every release.
- `.editor/` added to `.gitignore`.

## [0.5.2] - 2026-04-21

### Fixed
- **Cascading error dialogs** — a `DispatcherTimer` ticking at 250 ms could
  queue multiple UI-thread exceptions while a `MessageBox` was blocking the
  dispatcher, producing a cascade of identical "SysManager error" dialogs and
  eventually crashing the app. An interlocked flag now ensures at most one
  error dialog is shown at a time.
- **Ookla speed-test DLL dialogs** — `ProcessStartInfo.ErrorDialog` was not
  set to `false`, so Windows would show a native "DLL was not found" system
  dialog for every failed launch of `speedtest.exe`. The dialog is now
  suppressed; the error surfaces cleanly in the Speed Test status bar instead.
- **Corrupt `speedtest.exe` auto-recovery** — if the downloaded Ookla CLI is
  smaller than 1 KB (partial/corrupt download), it is deleted automatically
  so the next run re-downloads a clean copy.

### Changed
- **Dependencies** — LiveChartsCore 2.0.0-rc5.4 → 2.0.0 (stable release),
  System.Management 10.0.6 → 10.0.7, all GitHub Actions updated to latest
  major versions (checkout v6, setup-dotnet v5, cache v5, upload-artifact v7,
  action-gh-release v3).

### Added
- **CodeQL security scanning** — weekly scheduled analysis plus scan on every
  push/PR. Results visible in the Security tab.
- **Codecov coverage tracking** — unit-test coverage uploaded on every CI run;
  badge in README reflects latest `main` result.
- **App screenshots** — all major tabs captured under `docs/screenshots/`.
- **Repository hygiene** — `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`,
  `SECURITY.md`, `SUPPORT.md`, `.editorconfig`, and a full
  `.github/` folder (issue + PR templates, CI + release workflows,
  Dependabot config, CODEOWNERS, FUNDING placeholder).
- **CI** — GitHub Actions build + unit-test pipeline on every push/PR,
  plus a separate UI-automation job. Cache NuGet packages between runs.
- **Release workflow** — tag-driven build of a signed-free single-file
  exe, SHA256 checksum file, automatic extraction of release notes from
  `CHANGELOG.md`, uploaded together as a GitHub Release.
- **Copy environment info** button on the About tab — copies SysManager
  version, Windows version, architecture, .NET runtime and elevation
  state to the clipboard, ready to paste into a bug report.
- **Screenshots** folder (`docs/screenshots/`) with capture and privacy
  conventions documented.
- **Manual UI smoke script** (`docs/manual-smoke.ps1`) referenced from
  `TESTING.md` — walks every nav tab via the Windows UI Automation tree.
- **README badges** for CI status, latest release, downloads and open
  issues. New sections for reporting bugs, security and contributing.

### Fixed
- **Broken unit tests on main** — three tests in
  `DeepCleanupServiceTests` and `LargeFileScannerTests` no longer
  matched the service signatures introduced in 0.5.1 (progress reporting).
  They now compile and pass, and the cancellation tests correctly
  assert `TaskCanceledException` from `Task.Run(..., cancelledToken)`.
- **Flaky Network tests excluded from CI** — tests that depend on a
  captured WPF dispatcher (`NetworkViewModelSampleTests`,
  `NetworkViewModelDisableTests`, `NetworkHealthFeedbackTests`,
  `NetworkButtonsTests`, `NetworkViewModelTests`,
  `NetworkExhaustiveTests`) are now tagged
  `[Trait("Category", "LocalOnly")]`. CI runs with
  `--filter "Category!=LocalOnly"` so the build stays green while the
  tests continue to run locally where the dispatcher is deterministic.
- **More slow/real-system tests excluded from CI** — `EventLogServiceTests`,
  `DiskHealthServiceTests`, `PowerShellRunnerTests`,
  `PowerShellRunnerDebugTests`, `MemoryTestServiceTests`,
  `SystemInfoServiceTests`, `AboutViewUiTests`, `DeepCleanupViewUiTests`
  tagged `LocalOnly`; these hit real Windows APIs (Event Log, WMI,
  PowerShell process, WPF pack URIs) that are unavailable or too slow on
  the hosted runner.
- **Bug fixes in test data** — `UpdateServiceTests.IsNewer_HandlesMajorJumps`
  had `latest`/`current` columns swapped; corrected.
- **Bug fix: `UpdateService.ParseVersion`** — `TrimStart('v','V')` stripped
  all leading v characters, so `"vv1.2.3"` parsed successfully instead of
  returning null. Now strips at most one leading v/V.
- **Bug fix: `FixedDriveService.EnumerateAsync`** — passing a pre-cancelled
  `CancellationToken` to `Task.Run` caused `TaskCanceledException` before
  the synchronous `Enumerate()` delegate ran. Token is no longer forwarded.

## [0.5.1] - 2026-04-20

### Added
- **Progress bars** everywhere the scanner runs:
  - Deep cleanup scan — determinate bar with "[12/20] Scanning Steam..." status.
  - Deep cleanup clean — same, as each selected category is emptied.
  - Large files finder — indeterminate bar with live counter
    ("4,328 files · 12.3 GB scanned") and current folder.
- **Background task mini-tray** in the left sidebar (under the Admin
  badge) — shows live progress for any running scan/clean/large-files
  operation. Stays visible on every tab, clickable to jump back.

### Changed
- Scan and clean operations continue running when you navigate away to
  other tabs. Progress and results are preserved in the view model.

## [0.5.0] - 2026-04-20

### Fixed
- Update check would silently fail with "Couldn't reach GitHub" even when
  the network was fine. The GitHub client now uses an explicit
  `SocketsHttpHandler`, exposes the actual error message, retries once on
  transient network failures, and shows a visible "Retry" button in the
  About tab.

### Added

#### Deep cleanup (safe by design)
- New **Deep cleanup** tab with opt-in categories and a scan-first workflow.
- **System categories**: NVIDIA / AMD / Intel installer leftovers, Windows
  Update cache, Delivery Optimization cache, Windows Installer patch cache
  (`$PatchCache$`), TEMP folders, Prefetch, crash dumps and WER reports,
  old CBS logs (> 30 days), DirectX shader cache, Recycle Bin on every
  fixed drive.
- **Gaming launcher caches** (never game files, never logins):
  - Steam browser & depot cache (`appcache`, `htmlcache`, `depotcache`, `logs`)
  - Steam per-game shader cache (`steamapps\shadercache`)
  - Epic Games Launcher webcache and logs
  - Battle.net agent cache and Blizzard launcher cache
  - Riot Client / League of Legends client logs
  - GOG Galaxy webcache and redists
  - EA Desktop / Origin cache and logs
- **Windows.old** is detected and shown with an "Irreversible" tag — never
  selected by default.
- Every deletion is wrapped in try/catch so locked files are skipped, not
  forced. A live total shows how much space you'll reclaim.

#### Large files finder
- Scan any preset folder (Downloads, Documents, Desktop, Videos, Pictures,
  Music, Program Files, Program Files x86) or a whole fixed drive.
- Configurable min size (default 500 MB) and top N results (default 100).
- Read-only: results only expose "Show in Explorer" and "Copy path" —
  deletion is disabled by design, even with admin rights.
- Skips pagefile/hiberfil/swapfile, WinSxS, System Volume Information,
  Recycle Bin and critical system config folders.

#### Update system
- Auto update check on startup against the GitHub Releases API, plus a
  manual "Check for updates" button.
- New **About** tab showing the current version, build date, license, and
  a full release-note history pulled live from GitHub.
- Discreet banner in the main window when a newer version is detected,
  linking to the About tab for details.
- Automatic background download of the new build with a progress bar.
  If the automatic download is blocked, a "Manual download" button opens
  the GitHub release page in the browser.
- One-click "Install" button that launches the downloaded build and
  closes the current instance so the new version takes over.

### Security
- Deep cleanup **never** touches: browser caches / cookies / passwords,
  launcher login tokens, the registry, active drivers, Program Files,
  `AppData\Roaming` (live app settings), `ProgramData\NVIDIA` root, or
  actual game files in `steamapps\common`.
- Large files finder is read-only — no delete button exists, so a
  mis-click can't hurt anything important.

## [0.4.0] - 2026-04-20

### Added
- File-system scan auto-discovers all fixed NTFS/ReFS drives and shows a
  checkbox list. Scan one drive, a few, or all of them — runs sequentially
  so disks don't fight for I/O.
- "Scan selected" button in System Health for bulk chkdsk.
- Auto-check for the PSWindowsUpdate module on the Windows Update tab. A
  yellow card prompts installation if it's missing.
- Background-task indicators for SFC and DISM so you can navigate away while
  they grind in the background.

### Fixed
- chkdsk "Access is denied" when the app was launched from a non-system
  working directory (e.g. `E:\Downloads`). All spawned processes now start
  from `System32`.

### Changed
- SFC and DISM no longer block the whole Cleanup tab. Each has its own
  running state; you can keep cleaning TEMP or browsing other tabs while
  they run.

## [0.3.0] - 2026-04-20

### Added
- Self-contained single-file publish profile (`publish.ps1`).
- README, ARCHITECTURE, TESTING, and LICENSE documentation.
- `.gitignore` tuned for .NET / WPF projects.

### Changed
- README rewritten as a general-purpose local monitoring tool.

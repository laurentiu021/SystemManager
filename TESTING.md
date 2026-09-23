# Testing

SysManager has three test projects, each with a distinct scope and runner.

## Projects

| Project | What it tests | Runs on CI |
|---|---|---|
| `SysManager.Tests` | Unit tests — mostly pure logic, but some tests touch lightweight OS APIs (registry reads, process enumeration, Task Scheduler queries) and a few exercise STA/UI-thread code via `Xunit.StaFact` (`[StaFact]`). No WMI, no network I/O, no admin required. | ✅ Every push / PR |
| `SysManager.IntegrationTests` | Integration tests — real Windows APIs (Event Log, WMI, PowerShell, ICMP, WPF dispatcher) | ⚠️ CI (non-blocking) |
| `SysManager.UITests` | End-to-end UI automation via FlaUI — needs an interactive desktop session; runs in CI on a desktop-enabled runner, non-blocking (`continue-on-error`) and skipped on fork PRs | ⚠️ CI (non-blocking) |

## Running unit tests (CI-equivalent)

```powershell
dotnet test SysManager/SysManager.Tests/SysManager.Tests.csproj -c Release
```

## Running integration tests locally

```powershell
dotnet test SysManager/SysManager.IntegrationTests/SysManager.IntegrationTests.csproj -c Release
```

Some integration tests require admin rights (WMI storage queries, ICMP sockets).
Run from an elevated PowerShell prompt if you see access-denied failures.

CI also runs this suite as a **non-blocking** job, so a stale expectation surfaces as a
warning annotation instead of sitting invisible. It is not a merge gate: these tests touch
the live system, so a runner difference must not block a merge. A red here is still real —
read the `integration-test-results` artifact. Anything that fails only on a runner belongs
in this suite; anything pure belongs in `SysManager.Tests`, where it gates merges.

## Running UI automation tests locally

The app must not already be running. The test runner launches and closes it automatically.

```powershell
dotnet test SysManager/SysManager.UITests/SysManager.UITests.csproj -c Release
```

## Manual smoke test over the published exe

`docs/manual-smoke.ps1` launches the published executable, walks the nav tree with
Windows UI Automation, and fails loudly if a tab doesn't render. It complements the
UI test project: it exercises the single-file build a user actually downloads, rather
than a `bin` output, which is where publish-only problems (missing native assets,
single-file extraction, trimming) surface.

Needs a published exe and an interactive desktop session — a WPF app cannot render
over SSH or in a non-interactive scheduled task.

```powershell
./publish.ps1
./docs/manual-smoke.ps1
```

It checks 11 of the 58 tabs (the list is at the top of the script), so treat a pass as
"the shell starts and those tabs render", not as full coverage. Add a nav id to `$navIds`
when a new tab is worth including in the quick check.

## Running everything at once

```powershell
dotnet test SysManager/SysManager.Tests/SysManager.Tests.csproj -c Release
dotnet test SysManager/SysManager.IntegrationTests/SysManager.IntegrationTests.csproj -c Release
dotnet test SysManager/SysManager.UITests/SysManager.UITests.csproj -c Release
```

## Running one class or one test

Under xUnit v3 each test project builds an executable, so a single class can run without
starting the whole suite:

```powershell
dotnet build SysManager/SysManager.Tests/SysManager.Tests.csproj -c Release
./SysManager/SysManager.Tests/bin/Release/net10.0-windows/SysManager.Tests.exe `
    -class SysManager.Tests.ArchitectureTests
```

`-method <name>` narrows to one test, and `-list methods` prints every test the runner
discovered without running any of it. `-list` is the check to reach for after touching the
project file or a package version: it answers "is everything still being found" separately
from "does everything still pass", and those fail in different ways.

Use `-?` for the full option list. This is the same runner `dotnet test` drives, so a result
here and a result on CI mean the same thing.

## Coverage

Coverage is collected automatically on CI by `Microsoft.Testing.Extensions.CodeCoverage`
(`--coverage --coverage-output-format cobertura`) and uploaded to
[Codecov](https://codecov.io/gh/laurentiu021/SystemManager). The badge in
`README.md` reflects the latest `main` branch result.

The same CI job uploads test results to Codecov's test analytics, which reads JUnit XML
and no other format. The run therefore emits two reports: TRX for the CI artifact, and
JUnit for the upload. A guard step checks the JUnit report exists, has a `<testsuites>`
root, and holds at least 4000 test cases before the upload runs, because the CLI uploads
an unreadable report just as happily as a readable one and Codecov never reports the
rejection back.

## Test infrastructure

### Frameworks

| Package | Purpose |
|---|---|
| xUnit v3 4.0 | Test framework |
| NSubstitute 6.2 | Mocking/substitution for interface-based testing |
| NetArchTest.Rules 1.3 | Architecture fitness functions — MVVM dependency direction, and guards that pin recurring defect classes |
| Microsoft.Testing.Extensions.CodeCoverage | Code coverage collection (unit project only) |
| Microsoft.Testing.Extensions.HangDump | Dump on hang (integration project only) |
| Xunit.StaFact | STA thread support for WPF-dependent tests |

Package versions are managed centrally in `SysManager/Directory.Packages.props`
(`ManagePackageVersionsCentrally`), so a `PackageReference` in a `.csproj` carries no
`Version` attribute — adding one fails the restore.

There is no VSTest in that list, deliberately. xUnit v3 test projects are
[Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro)
applications, and the .NET 10 SDK refuses to run one through the VSTest target at all, so
`global.json` opts `dotnet test` into the MTP-based command:

```json
"test": {
  "runner": "Microsoft.Testing.Platform"
}
```

Two consequences worth knowing before editing a workflow. The command line is MTP's, not
VSTest's — `--report-xunit-trx` rather than `--logger trx`, `--coverage` rather than
`--collect`, `--hangdump` rather than `--blame-hang` — and every report format comes from
xUnit itself, so there is no logger package to add for TRX, JUnit, NUnit, HTML, CTRF or
xUnit XML. `dotnet test --project <csproj> --help` prints the full option list for a given
project, including the extension options its packages contribute.

### Parallelism

Each project's `xunit.runner.json` sets its own mode. Unit tests run collections in parallel
(`"parallelMode": "collections"`); the integration project runs strictly serially
(`"parallelMode": "none"`, `"maxParallelThreads": 1`) because its tests touch the live system.
`parallelMode` replaced v2's `parallelizeTestCollections` boolean, which xUnit v3 ignores — the runner
prints the mode it resolved at startup, so a config key that stopped being read is visible there rather
than silently reverting to the default.

Tests that share state or touch OS resources are isolated via xUnit
collection definitions (all defined in `TestCollections.cs`, each with
`DisableParallelization = true`):

- `[Collection("ProcessWideStatics")]` — tests that touch **any** process-wide static: swapping
  `DialogService.Instance`, acquiring `OperationLockService.Instance`, or pinning elevation with
  `AdminHelper.ForceElevation`. This was once two collections, `"DialogService"` and `"OperationLock"`,
  and the split was itself the defect: two *different* serialized collections still run in parallel
  **with each other**, so a test swapping the dialog could race a test holding the lock. xUnit allows
  one collection per class, so the fix was to merge them. This is the collection most of the suite uses.
- `[Collection("ProcessEnvironment")]` — tests that mutate the process's environment variables.
- `[Collection("IconCache")]` — tests touching the shared icon cache.
- `[Collection("Network")]` — tests using ICMP sockets. Defined here, but currently used only by
  `SysManager.IntegrationTests`.

### Shared helpers

- `DialogAnswer` — scopes a canned confirmation answer over `DialogService.Instance` and restores
  the previous instance on dispose, so a confirmation gate can be driven without a UI:
  `using var _ = new DialogAnswer(false);`. Its `Calls` counter lets a test assert a dialog was
  *not* shown — asserting the side effect alone cannot distinguish "the user said yes" from
  "no gate ran at all". Its `Messages` collection carries the wording shown, for the gates where the
  wording IS the behaviour: Context Menu explains a failed toggle one way without admin rights and
  another way with them, and swapping those two would send every user down a path that cannot help
  while a call count stayed green (#2180). Requires `[Collection("ProcessWideStatics")]` on the test
  class — a fitness function in `ArchitectureTests` fails the build if a class swaps a process-wide
  static without it.
- `AdminHelper.ForceElevation(bool)` — pins what `AdminHelper.IsElevated()` answers for the scope's
  lifetime and restores the previous probe on dispose: `using var notElevated =
  AdminHelper.ForceElevation(false);`. **Construct the view-model inside the scope** — view-models read
  elevation once, in their constructor, so a scope opened afterwards changes nothing they will look at.
  Requires `[Collection("ProcessWideStatics")]`.
- `SyncProgress<T>` — **`SysManager.Tests` only.** A synchronous `IProgress<T>` that records reports on
  the calling thread, so progress assertions need no `Task.Delay`. A `List<T>` plus an optional
  per-report callback, for the tests that have to act in the middle of the operation being reported on.
- `SynchronousProgress<T>` — the same idea for `SysManager.IntegrationTests`, backed by a
  `ConcurrentQueue<T>` because there the reporting thread is not the test's. Two declarations rather
  than one linked file because the bodies differ; unifying them is worth doing on its own merits, not as
  a precondition for anything. **Never capture progress through a bare `Progress<T>` in either project** —
  it has no `SynchronizationContext` to post to in a test, so its callbacks land on the thread pool and
  any assertion about the reports is a race that passes locally and fails under CI load.
  `ArchitectureTests.NoTest_CapturesProgressThroughTheAsynchronousProgressType` fails the build on that
  shape across both projects.
- `PropertyChangeRecorder` — records what an `INotifyPropertyChanged` raises, into a collection that
  is safe to read while it is still being written: `var changed = vm.RecordPropertyChanges();` for the
  property NAMES, and `var seen = vm.RecordChangesOf(nameof(vm.IsBusy), () => vm.IsBusy);` for the
  VALUES a named property took — which is the only way to assert "the bar went up and came down" on an
  operation too fast to sample. **Never record into a plain `List`.** A view model whose constructor
  started `InitializeAsync` is still raising notifications on the thread pool, so an `Assert.Contains`
  can enumerate a collection being appended to (#2169). `ArchitectureTests`
  `.NoTest_AppendsPropertyChangesToANonConcurrentCollection` fails the build on that shape; a
  hand-written handler is still allowed where a subscription must be removed by hand, provided its
  collection is concurrent. One file, compiled into both test projects rather than copied — the two
  helpers that were copied instead have since drifted (#2183).
- `StaHelper` — **`SysManager.IntegrationTests` only**, since it exists for tests that instantiate
  views. It queues a delegate onto **one** background STA thread shared by the whole suite and waits
  for it, rethrowing whatever the delegate threw. One thread rather than one per call because the
  `Application`'s resources are `DispatcherObject`s owned by whichever thread created them, and a
  thread-per-call helper let that thread exit underneath every later view (#2156). It drains a work
  queue and deliberately does **not** pump a dispatcher, so `DispatcherTimer` and `InvokeAsync` do not
  run under it — a pumping dispatcher schedules real layout, which reaches DirectWrite and dies on a
  headless runner. The unit project has no view-instantiating test and so needs no copy of this.

### Testing an admin-gated path

Never skip the assertion on an elevated host. Sixteen cases used to open with a variant of
`if (AdminHelper.IsElevated()) return;`, added so an elevated machine would not report a false failure —
but the CI runner *is* elevated and the primary workstation cannot run the suite at all, so the elevation
gate on SFC, DISM, Windows Update, precise bandwidth mode and the nine privileged tabs asserted nothing
anywhere. Pin the value with `ForceElevation` instead, and assert both sides: the negative test alone
cannot tell a working gate from a command that refuses unconditionally.

Where a screen genuinely has two behaviours rather than one gate — a UI test, say, where the app inherits
the test process's integrity level — pick the expected copy from the current level rather than returning
early. `UninstallerUiTests.CurrentSession_ShowsMatchingGuidanceWithoutAdminRelaunchButton` and
`AdminBannerUiTests.PrivilegedTab_ShowsTheElevationBannerForTheCurrentSession` both do this.

`ArchitectureTests.NoTestSkipsItselfBecauseTheSessionIsElevated` fails the build if the skip comes back,
across all three test projects.

### A missing precondition is a skip, never a return

`if (!File.Exists(path)) return;` reports a **pass**. Twelve tests opened with a variant of it — no default
gateway, no user SID to deny with, a `System32` binary a future Windows might drop, a health scan that
produced no score — and each landed in the run summary's `passed:` count having asserted nothing, on
exactly the hosts where the assertion mattered. Nothing anywhere said so, and that is what separates it
from a skip: `skipped: 3` is a number a reader can act on.

There are three honest endings and no fourth:

- **The condition cannot occur on a real host** → assert it. `WindowsIdentity.GetCurrent().User` is
  nullable only because an anonymous token has no user SID, and a process token is never anonymous — so
  `Assert.NotNull(sid)` satisfies the nullable analysis the guard clause was really there for while making
  the impossible case loud instead of reporting a pass that denied nothing.
- **It genuinely can** → `Assert.Skip`, carrying the reason. `CatalogSignatureTests` skips the binary it
  cannot find; `DashboardHealthFlagTests` skips when the scan produced no score.
- **Both branches are real behaviour** → assert both.
  `GatewayHelperTests.DetectDefaultGateway_ReturnsNullOrValidIPv4` asserts null when no eligible adapter
  offers a gateway and a parseable, in-set IPv4 when one does, rather than describing only the half the
  current machine happens to have.

Where the absent precondition is elevation, the answer is `ForceElevation` — see the section above; two of
the twelve were that shape, both prompt-wording assertions that the execute-time gate returned before.

A fourth *resolution*, not a fourth ending, is deletion: a test that is a strict subset of one asserting
more is coverage in name only. `QaAuditTests`' gateway parse check went that way, leaving a pointer
comment where it stood.

`ArchitectureTests.NoTest_ReportsAPassByReturningEarly` fails the build if one comes back, across all
three test projects. It partitions each file into members by indentation rather than by counting braces:
293 of the corpus's string literals hold an unbalanced brace and 89 lines are left net-skewed by them, and
a brace matcher over it already ran one body into the next.

### Dependency-graph validation

`ServiceRegistrationGraphTests` builds the real container from `ServiceRegistration.ConfigureServices`
with `ValidateOnBuild` and `ValidateScopes` enabled, so a registration that cannot be satisfied fails
CI instead of the tab that resolves it. `ValidateOnBuild` walks constructor call sites without invoking
any constructor, which is what makes it safe for services that touch WMI, the registry or the
filesystem — the whole graph validates in milliseconds.

The flag is deliberately a test rather than a production setting: `ConfigureServices` is static and
unconditional, so the graph the test builds is the graph the app builds, and enabling validation in
`App.OnStartup` would turn a broken tab into a refusal to start. Two negative tests assert the
validation is actually armed — one adds a type whose dependency is unregistered, one makes a singleton
capture a scoped service — because a passing graph test proves nothing if validation is silently off.
The single factory-lambda registration (`IGamingProfileService`) is opaque to validation; every other
registration is covered.

### Conventions

- Pure logic tests (parsers, analyzers, converters) need no mocking.
- Tests that depend on OS services should use NSubstitute to mock the
  service interface, keeping the test fast and deterministic.
- Time-dependent tests inject a time source. **A tolerance is not an alternative**, which this file
  used to imply: a "generous bound" is still an assertion about the host, and it fails on a loaded
  machine while telling you nothing about the code. `PingMonitorService`'s interval test counted how
  many real ticks fitted in 1.2 real seconds and went red with `got 2` against a floor of 4 (#2323); it
  now takes a `TimeProvider` whose timers fire on command and asserts the exact delay the pump requests,
  in 0.07s. `EtaCalculator`, `EtwBandwidthSource` and `TemperatureService` take the same parameter.
- A percentage a view model computes must reach a `ProgressBar`. `ArchitectureTests`
  `.EveryProgressPercentageAViewModelComputes_IsBoundToAProgressBar` fails the build when a view model
  assigns a `…Progress` property that no bar in its view binds to `Value` — the shape that would have
  left System Fixes' bar frozen at 0 for the length of an SFC run (#2324). It checks each bar
  separately, because Dashboard has eight and Deep Cleanup has two, and a sibling bar binding the other
  property must not vouch for the missing one.
- **A unit test must not reach the real filesystem through a service's default constructor.** The
  seam is usually already there and simply not taken: `DeepCleanupService` has accepted an
  `ICleanupRoots` since #2176 and `TempCleanupRoots` exists for it, but `DeepCleanupViewModelTests`
  built the production one — and `CleanAsync` ends with a rescan, so one test scanned the whole machine
  for **170 seconds**, 63% of the unit suite (#2333). Redirecting the roots took the suite from 269s to
  **49s**. `ArchitectureTests.NoUnitTestBuildsADeepCleanupViewModel_OnTheRealMachinesScanRoots` holds
  it. Note the failure mode: on a fresh CI runner the same scan is nearly free, so CI would never have
  reported this — a cost that only appears locally still needs a guard.

To generate a local coverage report:

```powershell
dotnet test --project SysManager/SysManager.Tests/SysManager.Tests.csproj `
  --results-directory TestResults `
  --coverage --coverage-output-format cobertura `
  --coverage-output coverage.cobertura.xml

# Install reportgenerator once:
dotnet tool install -g dotnet-reportgenerator-globaltool

reportgenerator -reports:TestResults/**/coverage.cobertura.xml -targetdir:TestResults/html
start TestResults/html/index.html
```

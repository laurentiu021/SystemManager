# Testing

SysManager has three test projects, each with a distinct scope and runner.

## Projects

| Project | What it tests | Runs on CI |
|---|---|---|
| `SysManager.Tests` | Unit tests — mostly pure logic, but some tests touch lightweight OS APIs (registry reads, process enumeration, Task Scheduler queries) and a few exercise STA/UI-thread code via `Xunit.StaFact` (`[StaFact]`). No WMI, no network I/O, no admin required. | ✅ Every push / PR |
| `SysManager.IntegrationTests` | Integration tests — real Windows APIs (Event Log, WMI, PowerShell, ICMP, WPF dispatcher) | ❌ Local only |
| `SysManager.UITests` | End-to-end UI automation via FlaUI — needs an interactive desktop session; runs in CI on a desktop-enabled runner, non-blocking (`continue-on-error`) and skipped on fork PRs | ⚠️ CI (non-blocking) |

## Running unit tests (CI-equivalent)

```powershell
dotnet test SysManager/SysManager.Tests/SysManager.Tests.csproj -c Release
```

## Running integration tests locally

Requires a real Windows machine (not a headless CI runner).

```powershell
dotnet test SysManager/SysManager.IntegrationTests/SysManager.IntegrationTests.csproj -c Release
```

Some integration tests require admin rights (WMI storage queries, ICMP sockets).
Run from an elevated PowerShell prompt if you see access-denied failures.

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

## Coverage

Coverage is collected automatically on CI via `coverlet` and uploaded to
[Codecov](https://codecov.io/gh/laurentiu021/SystemManager). The badge in
`README.md` reflects the latest `main` branch result.

The same CI job uploads test results to Codecov's test analytics, which reads JUnit XML
and no other format. `dotnet test` therefore runs two loggers: `trx` for the CI artifact
and the `Passed! - Failed: N` diagnostic line, and `junit` for the upload. A guard step
checks the JUnit report exists, has a `<testsuites>` root, and holds at least 4000 test
cases before the upload runs, because the CLI uploads an unreadable report just as
happily as a readable one and Codecov never reports the rejection back.

## Test infrastructure

### Frameworks

| Package | Purpose |
|---|---|
| xUnit 2.9 | Test framework |
| NSubstitute 6.1 | Mocking/substitution for interface-based testing |
| NetArchTest.Rules 1.3 | Architecture fitness functions — MVVM dependency direction, and guards that pin recurring defect classes |
| coverlet | Code coverage collection |
| JunitXml.TestLogger | JUnit XML test report — the only format Codecov's test analytics parses |
| Xunit.StaFact | STA thread support for WPF-dependent tests |

Package versions are managed centrally in `SysManager/Directory.Packages.props`
(`ManagePackageVersionsCentrally`), so a `PackageReference` in a `.csproj` carries no
`Version` attribute — adding one fails the restore.

### Parallelism

Unit tests run in parallel by default (`parallelizeTestCollections: true`).
Tests that share state or touch OS resources are isolated via xUnit
collection definitions (all defined in `TestCollections.cs`, each with
`DisableParallelization = true`):

- `[Collection("ProcessWideStatics")]` — tests that touch **any** process-wide static: swapping
  `DialogService.Instance`, or acquiring `OperationLockService.Instance`. This was once two
  collections, `"DialogService"` and `"OperationLock"`, and the split was itself the defect: two
  *different* serialized collections still run in parallel **with each other**, so a test swapping
  the dialog could race a test holding the lock. xUnit allows one collection per class, so the fix
  was to merge them. This is the collection most of the suite uses.
- `[Collection("ProcessEnvironment")]` — tests that mutate the process's environment variables.
- `[Collection("IconCache")]` — tests touching the shared icon cache.
- `[Collection("Network")]` — tests using ICMP sockets. Defined here, but currently used only by
  `SysManager.IntegrationTests`.

### Shared helpers

- `DialogAnswer` — scopes a canned confirmation answer over `DialogService.Instance` and restores
  the previous instance on dispose, so a confirmation gate can be driven without a UI:
  `using var _ = new DialogAnswer(false);`. Its `Calls` counter lets a test assert a dialog was
  *not* shown — asserting the side effect alone cannot distinguish "the user said yes" from
  "no gate ran at all". Requires `[Collection("ProcessWideStatics")]` on the test class — a fitness
  function in `ArchitectureTests` fails the build if a class swaps a process-wide static without it.
- `SyncProgress<T>` — a synchronous `IProgress<T>` that records reports on the calling thread, so
  progress assertions need no `Task.Delay`.
- `StaHelper` — runs a delegate on an STA thread for WPF-dependent types.

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
- Time-dependent tests should use injectable time sources or generous
  tolerances to avoid flakiness on slow CI runners.

To generate a local coverage report:

```powershell
dotnet test SysManager/SysManager.Tests/SysManager.Tests.csproj `
  --collect:"XPlat Code Coverage" `
  --results-directory TestResults

# Install reportgenerator once:
dotnet tool install -g dotnet-reportgenerator-globaltool

reportgenerator -reports:TestResults/**/coverage.cobertura.xml -targetdir:TestResults/html
start TestResults/html/index.html
```

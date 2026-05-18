# Testing

SysManager has three test projects, each with a distinct scope and runner.

## Projects

| Project | What it tests | Runs on CI |
|---|---|---|
| `SysManager.Tests` | Unit tests — mostly pure logic, but some tests touch lightweight OS APIs (registry reads, process enumeration, Task Scheduler queries). No WPF dispatcher, no WMI, no network I/O, no admin required. | ✅ Every push / PR |
| `SysManager.IntegrationTests` | Integration tests — real Windows APIs (Event Log, WMI, PowerShell, ICMP, WPF dispatcher) | ❌ Local only |
| `SysManager.UITests` | End-to-end UI automation via FlaUI | ✅ CI (headless, limited) |

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

## Test infrastructure

### Frameworks

| Package | Purpose |
|---|---|
| xUnit 2.9 | Test framework |
| NSubstitute 5.3 | Mocking/substitution for interface-based testing |
| coverlet | Code coverage collection |
| Xunit.StaFact | STA thread support for WPF-dependent tests |

### Parallelism

Unit tests run in parallel by default (`parallelizeTestCollections: true`).
Tests that share state or touch OS resources are isolated via xUnit
collection definitions:

- `[Collection("Network")]` — tests using ICMP sockets run sequentially.

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

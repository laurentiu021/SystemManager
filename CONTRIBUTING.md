# Contributing to SysManager

Thanks for wanting to help! SysManager is a small, solo-maintained project
and every clean PR is welcome — bug fixes, docs, tests, new features, or
even just a well-written bug report.

This document describes how to get set up, the conventions the codebase
follows, and what to expect when you open a pull request.

## Table of contents

- [Ways to contribute](#ways-to-contribute)
- [Development setup](#development-setup)
- [Project layout](#project-layout)
- [Coding conventions](#coding-conventions)
- [Running tests](#running-tests)
- [Commit messages](#commit-messages)
- [Pull request process](#pull-request-process)
- [Reporting bugs](#reporting-bugs)
- [Requesting features](#requesting-features)
- [Security issues](#security-issues)

## Ways to contribute

- 🐛 **Report a bug** — open an issue with the bug template. Crash logs,
  repro steps, and Windows version are gold.
- 💡 **Suggest a feature** — open an issue with the feature template.
  Small, targeted suggestions land faster than big design proposals.
- 🧪 **Improve tests** — more edge cases, more fixtures. The suite is the
  spec for this project.
- 📚 **Improve docs** — typos, clearer wording, fresh screenshots, better
  examples. Doc-only PRs are merged fast.
- 🛠 **Fix a bug or implement a feature** — see [open issues](https://github.com/laurentiu021/SysManager/issues).
  Please comment on the issue before starting significant work so we
  don't both build the same thing.

## Development setup

**Prerequisites**

- Windows 10 or later (WPF + Windows APIs won't build elsewhere).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- Git 2.40+.
- A Windows account with admin rights (needed to test elevated features;
  the app itself runs fine unelevated for everything else).

**Clone and build**

```powershell
git clone https://github.com/laurentiu021/SysManager.git
cd SysManager
dotnet build -c Debug
```

**Run the app**

```powershell
dotnet run --project SysManager/SysManager/SysManager.csproj
```

**Run the tests** (see [Running tests](#running-tests) for more)

```powershell
dotnet test -c Release
```

## Project layout

```
SysManager/
├── SysManager/                # main WPF app
│   ├── Models/                # POCOs (no logic)
│   ├── Services/              # Windows / PowerShell / CLI wrappers
│   ├── ViewModels/            # MVVM, one per tab
│   ├── Views/                 # XAML + minimal code-behind
│   ├── Helpers/               # small utilities, converters
│   └── Resources/             # icons, generated assets
├── SysManager.Tests/          # xUnit unit + integration tests
└── SysManager.UITests/        # FlaUI UI-automation tests
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the deeper tour.

## Coding conventions

The codebase is small enough that the existing files are the best style
guide. That said, a few explicit rules:

- **MVVM strict**: business logic lives in view models and services, not
  in code-behind. Views are XAML with bindings; `.xaml.cs` should be
  empty or near-empty.
- **CommunityToolkit.Mvvm source generators** (`[ObservableProperty]`,
  `[RelayCommand]`) — use them. No manual `INotifyPropertyChanged` or
  `ICommand` boilerplate.
- **Services are testable**: prefer constructor-injectable seams over
  statics. If a service must be static, keep it pure (`HealthAnalyzer`,
  `EventExplainer`).
- **No silent failures**: catch exceptions close to the boundary
  (PowerShell, file I/O, network) and surface them as log lines or UI
  state. Never swallow.
- **4 spaces, no tabs.** Braces on their own line (standard .NET style).
- **`var` freely** when the type is obvious from the right-hand side.
- **Async all the way down** for anything that touches I/O.
- **AutomationId on every nav item and primary button** so UI tests can
  find them.
- **Admin elevation is opt-in**: never demand admin unless the feature
  genuinely requires it. Show a banner, never a modal.

### XAML conventions

- Resources in `App.xaml` for anything reused more than once.
- Dark-gradient theme stays consistent; no hard-coded colours — use the
  existing brushes.
- Accessibility: every interactive control has a label or `AutomationProperties.Name`.

## Running tests

The suite runs sequentially (see `xunit.runner.json`) so file-system
fixtures can share temp folders safely.

Full run:

```powershell
dotnet test SysManager/SysManager.Tests/SysManager.Tests.csproj -c Release
dotnet test SysManager/SysManager.UITests/SysManager.UITests.csproj -c Release
```

UI tests require an interactive desktop session. Run them locally, not
over SSH/Remote PowerShell.

Filter to one class while iterating:

```powershell
dotnet test --filter "FullyQualifiedName~PingMonitorServiceTests"
```

Generate a coverage report:

```powershell
dotnet test -c Release --collect:"XPlat Code Coverage"
```

**Every non-trivial PR should include at least one test.** If you're
unsure where it belongs, look at the closest existing `*ServiceTests`
or `*ViewModelTests` file and match the pattern.

## Commit messages

Keep them short and imperative. The first line is the headline (≤ 72
chars), then a blank line, then optional detail.

Good:

```
Add SHA256 verification to UpdateService

Downloads now compare the advertised hash against the local file and
refuse to install on mismatch. Covered by UpdateServiceHashTests.
```

Bad:

```
fixed stuff
```

Prefixes aren't required, but if you want a convention: `fix:`, `feat:`,
`docs:`, `test:`, `refactor:`, `ci:` are all understood.

## Pull request process

1. **Fork** the repo and create a branch from `main`:
   `git checkout -b feat/my-feature`.
2. **Build and run the tests locally.** PRs that don't build or break
   existing tests will be asked to fix that first.
3. **Add or update tests** for your change.
4. **Update docs** if the change is user-visible (README, CHANGELOG,
   ARCHITECTURE where relevant).
5. **Open the PR** against `main`. Fill out the PR template honestly —
   if you didn't add tests, say why.
6. **CI must be green** before review starts. CI runs build + both test
   projects on a Windows runner.
7. **Review and iterate.** I try to respond within a few days. If a PR
   goes silent, ping me with a comment.
8. **Squash merge** is the default. Your individual commits are
   preserved in the PR but the `main` history stays linear.

### What makes a PR easy to merge

- Focused on one thing.
- Tests that fail before the fix and pass after.
- No unrelated reformatting or style churn.
- A clear "why" in the description (link the issue if there is one).

## Reporting bugs

Open an issue with the **Bug report** template. The template asks for:

- Which tab and which action.
- What you expected vs. what happened.
- Steps to reproduce (if deterministic).
- SysManager version (from the About tab) and Windows build.
- Logs, if possible: `%LOCALAPPDATA%\SysManager\logs\`.

The About tab has a **"Copy environment info"** button that dumps most
of this in a format ready to paste.

## Requesting features

Open an issue with the **Feature request** template. Tell me:

- The problem you're trying to solve (not the solution you imagine).
- Who else this would help.
- Any constraints — admin required? offline? specific Windows version?

Small, focused requests beat big open-ended ones.

## Security issues

Do not open a public issue for a security vulnerability. See
[SECURITY.md](SECURITY.md) for how to report privately.

---

Thanks again — and welcome aboard.

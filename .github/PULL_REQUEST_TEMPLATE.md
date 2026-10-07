## What does this PR do?

<!-- Brief description of the change. What problem does it solve? -->

## Related issues

<!-- Link issues this PR addresses. Use "Closes #NNN" to auto-close. -->

Closes #

## Type of change

Only `fix:` and `feat:` publish a release. Everything else below merges without one — which changes
what the rest of this checklist asks of you.

- [ ] Bug fix (`fix:`) — releases a patch
- [ ] New feature (`feat:`) — releases a minor
- [ ] Documentation (`docs:`)
- [ ] Tests (`test:`)
- [ ] Refactor, no behaviour change (`refactor:`)
- [ ] Code quality / CodeQL (`fix:`) — releases a patch
- [ ] CI / build (`ci:`)
- [ ] Dependency update (`chore:`)

## Checklist

- [ ] Branch created from `main` (not working on main directly)
- [ ] Code compiles with 0 errors
- [ ] `dotnet format <project> --verify-no-changes` passes on all four projects — CI checks all four
      and rejects formatting drift, which a clean build does not catch (CONTRIBUTING has the loop)
- [ ] Tests added/updated and passing locally
- [ ] Author headers on all new/modified files — the three-line `// SysManager · <ClassName>` block in
      `.cs`, the one-line `<!-- SysManager · Author: … -->` comment in `.xaml`; copy it from the top of
      an existing file of the same kind
- [ ] Self-review completed (no debug code, no hardcoded values, no generic catch)
- [ ] README updated (if features changed)

### Releasing changes only (`fix:` / `feat:`)

Skip these on `docs:` / `test:` / `refactor:` / `ci:` / `chore:`. Those merges publish nothing, so a
new version written on one is never tagged — **and CI fails either way**: a CHANGELOG heading without
the matching csproj bump fails this PR's version check, and both together fail the "Merged version is
tagged" check on `main` once merged.

- [ ] CHANGELOG entry added, opening with a one-line plain-English lead under the version heading
      before the first `###` category (CI checks the lead separately, because the release notes are
      copied from it verbatim)
- [ ] CHANGELOG heading dated **today in UTC** — and re-dated if the merge slips to another UTC day.
      The date is checked after the squash merge, when the branch is already gone: auto-release will
      not tag an entry that is not dated today, so a stale date fails the release rather than the PR.
      It has published yesterday's date twice.
- [ ] `Version` / `FileVersion` / `AssemblyVersion` in `SysManager/SysManager/SysManager.csproj`
      bumped one step from the newest release tag and equal to the new CHANGELOG heading
      (`fix:` = patch, `feat:` = minor)
- [ ] `feat:` only — supported-versions table in `SECURITY.md` updated to the new minor line

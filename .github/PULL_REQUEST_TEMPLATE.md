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
- [ ] `dotnet format <project> --verify-no-changes` passes on every project you touched — CI rejects
      formatting drift, and a clean build does not catch it
- [ ] Tests added/updated and passing locally
- [ ] Author headers on all new/modified files — the three-line `// SysManager · <ClassName>` block;
      copy it from the top of any existing `.cs` or `.xaml` file
- [ ] Self-review completed (no debug code, no hardcoded values, no generic catch)
- [ ] README updated (if features changed)
- [ ] No AI/IDE tool references in code or comments

### Releasing changes only (`fix:` / `feat:`)

Skip these on `docs:` / `test:` / `refactor:` / `ci:` / `chore:` — **doing them there makes CI fail**,
because the version gate requires the newest CHANGELOG heading to equal the csproj version, and a
non-releasing PR leaves that version alone.

- [ ] CHANGELOG entry added, opening with a one-line plain-English lead under the version heading
      before the first `###` category (CI checks the lead separately, because the release notes are
      copied from it verbatim)
- [ ] CHANGELOG heading dated **today in UTC** — and re-dated if the merge slips to another UTC day.
      This is the only gate that runs *after* the squash merge, when the branch is already gone: the
      tag push that triggers the release requires the date to be today, so a stale date fails the
      release rather than the PR. It has published yesterday's date twice.
- [ ] `Version` / `FileVersion` / `AssemblyVersion` in `SysManager/SysManager/SysManager.csproj`
      bumped one step from the newest release tag and equal to the new CHANGELOG heading
      (`fix:` = patch, `feat:` = minor)
- [ ] `feat:` only — supported-versions table in `SECURITY.md` updated to the new minor line

# Release Automation Plan

This plan defines the implemented task and script architecture for Windows, Android, and combined build/release/publish workflows.

## Goals

1. Build tasks compile only.
2. Release tasks create release artifacts only.
3. Publish tasks automate versioning, changelog generation, release commit/tag, push, and GitHub release upload.
4. Manual version and changelog editing is minimized.

## Implemented Structure

### VS Code Tasks

1. build windows
2. build mobile
3. build both
4. release windows
5. release mobile
6. release both
7. publish windows
8. publish mobile
9. publish both

Publish tasks depend on:

1. github cli login (cached)
2. preflight github publish

### Script Pipeline

Scripts are under tools/release.

1. common.ps1
2. preflight.ps1
3. version.ps1
4. changelog.ps1
5. build.ps1
6. release.ps1
7. publish.ps1
8. wrappers for windows/mobile/both build/release/publish entrypoints

## Publish Flow

1. Validate git and GitHub auth preconditions.
2. Compute next semantic version from Conventional Commit history.
3. Generate release notes and update CHANGELOG.md.
4. Produce release artifacts.
5. Update VERSION file.
6. Commit release metadata.
7. Create tag.
8. Push branch and tag.
9. Create GitHub release and upload artifacts.
10. Write publish summary logs under publish/logs.

## Safety Features

1. Branch check defaults to main.
2. Clean-working-tree check before publish.
3. Dry-run mode.
4. No-push mode.
5. Optional flags to allow dirty tree or non-main branch when explicitly requested.

## Validation Checklist

1. Run build windows, build mobile, build both.
2. Run release windows, release mobile, release both.
3. Run publish windows with dry-run first.
4. Run one real publish after validation.
5. Verify release tag, assets, and changelog output.

## Commit Convention Requirement

Conventional Commits are required for accurate automatic bumping.

1. feat: minor
2. fix/perf/refactor: patch
3. feat! or BREAKING CHANGE: major

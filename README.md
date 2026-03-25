# Crispy_Bills

Crispy_Bills is a local-first bill manager with a Windows desktop app (WPF, .NET 8) and companion apps built with .NET MAUI (.NET 9), including Android. It helps track monthly bills, recurring entries, and yearly planning with SQLite-backed data.

## What This Repository Includes

- Desktop app: .NET 8 WPF (`CrispyBills.csproj`)
- Mobile app: .NET MAUI (`CrispyBills.Mobile.Android`; Android is the primary documented target—the project also lists iOS, Mac Catalyst, and Windows when building on Windows)
- Parity tests and utility scripts for validation and regression
- Local release automation for build, release artifact generation, and GitHub publish

## Key Features

- Year/month bill tracking with inline edits
- Undo/redo, light/dark theme, and grid editing options such as font size (desktop)
- Monthly income alongside bills (desktop and mobile)
- Recurring bill propagation into future months
- Paid/unpaid/past-due status workflows
- Category summary and pie-chart support
- Global notes (desktop panel and mobile Notes flow; included in structured CSV import/export when present)
- CSV import/export and verification tooling
- Mobile: search and category filters, bill templates, year archive, export, and optional diagnostics logging

## Requirements

- Windows (desktop app and typical Android dev/publish workflow)
- .NET SDK 8 or newer for the WPF project (`net8.0-windows` in `CrispyBills.csproj`)
- .NET SDK 9 with the **.NET MAUI** workload for the mobile project (`net9.0-android` and other targets in `CrispyBills.Mobile.Android.csproj`; install with `dotnet workload install maui` if needed)
- For Android build/publish:
	- OpenJDK under `%LOCALAPPDATA%\Programs\OpenJDK`
	- Android SDK under `%LOCALAPPDATA%\Android\Sdk`
	- Android application ID: `com.crispybills.mobile.android`
- For publish-to-GitHub flow:
	- Git
	- GitHub CLI (`gh`) authenticated with `repo` scope

## Quick Start (Desktop)

Build solution (WPF app and `CrispyBills.Mobile.ParityTests` only—the MAUI project is not in the solution file):

```powershell
dotnet build CrispyBills.sln
```

Run desktop app from Debug output:

```powershell
Start-Process -FilePath "bin\Debug\net8.0-windows\CrispyBills.exe" -WorkingDirectory "$PWD"
```

## Building the Mobile App

The MAUI project lives at `CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj` and is **not** included in `CrispyBills.sln`. Build or publish it with `dotnet` as below (paths assume your current directory is the repository root).

### Android (CLI)

Build Android:

```powershell
$jdkPath=(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkPath=Join-Path $env:LOCALAPPDATA 'Android\Sdk'
dotnet build '.\CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj' -f net9.0-android -c Release -p:JavaSdkDirectory="$jdkPath" -p:AndroidSdkDirectory="$sdkPath"
```

### Publish Android APK

```powershell
$jdkPath=(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkPath=Join-Path $env:LOCALAPPDATA 'Android\Sdk'
dotnet publish '.\CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj' -f net9.0-android -c Release -o '.\publish\net9.0-android' -p:AndroidPackageFormats=apk -p:JavaSdkDirectory="$jdkPath" -p:AndroidSdkDirectory="$sdkPath"
```

Other target frameworks in the same project (for example `net9.0-ios` on a Mac) follow the usual `dotnet build` / `dotnet publish` `-f` workflow for that platform.

## VS Code Task Flows

Main task groups:

- Build: `build windows`, `build mobile`, `build both`
- Release artifacts only: `release windows`, `release mobile`, `release both`
- Full publish: `publish windows`, `publish mobile`, `publish both`
- Publish dry-run: `publish windows (dryrun)`, `publish mobile (dryrun)`, `publish both (dryrun)`

Recommended sequence before a real release:

1. Run `publish both (dryrun)`
2. Run `publish both`

## Release Wizard

The repository also includes an interactive release wizard at `tools/release/wizard.ps1`.

Typical dry-run (from repository root):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\release\wizard.ps1" -DryRun
```

Useful wizard flags:

- `-AutoConfirm` selects non-interactive defaults for prompts that support it.
- `-RequireCleanTree` opts out of the wizard's default dirty-tree allowance and enforces a clean working tree where supported.
- `-NoCommit` disables wizard-managed commit steps and tells publish wrappers not to auto-commit dirty working-tree changes.

Wizard contract:

1. The wizard coordinates selection, prompts, parameter forwarding, and publish commit-choice collection.
2. The publish scripts remain responsible for release-affecting operations such as performing the pre-publish auto-commit, release commit/tag creation, push, and GitHub release upload.
3. Before publish tasks, the wizard runs `recover-missing-release.ps1` in advisory dry-run mode so recovery issues are surfaced without mutating remote release state.

## Local Publish Automation Behavior

On a real publish, scripts do the following:

1. Preflight checks (auth, branch, remote, working tree policy)
2. Semantic version resolution from commit history
3. Changelog/release notes generation
4. Artifact build (desktop exe + android apk)
5. Release commit and tag creation
6. Atomic git push of branch and tag
7. GitHub release creation and artifact upload (with retries)

Safety and diagnostics:

- No-op when no new commits exist since latest tag
- Dry-run synthetic version fallback when semantic bump is unavailable
- Warning/error counts printed at script end
- Multi-line root failure details printed on publish errors
- Rollback avoids destructive local reset when remote already changed
- Publish fetches tags before version resolution and refuses to reuse an existing computed tag
- If remote push succeeds but release creation fails, publish attempts automatic release recovery

Release consistency note:

- Semantic versioning uses git commit history and tags, not the GitHub release list.
- If tags and release objects ever drift (for example tag exists without release), backfill missing releases first.
- Backfill format:

```powershell
gh release create vX.Y.Z --title vX.Y.Z --notes-file publish/logs/release-notes-vX.Y.Z.md <artifact1> <artifact2>
```

## Testing and Validation

Full regression script:

- `tools/test_all_functions.ps1`

Example:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\test_all_functions.ps1"
```

Additional helpers:

- `tools/apply_testdbs.ps1`
- `tools/compare_export_db.py`

Detailed guides:

- `docs/TESTING.md`
- `docs/RELEASE_AUTOMATION_PLAN.md`
- `docs/ANDROID_SIGNING.md`
- `docs/PLAY_UPLOAD.md`

## Data Location

By default, user data is under:

- `%USERPROFILE%\Documents\CrispyBills`

This includes yearly databases, notes database, backups, and test artifacts.

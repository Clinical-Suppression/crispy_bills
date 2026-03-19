# Crispy Bills

Crispy Bills is a local-first bill manager with a Windows desktop app (WPF) and an Android app (.NET MAUI). It helps track monthly bills, recurring entries, and yearly planning with SQLite-backed data.

## What This Repository Includes

- Desktop app: .NET 8 WPF (`CrispyBills.csproj`)
- Mobile app: .NET MAUI Android (`CrispyBills.Mobile.Android`)
- Parity tests and utility scripts for validation and regression
- Local release automation for build, release artifact generation, and GitHub publish

## Key Features

- Year/month bill tracking with inline edits
- Recurring bill propagation into future months
- Paid/unpaid/past-due status workflows
- Category summary and pie-chart support
- CSV import/export and verification tooling

## Requirements

- Windows
- .NET SDK (desktop + MAUI workloads used by this repo)
- For Android build/publish:
	- OpenJDK under `%LOCALAPPDATA%\Programs\OpenJDK`
	- Android SDK under `%LOCALAPPDATA%\Android\Sdk`
- For publish-to-GitHub flow:
	- Git
	- GitHub CLI (`gh`) authenticated with `repo` scope

## Quick Start (Desktop)

Build solution:

```powershell
dotnet build CrispyBills.sln
```

Run desktop app from Debug output:

```powershell
Start-Process -FilePath "bin\Debug\net8.0-windows\CrispyBills.exe" -WorkingDirectory "$PWD"
```

## Android Build and Publish (CLI)

Build Android:

```powershell
$jdkPath=(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkPath=Join-Path $env:LOCALAPPDATA 'Android\Sdk'
dotnet build '.\CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj' -f net9.0-android -c Release -p:JavaSdkDirectory="$jdkPath" -p:AndroidSdkDirectory="$sdkPath"
```

Publish Android APK:

```powershell
$jdkPath=(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkPath=Join-Path $env:LOCALAPPDATA 'Android\Sdk'
dotnet publish '.\CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj' -f net9.0-android -c Release -o '.\publish\net9.0-android' -p:AndroidPackageFormats=apk -p:JavaSdkDirectory="$jdkPath" -p:AndroidSdkDirectory="$sdkPath"
```

## VS Code Task Flows

Main task groups:

- Build: `build windows`, `build mobile`, `build both`
- Release artifacts only: `release windows`, `release mobile`, `release both`
- Full publish: `publish windows`, `publish mobile`, `publish both`
- Publish dry-run: `publish windows (dryrun)`, `publish mobile (dryrun)`, `publish both (dryrun)`

Recommended sequence before a real release:

1. Run `publish both (dryrun)`
2. Run `publish both`

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

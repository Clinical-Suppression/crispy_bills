# Crispy Bills

Crispy Bills is a .NET 8 WPF desktop app for managing monthly bills, recurring payments, and yearly budgeting in a local SQLite-backed workflow.

## Highlights

- Year/month bill tracking with quick inline editing.
- Recurring bill propagation across future months.
- Paid/unpaid/past-due status handling.
- Category-based dashboard and summary views.
- CSV import/export and verification helper scripts.

## Build and Run

From the repository root:

```powershell
dotnet build CrispyBills.sln
```

Run the app (Debug output):

```powershell
Start-Process -FilePath "bin\Debug\net8.0-windows\CrispyBills.exe" -WorkingDirectory "$PWD"
```

## Android Project (In Repository)

The Android project is in this repository:

`CrispyBills.Mobile.Android`

Build Android app:

```powershell
$jdkPath=(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkPath=Join-Path $env:LOCALAPPDATA 'Android\Sdk'
dotnet build '.\CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj' -f net9.0-android -c Release -p:JavaSdkDirectory="$jdkPath" -p:AndroidSdkDirectory="$sdkPath"
```

Publish APK:

```powershell
$jdkPath=(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkPath=Join-Path $env:LOCALAPPDATA 'Android\Sdk'
dotnet publish '.\CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj' -f net9.0-android -c Release -o '.\publish\net9.0-android' -p:AndroidPackageFormats=apk -p:JavaSdkDirectory="$jdkPath" -p:AndroidSdkDirectory="$sdkPath"
```

Publish both desktop EXE and Android APK in one run (VS Code task):

`publish all (exe + apk)`

Run Android smoke validation only (VS Code task):

`mobile smoke`

Combined release task outputs:

- `.\publish\win-x64`
- `.\publish\net9.0-android`

Mobile app improvements included in this repository:

- Clickable month and year selectors with left/right arrows.
- Year navigation limited to years that already exist.
- `Set Monthly Income` action that applies from selected month forward.
- `Import` action for structured CSV into current year (replace or merge).
- `Summary & Pie Chart` page for monthly category breakdown and year summary.
- `Diag` page for app data/log diagnostics.

## GitHub Release Automation

This repository includes a workflow that builds release artifacts for both apps and publishes them on version tags:

- Workflow: [ .github/workflows/release-build.yml ](.github/workflows/release-build.yml)
- Trigger for release publish: push tag like `v1.0.0`
- Manual trigger: workflow_dispatch

Tag builds upload these release assets:

- Windows EXE
- Android APK
- Android AAB (when signing secrets are configured)

Android signing and distribution setup is documented here:

- [ docs/ANDROID_SIGNING.md ](docs/ANDROID_SIGNING.md)

Google Play upload workflow setup and usage is documented here:

- [ docs/PLAY_UPLOAD.md ](docs/PLAY_UPLOAD.md)

## Local Release Automation (VS Code Tasks)

The repository also includes local task-driven release automation for Windows, Android, or both targets.

Key tasks:

- build windows, build mobile, build both
- release windows, release mobile, release both
- publish windows, publish mobile, publish both
- publish windows (dryrun), publish mobile (dryrun), publish both (dryrun)

Behavior during real publish:

- Runs GitHub auth precheck from scripts (task dependency runs cached login first).
- Computes semantic version from commit history.
- Generates release notes and changelog updates.
- Builds artifacts and writes artifact manifest under publish/logs.
- If working tree is dirty, prompts for auto-commit type and auto-generates description (editable).
- Pushes release commit and tag, then creates GitHub release.

Safety behavior:

- If no new commits exist since the previous tag, publish exits as a safe no-op.
- Dry-run uses a synthetic preview version if no semantic bump is available.
- Publish/build/release scripts print warnings and errors summary counts.
- If publish fails after remote state changed, local reset is skipped to avoid desync.

Republish verification sequence:

```powershell
# 1) Validate automation without mutating remote state
Run Task: publish both (dryrun)

# 2) Real publish
Run Task: publish both
```

If a publish partially succeeds (for example branch pushed but tag/release missing), recover with:

```powershell
git fetch origin
git checkout main
git pull --ff-only

git tag -a vX.Y.Z <release-commit-sha> -m "Release vX.Y.Z"
git push origin vX.Y.Z
gh release create vX.Y.Z --title vX.Y.Z --notes-file publish/logs/release-notes-vX.Y.Z.md
```

## Current UX Notes

- `View > Enter Moves Down` is checkable and shows a checkmark when enabled.
- The DataGrid right-click context menu includes `Add Bill`, `Edit`, and `Delete`.
- `Add Bill` opens the calendar on the currently selected app year/month context.

## Automation Scripts

### Full app regression runner

Script: [tools/test_all_functions.ps1](tools/test_all_functions.ps1)

Runs:

1. Solution build
2. Startup auto-test roundtrip path
3. Export-to-testDB row compare
4. Live DB verification

Examples:

```powershell
# Full pass
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\test_all_functions.ps1"

# Skip UI auto pass
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\test_all_functions.ps1" -SkipUiPass
```

### Apply generated test DBs to live DBs

Script: [tools/apply_testdbs.ps1](tools/apply_testdbs.ps1)

Parameters:

- `-RootPath`: CrispyBills data root (default: Documents/CrispyBills)
- `-Years`: years to apply (default: 2026, 2027)
- `-TestDbDir`: source directory for `CrispyBills_<year>_test.db`

Example:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\apply_testdbs.ps1" -RootPath "C:\Data\CrispyBills" -Years 2026,2027
```

### Compare exported CSV rows to test DBs

Script: [tools/compare_export_db.py](tools/compare_export_db.py)

Parameters:

- `--root`: base data directory (default: `~/Documents/CrispyBills`)
- `--csv-path`: explicit export CSV (optional, falls back to latest `auto_export_*.csv` under root)
- `--test-db-dir`: explicit test DB directory (optional, falls back under root)

Examples:

```powershell
python "tools\compare_export_db.py" --root "C:\Data\CrispyBills"
python "tools\compare_export_db.py" --root "C:\Data\CrispyBills" --csv-path "C:\temp\auto_export_20260315_154749.csv"
```

## Data Location

By default, app data is stored in:

`%USERPROFILE%\Documents\CrispyBills`

This includes yearly DB files, notes DB, backups, and auto-test artifacts.

## Troubleshooting

- If build fails with `MSB3026/MSB3027/MSB3021`, close running `CrispyBills.exe` instances and rebuild.
- If script runs produce stale results, delete/refresh temporary auto-test outputs under `db_backups\auto_tests`.

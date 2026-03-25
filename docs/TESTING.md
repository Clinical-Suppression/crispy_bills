# Testing Guide

This project uses lightweight script-driven regression checks for app flows and data integrity.

## 1) Full Regression Script

Use [tools/test_all_functions.ps1](../tools/test_all_functions.ps1).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\test_all_functions.ps1"
```

What it validates:

1. Build succeeds.
2. Startup auto-test path generates expected files.
3. CSV export rows match generated test DB rows.
4. Live DB month/year totals report can be generated.

Useful flags:

- `-SkipBuild`
- `-SkipUiPass`
- `-UiTimeoutSeconds <int>`

## 2) Script-Level Data Checks

### Compare export CSV to test DBs

```powershell
python "tools\compare_export_db.py" --root "$env:USERPROFILE\Documents\CrispyBills"
```

Pass condition: report contains `Total missing in DB: 0`.

### Apply test DBs to live DBs

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\apply_testdbs.ps1" -RootPath "$env:USERPROFILE\Documents\CrispyBills" -Years 2026,2027
```

## 3) Manual UX Regression Checklist

- `View > Enter Moves Down` toggles and displays checkmark state.
- DataGrid context menu includes `Add Bill`, `Edit`, `Delete`.
- Add Bill calendar opens on active selected app year/month.
- Category combo edit in DataGrid commits correctly on Enter/Tab and does not reopen unexpectedly.
- Due date paste rejects invalid formats and normalizes valid values.

## 3.0) Android crash diagnosis (splash then closes)

When the app shows the MAUI/.NET splash and then exits, capture the fatal exception from the device:

```powershell
adb logcat -c
# Launch the app on the phone/emulator, then:
adb logcat -d | findstr /i "AndroidRuntime FATAL EXCEPTION mono-rt maui xaml crispy CrispyBills"
```

- Prefer a **Debug** build first so stacks include managed symbols.
- Also check the in-app diagnostics log path (written under app private storage) after a failed start if the app shows the **Startup error** fallback page.

## 3.1) Mobile Parity Regression Cases

- New Year creation guardrail when next year already has data:
	1. Ensure current year is selected (example: 2026).
	2. Ensure next year (example: 2027) already contains at least one bill or non-zero income.
	3. Trigger Create New Year from the mobile app.
	4. Verify the app reports that the target year already exists and no new snapshot was created.
	5. Verify next-year data remains unchanged (no overwrite, no duplicate generated templates).

## 4) Build Lock Troubleshooting

If build reports lock errors:

```powershell
Get-Process CrispyBills -ErrorAction SilentlyContinue | Stop-Process -Force
```

Then rebuild:

```powershell
dotnet build CrispyBills.sln
```

## 5) Release Automation Verification

Use this before running a real release publish:

```powershell
# Recommended dry run first
Run Task: publish both (dryrun)
```

Verify:

1. Preflight reports auth, remote, and branch checks passed.
2. Artifact paths are printed for both Windows EXE and Android APK.
3. Publish task diagnostics summary appears with warning/error counts.
4. Artifact manifest file exists under publish/logs.

Then run:

```powershell
Run Task: publish both
```

Post-publish checks:

1. A new release tag exists on origin.
2. GitHub release exists for that tag.
3. publish/logs contains release notes, publish summary, and artifact manifest files.
4. If publish fails, terminal output includes full multi-line root-failure details.

If publish partially succeeds (remote push completed but release is missing), use the recovery flow in [docs/RELEASE_AUTOMATION_PLAN.md](RELEASE_AUTOMATION_PLAN.md).

## 6) Release wizard (Pester + harness)

Automated checks for `tools/release/wizard.ps1` live beside the script.

**Pester** — [tools/release/wizard.tests.ps1](../tools/release/wizard.tests.ps1) (single `Describe 'Crispy_Bills Release Wizard'` block). Covers task selection parsing (`Prompt-MultiSelect`, `Resolve-TaskSelectionValue`), `Prompt-YesNo` / non-interactive responses, `Get-WizardTaskArguments` for changelog and publish scripts (including `-NonInteractive` / `-ResponsesFile` / `-ApproveMajorVersion`), major-version approval, git status line parsing (`ConvertFrom-GitStatusLines`), recovery script argument rules, `Check-VersionAgreements`, commit-type heuristics, and related helpers.

```powershell
# Requires Pester (e.g. Install-Module Pester -Scope CurrentUser)
Invoke-Pester -Path "tools\release\wizard.tests.ps1"
```

**Harness** — [tools/release/tests/harness.ps1](../tools/release/tests/harness.ps1) runs a copy of the wizard against stubbed release scripts under `publish\logs\wizard-tests\`, with scripted stdin and a timeout watchdog (exit `124` if the run exceeds `-TimeoutSeconds`). Use `-StubMode` (`heartbeat`, `quiet`, `prompt`, `fail`) to vary the `preflight.ps1` stub; use `-NoCommit` for the no-commit stdin path. See [tools/release/README.md](../tools/release/README.md) for parameter summary.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\release\tests\harness.ps1" -StubMode heartbeat -TimeoutSeconds 60
```

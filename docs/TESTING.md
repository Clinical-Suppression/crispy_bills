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

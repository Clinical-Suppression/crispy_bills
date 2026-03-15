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

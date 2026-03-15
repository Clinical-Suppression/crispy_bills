$root = 'C:\Users\Chris\Documents\CrispyBills'
$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$backup = Join-Path $root "db_backups\import_restore_backups\$ts"
New-Item -ItemType Directory -Force -Path $backup | Out-Null
foreach ($y in 2026,2027) {
    $live = Join-Path $root ("CrispyBills_$y.db")
    if (Test-Path $live) { Copy-Item -Path $live -Destination $backup -Force }
}
Copy-Item -Path 'C:\Users\Chris\Documents\CrispyBills\db_backups\auto_tests\test_dbs\CrispyBills_2026_test.db' -Destination (Join-Path $root 'CrispyBills_2026.db') -Force
Copy-Item -Path 'C:\Users\Chris\Documents\CrispyBills\db_backups\auto_tests\test_dbs\CrispyBills_2027_test.db' -Destination (Join-Path $root 'CrispyBills_2027.db') -Force
Write-Output "Backup dir: $backup"
Get-ChildItem $backup | Select-Object Name,Length

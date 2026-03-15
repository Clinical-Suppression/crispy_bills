param(
    [string]$RootPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'CrispyBills'),
    [int[]]$Years = @(2026, 2027),
    [string]$TestDbDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $TestDbDir) {
    $TestDbDir = Join-Path $RootPath 'db_backups\auto_tests\test_dbs'
}

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$backup = Join-Path $RootPath "db_backups\import_restore_backups\$ts"
New-Item -ItemType Directory -Force -Path $backup | Out-Null

foreach ($y in $Years) {
    $live = Join-Path $RootPath ("CrispyBills_$y.db")
    if (Test-Path $live) {
        Copy-Item -Path $live -Destination $backup -Force
    }

    $testDb = Join-Path $TestDbDir ("CrispyBills_{0}_test.db" -f $y)
    if (-not (Test-Path $testDb)) {
        throw "Missing test DB for year ${y}: $testDb"
    }

    Copy-Item -Path $testDb -Destination (Join-Path $RootPath ("CrispyBills_{0}.db" -f $y)) -Force
}

Write-Output "Backup dir: $backup"
Get-ChildItem $backup | Select-Object Name,Length

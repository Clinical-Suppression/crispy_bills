param(
    [switch]$SkipBuild,
    [switch]$SkipUiPass,
    [int]$UiTimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Resolve-PythonCommand {
    $candidates = @(
        @{ Cmd = "python"; Args = @() },
        @{ Cmd = "py"; Args = @("-3") }
    )

    foreach ($candidate in $candidates) {
        try {
            & $candidate.Cmd @($candidate.Args + @("-c", "import sys; print(sys.version)")) *> $null
            if ($LASTEXITCODE -eq 0) {
                return $candidate
            }
        }
        catch {
            # Try next candidate.
        }
    }

    throw "Python 3 is required but was not found on PATH (tried 'python' and 'py -3')."
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [string[]]$Arguments = @()
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $Command $($Arguments -join ' ')"
    }
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent
Set-Location $repoRoot

$dataRoot = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "CrispyBills"
$autoTestsRoot = Join-Path $dataRoot "db_backups\auto_tests"
$sentinelPath = Join-Path $dataRoot "auto_test_roundtrip.txt"
$appExe = Join-Path $repoRoot "bin\Debug\net8.0-windows\CrispyBills.exe"
$compareScript = Join-Path $repoRoot "tools\compare_export_db.py"
$verifyScript = Join-Path $repoRoot "tools\verify_live_dbs.py"

$result = [ordered]@{
    Build = "SKIPPED"
    UiRoundTrip = "SKIPPED"
    CompareExport = "SKIPPED"
    VerifyLiveDbs = "SKIPPED"
    MissingRows = "UNKNOWN"
}

try {
    Write-Step "Preparing environment"
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $autoTestsRoot -Force | Out-Null

    # Avoid file-lock build failures and stale UI instances.
    Get-Process CrispyBills -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if (-not $SkipBuild) {
        Write-Step "Building solution"
        Invoke-External -Command "dotnet" -Arguments @("build", "CrispyBills.sln")
        $result.Build = "PASS"
    }

    if (-not $SkipUiPass) {
        Write-Step "Running startup UI auto-test path"
        "run" | Set-Content -Path $sentinelPath -Encoding UTF8

        $env:CRISPYBILLS_RUN_AUTOTEST = "1"
        try {
            if (-not (Test-Path $appExe)) {
                throw "Expected app binary not found: $appExe"
            }

            $proc = Start-Process -FilePath $appExe -WorkingDirectory $repoRoot -PassThru
            Start-Sleep -Seconds $UiTimeoutSeconds

            if (-not $proc.HasExited) {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
        }
        finally {
            Remove-Item Env:CRISPYBILLS_RUN_AUTOTEST -ErrorAction SilentlyContinue
        }

        $latestExport = Get-ChildItem $autoTestsRoot -Filter "auto_export_*.csv" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        $latestDiag = Get-ChildItem $autoTestsRoot -Filter "db_snapshot_*.txt" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        $testDbDir = Join-Path $autoTestsRoot "test_dbs"
        $testDbs = Get-ChildItem $testDbDir -Filter "CrispyBills_*_test.db" -File -ErrorAction SilentlyContinue

        if (-not $latestExport -or -not $latestDiag -or -not $testDbs -or $testDbs.Count -eq 0) {
            throw "UI auto-test outputs are incomplete. Expected export CSV, DB snapshot, and test DB files under $autoTestsRoot"
        }

        $result.UiRoundTrip = "PASS"

        Write-Step "Comparing export rows against generated test DBs"
        $python = Resolve-PythonCommand
        Invoke-External -Command $python.Cmd -Arguments @($python.Args + @($compareScript, "--csv-path", $latestExport.FullName, "--test-db-dir", $testDbDir))

        $diffReport = Get-ChildItem $testDbDir -Filter "per_row_diff_*.txt" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if (-not $diffReport) {
            throw "compare_export_db.py did not produce a per_row_diff report."
        }

        $missingLine = Select-String -Path $diffReport.FullName -Pattern "Total missing in DB:\s*(\d+)" | Select-Object -First 1
        if (-not $missingLine) {
            throw "Could not parse missing-row summary from $($diffReport.FullName)"
        }

        $missingCount = [int]$missingLine.Matches[0].Groups[1].Value
        $result.MissingRows = $missingCount
        if ($missingCount -ne 0) {
            throw "Export/DB mismatch detected: $missingCount row(s) missing in DB. See $($diffReport.FullName)"
        }

        $result.CompareExport = "PASS"
    }

    Write-Step "Verifying live DB month/year totals"
    $python = Resolve-PythonCommand
    Invoke-External -Command $python.Cmd -Arguments @($python.Args + @($verifyScript))
    $result.VerifyLiveDbs = "PASS"

    Write-Step "Test Summary"
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value } | Write-Host
    Write-Host "`nALL CHECKS PASSED" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "`nTEST FAILURE: $($_.Exception.Message)" -ForegroundColor Red
    Write-Step "Test Summary"
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value } | Write-Host
    exit 1
}

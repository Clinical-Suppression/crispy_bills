param([string]$Configuration = 'Debug')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Configuration -is [System.Array]) {
    if ($Configuration.Count -gt 0) {
        $Configuration = $Configuration[0]
    }
    else {
        $Configuration = 'Debug'
    }
}

Write-Host 'Running clean before build to avoid stale generated files.' -ForegroundColor Yellow
& dotnet clean (Join-Path (Get-Location) '..\CrispyBills\CrispyBills.csproj') -c $Configuration | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -Target both -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

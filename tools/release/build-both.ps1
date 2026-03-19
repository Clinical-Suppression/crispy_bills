param([string]$Configuration = 'Debug')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build.ps1') -Target both -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

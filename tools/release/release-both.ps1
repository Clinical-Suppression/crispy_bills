param([string]$Version = 'dev')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'release.ps1') -Target both -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

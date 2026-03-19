param(
    [string]$Branch = 'main',
    [switch]$DryRun,
    [switch]$NoPush,
    [switch]$AllowDirty,
    [switch]$AllowNonMain
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'publish.ps1') -Target mobile -Branch $Branch -DryRun:$DryRun -NoPush:$NoPush -AllowDirty:$AllowDirty -AllowNonMain:$AllowNonMain
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

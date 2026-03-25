param(
    [string]$Version = 'dev',
    [string]$Branch = 'main',
    [switch]$DryRun,
    [switch]$NoPush,
    [switch]$AllowDirty,
    [switch]$AllowNonMain,
    [switch]$NonInteractive,
    [string]$ResponsesFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'release.ps1') -Target windows -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

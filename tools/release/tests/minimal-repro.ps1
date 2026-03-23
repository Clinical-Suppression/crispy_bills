<#
Minimal repro: run `release-mobile.ps1` in dry-run to ensure Version propagates
and that no commits are made. Safe to run locally; uses -DryRun and -NoPush.
#>
param(
    [string]$Version = '9.9.9'
)

Write-Host "Running minimal repro: release-mobile -Version $Version (dry-run)"
pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\release-mobile.ps1') -DryRun -NonInteractive -NoPush -AllowDirty -Version $Version -Verbose -Debug

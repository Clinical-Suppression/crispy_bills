<#
Combined release wrapper for both Windows and Mobile targets.

Usage:
	pwsh release-both.ps1 -Branch main -DryRun

Invokes `release.ps1` with the `both` target and is intended for combined
packaging workflows. Use `-DryRun` for verification in CI environments.
#>

param(
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

& (Join-Path $PSScriptRoot 'release.ps1') -Target both -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

<#
Mobile-specific release flow wrapper (Android/iOS).

Usage:
	pwsh release-mobile.ps1 -Branch main -DryRun

This script delegates to `release.ps1 -Target mobile` and is a convenience
entrypoint for CI and maintainers packaging mobile releases.
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

& (Join-Path $PSScriptRoot 'release.ps1') -Target mobile -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

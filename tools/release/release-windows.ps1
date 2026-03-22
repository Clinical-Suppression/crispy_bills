param([string]$Version = 'dev')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
Windows-specific release flow wrapper.

Usage:
	pwsh release-windows.ps1 -Branch main -DryRun

This wraps platform packaging, signing, and publish steps for the Windows
target. Prefer running `wizard.ps1` for interactive guidance.
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

& (Join-Path $PSScriptRoot 'release.ps1') -Target windows -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

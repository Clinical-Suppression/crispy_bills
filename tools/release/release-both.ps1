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
		[string]$ResponsesFile,
		[string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Build argument list and only include -Version when provided to avoid
# referencing an undefined variable (which causes a runtime error).
# Use parameter splatting to pass named parameters to the target script.
$releaseParams = @{ 'Target' = 'both' }
if ($PSBoundParameters.ContainsKey('Version') -and $Version) {
	$releaseParams['Version'] = $Version
}

& (Join-Path $PSScriptRoot 'release.ps1') @releaseParams
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

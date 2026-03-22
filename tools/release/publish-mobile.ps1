<#
Publish mobile artifacts (Android/iOS).

Usage:
    pwsh publish-mobile.ps1 -Branch main -DryRun

This script delegates to `publish.ps1 -Target mobile` and is a convenience
entrypoint for CI and developers. Use `-DryRun` in CI to validate the flow.
#>

param(
        [string]$Branch = 'main',
        [switch]$DryRun,
        [switch]$NoPush,
        [switch]$AllowDirty,
        [switch]$AllowNonMain,
        [switch]$NonInteractive,
        [string]$ResponsesFile,
        [switch]$ApproveMajorVersion,
        [bool]$AutoCommitChanges = $true,
        [ValidateSet('feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'chore')][string]$AutoCommitType = 'chore',
        [string]$AutoCommitScope,
        [string]$AutoCommitDescription = 'prepare release changes'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'publish.ps1') -Target mobile -Branch $Branch -DryRun:$DryRun -NoPush:$NoPush -AllowDirty:$AllowDirty -AllowNonMain:$AllowNonMain -NonInteractive:$NonInteractive -ResponsesFile $ResponsesFile -ApproveMajorVersion:$ApproveMajorVersion -AutoCommitChanges:$AutoCommitChanges -AutoCommitType $AutoCommitType -AutoCommitScope $AutoCommitScope -AutoCommitDescription $AutoCommitDescription
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

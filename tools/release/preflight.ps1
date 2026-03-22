<#
Preflight checks for release automation.

Purpose:
    Verify that required tools, credentials, and repository state are present
    before running release or publish scripts. Intended to be safe to run in CI
    and locally. Prefer `-DryRun` on downstream scripts to prevent side-effects.

Usage:
    pwsh preflight.ps1 -Branch main

This script performs Git and GitHub CLI checks and will throw if validation
fails. Callers should handle exceptions and present user-friendly guidance.
#>

param(
        [string]$Branch = 'main',
        [switch]$AllowDirty,
        [switch]$AllowNonMain
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-WorkspaceRoot

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    throw 'GitHub CLI not found. Install GitHub CLI and/or restart VS Code so PATH updates.'
}

Invoke-LoggedCommand -Command $gh.Source -Arguments @('auth', 'status', '-h', 'github.com') -WorkingDirectory $root

$scopeOutput = & $gh.Source auth status -t -h github.com 2>&1 | Out-String
if ($scopeOutput -notmatch '(?im)\brepo\b') {
    throw 'GitHub token is missing required repo scope for releases.'
}

$origin = Get-GitOutput -Args @('remote', 'get-url', 'origin') -WorkingDirectory $root
if (-not $origin) {
    throw 'Git origin remote is not configured.'
}

Write-Host "Preflight remote check: origin=$origin"

$currentBranch = Get-GitOutput -Args @('rev-parse', '--abbrev-ref', 'HEAD') -WorkingDirectory $root
Write-Host "Preflight branch check: current=$currentBranch expected=$Branch"
if (-not $AllowNonMain -and $currentBranch -ne $Branch) {
    throw "Publishing is restricted to branch $Branch. Current branch: $currentBranch"
}

Invoke-LoggedCommand -Command 'git' -Arguments @('fetch', 'origin', $Branch) -WorkingDirectory $root

$branchSyncCounts = Get-GitOutput -Args @('rev-list', '--left-right', '--count', ("HEAD...origin/" + $Branch)) -WorkingDirectory $root
$branchSyncParts = @($branchSyncCounts -split '\s+')
if ($branchSyncParts.Count -lt 2) {
    throw "Unable to determine sync status against origin/$Branch. Output: $branchSyncCounts"
}

$aheadCount = [int]$branchSyncParts[0]
$behindCount = [int]$branchSyncParts[1]
Write-Host "Preflight branch sync: ahead=$aheadCount behind=$behindCount"
if ($behindCount -ne 0) {
    throw "Local branch is behind origin/$Branch (ahead=$aheadCount, behind=$behindCount). Pull/rebase before publishing."
}

if ($aheadCount -ne 0) {
    Write-Host 'Preflight branch sync: ahead-only state detected; publish will push local commits.'
}

if (-not $AllowDirty) {
    $status = Get-GitOutput -Args @('status', '--porcelain') -WorkingDirectory $root
    if ($status) {
        throw "Working tree has uncommitted changes. To proceed, run the wizard with -AllowDirty or commit/stash changes before publishing."
    }
    Write-Host 'Preflight working-tree check: clean'
}
else {
    Write-Host 'Preflight working-tree check: dirty allowed by flag'
}

Write-Host "Preflight passed. origin=$origin branch=$currentBranch"

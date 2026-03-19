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

if (-not $AllowDirty) {
    $status = Get-GitOutput -Args @('status', '--porcelain') -WorkingDirectory $root
    if ($status) {
        throw 'Working tree is not clean. Commit or stash changes before publish.'
    }
    Write-Host 'Preflight working-tree check: clean'
}
else {
    Write-Host 'Preflight working-tree check: dirty allowed by flag'
}

Write-Host "Preflight passed. origin=$origin branch=$currentBranch"

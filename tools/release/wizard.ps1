<#
Interactive release wizard for local-first release orchestration.

This script is an orchestrator that delegates to the existing scripts in this
folder (build-*.ps1, publish-*.ps1, release-*.ps1, preflight.ps1, etc.). It
keeps the original scripts as the execution engines and only provides an
interactive selection, dry-run, commit synthesis, and confirmation checkpoints.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#. Load shared helpers (Invoke-LoggedCommand, Get-WorkspaceRoot, etc.)
. "$PSScriptRoot\common.ps1"

function Prompt-YesNo {
    param([string]$Message, [bool]$Default = $false)
    $defaultChar = if ($Default) { 'Y/n' } else { 'y/N' }
    while ($true) {
        $r = Read-Host "$Message ($defaultChar)"
        if ([string]::IsNullOrWhiteSpace($r)) { return $Default }
        switch ($r.ToLowerInvariant()) {
            'y' { return $true }
            'yes' { return $true }
            'n' { return $false }
            'no' { return $false }
            default { Write-Host "Please answer 'y' or 'n'." }
        }
    }
}

function Prompt-MultiSelect {
    param([string[]]$Options)
    for ($i = 0; $i -lt $Options.Length; $i++) {
        Write-Host "[$($i+1)] $($Options[$i])"
    }
    $s = Read-Host "Select tasks to run (comma-separated numbers, or 'all')"
    if ($s -eq 'all') { return 0..($Options.Length-1) }
    $indices = @()
    foreach ($part in ($s -split ',')) {
        $part = $part.Trim()
        if (-not $part) { continue }
        if ([int]::TryParse($part, [ref]$n)) {
            if ($n -ge 1 -and $n -le $Options.Length) { $indices += ($n-1) }
        }
    }
    return $indices | Sort-Object -Unique
}

function Compose-CommandForScript {
    param([string]$ScriptName)
    return @('powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $ScriptName))
}

function Run-ScriptByName {
    param(
        [string]$ScriptName,
        [bool]$DryRun = $false
    )

    $cmd = Compose-CommandForScript -ScriptName $ScriptName
    $command = $cmd[0]
    $args = $cmd[1..($cmd.Length-1)]

    if ($DryRun) {
        Write-Host "DRY-RUN: $command $($args -join ' ')"
        return
    }

    Invoke-LoggedCommand -Command $command -Arguments $args -WorkingDirectory (Get-WorkspaceRoot)
}

function Show-Header { Write-Host '=== Crispy Bills Release Wizard ===' -ForegroundColor Cyan }

Show-Header

$available = @(
    'preflight.ps1',
    'build-both.ps1',
    'build-windows.ps1',
    'build-mobile.ps1',
    'changelog.ps1',
    'version.ps1',
    'publish-both.ps1',
    'publish-windows.ps1',
    'publish-mobile.ps1',
    'release-both.ps1',
    'release-windows.ps1',
    'release-mobile.ps1',
    'recover-missing-release.ps1'
)

Write-Host "Discovered release scripts (from: $PSScriptRoot):" -ForegroundColor Yellow
$sel = Prompt-MultiSelect -Options $available
if (-not $sel -or $sel.Count -eq 0) {
    Write-Host 'No tasks selected; exiting.' -ForegroundColor Yellow
    exit 0
}

$chosen = $sel | ForEach-Object { $available[$_] }
Write-Host "Selected tasks:" -ForegroundColor Green
$chosen | ForEach-Object { Write-Host " - $_" }

$doCommit = Prompt-YesNo -Message 'Create a commit as part of the flow (will run conventional-commit.ps1 if available)?' -Default $true

$dryRun = Prompt-YesNo -Message 'Run in dry-run mode (show commands but do not execute)?' -Default $false

if ($doCommit) {
    if (Test-Path (Join-Path $PSScriptRoot 'conventional-commit.ps1')) {
        Write-Host 'Will run conventional-commit.ps1 during the flow.'
        $commitScript = 'conventional-commit.ps1'
    }
    else {
        Write-Host 'No conventional-commit.ps1 found; will prompt for a manual commit message.'
        $commitScript = $null
    }
}

if (-not (Prompt-YesNo -Message 'Proceed with the selected operations?' -Default $true)) {
    Write-Host 'Aborted by user.' -ForegroundColor Yellow
    exit 1
}

try {
    foreach ($s in $chosen) {
        Write-Host "`n=== Running: $s ===" -ForegroundColor Cyan
        Run-ScriptByName -ScriptName $s -DryRun:$dryRun
    }

    if ($doCommit) {
        Write-Host '`n=== Commit Step ===' -ForegroundColor Cyan
        if ($commitScript) {
            Run-ScriptByName -ScriptName $commitScript -DryRun:$dryRun
        }
        else {
            $msg = Read-Host 'Enter commit message (conventional style recommended)'
            if (-not $dryRun) {
                Push-Location (Get-WorkspaceRoot)
                try { & git add -A; git commit -m $msg }
                finally { Pop-Location }
            }
            else { Write-Host "DRY-RUN: git add -A && git commit -m '$msg'" }
        }
    }

    Write-TaskDiagnostics -Prefix 'Wizard'
    Write-Host '`nWizard completed.' -ForegroundColor Green
}
catch {
    Write-Host "Wizard failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-TaskDiagnostics -Prefix 'Wizard (partial)'
    exit 1
}

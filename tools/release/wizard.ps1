<#
Interactive release wizard for local-first release orchestration.

This script is an orchestrator that delegates to the existing scripts in this
folder (build-*.ps1, publish-*.ps1, release-*.ps1, preflight.ps1, etc.). It
keeps the original scripts as the execution engines and only provides an
interactive selection, dry-run, commit synthesis, and confirmation checkpoints.
#>

param(
    [string[]]$Tasks,
    [switch]$DryRun,
    [switch]$NoCommit,
    [switch]$Verbose,
    [switch]$AutoConfirm,
    [switch]$AllowDirty
)

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
    $s = Read-Host "Select tasks to run (comma-separated numbers, ranges e.g. 1-3, 'all', or 'none')"
    if ([string]::IsNullOrWhiteSpace($s)) { return @() }

    $s = $s.Trim().ToLowerInvariant()
    if ($s -eq 'all' -or $s -eq 'a') { return 0..($Options.Length-1) }
    if ($s -eq 'none' -or $s -eq 'n') { return @() }

    $indices = New-Object System.Collections.Generic.List[int]
    foreach ($part in $s -split ',') {
        $part = $part.Trim()
        if (-not $part) { continue }

        if ($part -match '^(\d+)-(\d+)$') {
            $start = [int]$Matches[1]
            $end = [int]$Matches[2]
            if ($start -le 0 -or $end -le 0 -or $start -gt $end) {
                Write-Host "Ignoring invalid range '$part'" -ForegroundColor Yellow
                continue
            }
            for ($n = $start; $n -le $end; $n++) {
                if ($n -ge 1 -and $n -le $Options.Length) { $indices.Add($n - 1) }
            }
            continue
        }

        $n = 0
        if ([int]::TryParse($part, [ref]$n)) {
            if ($n -ge 1 -and $n -le $Options.Length) {
                $indices.Add($n - 1)
            } else {
                Write-Host "Ignoring out-of-range selection '$part'" -ForegroundColor Yellow
            }
        } else {
            Write-Host "Ignoring invalid selection '$part'" -ForegroundColor Yellow
        }
    }

    return $indices | Sort-Object -Unique
}

function Get-ShellCommand {
    if (Get-Command pwsh -ErrorAction SilentlyContinue) { return 'pwsh' }
    if (Get-Command powershell -ErrorAction SilentlyContinue) { return 'powershell' }
    throw 'No PowerShell executable found (pwsh or powershell).'
}

function Compose-CommandForScript {
    param([string]$ScriptName)
    $shell = Get-ShellCommand
    return @($shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $ScriptName))
}

function Run-ScriptByName {
    param(
        [string]$ScriptName,
        [bool]$DryRun = $false,
        [string[]]$ExtraArgs
    )

    $scriptPath = Join-Path $PSScriptRoot $ScriptName
    if (-not (Test-Path $scriptPath)) {
        throw "Script not found: $scriptPath"
    }

    $cmd = Compose-CommandForScript -ScriptName $ScriptName
    $command = $cmd[0]
    $args = $cmd[1..($cmd.Length-1)]
    if ($ExtraArgs) { $args += $ExtraArgs }

    if ($DryRun) {
        Write-Host "DRY-RUN: $command $($args -join ' ')"
        return
    }

    Invoke-LoggedCommand -Command $command -Arguments $args -WorkingDirectory (Get-WorkspaceRoot)
}

function Show-Header { Write-Host '=== Crispy_Bills Release Wizard ===' -ForegroundColor Cyan }

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
$chosen = @()

if ($Tasks -and $Tasks.Count -gt 0) {
    $lowerAvailable = $available | ForEach-Object { $_.ToLowerInvariant() }
    foreach ($t in $Tasks) {
        $ttrim = $t.Trim()
        if ($lowerAvailable -contains $ttrim.ToLowerInvariant()) {
            $chosen += $available[$lowerAvailable.IndexOf($ttrim.ToLowerInvariant())]
        } else {
            Write-Host "Unknown task specified: $ttrim" -ForegroundColor Yellow
        }
    }

    if ($chosen.Count -eq 0) {
        Write-Host 'No valid tasks provided via -Tasks; exiting.' -ForegroundColor Yellow
        exit 0
    }
}
else {
    $sel = @((Prompt-MultiSelect -Options $available))
    if ($sel.Count -eq 0) {
        Write-Host 'No tasks selected; exiting.' -ForegroundColor Yellow
        exit 0
    }
    $chosen = $sel | ForEach-Object { $available[$_] }
}

Write-Host "Selected tasks:" -ForegroundColor Green
$chosen | ForEach-Object { Write-Host " - $_" }

$doCommit = Prompt-YesNo -Message 'Create a commit as part of the flow (will run conventional-commit.ps1 if available)?' -Default $true

$dryRun = Prompt-YesNo -Message 'Run in dry-run mode (show commands but do not execute)?' -Default $false

if (-not $AllowDirty) {
    $AllowDirty = Prompt-YesNo -Message 'Allow dirty working tree for preflight (skip clean check)?' -Default $false
}

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
        $extraArgs = @()
        if ($s -eq 'preflight.ps1' -and $AllowDirty) { $extraArgs += '-AllowDirty' }
        Run-ScriptByName -ScriptName $s -DryRun:$dryRun -ExtraArgs $extraArgs
    }

    if ($doCommit) {
        Write-Host "`n=== Commit Step ===" -ForegroundColor Cyan
        if ($commitScript) {
            Run-ScriptByName -ScriptName $commitScript -DryRun:$dryRun
        }
        else {
            if ($dryRun) {
                Write-Host "DRY-RUN: git add -A && git commit -m '<message>'"
            }
            else {
                do {
                    $msg = Read-Host 'Enter commit message (conventional style recommended, or type SKIP to skip commit)'
                    if ($msg -eq 'SKIP') {
                        Write-Host 'Skipping commit step.' -ForegroundColor Yellow
                        break
                    }

                    if ([string]::IsNullOrWhiteSpace($msg)) {
                        Write-Host 'Commit message cannot be empty unless SKIP is entered.' -ForegroundColor Yellow
                    }
                } while ([string]::IsNullOrWhiteSpace($msg))

                if ($msg -and $msg -ne 'SKIP') {
                    Push-Location (Get-WorkspaceRoot)
                    try { & git add -A; git commit -m $msg }
                    finally { Pop-Location }
                }
            }
        }
    }

    if (Get-Command Write-TaskDiagnostics -ErrorAction SilentlyContinue) {
        Write-TaskDiagnostics -Prefix 'Wizard'
    } else {
        Write-Host 'Warning: Write-TaskDiagnostics not available.' -ForegroundColor Yellow
    }
    Write-Host "`nWizard completed." -ForegroundColor Green
}
catch {
    Write-Host "Wizard failed: $($_.Exception.Message)" -ForegroundColor Red
    if (Get-Command Write-TaskDiagnostics -ErrorAction SilentlyContinue) {
        Write-TaskDiagnostics -Prefix 'Wizard (partial)'
    } else {
        Write-Host 'Warning: Write-TaskDiagnostics not available.' -ForegroundColor Yellow
    }
    exit 1
}

param(
    [ValidateSet('feat','fix','perf','refactor','docs','test','build','ci','chore')] [string]$Type,
    [string]$Scope,
    [string]$Description,
    [string]$Body,
    [switch]$Breaking,
    [switch]$Auto,
    [switch]$DryRun,
    [switch]$SkipPrompt,
    [switch]$NonInteractive,
    [string]$ResponsesFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$helpersPath = Join-Path $PSScriptRoot 'prompt-helpers.ps1'
if (Test-Path $helpersPath) {
    . $helpersPath
    if (Get-Command -Name Initialize-ReleasePromptContext -ErrorAction SilentlyContinue) {
        Initialize-ReleasePromptContext -ResponsesFile $ResponsesFile -NonInteractive:$NonInteractive
    }
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $root
try {
    $null = Invoke-GitMergedOutput -Arguments @('rev-parse', '--is-inside-work-tree')
    if ($LASTEXITCODE -ne 0) {
<#
Helper to build a conventional commit message interactively.

This utility collects `type(scope): description` style input from the user and
prints a commit subject/body that other release tools can consume. It is
primarily intended for local interactive use and for the wizard to surface a
recommended commit message.
#>
        throw 'This script must be run inside a git repository.'
    }

    $types = @(
        [PSCustomObject]@{ Key = 'feat'; Description = 'New feature (minor bump)' },
        [PSCustomObject]@{ Key = 'fix'; Description = 'Bug fix (patch bump)' },
        [PSCustomObject]@{ Key = 'perf'; Description = 'Performance improvement (patch bump)' },
        [PSCustomObject]@{ Key = 'refactor'; Description = 'Internal code change without user-visible behavior change (patch bump)' },
        [PSCustomObject]@{ Key = 'docs'; Description = 'Documentation only changes (patch bump if released)' },
        [PSCustomObject]@{ Key = 'test'; Description = 'Tests only changes (patch bump if released)' },
        [PSCustomObject]@{ Key = 'build'; Description = 'Build system or dependency changes (patch bump if released)' },
        [PSCustomObject]@{ Key = 'ci'; Description = 'CI/CD pipeline changes (patch bump if released)' },
        [PSCustomObject]@{ Key = 'chore'; Description = 'Maintenance tasks (patch bump if released)' }
    )

    if ($Auto -and -not $Type) { $Type = 'chore' }
    if (-not $Type) {
        if (-not (Test-IsInteractive)) {
            $respType = Get-Response -ScriptName 'conventional-commit' -Key 'Type' -Default $null
            if ($null -ne $respType -and $types.Key -contains $respType) { $Type = $respType }
            else { $Type = 'chore' }
        }
        elseif (Get-Command -Name Prompt-Select -ErrorAction SilentlyContinue) {
            $Type = Prompt-Select -PromptText 'Select commit type:' -Options $types.Key -ScriptName 'conventional-commit' -Key 'Type' -DefaultIndex 8
        }
        else {
            Write-Host ''
            Write-Host 'Select commit type:'
            for ($i = 0; $i -lt $types.Count; $i++) {
                Write-Host ("[{0}] {1} - {2}" -f ($i + 1), $types[$i].Key, $types[$i].Description)
            }

            $selectedType = $null
            while (-not $selectedType) {
                $selection = Read-Host 'Type number'
                $num = 0
                if ([int]::TryParse($selection, [ref]$num) -and $num -ge 1 -and $num -le $types.Count) {
                    $selectedType = $types[$num - 1].Key
                }
                else {
                    Write-Host 'Invalid selection. Enter a valid number.' -ForegroundColor Yellow
                }
            }

            $Type = $selectedType
        }
    }

    if ($Auto -and -not $Description) { $Description = 'automated release commit' }
    if (-not $Description) {
        if (-not (Test-IsInteractive)) {
            $Description = Get-Response -ScriptName 'conventional-commit' -Key 'Description' -Default $Description
        }
        elseif (Get-Command -Name Prompt-Text -ErrorAction SilentlyContinue) {
            $Description = (Prompt-Text -PromptText 'Commit description (required)' -ScriptName 'conventional-commit' -Key 'Description' -Default '').Trim()
            while ([string]::IsNullOrWhiteSpace($Description)) {
                Write-Host 'Description cannot be empty.' -ForegroundColor Yellow
                $Description = (Prompt-Text -PromptText 'Commit description (required)' -ScriptName 'conventional-commit' -Key 'Description' -Default '').Trim()
            }
        }
        else {
            $Description = (Read-Host 'Commit description (required)').Trim()
            while ([string]::IsNullOrWhiteSpace($Description)) {
                Write-Host 'Description cannot be empty.' -ForegroundColor Yellow
                $Description = (Read-Host 'Commit description (required)').Trim()
            }
        }
    }

    if ($Auto -and -not $Scope) { $Scope = '' }
    if (-not $Scope -and !$Auto -and !$SkipPrompt) {
        if (-not (Test-IsInteractive)) {
            $Scope = Get-Response -ScriptName 'conventional-commit' -Key 'Scope' -Default $Scope
        }
        elseif (Get-Command -Name Prompt-Text -ErrorAction SilentlyContinue) {
            $Scope = (Prompt-Text -PromptText 'Optional scope (example: billing, mobile, release)' -ScriptName 'conventional-commit' -Key 'Scope' -Default '').Trim()
        }
        else { $Scope = (Read-Host 'Optional scope (example: billing, mobile, release)').Trim() }
    }

    if ($Auto -and -not $Body) { $Body = '' }
    if (-not $Body -and !$Auto) {
        if (-not (Test-IsInteractive)) {
            $Body = Get-Response -ScriptName 'conventional-commit' -Key 'Body' -Default $Body
        }
        elseif (Get-Command -Name Prompt-Text -ErrorAction SilentlyContinue) {
            $Body = (Prompt-Text -PromptText 'Optional commit body (single line, press Enter to skip)' -ScriptName 'conventional-commit' -Key 'Body' -Default '').Trim()
        }
        else { $Body = (Read-Host 'Optional commit body (single line, press Enter to skip)').Trim() }
    }

    if ($Auto -and -not $Breaking) { $isBreaking = $false } else { $isBreaking = $Breaking }

    $breakingNote = ''
    if ($isBreaking) {
        Write-Host 'Breaking changes trigger a major release and require explicit approval before publish.' -ForegroundColor Yellow
        if ($Auto) {
            $breakingNote = 'Automated BREAKING CHANGE generated by release wizard.'
        }
        elseif ($SkipPrompt) {
            $breakingNote = 'BREAKING CHANGE (details in release wizard metadata).'
        }
        else {
            if (-not (Test-IsInteractive)) {
                $breakingNote = Get-Response -ScriptName 'conventional-commit' -Key 'BreakingNote' -Default ''
            }
            elseif (Get-Command -Name Prompt-Text -ErrorAction SilentlyContinue) {
                $breakingNote = (Prompt-Text -PromptText 'BREAKING CHANGE note (required)' -ScriptName 'conventional-commit' -Key 'BreakingNote' -Default '').Trim()
                while ([string]::IsNullOrWhiteSpace($breakingNote)) {
                    $breakingNote = (Prompt-Text -PromptText 'Please provide BREAKING CHANGE note' -ScriptName 'conventional-commit' -Key 'BreakingNote' -Default '').Trim()
                }
            }
            else {
                $breakingNote = (Read-Host 'BREAKING CHANGE note (required)').Trim()
                while ([string]::IsNullOrWhiteSpace($breakingNote)) {
                    $breakingNote = (Read-Host 'Please provide BREAKING CHANGE note').Trim()
                }
            }
        }
    }

    $header = $Type
    if (-not [string]::IsNullOrWhiteSpace($Scope)) {
        $header += "($Scope)"
    }
    if ($isBreaking) { $header += '!' }
    $header += ": $Description"

    Write-Host ''
    Write-Host 'Generated commit message:' -ForegroundColor Cyan
    Write-Host $header
    if (-not [string]::IsNullOrWhiteSpace($Body)) {
        Write-Host ''
        Write-Host $Body
    }
    if ($isBreaking) {
        Write-Host ''
        Write-Host ("BREAKING CHANGE: {0}" -f $breakingNote)
    }

    $stageAll = $true
    if (-not ($Auto -or $SkipPrompt)) {
        if (Get-Command -Name Prompt-YesNo -ErrorAction SilentlyContinue) {
            $stageAll = Prompt-YesNo 'Stage all changes with git add -A? (Y/n)' 'conventional-commit' 'StageAll' $true
        }
        else {
            $stageInput = (Read-Host 'Stage all changes with git add -A? (Y/n)').Trim().ToLowerInvariant()
            $stageAll = -not ($stageInput -eq 'n' -or $stageInput -eq 'no')
        }
    }

    if ($stageAll) {
        $null = Invoke-GitMergedOutput -Arguments @('add', '-A')
        if ($LASTEXITCODE -ne 0) { throw 'git add -A failed.' }
    }

    $staged = (Invoke-GitMergedOutput -Arguments @('diff', '--cached', '--name-only') | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($staged)) {
        throw 'No staged changes found. Stage files and re-run the script.'
    }

    Write-Host ''
    Write-Host 'Staged files:' -ForegroundColor Cyan
    Write-Host $staged

    if (-not ($Auto -or $SkipPrompt)) {
        if (Get-Command -Name Prompt-YesNo -ErrorAction SilentlyContinue) {
            $confirm = Prompt-YesNo 'Create commit now? (Y/n)' 'conventional-commit' 'Confirm' $true
            if (-not $confirm) { Write-Host 'Commit canceled.' -ForegroundColor Yellow; exit 0 }
        }
        else {
            $confirmInput = (Read-Host 'Create commit now? (Y/n)').Trim().ToLowerInvariant()
            $confirm = -not ($confirmInput -eq 'n' -or $confirmInput -eq 'no')
            if (-not $confirm) { Write-Host 'Commit canceled.' -ForegroundColor Yellow; exit 0 }
        }
    }

    if ($DryRun) {
        Write-Host "DRY-RUN: git commit -m '$header'"
        exit 0
    }

    $commitArgs = @('commit', '-m', $header)
    if (-not [string]::IsNullOrWhiteSpace($Body)) { $commitArgs += @('-m', $Body) }
    if ($isBreaking) { $commitArgs += @('-m', ("BREAKING CHANGE: {0}" -f $breakingNote)) }

    $commitOutput = Invoke-GitMergedOutput -Arguments $commitArgs
    if ($LASTEXITCODE -ne 0) {
        $detail = ($commitOutput | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($detail)) {
            throw "git commit failed (exit code $LASTEXITCODE)."
        }
        throw "git commit failed (exit code $LASTEXITCODE): $detail"
    }

    Write-Host 'Conventional commit created successfully.' -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}

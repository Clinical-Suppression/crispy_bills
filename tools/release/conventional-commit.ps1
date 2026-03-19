Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $root
try {
    & git rev-parse --is-inside-work-tree *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'This script must be run inside a git repository.'
    }

    $types = @(
        [PSCustomObject]@{ Key = 'feat'; Description = 'New feature (minor bump)' },
        [PSCustomObject]@{ Key = 'fix'; Description = 'Bug fix (patch bump)' },
        [PSCustomObject]@{ Key = 'perf'; Description = 'Performance improvement (patch bump)' },
        [PSCustomObject]@{ Key = 'refactor'; Description = 'Code change without behavior change' },
        [PSCustomObject]@{ Key = 'docs'; Description = 'Documentation only changes' },
        [PSCustomObject]@{ Key = 'test'; Description = 'Tests only changes' },
        [PSCustomObject]@{ Key = 'build'; Description = 'Build system or dependency changes' },
        [PSCustomObject]@{ Key = 'ci'; Description = 'CI/CD pipeline changes' },
        [PSCustomObject]@{ Key = 'chore'; Description = 'Maintenance tasks' }
    )

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

    $scope = (Read-Host 'Optional scope (example: billing, mobile, release)').Trim()

    $description = ''
    while ([string]::IsNullOrWhiteSpace($description)) {
        $description = (Read-Host 'Commit description (required)').Trim()
        if ([string]::IsNullOrWhiteSpace($description)) {
            Write-Host 'Description cannot be empty.' -ForegroundColor Yellow
        }
    }

    $body = (Read-Host 'Optional commit body (single line, press Enter to skip)').Trim()

    $breakingInput = (Read-Host 'Breaking change? (y/N)').Trim().ToLowerInvariant()
    $isBreaking = $breakingInput -eq 'y' -or $breakingInput -eq 'yes'

    $breakingNote = ''
    if ($isBreaking) {
        $breakingNote = (Read-Host 'BREAKING CHANGE note (required)').Trim()
        while ([string]::IsNullOrWhiteSpace($breakingNote)) {
            $breakingNote = (Read-Host 'Please provide BREAKING CHANGE note').Trim()
        }
    }

    $header = $selectedType
    if (-not [string]::IsNullOrWhiteSpace($scope)) {
        $header += "($scope)"
    }
    if ($isBreaking) {
        $header += '!'
    }
    $header += ": $description"

    Write-Host ''
    Write-Host 'Generated commit message:' -ForegroundColor Cyan
    Write-Host $header
    if (-not [string]::IsNullOrWhiteSpace($body)) {
        Write-Host ''
        Write-Host $body
    }
    if ($isBreaking) {
        Write-Host ''
        Write-Host ("BREAKING CHANGE: {0}" -f $breakingNote)
    }

    $stageInput = (Read-Host 'Stage all changes with git add -A? (Y/n)').Trim().ToLowerInvariant()
    $stageAll = -not ($stageInput -eq 'n' -or $stageInput -eq 'no')

    if ($stageAll) {
        & git add -A
        if ($LASTEXITCODE -ne 0) {
            throw 'git add -A failed.'
        }
    }

    $staged = (& git diff --cached --name-only | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($staged)) {
        throw 'No staged changes found. Stage files and re-run the script.'
    }

    Write-Host ''
    Write-Host 'Staged files:' -ForegroundColor Cyan
    Write-Host $staged

    $confirmInput = (Read-Host 'Create commit now? (Y/n)').Trim().ToLowerInvariant()
    $confirm = -not ($confirmInput -eq 'n' -or $confirmInput -eq 'no')
    if (-not $confirm) {
        Write-Host 'Commit canceled.' -ForegroundColor Yellow
        exit 0
    }

    $args = @('commit', '-m', $header)
    if (-not [string]::IsNullOrWhiteSpace($body)) {
        $args += @('-m', $body)
    }
    if ($isBreaking) {
        $args += @('-m', ("BREAKING CHANGE: {0}" -f $breakingNote))
    }

    & git @args
    if ($LASTEXITCODE -ne 0) {
        throw 'git commit failed.'
    }

    Write-Host 'Conventional commit created successfully.' -ForegroundColor Green
}
finally {
    Pop-Location
}

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PreviousTag,
    [string]$OutNotesFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Parse-CommitRecords {
    param([string]$Range)

    $args = @('log')
    if ($Range) {
        $args += $Range
    }

    $args += '--pretty=format:%H%x1f%s%x1f%b%x1e'
    $raw = Get-GitOutput -Args $args

    if (-not $raw) {
        return @()
    }

    $records = @()
    $entries = $raw -split [char]0x1e
    foreach ($entry in $entries) {
        if (-not $entry.Trim()) { continue }

        $parts = $entry -split [char]0x1f
        if ($parts.Count -lt 3) { continue }

        $records += [PSCustomObject]@{
            Hash = $parts[0].Trim()
            Subject = $parts[1].Trim()
            Body = $parts[2].Trim()
        }
    }

    return @($records)
}

function Get-CommitGroup {
    param([string]$Subject, [string]$Body)

    if ($Subject -match '^[a-zA-Z]+(\([^)]+\))?!:' -or $Body -match '(?im)^BREAKING CHANGE:') {
        return 'Breaking Changes'
    }

    if ($Subject -match '^feat(\([^)]+\))?:') { return 'Features' }
    if ($Subject -match '^fix(\([^)]+\))?:') { return 'Fixes' }
    if ($Subject -match '^perf(\([^)]+\))?:') { return 'Performance' }
    if ($Subject -match '^refactor(\([^)]+\))?:') { return 'Refactoring' }
    if ($Subject -match '^docs(\([^)]+\))?:') { return 'Documentation' }
    if ($Subject -match '^test(\([^)]+\))?:') { return 'Tests' }
    if ($Subject -match '^build(\([^)]+\))?:') { return 'Build' }
    if ($Subject -match '^ci(\([^)]+\))?:') { return 'CI' }
    if ($Subject -match '^chore(\([^)]+\))?:') { return 'Chores' }
    return 'Other'
}

function Build-ReleaseNotes {
    param(
        [string]$Version,
        [object[]]$Commits
    )

    $today = Get-Date -Format 'yyyy-MM-dd'
    $groups = [ordered]@{}
    foreach ($c in $Commits) {
        $group = Get-CommitGroup -Subject $c.Subject -Body $c.Body
        if (-not $groups.Contains($group)) {
            $groups[$group] = New-Object System.Collections.Generic.List[object]
        }

        $groups[$group].Add($c)
    }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("## $Version - $today")
    [void]$sb.AppendLine('')

    foreach ($groupName in $groups.Keys) {
        [void]$sb.AppendLine("### $groupName")
        foreach ($c in $groups[$groupName]) {
            $short = if ($c.Hash.Length -ge 7) { $c.Hash.Substring(0, 7) } else { $c.Hash }
            [void]$sb.AppendLine("- $($c.Subject) ($short)")
        }
        [void]$sb.AppendLine('')
    }

    return $sb.ToString().TrimEnd()
}

$root = Get-WorkspaceRoot
$range = if ($PreviousTag) { "$PreviousTag..HEAD" } else { 'HEAD' }
$commits = @(Parse-CommitRecords -Range $range)
if (@($commits).Count -eq 0) {
    throw 'No commits found to include in changelog.'
}

$notes = Build-ReleaseNotes -Version $Version -Commits $commits

$changelogPath = Join-Path $root 'CHANGELOG.md'
$header = "# Changelog`r`n`r`nAll notable changes to this project will be documented in this file.`r`n`r`n"
if (-not (Test-Path $changelogPath)) {
    Set-Content -Path $changelogPath -Value $header -Encoding UTF8
}

$existing = Get-Content -Path $changelogPath -Raw
$escapedVersion = [Regex]::Escape($Version)
$sectionPattern = "(?ms)^##\s+$escapedVersion\s+-\s+\d{4}-\d{2}-\d{2}\s*\r?\n.*?(?=^##\s+|\z)"
$replacement = $notes + "`r`n`r`n"

if ([Regex]::IsMatch($existing, $sectionPattern)) {
    $updated = [Regex]::Replace($existing, $sectionPattern, $replacement)
}
else {
    if ($existing -notmatch '^# Changelog') {
        $existing = $header + $existing
    }

    $updated = $existing.TrimEnd() + "`r`n`r`n" + $notes + "`r`n"
}

Set-Content -Path $changelogPath -Value $updated -Encoding UTF8

if ($OutNotesFile) {
    Set-Content -Path $OutNotesFile -Value $notes -Encoding UTF8
}

$notes

param(
    [string]$OutFile,
    [switch]$AllowNoCommits
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Get-LatestVersionTag {
    $tagsText = Get-GitOutput -Args @('tag', '--list', 'v*')
    if (-not $tagsText) {
        return $null
    }

    $tags = $tagsText -split "`r?`n" | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' }
    if (-not $tags -or $tags.Count -eq 0) {
        return $null
    }

    $sorted = $tags | Sort-Object {
        $parts = $_.TrimStart('v').Split('.')
        [Version]::new([int]$parts[0], [int]$parts[1], [int]$parts[2])
    }

    return $sorted[-1]
}

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

function Get-BumpLevel {
    param([object[]]$Commits)

    $level = 'patch'
    foreach ($commit in $Commits) {
        $subject = $commit.Subject
        $body = $commit.Body

        if ($subject -match '^[a-zA-Z]+(\([^)]+\))?!:' -or $body -match '(?im)^BREAKING CHANGE:') {
            return 'major'
        }

        if ($subject -match '^feat(\([^)]+\))?:') {
            if ($level -ne 'major') {
                $level = 'minor'
            }
            continue
        }

        if ($subject -match '^(fix|perf|refactor)(\([^)]+\))?:') {
            if ($level -eq 'patch') {
                $level = 'patch'
            }
        }
    }

    return $level
}

function Get-NextVersion {
    param(
        [string]$Current,
        [string]$Level
    )

    if ($Current) {
        $v = [Version]::Parse($Current.TrimStart('v'))
        $major = $v.Major
        $minor = $v.Minor
        $patch = $v.Build
    }
    else {
        $major = 0
        $minor = 0
        $patch = 0
    }

    switch ($Level) {
        'major' { $major += 1; $minor = 0; $patch = 0 }
        'minor' { $minor += 1; $patch = 0 }
        default { $patch += 1 }
    }

    return "v$major.$minor.$patch"
}

$latestTag = Get-LatestVersionTag
$range = if ($latestTag) { "$latestTag..HEAD" } else { $null }
$commits = @(Parse-CommitRecords -Range $range)

if (@($commits).Count -eq 0) {
    if (-not $AllowNoCommits) {
        throw 'No new commits found to version. Create at least one commit before publish.'
    }

    $noChangeResult = [PSCustomObject]@{
        HasChanges = $false
        CurrentTag = $latestTag
        NextTag = $null
        Version = $null
        Bump = 'none'
        CommitCount = 0
    }

    if ($OutFile) {
        Write-JsonFile -Object $noChangeResult -Path $OutFile
    }

    $noChangeResult | ConvertTo-Json -Depth 5
    return
}

$bump = Get-BumpLevel -Commits $commits
$nextTag = Get-NextVersion -Current $latestTag -Level $bump
$version = $nextTag.TrimStart('v')

$result = [PSCustomObject]@{
    HasChanges = $true
    CurrentTag = $latestTag
    NextTag = $nextTag
    Version = $version
    Bump = $bump
    CommitCount = @($commits).Count
}

if ($OutFile) {
    Write-JsonFile -Object $result -Path $OutFile
}

$result | ConvertTo-Json -Depth 5

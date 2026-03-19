param(
    [string]$Tag,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Test-GhReleaseExists {
    param(
        [Parameter(Mandatory = $true)][string]$GhCommand,
        [Parameter(Mandatory = $true)][string]$ReleaseTag,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        $output = & $GhCommand 'release' 'view' $ReleaseTag 2>&1
        if ($LASTEXITCODE -eq 0) {
            return $true
        }

        $outputText = ($output | Out-String).Trim()
        if ($outputText -match '(?i)release not found|not found') {
            return $false
        }

        throw "gh release view $ReleaseTag failed: $outputText"
    }
    finally {
        Pop-Location
    }
}

function Get-LatestMissingReleaseTag {
    param(
        [Parameter(Mandatory = $true)][string]$GhCommand,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $raw = Get-GitOutput -Args @('tag', '--list', 'v*', '--sort=-v:refname') -WorkingDirectory $WorkingDirectory
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    foreach ($candidate in ($raw -split "`r?`n")) {
        $tagName = $candidate.Trim()
        if ([string]::IsNullOrWhiteSpace($tagName)) {
            continue
        }

        if (-not (Test-GhReleaseExists -GhCommand $GhCommand -ReleaseTag $tagName -WorkingDirectory $WorkingDirectory)) {
            return $tagName
        }
    }

    return $null
}

function Resolve-ArtifactPaths {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspaceRoot,
        [Parameter(Mandatory = $true)][string]$ReleaseTag
    )

    if ($ReleaseTag.Length -lt 2 -or -not $ReleaseTag.StartsWith('v')) {
        throw "Release tag must start with 'v' (example: v1.3.1). Received: $ReleaseTag"
    }

    $version = $ReleaseTag.Substring(1)
    $releaseRoot = Join-Path $WorkspaceRoot (Join-Path 'publish\releases' $version)

    $windowsExe = Join-Path $releaseRoot (Join-Path 'windows' ("crispybills-" + $ReleaseTag + '-win-x64.exe'))
    $androidApk = Join-Path $releaseRoot (Join-Path 'mobile' ("crispybills-" + $ReleaseTag + '-android.apk'))

    $resolved = New-Object System.Collections.Generic.List[string]
    if (Test-Path $windowsExe) { $resolved.Add($windowsExe) }
    if (Test-Path $androidApk) { $resolved.Add($androidApk) }

    if ($resolved.Count -eq 0) {
        throw "No known release artifacts were found for $ReleaseTag under $releaseRoot"
    }

    return @($resolved)
}

$root = Get-WorkspaceRoot
$logsRoot = Join-Path $root 'publish\logs'

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    throw 'GitHub CLI not found. Cannot recover release.'
}

Invoke-LoggedCommand -Command 'git' -Arguments @('fetch', '--tags', 'origin') -WorkingDirectory $root

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = Get-LatestMissingReleaseTag -GhCommand $gh.Source -WorkingDirectory $root
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        Write-Host 'No missing GitHub releases found for existing tags.'
        return
    }

    Write-Host "Detected latest missing release tag: $Tag"
}

if (Test-GhReleaseExists -GhCommand $gh.Source -ReleaseTag $Tag -WorkingDirectory $root) {
    Write-Host "GitHub release already exists for $Tag. Nothing to recover."
    return
}

$notesPath = Join-Path $logsRoot ("release-notes-" + $Tag + '.md')
if (-not (Test-Path $notesPath)) {
    throw "Release notes file not found for ${Tag}: $notesPath"
}

$artifacts = Resolve-ArtifactPaths -WorkspaceRoot $root -ReleaseTag $Tag

if ($DryRun) {
    Write-Host "Dry run: would create GitHub release $Tag with notes file: $notesPath"
    Write-Host 'Dry run artifacts:'
    foreach ($artifact in $artifacts) {
        Write-Host " - $artifact"
    }
    return
}

$createArgs = @('release', 'create', $Tag)
$createArgs += $artifacts
$createArgs += @('--title', $Tag, '--notes-file', $notesPath)
Invoke-LoggedCommand -Command $gh.Source -Arguments $createArgs -WorkingDirectory $root

Invoke-LoggedCommand -Command $gh.Source -Arguments @('release', 'view', $Tag, '--json', 'tagName,name,isDraft,assets,url') -WorkingDirectory $root
Write-Host "Recovered GitHub release for $Tag"
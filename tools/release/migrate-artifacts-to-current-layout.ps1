<#
.SYNOPSIS
Move legacy artifact trees into the current artifacts/ layout (safe repeat).

.DESCRIPTION
Handles:
  - Repo-root publish/          -> same mapping as migrate-publish-to-artifacts.ps1 (invoked if present)
  - artifacts/ci/win-x64       -> artifacts/publish/ci/win-x64
  - artifacts/ci/net9.0-android -> artifacts/publish/ci/net9.0-android
  - artifacts/ci/<other>       -> artifacts/publish/ci/<other>
  - artifacts/local/windows-debug -> artifacts/build/windows/Debug
  - artifacts/local/android-debug -> artifacts/build/android/Debug
  - artifacts/local/net9.0-android -> artifacts/publish/ci/net9.0-android
  - Other artifacts/local/* (except from-publish-misc, legacy-unclassified) ->
      artifacts/local/legacy-unclassified/<name>

File name clashes at the destination: renamed with _from_legacy_<utcstamp>.

.PARAMETER DryRun
List planned operations only.

.PARAMETER WorkspaceRoot
Repository root (default: inferred from this script).
#>
param(
    [switch]$DryRun,
    [string]$WorkspaceRoot,
    [switch]$SkipPublishFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Get-WorkspaceRoot
}

$artifactsRoot = Join-Path $WorkspaceRoot 'artifacts'
$publishRoot = Join-Path $WorkspaceRoot 'publish'
$ciRoot = Join-Path $artifactsRoot 'ci'
$localRoot = Join-Path $artifactsRoot 'local'

function Ensure-Dir {
    param([string]$Path)
    if ($DryRun) { return }
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Get-UniqueDestPath {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$FileName
    )
    $base = [System.IO.Path]::GetFileNameWithoutExtension($FileName)
    $ext = [System.IO.Path]::GetExtension($FileName)
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    $candidate = Join-Path $Directory $FileName
    if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    $i = 0
    while ($true) {
        $suffix = if ($i -eq 0) { "_from_legacy_$stamp" } else { "_from_legacy_${stamp}_$i" }
        $candidate = Join-Path $Directory ($base + $suffix + $ext)
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
        $i++
    }
}

function Move-MergeTree {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestRoot
    )

    if (-not (Test-Path -LiteralPath $SourceRoot)) { return }

    $items = @(Get-ChildItem -LiteralPath $SourceRoot -Force)
    foreach ($item in $items) {
        $destPath = Join-Path $DestRoot $item.Name
        if ($item.PSIsContainer) {
            if (Test-Path -LiteralPath $destPath) {
                Write-Host ("  [merge dir] {0} into {1}" -f $item.Name, $destPath)
                if (-not $DryRun) {
                    Move-MergeTree -SourceRoot $item.FullName -DestRoot $destPath
                    Remove-Item -LiteralPath $item.FullName -Force -Recurse -ErrorAction SilentlyContinue
                }
                else {
                    Move-MergeTree -SourceRoot $item.FullName -DestRoot $destPath
                }
            }
            else {
                Write-Host ("  [dir]  {0} -> {1}" -f $item.Name, $destPath)
                if (-not $DryRun) {
                    Ensure-Dir -Path $DestRoot
                    Move-Item -LiteralPath $item.FullName -Destination $destPath
                }
            }
        }
        else {
            $finalDest = if ((Test-Path -LiteralPath $destPath) -and -not $DryRun) {
                Get-UniqueDestPath -Directory $DestRoot -FileName $item.Name
            }
            elseif ((Test-Path -LiteralPath $destPath) -and $DryRun) {
                Join-Path $DestRoot ([System.IO.Path]::GetFileNameWithoutExtension($item.Name) + '_from_legacy_<ts>' + [System.IO.Path]::GetExtension($item.Name))
            }
            else {
                $destPath
            }
            Write-Host ("  [file] {0} -> {1}" -f $item.Name, $finalDest)
            if (-not $DryRun) {
                Ensure-Dir -Path $DestRoot
                Move-Item -LiteralPath $item.FullName -Destination $finalDest -Force
            }
        }
    }
}

function Move-FolderContents {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestDir,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $SourceDir)) {
        Write-Host "Skip $Label (missing): $SourceDir" -ForegroundColor DarkGray
        return
    }

    Write-Host "`n=== $Label ===" -ForegroundColor Cyan
    Write-Host "  $SourceDir  -->  $DestDir"
    if (-not $DryRun) {
        Ensure-Dir -Path $DestDir
    }
    Move-MergeTree -SourceRoot $SourceDir -DestRoot $DestDir
    if (-not $DryRun) {
        $left = @(Get-ChildItem -LiteralPath $SourceDir -Force -ErrorAction SilentlyContinue)
        if ($left.Count -eq 0) {
            Remove-Item -LiteralPath $SourceDir -Force -Recurse -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "Workspace: $WorkspaceRoot" -ForegroundColor Green
Write-Host "Migrating legacy artifact paths to current layout (DryRun=$DryRun)" -ForegroundColor Green

if (-not (Test-Path -LiteralPath $artifactsRoot)) {
    Write-Host "No artifacts/ folder — nothing to do." -ForegroundColor Yellow
    exit 0
}

if (-not $SkipPublishFolder -and (Test-Path -LiteralPath $publishRoot)) {
    Write-Host "`n=== Repo-root publish/ (delegate) ===" -ForegroundColor Cyan
    $mp = Join-Path $PSScriptRoot 'migrate-publish-to-artifacts.ps1'
    if ($DryRun) {
        & $mp -DryRun -WorkspaceRoot $WorkspaceRoot
    }
    else {
        & $mp -WorkspaceRoot $WorkspaceRoot
    }
}

$publishCi = Get-ArtifactPublishCiRoot
$winDebug = Get-ArtifactBuildWindowsDir -Configuration Debug
$droidDebug = Get-ArtifactBuildAndroidDir -Configuration Debug
$legacyBucket = Join-Path $localRoot 'legacy-unclassified'

Ensure-Dir -Path (Get-ArtifactBuildRoot)
Ensure-Dir -Path (Get-ArtifactPublishRoot)

if (Test-Path -LiteralPath $ciRoot) {
    $ciChildren = @(Get-ChildItem -LiteralPath $ciRoot -Force -ErrorAction SilentlyContinue)
    foreach ($ch in $ciChildren) {
        $dest = Join-Path $publishCi $ch.Name
        if ($ch.PSIsContainer) {
            Move-FolderContents -SourceDir $ch.FullName -DestDir $dest -Label ("artifacts/ci/{0} -> publish/ci/{0}" -f $ch.Name)
        }
        else {
            Write-Host "`n=== artifacts/ci file -> publish/ci ===" -ForegroundColor Cyan
            Write-Host ("  {0} -> {1}" -f $ch.FullName, (Join-Path $publishCi $ch.Name))
            if (-not $DryRun) {
                Ensure-Dir -Path $publishCi
                $df = Join-Path $publishCi $ch.Name
                if (Test-Path -LiteralPath $df) {
                    $df = Get-UniqueDestPath -Directory $publishCi -FileName $ch.Name
                }
                Move-Item -LiteralPath $ch.FullName -Destination $df -Force
            }
        }
    }
    if (-not $DryRun -and (Test-Path -LiteralPath $ciRoot)) {
        $left = @(Get-ChildItem -LiteralPath $ciRoot -Force -ErrorAction SilentlyContinue)
        if ($left.Count -eq 0) {
            Remove-Item -LiteralPath $ciRoot -Force -Recurse -ErrorAction SilentlyContinue
            Write-Host "`nRemoved empty artifacts/ci." -ForegroundColor Green
        }
    }
}

if (Test-Path -LiteralPath $localRoot) {
    $skipLocal = @('from-publish-misc', 'legacy-unclassified')
    $localChildren = @(Get-ChildItem -LiteralPath $localRoot -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $skipLocal })

    foreach ($ch in $localChildren) {
        $dest = $null
        $label = $null
        switch ($ch.Name) {
            'windows-debug' {
                $dest = $winDebug
                $label = 'artifacts/local/windows-debug -> build/windows/Debug'
            }
            'android-debug' {
                $dest = $droidDebug
                $label = 'artifacts/local/android-debug -> build/android/Debug'
            }
            'net9.0-android' {
                $dest = Join-Path $publishCi 'net9.0-android'
                $label = 'artifacts/local/net9.0-android -> publish/ci/net9.0-android'
            }
            default {
                $dest = Join-Path $legacyBucket $ch.Name
                $label = ("artifacts/local/{0} -> local/legacy-unclassified/{0}" -f $ch.Name)
            }
        }

        if ($ch.PSIsContainer) {
            Move-FolderContents -SourceDir $ch.FullName -DestDir $dest -Label $label
        }
        else {
            Write-Host "`n=== $label (file) ===" -ForegroundColor Cyan
            if (-not $DryRun) {
                Ensure-Dir -Path (Split-Path $dest -Parent)
                $df = $dest
                if (Test-Path -LiteralPath $df) {
                    $df = Get-UniqueDestPath -Directory (Split-Path $dest -Parent) -FileName $ch.Name
                }
                Move-Item -LiteralPath $ch.FullName -Destination $df -Force
            }
        }
    }

    if (-not $DryRun -and (Test-Path -LiteralPath $localRoot)) {
        $remaining = @(Get-ChildItem -LiteralPath $localRoot -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $skipLocal })
        if ($remaining.Count -eq 0) {
            $keep = @(Get-ChildItem -LiteralPath $localRoot -Force -ErrorAction SilentlyContinue)
            if ($keep.Count -eq 0) {
                Remove-Item -LiteralPath $localRoot -Force -Recurse -ErrorAction SilentlyContinue
                Write-Host "`nRemoved empty artifacts/local." -ForegroundColor Green
            }
        }
    }
}

Write-Host "`nDone." -ForegroundColor Green

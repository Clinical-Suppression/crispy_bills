<#
.SYNOPSIS
One-time (or safe repeat) migration: merge legacy repo-root publish/ into artifacts/.

.DESCRIPTION
Maps:
  publish/logs/          -> artifacts/logs/
  publish/releases/      -> artifacts/releases/
  publish/debug-win/     -> artifacts/local/windows-debug/
  publish/debug-android/ -> artifacts/local/android-debug/

Name conflicts in the destination: the incoming file is renamed with a _from_publish_<timestamp> suffix
before move. Dry-run lists actions without moving.

.PARAMETER DryRun
If set, only report planned operations.

.PARAMETER WorkspaceRoot
Optional; defaults to two levels above this script (repository root).
#>
param(
    [switch]$DryRun,
    [string]$WorkspaceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Get-WorkspaceRoot
}

$publishRoot = Join-Path $WorkspaceRoot 'publish'
if (-not (Test-Path -LiteralPath $publishRoot)) {
    Write-Host "No publish/ folder at $publishRoot — nothing to migrate." -ForegroundColor Yellow
    exit 0
}

$destLogs = Get-ArtifactLogsRoot
$destReleases = Get-ArtifactReleasesRoot
$destLocal = Join-Path (Get-ArtifactsRoot) 'local'
$destWinDebug = Join-Path $destLocal 'windows-debug'
$destAndroidDebug = Join-Path $destLocal 'android-debug'

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
        $suffix = if ($i -eq 0) { "_from_publish_$stamp" } else { "_from_publish_${stamp}_$i" }
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
                Join-Path $DestRoot ([System.IO.Path]::GetFileNameWithoutExtension($item.Name) + '_from_publish_<ts>' + [System.IO.Path]::GetExtension($item.Name))
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
Write-Host "Migrating publish/ -> artifacts/ (DryRun=$DryRun)" -ForegroundColor Green

Ensure-Dir -Path (Get-ArtifactsRoot)
Ensure-Dir -Path $destLogs
Ensure-Dir -Path $destReleases
Ensure-Dir -Path $destLocal

Move-FolderContents -SourceDir (Join-Path $publishRoot 'logs') -DestDir $destLogs -Label 'Logs'
Move-FolderContents -SourceDir (Join-Path $publishRoot 'releases') -DestDir $destReleases -Label 'Releases'
Move-FolderContents -SourceDir (Join-Path $publishRoot 'debug-win') -DestDir $destWinDebug -Label 'Debug Windows build (ad-hoc)'
Move-FolderContents -SourceDir (Join-Path $publishRoot 'debug-android') -DestDir $destAndroidDebug -Label 'Debug Android build (ad-hoc)'

# Any other top-level entries under publish/
$extras = @(Get-ChildItem -LiteralPath $publishRoot -Force -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -notin @('logs', 'releases', 'debug-win', 'debug-android')
})
if ($extras.Count -gt 0) {
    $misc = Join-Path $destLocal 'from-publish-misc'
    Write-Host "`n=== Other items under publish/ -> local/from-publish-misc ===" -ForegroundColor Cyan
    foreach ($ex in $extras) {
        $target = Join-Path $misc $ex.Name
        Write-Host ("  {0} -> {1}" -f $ex.FullName, $target)
        if (-not $DryRun) {
            Ensure-Dir -Path $misc
            if ($ex.PSIsContainer) {
                Move-MergeTree -SourceRoot $ex.FullName -DestRoot $target
                if ((@(Get-ChildItem -LiteralPath $ex.FullName -Force -ErrorAction SilentlyContinue)).Count -eq 0) {
                    Remove-Item -LiteralPath $ex.FullName -Force -Recurse -ErrorAction SilentlyContinue
                }
            }
            else {
                $destFile = if (Test-Path -LiteralPath $target) { Get-UniqueDestPath -Directory $misc -FileName $ex.Name } else { $target }
                Move-Item -LiteralPath $ex.FullName -Destination $destFile -Force
            }
        }
    }
}

if (-not $DryRun) {
    $remaining = @(Get-ChildItem -LiteralPath $publishRoot -Force -ErrorAction SilentlyContinue)
    if ($remaining.Count -eq 0) {
        Remove-Item -LiteralPath $publishRoot -Force -Recurse -ErrorAction SilentlyContinue
        Write-Host "`nRemoved empty publish/ folder." -ForegroundColor Green
    }
    else {
        Write-Host "`nNote: publish/ still contains: $($remaining.Name -join ', ')" -ForegroundColor Yellow
    }
}

Write-Host "`nDone." -ForegroundColor Green

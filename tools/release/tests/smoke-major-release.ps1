<#
Creates a disposable git repo and verifies that release tooling detects a
major semantic bump, shows the proposal in the wizard dry-run, and blocks a
real non-interactive wizard publish unless major approval is explicitly given.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [switch]$IgnoreExitCode
    )

    Push-Location $WorkingDirectory
    try {
        $output = & $Command @Arguments 2>&1 | Out-String
        $exitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
        if (-not $IgnoreExitCode -and $exitCode -ne 0) {
            throw "Command failed ($Command $($Arguments -join ' ')) with exit code $exitCode.`n$output"
        }

        return [PSCustomObject]@{
            ExitCode = $exitCode
            Output = $output
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-True {
    param(
        [bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$sourceReleaseDir = (Resolve-Path (Join-Path $scriptDir '..')).Path
$sourceProjectRoot = (Resolve-Path (Join-Path $sourceReleaseDir '..\..')).Path

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$suffix = [System.Guid]::NewGuid().ToString('N').Substring(0, 6)
$tempRepo = Join-Path $env:TEMP "temp-major-release-repo-$timestamp-$suffix"

Write-Host "Creating temp repo: $tempRepo"
New-Item -ItemType Directory -Path $tempRepo -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $tempRepo 'tools\release') -Force | Out-Null

Copy-Item -Path (Join-Path $sourceReleaseDir '*') -Destination (Join-Path $tempRepo 'tools\release') -Recurse -Force
foreach ($rootFile in @('VERSION', 'CHANGELOG.md')) {
    $sourcePath = Join-Path $sourceProjectRoot $rootFile
    if (Test-Path $sourcePath) {
        Copy-Item -Path $sourcePath -Destination (Join-Path $tempRepo $rootFile) -Force
    }
}

$versionScript = Join-Path $tempRepo 'tools\release\version.ps1'
$wizardScript = Join-Path $tempRepo 'tools\release\wizard.ps1'

Assert-True -Condition:(Test-Path $versionScript) -Message "Missing copied version script at $versionScript"
Assert-True -Condition:(Test-Path $wizardScript) -Message "Missing copied wizard script at $wizardScript"

Push-Location $tempRepo
try {
    Invoke-CheckedCommand -Command 'git' -Arguments @('init', '-q') -WorkingDirectory $tempRepo | Out-Null
    Invoke-CheckedCommand -Command 'git' -Arguments @('config', 'user.name', 'Smoke Test') -WorkingDirectory $tempRepo | Out-Null
    Invoke-CheckedCommand -Command 'git' -Arguments @('config', 'user.email', 'smoke-test@example.com') -WorkingDirectory $tempRepo | Out-Null

    'Initial' | Set-Content -Path (Join-Path $tempRepo 'README.md') -Encoding UTF8
    Invoke-CheckedCommand -Command 'git' -Arguments @('add', '.') -WorkingDirectory $tempRepo | Out-Null
    Invoke-CheckedCommand -Command 'git' -Arguments @('commit', '-q', '-m', 'chore: initial commit') -WorkingDirectory $tempRepo | Out-Null
    Invoke-CheckedCommand -Command 'git' -Arguments @('tag', '-a', 'v1.1.0', '-m', 'v1.1.0') -WorkingDirectory $tempRepo | Out-Null

    New-Item -ItemType Directory -Path (Join-Path $tempRepo 'src') -Force | Out-Null
    'Breaking change content' | Set-Content -Path (Join-Path $tempRepo 'src\breaking.txt') -Encoding UTF8
    Invoke-CheckedCommand -Command 'git' -Arguments @('add', '.') -WorkingDirectory $tempRepo | Out-Null
    Invoke-CheckedCommand -Command 'git' -Arguments @('commit', '-q', '-m', 'feat!: simulate breaking change', '-m', 'BREAKING CHANGE: API changed in incompatible way') -WorkingDirectory $tempRepo | Out-Null

    $versionInfoPath = Join-Path $tempRepo 'version-info.json'
    Invoke-CheckedCommand -Command 'pwsh' -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $versionScript, '-OutFile', $versionInfoPath, '-AllowNoCommits') -WorkingDirectory $tempRepo | Out-Null
    Assert-True -Condition:(Test-Path $versionInfoPath) -Message 'version.ps1 did not write its JSON output file.'
    $versionInfo = Get-Content -Path $versionInfoPath -Raw | ConvertFrom-Json

    Assert-True -Condition:$versionInfo.HasChanges -Message 'version.ps1 unexpectedly reported no changes.'
    Assert-True -Condition:($versionInfo.Bump -eq 'major') -Message "Expected bump 'major' but got '$($versionInfo.Bump)'"
    Assert-True -Condition:([bool]$versionInfo.RequiresApproval) -Message 'Expected RequiresApproval=true for a major bump.'

    Write-Host "version.ps1 detected major bump: $($versionInfo.CurrentTag) -> $($versionInfo.NextTag)"

    $dryRunResult = Invoke-CheckedCommand -Command 'pwsh' -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $wizardScript, '-Tasks', 'publish-both.ps1', '-DryRun', '-AutoConfirm', '-NonInteractive') -WorkingDirectory $tempRepo
    Assert-True -Condition:($dryRunResult.Output -match 'MAJOR VERSION PROPOSAL') -Message "wizard.ps1 dry-run did not show the major proposal banner.`n$($dryRunResult.Output)"
    Assert-True -Condition:($dryRunResult.Output -match 'Major releases require explicit approval before a real publish\.') -Message "wizard.ps1 dry-run did not explain the approval gate.`n$($dryRunResult.Output)"

    Write-Host 'wizard.ps1 dry-run shows the major proposal and approval warning.'

    $realRunResult = Invoke-CheckedCommand -Command 'pwsh' -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $wizardScript, '-Tasks', 'publish-both.ps1', '-AutoConfirm', '-NonInteractive') -WorkingDirectory $tempRepo -IgnoreExitCode
    Assert-True -Condition:($realRunResult.ExitCode -ne 0) -Message "Non-interactive wizard publish unexpectedly succeeded.`n$($realRunResult.Output)"
    Assert-True -Condition:($realRunResult.Output -match 'Major version bump detected but not explicitly approved') -Message "wizard.ps1 failed for the wrong reason.`n$($realRunResult.Output)"

    Write-Host 'wizard.ps1 blocks a real non-interactive major publish without approval.'
    Write-Host "Smoke test completed successfully. Temp repo left at: $tempRepo"
}
finally {
    Pop-Location
}

exit 0

param(
    [string]$ScenarioId = 'heartbeat-01',
    [ValidateSet('heartbeat', 'quiet', 'prompt', 'fail')][string]$StubMode = 'heartbeat',
    [int]$TimeoutSeconds = 60,
    [switch]$UseRealWizard,
    [switch]$NoCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Ensure-Directory {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Content
    )
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Get-Classification {
    param(
        [int]$ExitCode,
        [bool]$TimedOut,
        [string[]]$Output,
        [string[]]$ErrorOutput
    )

    $joined = ($Output -join "`n")
    $errJoined = ($ErrorOutput -join "`n")
    if ($TimedOut) {
        if ($joined -match 'stub-prompt waiting') {
            return 'prompt-blocked'
        }
        if ($joined -match 'stub-quiet start') {
            return 'running-but-quiet'
        }
        if ($joined -match '=== Running: preflight\.ps1 ===' -and $joined -match '==> powershell .*preflight\.ps1') {
            return 'running-but-quiet'
        }
        return 'unknown-timeout'
    }

    if ($ExitCode -eq 0) {
        if ($errJoined -match 'Exception:' -or $errJoined -match 'Write-Error:' -or $joined -match 'failed with exit code') {
            return 'delegated-failure'
        }
        return 'completed'
    }

    return 'delegated-failure'
}

$releaseRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $releaseRoot '..\..')).Path
$runRoot = Join-Path $repoRoot 'publish\logs\wizard-tests'
Ensure-Directory -Path $runRoot

$timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$work = Join-Path $runRoot ("$ScenarioId-$timestamp")
Ensure-Directory -Path $work

$wizardSource = Join-Path $releaseRoot 'wizard.ps1'
$commonSource = Join-Path $releaseRoot 'common.ps1'
Copy-Item -Path $wizardSource -Destination (Join-Path $work 'wizard.ps1') -Force
Copy-Item -Path $commonSource -Destination (Join-Path $work 'common.ps1') -Force

$stubScripts = @(
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
    'recover-missing-release.ps1',
    'conventional-commit.ps1'
)

$defaultStub = @"
Write-Host 'stub-default: completed'
exit 0
"@

$heartbeatStub = @"
Write-Host 'stub-heartbeat start'
for (`$i = 0; `$i -lt 5; `$i++) {
    Write-Host "stub-heartbeat tick `$i"
    Start-Sleep -Seconds 1
}
Write-Host 'stub-heartbeat done'
exit 0
"@

$quietStub = @"
Write-Host 'stub-quiet start'
Start-Sleep -Seconds 20
Write-Host 'stub-quiet done'
exit 0
"@

$promptStub = @"
Write-Host 'stub-prompt waiting'
`$null = Read-Host 'stub-prompt input'
Write-Host 'stub-prompt done'
exit 0
"@

$failStub = @"
Write-Host 'stub-fail start'
throw 'stub-fail forced error'
"@

foreach ($name in $stubScripts) {
    $content = $defaultStub
    if ($name -eq 'preflight.ps1') {
        switch ($StubMode) {
            'heartbeat' { $content = $heartbeatStub }
            'quiet' { $content = $quietStub }
            'prompt' { $content = $promptStub }
            'fail' { $content = $failStub }
        }
    }

    if ($name -eq 'conventional-commit.ps1') {
        $content = @"
Write-Host 'stub-conventional start'
exit 0
"@
    }

    Write-Utf8NoBom -Path (Join-Path $work $name) -Content $content
}

$stdinPath = Join-Path $work 'stdin.txt'
$inputs = @()
if ($StubMode -ne 'prompt') {
    $inputs += '1'
    if ($NoCommit) {
        $inputs += 'n'
    }
    else {
        $inputs += 'y'
    }
    $inputs += 'n'
    $inputs += 'y'
}
Set-Content -Path $stdinPath -Value $inputs -Encoding ASCII

$stdoutPath = Join-Path $work 'wizard.stdout.txt'
$stderrPath = Join-Path $work 'wizard.stderr.txt'
Set-Content -Path $stdoutPath -Value '' -Encoding UTF8
Set-Content -Path $stderrPath -Value '' -Encoding UTF8

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'cmd.exe'
$wizardCommand = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$work\wizard.ps1`" < `"$stdinPath`" 1> `"$stdoutPath`" 2> `"$stderrPath`""
$psi.Arguments = "/d /s /c `"$wizardCommand`""
$psi.WorkingDirectory = $work
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi

$null = $proc.Start()

$allOut = New-Object System.Collections.Generic.List[string]
$allErr = New-Object System.Collections.Generic.List[string]
$start = Get-Date
$timedOut = $false

$finished = $proc.WaitForExit($TimeoutSeconds * 1000)
if (-not $finished) {
    $timedOut = $true
    try { $proc.Kill() } catch { }
    Add-Content -Path $stderrPath -Value "watchdog: killed after $TimeoutSeconds seconds"
}

$proc.WaitForExit()
$stdoutText = Get-Content -Path $stdoutPath -Raw -ErrorAction SilentlyContinue
$stderrText = Get-Content -Path $stderrPath -Raw -ErrorAction SilentlyContinue

if (-not [string]::IsNullOrWhiteSpace($stdoutText)) {
    foreach ($line in ($stdoutText -split "`r?`n")) {
        $allOut.Add($line)
    }
}

if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
    foreach ($line in ($stderrText -split "`r?`n")) {
        $allErr.Add($line)
    }
}

$end = Get-Date
$exitCode = if ($proc.HasExited) { $proc.ExitCode } else { -1 }
$classification = Get-Classification -ExitCode $exitCode -TimedOut:$timedOut -Output @($allOut) -ErrorOutput @($allErr)
$lastOutput = if ($allOut.Count -gt 0) { $allOut[$allOut.Count - 1] } else { '' }

$ledger = [PSCustomObject]@{
    ScenarioId = $ScenarioId
    StubMode = $StubMode
    StartTime = $start.ToString('o')
    EndTime = $end.ToString('o')
    ElapsedSeconds = [math]::Round((($end - $start).TotalSeconds), 3)
    ExitCode = $exitCode
    TimedOut = $timedOut
    Classification = $classification
    LastOutput = $lastOutput
    WorkDir = $work
    StdoutPath = $stdoutPath
    StderrPath = $stderrPath
}

$ledgerPath = Join-Path $work 'ledger.json'
$ledger | ConvertTo-Json -Depth 5 | Set-Content -Path $ledgerPath -Encoding UTF8

Write-Host "Scenario: $ScenarioId"
Write-Host "Classification: $classification"
Write-Host "ExitCode: $exitCode"
Write-Host "TimedOut: $timedOut"
Write-Host "Ledger: $ledgerPath"
Write-Host "Stdout: $stdoutPath"
Write-Host "Stderr: $stderrPath"

if ($timedOut) {
    exit 124
}

if ($exitCode -ne 0) {
    exit $exitCode
}

exit 0

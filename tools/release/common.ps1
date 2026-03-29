<#
.SYNOPSIS
Shared helpers for release tooling (wizard, version, preflight, publish). No interactive I/O here.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PS 7.4+: stderr from native executables can become terminating errors when $ErrorActionPreference is Stop.
# Git prints LF/CRLF normalization hints to stderr even on success; keep native stderr non-terminating for this session.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$script:TaskWarningCount = 0
$script:TaskErrorCount = 0

<# .SYNOPSIS Reset warning/error counters between tasks. #>
function Reset-TaskDiagnostics {
    $script:TaskWarningCount = 0
    $script:TaskErrorCount = 0
}

<# .SYNOPSIS Scan command output and update diagnostic counters. #>
function Add-TaskDiagnosticsFromOutput {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputText
    )

    if ([string]::IsNullOrWhiteSpace($OutputText)) {
        return
    }

    $warningMatches = [regex]::Matches($OutputText, '(?im)^\s*(\d+)\s+Warning\(s\)\s*$')
    $errorMatches = [regex]::Matches($OutputText, '(?im)^\s*(\d+)\s+Error\(s\)\s*$')

    if ($warningMatches.Count -gt 0 -or $errorMatches.Count -gt 0) {
        foreach ($m in $warningMatches) {
            $script:TaskWarningCount += [int]$m.Groups[1].Value
        }
        foreach ($m in $errorMatches) {
            $script:TaskErrorCount += [int]$m.Groups[1].Value
        }
        return
    }

    if ($Command -eq 'dotnet') {
        $script:TaskWarningCount += [regex]::Matches($OutputText, '(?im)\bwarning\b').Count
        $script:TaskErrorCount += [regex]::Matches($OutputText, '(?im)\berror\b').Count
    }
}

<# .SYNOPSIS Return accumulated Warnings and Errors counts. #>
function Get-TaskDiagnostics {
    return [PSCustomObject]@{
        Warnings = $script:TaskWarningCount
        Errors = $script:TaskErrorCount
    }
}

<# .SYNOPSIS Print diagnostic counts to the host. #>
function Write-TaskDiagnostics {
    param([string]$Prefix = 'Task')

    $diag = Get-TaskDiagnostics
    Write-Host "$Prefix diagnostics: warnings=$($diag.Warnings) errors=$($diag.Errors)"
}

<# .SYNOPSIS Workspace root (two levels above this script directory). #>
function Get-WorkspaceRoot {
    $resolved = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    if ($resolved -is [System.Array]) {
        $resolved = $resolved[0]
    }
    return $resolved.Path
}

<# .SYNOPSIS Root folder for wizard/publish automation output (logs, versioned release drops). Gitignored. Not the same as dotnet publish -o. #>
function Get-ArtifactsRoot {
    return Join-Path (Get-WorkspaceRoot) 'artifacts'
}

<# .SYNOPSIS Logs under artifacts (wizard JSON, release notes, publish summaries, binlogs). #>
function Get-ArtifactLogsRoot {
    return Join-Path (Get-ArtifactsRoot) 'logs'
}

<# .SYNOPSIS Versioned release build output root (per semver or wizard run id). #>
function Get-ArtifactReleasesRoot {
    return Join-Path (Get-ArtifactsRoot) 'releases'
}

<# .SYNOPSIS First non-empty path from a scalar or array; $null if none. #>
function Get-FirstPathValue {
    param([Parameter(Mandatory = $true)]$Value)

    if ($Value -is [System.Array]) {
        foreach ($item in $Value) {
            if ($null -ne $item -and -not [string]::IsNullOrWhiteSpace([string]$item)) {
                return [string]$item
            }
        }

        return $null
    }

    if ($null -eq $Value) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace([string]$Value)) {
        return $null
    }

    return [string]$Value
}

<# .SYNOPSIS Quote/escape one argument for Start-Process -ArgumentList string. #>
function ConvertTo-CommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ($null -eq $Value) {
        return '""'
    }

    if ($Value.Length -eq 0) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

<# .SYNOPSIS Run a process with logged stdout/stderr, diagnostics, and non-zero exit as throw. #>
function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$WorkingDirectory
    )

    if (-not $WorkingDirectory) {
        $WorkingDirectory = Get-WorkspaceRoot
    }

    $display = "$Command " + ($Arguments -join ' ')
    Write-Host "==> $display"

    Push-Location $WorkingDirectory
    try {
        $stdoutPath = [System.IO.Path]::GetTempFileName()
        $stderrPath = [System.IO.Path]::GetTempFileName()
        $exitCode = 0
        try {
            function Write-OutputDelta {
                param(
                    [Parameter(Mandatory = $true)][string]$Path,
                    [Parameter(Mandatory = $true)][ref]$LastLength
                )

                if (-not (Test-Path -Path $Path)) { return }

                $currentLength = 0L
                try {
                    $currentLength = (Get-Item -LiteralPath $Path).Length
                }
                catch {
                    return
                }

                if ($currentLength -le $LastLength.Value) { return }

                $stream = $null
                try {
                    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                    [void]$stream.Seek($LastLength.Value, [System.IO.SeekOrigin]::Begin)
                    $bufferSize = [int]($currentLength - $LastLength.Value)
                    if ($bufferSize -le 0) { return }

                    $buffer = New-Object byte[] $bufferSize
                    $bytesRead = $stream.Read($buffer, 0, $bufferSize)
                    if ($bytesRead -gt 0) {
                        [Console]::Out.Write([System.Text.Encoding]::UTF8.GetString($buffer, 0, $bytesRead))
                    }
                    $LastLength.Value = $currentLength
                }
                catch {
                    # Temp output files can be briefly inaccessible while the child process writes.
                    return
                }
                finally {
                    if ($null -ne $stream) { $stream.Dispose() }
                }
            }

            # Start-Process: build one quoted string (array quoting is unreliable).
            $quotedArguments = @($Arguments | ForEach-Object { ConvertTo-CommandLineArgument -Value $_ }) -join ' '
            $process = Start-Process -FilePath $Command -ArgumentList $quotedArguments -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

            $stdoutLength = 0L
            $stderrLength = 0L
            while (-not $process.HasExited) {
                Write-OutputDelta -Path $stdoutPath -LastLength ([ref]$stdoutLength)
                Write-OutputDelta -Path $stderrPath -LastLength ([ref]$stderrLength)
                Start-Sleep -Milliseconds 200
            }
            $process.WaitForExit()
            Write-OutputDelta -Path $stdoutPath -LastLength ([ref]$stdoutLength)
            Write-OutputDelta -Path $stderrPath -LastLength ([ref]$stderrLength)

            $stdoutText = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath -Raw } else { '' }
            $stderrText = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath -Raw } else { '' }

            $commandOutput = @()
            if (-not [string]::IsNullOrWhiteSpace($stdoutText)) {
                $commandOutput += $stdoutText.TrimEnd("`r", "`n")
            }
            if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
                $commandOutput += $stderrText.TrimEnd("`r", "`n")
            }

            $exitCode = [int]$process.ExitCode
            $global:LASTEXITCODE = $exitCode
        }
        finally {
            if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force -ErrorAction SilentlyContinue }
            if (Test-Path $stderrPath) { Remove-Item $stderrPath -Force -ErrorAction SilentlyContinue }
        }

        Add-TaskDiagnosticsFromOutput -Command $Command -OutputText (($commandOutput | Out-String))

        if ($exitCode -ne 0) {
            $outputText = ($commandOutput | Out-String).Trim()
            if ([string]::IsNullOrWhiteSpace($outputText)) {
                throw "Command failed with exit code ${exitCode}: $display"
            }

            throw "Command failed with exit code ${exitCode}: $display`n$outputText"
        }
    }
    finally {
        Pop-Location
    }
}

<# .SYNOPSIS Resolve JDK and Android SDK paths; throw if missing. #>
function Get-JavaAndAndroidSdk {
    $jdkCandidates = @()
    if ($env:JAVA_HOME) { $jdkCandidates += $env:JAVA_HOME }

    $localJdkRoot = Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK'
    if (Test-Path $localJdkRoot) {
        $localJdks = Get-ChildItem $localJdkRoot -Directory | ForEach-Object { $_.FullName }
        $jdkCandidates += $localJdks
    }

    $validJdk = $null
    foreach ($c in $jdkCandidates | Sort-Object -Unique -Descending) {
        $c = Get-FirstPathValue -Value $c
        if (-not $c) { continue }
        $jarExe = Join-Path $c 'bin\jar.exe'
        $jar = $jarExe
        if (-not (Test-Path $jar)) { $jar = Join-Path $c 'bin\jar' }
        if (Test-Path $jar) {
            $validJdk = $c
            break
        }
    }

    if (-not $validJdk) {
        throw "No valid JDK found. Set `JAVA_HOME` to a JDK 17+ installation (containing `bin\\jar.exe`) or install OpenJDK under `$env:LOCALAPPDATA\\Programs\\OpenJDK`."
    }

    $sdkCandidates = @()
    if ($env:ANDROID_SDK_ROOT) { $sdkCandidates += $env:ANDROID_SDK_ROOT }
    if ($env:ANDROID_HOME) { $sdkCandidates += $env:ANDROID_HOME }
    $localSdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    if (Test-Path $localSdk) { $sdkCandidates += $localSdk }

    $validSdk = $null
    foreach ($s in $sdkCandidates | Sort-Object -Unique -Descending) {
        $s = Get-FirstPathValue -Value $s
        if (-not $s) { continue }
        if (-not (Test-Path $s)) { continue }

        $sdkManagerPaths = @(
            Join-Path $s 'cmdline-tools\latest\bin\sdkmanager.bat'
            Join-Path $s 'cmdline-tools\latest\bin\sdkmanager.cmd'
            Join-Path $s 'cmdline-tools\bin\sdkmanager.bat'
            Join-Path $s 'cmdline-tools\bin\sdkmanager.cmd'
            Join-Path $s 'tools\bin\sdkmanager.bat'
            Join-Path $s 'tools\bin\sdkmanager.cmd'
        )

        foreach ($p in $sdkManagerPaths) {
            if (Test-Path $p) {
                $validSdk = $s
                break
            }
        }
        if ($validSdk) { break }
    }

    if (-not $validSdk) {
        throw "Android SDK not found. Install Android SDK and set `ANDROID_SDK_ROOT` (or install under `%LOCALAPPDATA%\\Android\\Sdk`)."
    }

    return @{
        JdkPath = $validJdk
        SdkPath = $validSdk
    }
}

<# .SYNOPSIS Run git with stderr merged into the output stream.

PowerShell 7.4+ sets $PSNativeCommandUseErrorActionPreference such that native
commands writing to stderr can throw when $ErrorActionPreference is Stop. Git
emits LF/CRLF normalization warnings to stderr even on success; suppressing
that behavior avoids spurious failures (see Invoke-GitMergedOutput).
#>
function Invoke-GitMergedOutput {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $prevNative = $null
    if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
        $prevNative = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
    }

    try {
        return & git @Arguments 2>&1
    }
    finally {
        if ($null -ne $prevNative) {
            $PSNativeCommandUseErrorActionPreference = $prevNative
        }
    }
}

<# .SYNOPSIS Run git @Args in a working directory; return trimmed output or throw. #>
function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true)][string[]]$Args,
        [string]$WorkingDirectory
    )

    if (-not $WorkingDirectory) {
        $WorkingDirectory = Get-WorkspaceRoot
    }

    Push-Location $WorkingDirectory
    try {
        $output = Invoke-GitMergedOutput -Arguments $Args
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Args -join ' ') failed: $($output | Out-String)"
        }

        return ($output | Out-String).Trim()
    }
    finally {
        Pop-Location
    }
}

<# .SYNOPSIS Create directory $Path if missing. #>
function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

<# .SYNOPSIS Paths for a release version (creates artifacts/logs and artifacts/releases/<Version> as needed). #>
function Get-ReleasePaths {
    param(
        [Parameter(Mandatory = $true)][string]$Version
    )

    $root = Get-WorkspaceRoot
    $releaseRoot = Join-Path (Get-ArtifactReleasesRoot) $Version
    $logsRoot = Get-ArtifactLogsRoot

    Ensure-Directory -Path $releaseRoot
    Ensure-Directory -Path $logsRoot

    return @{
        WorkspaceRoot = $root
        ReleaseRoot = $releaseRoot
        LogsRoot = $logsRoot
    }
}

<# .SYNOPSIS Write object as UTF-8 JSON (depth 8). #>
function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $json = $Object | ConvertTo-Json -Depth 8
    Set-Content -Path $Path -Value $json -Encoding UTF8
}

function New-WizardRunState {
    param(
        [Parameter(Mandatory = $true)][string[]]$Steps,
        [bool]$DryRun = $false,
        [bool]$NonInteractive = $false
    )

    $runId = [Guid]::NewGuid().ToString('N')
    $startedAt = [DateTimeOffset]::UtcNow
    $stepObjects = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $Steps.Count; $i++) {
        $stepObjects.Add([PSCustomObject]@{
            Index = $i + 1
            Name = $Steps[$i]
            State = 'queued'
            StartTimeUtc = $null
            EndTimeUtc = $null
            DurationMs = $null
            ExitCode = $null
            Message = $null
        })
    }

    return [PSCustomObject]@{
        RunId = $runId
        StartedAtUtc = $startedAt.ToString('o')
        EndedAtUtc = $null
        DurationMs = $null
        DryRun = [bool]$DryRun
        NonInteractive = [bool]$NonInteractive
        Status = 'running'
        Steps = $stepObjects
    }
}

function Update-WizardStepState {
    param(
        [Parameter(Mandatory = $true)]$RunState,
        [Parameter(Mandatory = $true)][int]$StepIndex,
        [Parameter(Mandatory = $true)][ValidateSet('queued', 'running', 'succeeded', 'failed', 'skipped')][string]$State,
        [int]$ExitCode = 0,
        [string]$Message
    )

    if ($null -eq $RunState -or $null -eq $RunState.Steps) { return }
    if ($StepIndex -lt 1 -or $StepIndex -gt $RunState.Steps.Count) { return }

    $step = $RunState.Steps[$StepIndex - 1]
    $now = [DateTimeOffset]::UtcNow
    $step.State = $State
    if ($PSBoundParameters.ContainsKey('Message')) {
        $step.Message = $Message
    }

    if ($State -eq 'running') {
        $step.StartTimeUtc = $now.ToString('o')
        $step.EndTimeUtc = $null
        $step.DurationMs = $null
        $step.ExitCode = $null
        return
    }

    if ($State -in @('succeeded', 'failed', 'skipped')) {
        if ($null -eq $step.StartTimeUtc) {
            $step.StartTimeUtc = $now.ToString('o')
        }
        $step.EndTimeUtc = $now.ToString('o')
        try {
            $start = [DateTimeOffset]::Parse($step.StartTimeUtc)
            $step.DurationMs = [int][Math]::Round(($now - $start).TotalMilliseconds)
        }
        catch {
            $step.DurationMs = $null
        }
        $step.ExitCode = $ExitCode
    }
}

function Complete-WizardRunState {
    param(
        [Parameter(Mandatory = $true)]$RunState,
        [Parameter(Mandatory = $true)][ValidateSet('succeeded', 'failed')][string]$Status
    )

    if ($null -eq $RunState) { return }
    $ended = [DateTimeOffset]::UtcNow
    $RunState.EndedAtUtc = $ended.ToString('o')
    $RunState.Status = $Status
    try {
        $start = [DateTimeOffset]::Parse($RunState.StartedAtUtc)
        $RunState.DurationMs = [int][Math]::Round(($ended - $start).TotalMilliseconds)
    }
    catch {
        $RunState.DurationMs = $null
    }
}

<# .SYNOPSIS Print wizard step table from run-state. #>
function Write-WizardSummary {
    param([Parameter(Mandatory = $true)]$RunState)

    if ($null -eq $RunState -or $null -eq $RunState.Steps) { return }

    Write-Host ''
    Write-Host '=== Wizard Step Summary ===' -ForegroundColor Cyan
    foreach ($step in $RunState.Steps) {
        $durationText = if ($null -eq $step.DurationMs) { '-' } else { ('{0:N1}s' -f ($step.DurationMs / 1000.0)) }
        $exitText = if ($null -eq $step.ExitCode) { '-' } else { [string]$step.ExitCode }
        $line = ('[{0,2}] {1,-30} {2,-10} duration={3,-7} exit={4}' -f $step.Index, $step.Name, $step.State, $durationText, $exitText)
        if ($step.State -eq 'failed') {
            Write-Host $line -ForegroundColor Red
            if (-not [string]::IsNullOrWhiteSpace($step.Message)) {
                Write-Host ("     reason: {0}" -f $step.Message) -ForegroundColor Yellow
            }
        }
        elseif ($step.State -eq 'succeeded') {
            Write-Host $line -ForegroundColor Green
        }
        else {
            Write-Host $line -ForegroundColor Yellow
        }
    }

    $overallDuration = if ($null -eq $RunState.DurationMs) { '-' } else { ('{0:N1}s' -f ($RunState.DurationMs / 1000.0)) }
    Write-Host ("Run status: {0} | Duration: {1} | RunId: {2}" -f $RunState.Status, $overallDuration, $RunState.RunId) -ForegroundColor Cyan
}

<# .SYNOPSIS Write wizard run-state JSON under artifacts/logs; return path. #>
function Write-WizardProgressJson {
    param([Parameter(Mandatory = $true)]$RunState)

    if ($null -eq $RunState) { return $null }

    $versionForPath = if ([string]::IsNullOrWhiteSpace([string]$RunState.RunId)) { 'wizard' } else { "wizard-$($RunState.RunId)" }
    $paths = Get-ReleasePaths -Version $versionForPath
    $fileName = "wizard-progress-$([DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss')).json"
    $path = Join-Path $paths.LogsRoot $fileName
    Write-JsonFile -Object $RunState -Path $path
    return $path
}

<# .SYNOPSIS
If GitHub CLI is not logged in to github.com, authenticate using a PAT read from a file.
Token path: -TokenFile, then env CRISPY_BILLS_GH_TOKEN_FILE, then GITHUB_TOKEN_FILE, then a default under the user profile.
#>
function Ensure-GitHubCliAuthenticated {
    param(
        [string]$TokenFile
    )

    $ghCmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($ghCmd) {
        $gh = $ghCmd.Source
    }
    else {
        $fallback = 'C:\Program Files\GitHub CLI\gh.exe'
        if (Test-Path -LiteralPath $fallback) {
            $gh = $fallback
        }
        else {
            throw 'GitHub CLI not found. Install GitHub CLI and/or restart the terminal so PATH updates.'
        }
    }

    Write-Host ("Using GitHub CLI: {0}" -f $gh) -ForegroundColor DarkGray

    $null = & $gh auth status -h github.com 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'GitHub CLI: already authenticated (github.com).' -ForegroundColor Green
        return
    }

    $resolvedPath = Get-FirstPathValue @($TokenFile, [Environment]::GetEnvironmentVariable('CRISPY_BILLS_GH_TOKEN_FILE'), [Environment]::GetEnvironmentVariable('GITHUB_TOKEN_FILE'))
    if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
        $resolvedPath = Join-Path $env:USERPROFILE 'Documents\Visual Studio Projects\Projects\Tokens\Clinical_Suppression All Repos Token.txt'
    }

    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        throw @'
GitHub CLI is not authenticated and no token file was found.
Set environment variable CRISPY_BILLS_GH_TOKEN_FILE or GITHUB_TOKEN_FILE to your PAT file path,
or pass -GitHubTokenFile to the wizard, or run: gh auth login
'@
    }

    $token = (Get-Content -LiteralPath $resolvedPath -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "GitHub token file is empty: $resolvedPath"
    }

    Write-Host 'GitHub CLI: authenticating with token from file...' -ForegroundColor Yellow
    $token | & $gh auth login -h github.com --with-token
    if ($LASTEXITCODE -ne 0) {
        throw 'gh auth login --with-token failed.'
    }

    $null = & $gh auth setup-git 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'gh auth setup-git failed.'
    }

    Write-Host 'GitHub CLI: login complete.' -ForegroundColor Green
}

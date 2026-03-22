<#
Common helpers for the release tooling.

This file exposes utility functions used by the release scripts (wizard, version,
preflight, publish, etc.). Keep helpers small and well-documented; do not perform
interactive work here — prefer to return data for callers to display or act on.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:TaskWarningCount = 0
$script:TaskErrorCount = 0

<#
Reset-TaskDiagnostics

Reset the in-memory counters used to accumulate warnings and errors
observed from invoked child commands. Useful to clear diagnostics
between independent tasks.
#>
function Reset-TaskDiagnostics {
    $script:TaskWarningCount = 0
    $script:TaskErrorCount = 0
}

<#
Add-TaskDiagnosticsFromOutput

Parse a command's combined stdout/stderr text for common warning/error
summaries and increment the global diagnostic counters. Accepts the
`$Command` that produced the output (used to apply heuristics) and the
raw `$OutputText` to inspect.
#>
function Add-TaskDiagnosticsFromOutput {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputText
    )

    if ([string]::IsNullOrWhiteSpace($OutputText)) {
        return
    }

    # Prefer MSBuild summary lines when available.
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

    # Fallback for commands that emit compiler-style warning/error lines without summary.
    if ($Command -eq 'dotnet') {
        $script:TaskWarningCount += [regex]::Matches($OutputText, '(?im)\bwarning\b').Count
        $script:TaskErrorCount += [regex]::Matches($OutputText, '(?im)\berror\b').Count
    }
}

<#
Get-TaskDiagnostics

Return a PSCustomObject containing the current `Warnings` and `Errors`
counts accumulated by helper functions. This is intended for reporting
and test assertions.
#>
function Get-TaskDiagnostics {
    return [PSCustomObject]@{
        Warnings = $script:TaskWarningCount
        Errors = $script:TaskErrorCount
    }
}

<#
Write-TaskDiagnostics

Write the current task diagnostics to host output using an optional
`$Prefix` to clarify context (for example, "Build", "Publish").
#>
function Write-TaskDiagnostics {
    param([string]$Prefix = 'Task')

    $diag = Get-TaskDiagnostics
    Write-Host "$Prefix diagnostics: warnings=$($diag.Warnings) errors=$($diag.Errors)"
}

<#
Get-WorkspaceRoot

Return the workspace root path by resolving two levels above the
script directory. Callers should treat the returned value as a
string path suitable for joining with project-specific subpaths.
#>
function Get-WorkspaceRoot {
    $resolved = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    if ($resolved -is [System.Array]) {
        $resolved = $resolved[0]
    }
    return $resolved.Path
}

<#
Get-FirstPathValue

Accept a value that may be a single path or an array of path-like
values and return the first non-empty, non-null string. Returns `$null`
if no usable value is found.
#>
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

<#
ConvertTo-CommandLineArgument

Quote and escape a single command-line argument so it can be safely
passed through a single string invocation to Start-Process. Always
returns a string suitable for concatenation into an argument list.
#>
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

<#
Invoke-LoggedCommand

Start a process and capture its stdout/stderr to temporary files, echo
the combined output to the host, and update task diagnostics based on
the output. Throws on non-zero exit codes with captured output for
diagnosis. Parameters: `$Command` (executable), `$Arguments` (string[]),
and optional `$WorkingDirectory`.
#>
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
            try {
                # Build a properly quoted argument string so that arguments containing
                # spaces are preserved when passed to the child process. Start-Process
                # does not reliably quote complex arguments when given an array,
                # so we compose a single string using ConvertTo-CommandLineArgument.
                $quotedArguments = @($Arguments | ForEach-Object { ConvertTo-CommandLineArgument -Value $_ }) -join ' '
                $process = Start-Process -FilePath $Command -ArgumentList $quotedArguments -WorkingDirectory $WorkingDirectory -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

                $stdoutText = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath -Raw } else { '' }
                $stderrText = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath -Raw } else { '' }

                $commandOutput = @()
                if (-not [string]::IsNullOrWhiteSpace($stdoutText)) {
                    $commandOutput += $stdoutText.TrimEnd("`r", "`n")
                }
                if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
                    $commandOutput += $stderrText.TrimEnd("`r", "`n")
                }

                $global:LASTEXITCODE = $process.ExitCode
            }
            finally {
                if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force -ErrorAction SilentlyContinue }
                if (Test-Path $stderrPath) { Remove-Item $stderrPath -Force -ErrorAction SilentlyContinue }
            }

        if ($null -ne $commandOutput) {
            $commandOutput | Out-Host
        }

        Add-TaskDiagnosticsFromOutput -Command $Command -OutputText (($commandOutput | Out-String))

        if ($LASTEXITCODE -ne 0) {
            $outputText = ($commandOutput | Out-String).Trim()
            if ([string]::IsNullOrWhiteSpace($outputText)) {
                throw "Command failed with exit code ${LASTEXITCODE}: $display"
            }

            throw "Command failed with exit code ${LASTEXITCODE}: $display`n$outputText"
        }
    }
    finally {
        Pop-Location
    }
}

<#
Get-JavaAndAndroidSdk

Detect a usable JDK (preferring `$env:JAVA_HOME` or OpenJDK under
`%LOCALAPPDATA%`) and an Android SDK (preferring `$env:ANDROID_SDK_ROOT`).
Throws with a helpful message when detection fails.
#>
function Get-JavaAndAndroidSdk {
    # Prefer explicit environment variables when provided
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

    # Android SDK detection
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

<#
Get-GitOutput

Run `git` with the provided argument array in the specified working
directory (defaults to workspace root) and return the trimmed combined
output as a string. Throws if the git command exits non-zero.
#>
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
        $output = & git @Args 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Args -join ' ') failed: $($output | Out-String)"
        }

        return ($output | Out-String).Trim()
    }
    finally {
        Pop-Location
    }
}

<#
Ensure-Directory

Ensure that the directory at `$Path` exists, creating parent
directories as required. Does nothing if the directory already exists.
#>
function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

<#
Get-ReleasePaths

Given a release `$Version` string, ensure and return a map of paths
used for publishing: `WorkspaceRoot`, `ReleaseRoot`, and `LogsRoot`.
Creates directories as needed.
#>
function Get-ReleasePaths {
    param(
        [Parameter(Mandatory = $true)][string]$Version
    )

    $root = Get-WorkspaceRoot
    $releaseRoot = Join-Path $root (Join-Path 'publish\releases' $Version)
    $logsRoot = Join-Path $root 'publish\logs'

    Ensure-Directory -Path $releaseRoot
    Ensure-Directory -Path $logsRoot

    return @{
        WorkspaceRoot = $root
        ReleaseRoot = $releaseRoot
        LogsRoot = $logsRoot
    }
}

<#
Write-JsonFile

Serialize an object to JSON (UTF8) and write it to the specified
file path. Uses `ConvertTo-Json -Depth 8` to preserve nested structures.
#>
function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $json = $Object | ConvertTo-Json -Depth 8
    Set-Content -Path $Path -Value $json -Encoding UTF8
}

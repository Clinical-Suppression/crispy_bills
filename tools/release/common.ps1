Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:TaskWarningCount = 0
$script:TaskErrorCount = 0

function Reset-TaskDiagnostics {
    $script:TaskWarningCount = 0
    $script:TaskErrorCount = 0
}

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

function Get-TaskDiagnostics {
    return [PSCustomObject]@{
        Warnings = $script:TaskWarningCount
        Errors = $script:TaskErrorCount
    }
}

function Write-TaskDiagnostics {
    param([string]$Prefix = 'Task')

    $diag = Get-TaskDiagnostics
    Write-Host "$Prefix diagnostics: warnings=$($diag.Warnings) errors=$($diag.Errors)"
}

function Get-WorkspaceRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

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
            $process = Start-Process -FilePath $Command -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

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

function Get-JavaAndAndroidSdk {
    $jdkRoot = Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK'
    if (-not (Test-Path $jdkRoot)) {
        throw "OpenJDK directory not found: $jdkRoot"
    }

    $jdkPath = (Get-ChildItem $jdkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
    if (-not $jdkPath) {
        throw 'No OpenJDK installation found under LOCALAPPDATA.'
    }

    $sdkPath = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    if (-not (Test-Path $sdkPath)) {
        throw "Android SDK directory not found: $sdkPath"
    }

    return @{
        JdkPath = $jdkPath
        SdkPath = $sdkPath
    }
}

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

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

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

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $json = $Object | ConvertTo-Json -Depth 8
    Set-Content -Path $Path -Value $json -Encoding UTF8
}

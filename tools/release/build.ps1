param(
    [Parameter(Mandatory = $true)][ValidateSet('windows', 'mobile', 'both')][string]$Target,
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Defensive: ensure $Configuration is a single string (not an array) so Join-Path
# and other callers that expect a scalar don't fail with "System.Object[]".
if ($Configuration -is [System.Array]) {
    if ($Configuration.Count -gt 0) {
        $Configuration = $Configuration[0]
    }
    else {
        $Configuration = 'Debug'
    }
}

. (Join-Path $PSScriptRoot 'common.ps1')

Reset-TaskDiagnostics

$root = Get-WorkspaceRoot
$windowsProject = Join-Path $root 'CrispyBills.csproj'
$androidProject = Join-Path $root 'CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj'
$buildLockPath = Join-Path $root 'publish\logs\build.lock'
$lockAcquired = $false

function Acquire-BuildLock {
    Ensure-Directory -Path (Join-Path $root 'publish\logs')

    $lockContent = "pid=$PID`ntarget=$Target`nconfiguration=$Configuration`ntime=$([DateTime]::UtcNow.ToString('o'))"
    try {
        New-Item -ItemType File -Path $buildLockPath -Force:$false -Value $lockContent -ErrorAction Stop | Out-Null
    }
    catch {
        $holder = ''
        $holderPid = $null
        if (Test-Path $buildLockPath) {
            $holder = (Get-Content -Path $buildLockPath -Raw -ErrorAction SilentlyContinue).Trim()
            if ($holder -match '(?m)^pid=(\d+)\s*$') {
                $holderPid = [int]$Matches[1]
            }
        }

        if ($holderPid) {
            $isAlive = $null -ne (Get-Process -Id $holderPid -ErrorAction SilentlyContinue)
            if (-not $isAlive) {
                Remove-Item -Path $buildLockPath -Force -ErrorAction SilentlyContinue
                New-Item -ItemType File -Path $buildLockPath -Force:$false -Value $lockContent -ErrorAction Stop | Out-Null
                return
            }
        }

        if ([string]::IsNullOrWhiteSpace($holder)) {
            $holder = 'unknown holder'
        }

        throw "Another build appears to be running (lock file: $buildLockPath). Holder details: $holder"
    }
}

function Release-BuildLock {
    if (Test-Path $buildLockPath) {
        Remove-Item -Path $buildLockPath -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-BuildWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string[]]$BuildArgs,
        [string[]]$CleanArgs
    )

    try {
        Invoke-LoggedCommand -Command 'dotnet' -Arguments $BuildArgs -WorkingDirectory $root
    }
    catch {
        Write-Warning "Build failed for $Project. Running clean and retrying once."

        if ($CleanArgs -and $CleanArgs.Count -gt 0) {
            Invoke-LoggedCommand -Command 'dotnet' -Arguments $CleanArgs -WorkingDirectory $root
        }
        else {
            Invoke-LoggedCommand -Command 'dotnet' -Arguments @('clean', $Project, '-c', $Configuration) -WorkingDirectory $root
        }

        Invoke-LoggedCommand -Command 'dotnet' -Arguments $BuildArgs -WorkingDirectory $root
    }
}

function Build-Windows {
    $generatedDir = Join-Path $root (Join-Path 'obj' (Join-Path $Configuration 'net8.0-windows'))
    if (Test-Path $generatedDir) {
        # WPF generated files can occasionally become stale across incremental builds.
        Remove-Item $generatedDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Invoke-BuildWithRetry -Project $windowsProject -BuildArgs @('build', $windowsProject, '-c', $Configuration) -CleanArgs @('clean', $windowsProject, '-c', $Configuration)
}

function Build-Mobile {
    $sdk = Get-JavaAndAndroidSdk
    $buildArgs = @('build', $androidProject, '-f', 'net9.0-android', '-c', $Configuration, "-p:JavaSdkDirectory=$($sdk.JdkPath)", "-p:AndroidSdkDirectory=$($sdk.SdkPath)")
    $cleanArgs = @('clean', $androidProject, '-f', 'net9.0-android', '-c', $Configuration, "-p:JavaSdkDirectory=$($sdk.JdkPath)", "-p:AndroidSdkDirectory=$($sdk.SdkPath)")
    Invoke-BuildWithRetry -Project $androidProject -BuildArgs $buildArgs -CleanArgs $cleanArgs
}

try {
    Acquire-BuildLock
    $lockAcquired = $true

    switch ($Target) {
        'windows' { Build-Windows }
        'mobile' { Build-Mobile }
        'both' {
            Build-Windows
            Build-Mobile
        }
    }

    Write-Host "Build completed. target=$Target configuration=$Configuration"
}
finally {
    if ($lockAcquired) {
        Release-BuildLock
    }

    Write-TaskDiagnostics -Prefix 'Build task'
}

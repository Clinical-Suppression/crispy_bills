param(
    [Parameter(Mandatory = $true)][ValidateSet('windows', 'mobile', 'both')][string]$Target,
    [string]$Version = 'dev',
    [string]$OutFile
)
<#
Top-level release orchestration.

This script coordinates building, versioning, changelog generation, and
publishing across targets. It is intended for maintainers running local
releases and for CI jobs that perform automated releases.

Run the `wizard.ps1` for an interactive guided flow instead of invoking this
script directly when performing manual releases.
#>
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

Reset-TaskDiagnostics

# Lightweight trace for debugging/version propagation
if ($PSBoundParameters.Count -gt 0) {
    Write-Verbose "TRACE: release.ps1 invoked with parameters: $($PSBoundParameters.Keys -join ', ')"
}
$branchStr = if ($PSBoundParameters.ContainsKey('Branch')) { $Branch } else { '' }
$dryRunStr = if ($PSBoundParameters.ContainsKey('DryRun')) { $DryRun } else { $false }
Write-Verbose "TRACE: Version='$Version' Target='$Target' Branch='$branchStr' DryRun='$dryRunStr'"

$root = Get-WorkspaceRoot
$paths = Get-ReleasePaths -Version $Version
$windowsProject = Join-Path $root 'CrispyBills.csproj'
$androidProject = Join-Path $root 'CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj'

function Release-Windows {
    $outDir = Join-Path $paths.ReleaseRoot 'windows'
    Ensure-Directory -Path $outDir

    Invoke-LoggedCommand -Command 'dotnet' -Arguments @('publish', $windowsProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '/p:PublishSingleFile=true', '/p:IncludeNativeLibrariesForSelfExtract=true', '/p:EnableCompressionInSingleFile=true', '/p:PublishReadyToRun=true', '-o', $outDir) -WorkingDirectory $root

    $exe = Get-ChildItem -Path $outDir -Filter '*.exe' -File | Select-Object -First 1
    if (-not $exe) {
        throw 'Windows publish completed but no exe artifact was found.'
    }

    $finalName = "crispybills-v$Version-win-x64.exe"
    $finalPath = Join-Path $outDir $finalName
    Move-Item -Path $exe.FullName -Destination $finalPath -Force

    return $finalPath
}

function Release-Mobile {
    $outDir = Join-Path $paths.ReleaseRoot 'mobile'
    Ensure-Directory -Path $outDir
    # Validate JDK and Android SDK (throws with actionable message if missing)
    $sdk = Get-JavaAndAndroidSdk

    # Attempt to auto-accept SDK licenses non-interactively by piping 'y' repeatedly.
    # This may succeed on developer machines; if it fails, we emit a clear instruction.
    # Coerce SdkPath to a single string in case callers returned an array
    $sdkPath = $sdk.SdkPath
    if ($sdkPath -is [System.Array]) { $sdkPath = $sdkPath | Select-Object -First 1 }

    $sdkmanagerPaths = @(
        Join-Path $sdkPath 'cmdline-tools\latest\bin\sdkmanager.bat'
        Join-Path $sdkPath 'cmdline-tools\latest\bin\sdkmanager.cmd'
        Join-Path $sdkPath 'cmdline-tools\bin\sdkmanager.bat'
        Join-Path $sdkPath 'cmdline-tools\bin\sdkmanager.cmd'
        Join-Path $sdkPath 'tools\bin\sdkmanager.bat'
        Join-Path $sdkPath 'tools\bin\sdkmanager.cmd'
    )
    $sdkmanager = $sdkmanagerPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($sdkmanager) {
        try {
            Write-Host "Attempting to accept Android SDK licenses via: $sdkmanager"
            # Pipe many 'y' responses to cover multiple license prompts.
            1..100 | ForEach-Object { 'y' } | & $sdkmanager --sdk_root=$($sdk.SdkPath) --licenses | Out-Null
            Write-Host 'Android SDK licenses accepted (or were already accepted).'
        }
        catch {
            Write-Host 'Could not auto-accept Android SDK licenses. Run manually:'
            Write-Host "  $sdkmanager --sdk_root=$($sdk.SdkPath) --licenses"
            Write-Host 'Then re-run the release.'
        }
    }
    else {
        Write-Host 'sdkmanager not found; ensure Android cmdline-tools are installed and on disk.'
    }
    Invoke-LoggedCommand -Command 'dotnet' -Arguments @('publish', $androidProject, '-f', 'net9.0-android', '-c', 'Release', '-o', $outDir, '-p:AndroidPackageFormats=apk', "-p:JavaSdkDirectory=$($sdk.JdkPath)", "-p:AndroidSdkDirectory=$($sdk.SdkPath)") -WorkingDirectory $root

    $apk = Get-ChildItem -Path $outDir -Recurse -Filter '*.apk' -File | Select-Object -First 1
    if (-not $apk) {
        throw 'Android publish completed but no apk artifact was found.'
    }

    $finalName = "crispybills-v$Version-android.apk"
    $finalPath = Join-Path $outDir $finalName
    Copy-Item -Path $apk.FullName -Destination $finalPath -Force

    return $finalPath
}

try {
    $artifacts = New-Object System.Collections.Generic.List[string]
    switch ($Target) {
        'windows' { $artifacts.Add((Release-Windows)) }
        'mobile' { $artifacts.Add((Release-Mobile)) }
        'both' {
            $artifacts.Add((Release-Windows))
            $artifacts.Add((Release-Mobile))
        }
    }

    $result = [PSCustomObject]@{
        Target = $Target
        Version = $Version
        ReleaseRoot = $paths.ReleaseRoot
        Artifacts = $artifacts
    }

    if ($OutFile) {
        Write-JsonFile -Object $result -Path $OutFile
    }

    $result | ConvertTo-Json -Depth 5
}
finally {
    Write-TaskDiagnostics -Prefix 'Release task'
}

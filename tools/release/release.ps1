param(
    [Parameter(Mandatory = $true)][ValidateSet('windows', 'mobile', 'both')][string]$Target,
    [string]$Version = 'dev',
    [string]$OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

Reset-TaskDiagnostics

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

    $sdk = Get-JavaAndAndroidSdk
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

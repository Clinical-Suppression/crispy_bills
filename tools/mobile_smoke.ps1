param(
    [string]$ProjectPath = ".\CrispyBills.Mobile.Android\CrispyBills.Mobile.Android.csproj",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$jdkPath = (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\OpenJDK') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkPath = Join-Path $env:LOCALAPPDATA 'Android\Sdk'

if (-not (Test-Path $ProjectPath)) {
    throw "Mobile project not found at $ProjectPath"
}

if (-not (Test-Path $jdkPath)) {
    throw "JDK path not found: $jdkPath"
}

if (-not (Test-Path $sdkPath)) {
    throw "Android SDK path not found: $sdkPath"
}

Write-Host "[mobile-smoke] Building Android project..."
dotnet build $ProjectPath -f net9.0-android -c $Configuration -p:JavaSdkDirectory="$jdkPath" -p:AndroidSdkDirectory="$sdkPath"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "[mobile-smoke] Mobile build smoke check passed."

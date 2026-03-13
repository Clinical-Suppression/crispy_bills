# build-installer.ps1
# Builds CrispyBills as a self-contained single-file exe, then compiles the Inno Setup installer.
# Run this script from the project root: .\build-installer.ps1

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot

Write-Host ""
Write-Host "=== Step 1: Publishing self-contained exe ===" -ForegroundColor Cyan

dotnet publish "$projectRoot\CrispyBills.csproj" `
    /p:PublishProfile=win-x64-installer `
    --configuration Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish failed. Aborting." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Step 2: Compiling Inno Setup installer ===" -ForegroundColor Cyan

# Look for Inno Setup compiler in common install paths
$isCompiler = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 5\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $isCompiler) {
    Write-Host ""
    Write-Host "Inno Setup not found. Please download and install it from:" -ForegroundColor Yellow
    Write-Host "  https://jrsoftware.org/isinfo.php" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Then re-run this script, or open Installer\CrispyBills.iss manually in the Inno Setup IDE and click Build > Compile." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "The published exe is ready at: $projectRoot\publish\win-x64\CrispyBills.exe" -ForegroundColor Green
    exit 0
}

Write-Host "Found Inno Setup at: $isCompiler"
& $isCompiler "$projectRoot\Installer\CrispyBills.iss"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Inno Setup compilation failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Done! ===" -ForegroundColor Green
Write-Host "Installer created at: $projectRoot\Installer\Output\CrispyBills_Setup.exe" -ForegroundColor Green

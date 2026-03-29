param([string]$Configuration = 'Debug')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

if ($Configuration -is [System.Array]) {
    if ($Configuration.Count -gt 0) {
        $Configuration = $Configuration[0]
    }
    else {
        $Configuration = 'Debug'
    }
}

# Do not run `dotnet clean` here before the combined build. A standalone clean deletes WPF
# XAML outputs (*.g.cs) under obj; the next plain `dotnet build` can hit CS2001 (MarkupCompile
# not yet materializing those files). build.ps1 uses `msbuild /t:Rebuild` for Windows so
# Clean + markup compile + compile run in one graph.
Write-Host 'Starting combined Windows + Android build...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'build.ps1') -Target both -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

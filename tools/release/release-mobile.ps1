param(
    [string]$Branch = 'main',
    [switch]$DryRun,
    [switch]$NoPush,
    [switch]$AllowDirty,
    [switch]$AllowNonMain,
    [switch]$NonInteractive,
    [string]$ResponsesFile,
    [Parameter(Mandatory = $false)][ValidateNotNullOrEmpty()][string]$Version = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSBoundParameters.Count -gt 0) {
    Write-Verbose "TRACE: release-mobile.ps1 invoked with parameters: $($PSBoundParameters.Keys -join ', ')"
}
Write-Verbose "TRACE: Version='$Version' Branch='$Branch' DryRun='$DryRun' NoPush='$NoPush' AllowDirty='$AllowDirty' NonInteractive='$NonInteractive' ResponsesFile='$ResponsesFile'"

& (Join-Path $PSScriptRoot 'release.ps1') -Target mobile -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

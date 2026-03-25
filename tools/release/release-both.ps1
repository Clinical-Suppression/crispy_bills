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

$releaseParams = @{ 'Target' = 'both' }
if ($PSBoundParameters.ContainsKey('Version') -and $Version) {
    $releaseParams['Version'] = $Version
}

& (Join-Path $PSScriptRoot 'release.ps1') @releaseParams
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

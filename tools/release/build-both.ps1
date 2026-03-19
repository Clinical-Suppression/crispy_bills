param([string]$Configuration = 'Debug')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Defensive: coerce $Configuration to single string if an array was passed in.
if ($Configuration -is [System.Array]) {
	if ($Configuration.Count -gt 0) {
		$Configuration = $Configuration[0]
	}
	else {
		$Configuration = 'Debug'
	}
}

& (Join-Path $PSScriptRoot 'build.ps1') -Target both -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

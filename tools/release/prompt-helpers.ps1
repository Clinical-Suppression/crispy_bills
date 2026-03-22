<#
Prompt helpers for release scripts

Provides: Load-ResponsesFile, Get-Response, Test-IsInteractive,
and Prompt-* helpers that return responses from a responses JSON
when running non-interactively.

Responses JSON schema (example):
{
  "conventional-commit": { "Type": "fix", "Description": "ci: runner" },
  "publish": { "Type": "chore", "Description": "prepare release", "Scope": "release" }
}
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ReleasePromptNonInteractive = $false
$global:RELEASE_RESPONSES = @{}

function Initialize-ReleasePromptContext {
    param(
        [string]$ResponsesFile,
        [switch]$NonInteractive
    )

    $script:ReleasePromptNonInteractive = $NonInteractive.IsPresent -or [bool]$env:CRISPYBILLS_NONINTERACTIVE
    if ($PSBoundParameters.ContainsKey('ResponsesFile')) {
        Load-ResponsesFile -Path $ResponsesFile | Out-Null
    }
}

function Load-ResponsesFile {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) {
        $global:RELEASE_RESPONSES = @{}
        return $null
    }

    try {
        $content = Get-Content -Raw -Path $Path
        $global:RELEASE_RESPONSES = $content | ConvertFrom-Json -ErrorAction Stop
        return $global:RELEASE_RESPONSES
    }
    catch {
        Write-Warning "Failed to load responses file ${Path}: $($_.Exception.Message)"
        $global:RELEASE_RESPONSES = @{}
        return $null
    }
}

function Get-Response {
    param(
        [string]$ScriptName,
        [string]$Key,
        $Default
    )

    if (-not $ScriptName -or -not $Key) { return $Default }
    if (-not $global:RELEASE_RESPONSES) { return $Default }

    if ($global:RELEASE_RESPONSES -is [System.Collections.IDictionary]) {
        if (-not $global:RELEASE_RESPONSES.Contains($ScriptName)) {
            return $Default
        }

        $obj = $global:RELEASE_RESPONSES[$ScriptName]
        if ($obj -is [System.Collections.IDictionary]) {
            if ($obj.Contains($Key)) { return $obj[$Key] }
            return $Default
        }

        $property = $obj.PSObject.Properties[$Key]
        if ($null -ne $property) { return $property.Value }
        return $Default
    }

    try {
        $ps = $global:RELEASE_RESPONSES.PSObject.Properties.Name
        if ($ps -contains $ScriptName) {
            $obj = $global:RELEASE_RESPONSES.$ScriptName
            try {
                if ($obj.PSObject.Properties.Name -contains $Key) { return $obj.$Key }
            }
            catch { }
        }
    }
    catch {
        return $Default
    }

    return $Default
}

function Test-IsInteractive {
    try {
        if ($script:ReleasePromptNonInteractive -or $env:CRISPYBILLS_NONINTERACTIVE) {
            return $false
        }
        return (-not [Console]::IsInputRedirected) -and (-not [Console]::IsOutputRedirected)
    }
    catch {
        return $true
    }
}

function Prompt-Text {
    param(
        [string]$PromptText,
        [string]$ScriptName = '',
        [string]$Key = '',
        [string]$Default = ''
    )

    if (-not (Test-IsInteractive)) {
        return (Get-Response -ScriptName $ScriptName -Key $Key -Default $Default)
    }

    return (Read-Host $PromptText).Trim()
}

function Prompt-YesNo {
    param(
        [string]$PromptText,
        [string]$ScriptName = '',
        [string]$Key = '',
        [bool]$Default = $false
    )

    if (-not (Test-IsInteractive)) {
        $resp = Get-Response -ScriptName $ScriptName -Key $Key -Default $null
        if ($null -ne $resp) {
            if ($resp -is [bool]) { return [bool]$resp }
            $s = $resp.ToString().ToLowerInvariant()
            return -not ($s -eq 'n' -or $s -eq 'no' -or $s -eq 'false' -or $s -eq '0')
        }
        return $Default
    }

    $input = (Read-Host $PromptText).Trim().ToLowerInvariant()
    if ($input -eq '') { return $Default }
    return -not ($input -eq 'n' -or $input -eq 'no')
}

function Prompt-Select {
    param(
        [string]$PromptText,
        [string[]]$Options,
        [string]$ScriptName = '',
        [string]$Key = '',
        $DefaultIndex = 0
    )

    if (-not (Test-IsInteractive)) {
        $resp = Get-Response -ScriptName $ScriptName -Key $Key -Default $null
        if ($null -ne $resp -and $Options -contains $resp) { return $resp }
        if ($DefaultIndex -ge 0 -and $DefaultIndex -lt $Options.Count) { return $Options[$DefaultIndex] }
        return $Options[0]
    }

    Write-Host $PromptText
    for ($i = 0; $i -lt $Options.Count; $i++) { Write-Host "[$($i)] $($Options[$i])" }
    while ($true) {
        $idx = -1
        $choice = (Read-Host 'Select number').Trim()
        if ([int]::TryParse($choice, [ref]$idx) -and $idx -ge 0 -and $idx -lt $Options.Count) {
            return $Options[$idx]
        }
        Write-Host 'Invalid selection.' -ForegroundColor Yellow
    }
}

function Prompt-MultiSelect {
    param(
        [string]$PromptText,
        [string[]]$Options,
        [string]$ScriptName = '',
        [string]$Key = '',
        [string[]]$Default = @()
    )

    if (-not (Test-IsInteractive)) {
        $resp = Get-Response -ScriptName $ScriptName -Key $Key -Default $null
        if ($null -ne $resp) {
            if ($resp -is [System.Collections.IEnumerable]) { return ,$resp }
            return @($resp)
        }
        return $Default
    }

    Write-Host $PromptText
    for ($i = 0; $i -lt $Options.Count; $i++) { Write-Host "[$($i)] $($Options[$i])" }
    $selection = (Read-Host 'Enter comma-separated numbers (e.g. 0,2)').Trim()
    if ([string]::IsNullOrWhiteSpace($selection)) { return @() }
    $parts = $selection -split ',' | ForEach-Object { $_.Trim() }
    $out = @()
    foreach ($p in $parts) {
        $idx = -1
        if ([int]::TryParse($p, [ref]$idx) -and $idx -ge 0 -and $idx -lt $Options.Count) {
            $out += $Options[$idx]
        }
    }
    return $out
}

# End of prompt-helpers.ps1

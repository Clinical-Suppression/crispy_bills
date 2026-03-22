<#
Prompt helpers for release scripts

Purpose:
    Provide a small interactive abstraction used by the wizard and other
    release scripts. Supports reading a JSON `responses` file for non-interactive
    automation and exposing `Prompt-*` helpers that fall back to the responses.

Provided functions:
    - Initialize-ReleasePromptContext
    - Load-ResponsesFile
    - Get-Response
    - Test-IsInteractive
    - Prompt-Text / Prompt-YesNo / Prompt-Select / Prompt-MultiSelect

Responses JSON schema (example):
{
    "conventional-commit": { "Type": "fix", "Description": "ci: runner" },
    "publish": { "Type": "chore", "Description": "prepare release", "Scope": "release" }
}

Automation note:
    When running in CI prefer providing a `ResponsesFile` and using the
    `-NonInteractive`/`-RequireNonInteractiveReady` flags on the wizard.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ReleasePromptNonInteractive = $false
$global:RELEASE_RESPONSES = @{}

<#
Initialize-ReleasePromptContext

Initialize the prompt system for release scripts. Optionally load a
`ResponsesFile` (JSON) for non-interactive automation and set the
`-NonInteractive` flag to force prompt helpers to prefer responses.
Parameters:
  - `$ResponsesFile`: path to a JSON responses file.
  - `-NonInteractive`: switch to disable interactive prompts.
#>
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

<#
Load-ResponsesFile

Load a JSON responses file into the global `RELEASE_RESPONSES` map.
If the file is missing or invalid the function resets `RELEASE_RESPONSES`
to an empty map and returns `$null`.
#>
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

<#
Get-Response

Retrieve a value from the loaded responses for a given `$ScriptName`
and `$Key`. Returns `$Default` when the value is missing or the
responses map is not available. Handles common JSON-to-PSObject
representations.
#>
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

<#
Test-IsInteractive

Return `$true` when the current host appears interactive and prompts
should be used. Honors the `ReleasePromptNonInteractive` flag and the
`CRISPYBILLS_NONINTERACTIVE` environment variable.
#>
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

<#
Prompt-Text

Prompt for a single-line text value. When non-interactive, the value
is retrieved from the responses file using `Get-Response`.
#>
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

<#
Prompt-YesNo

Prompt for a boolean yes/no answer. When non-interactive attempts to
coerce the response from the responses file into a boolean. Returns the
provided `$Default` when no response is available.
#>
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

<#
Prompt-Select

Display a numbered selection list and return the chosen option. When
non-interactive, attempt to match the responses file value to an option
or fall back to the specified `$DefaultIndex`.
#>
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

<#
Prompt-MultiSelect

Prompt the user to select multiple items from a list. Returns an array
of chosen options. When non-interactive attempts to coerce the response
into an enumerable result from the responses file.
#>
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

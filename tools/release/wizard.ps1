<#
Interactive release wizard for local-first release orchestration.

This orchestrator delegates to the scripts in this folder (build-*, publish-*,
release-*, preflight.ps1, etc.). It provides an interactive selection UI,
dry-run mode, commit synthesis, and confirmation checkpoints.

Automation notes:
    - For non-interactive automation, provide a responses JSON and use
        -NonInteractive -RequireNonInteractiveReady.
    - Prefer reading machine output (version.ps1 -OutFile) rather than parsing
        stdout when automating.
    - Publish flows: if gh is not logged in, the wizard reads a PAT from
        -GitHubTokenFile or CRISPY_BILLS_GH_TOKEN_FILE / GITHUB_TOKEN_FILE.
#>

param(
    [string[]]$Tasks,
    [switch]$DryRun,
    [switch]$NoCommit,
    [switch]$NoSpinner,
    [switch]$Verbose,
    [switch]$AutoConfirm,
    [switch]$AllowDirty,
    [switch]$RequireCleanTree,
    [switch]$All,
    [switch]$AutoCommit,
    [switch]$NonInteractive,
    [switch]$RequireNonInteractiveReady,
    [ValidateSet('feat','fix','perf','refactor','docs','test','build','ci','chore')] [string]$CommitType = 'chore',
    [string]$CommitScope,
    [string]$CommitMessage,
    [string]$CommitBody,
    [switch]$BreakingChange,
    [switch]$ApproveMajorVersion,
    [switch]$SkipVersion,
    [string]$ResponsesFile,
    [switch]$NoProgressJson,
    [int]$TaskTimeoutSeconds = 0,
    [string]$GitHubTokenFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\common.ps1"

$helpersPath = Join-Path $PSScriptRoot 'prompt-helpers.ps1'
if (Test-Path $helpersPath) {
    . $helpersPath
    if (Get-Command -Name Initialize-ReleasePromptContext -ErrorAction SilentlyContinue) {
        Initialize-ReleasePromptContext -ResponsesFile $ResponsesFile -NonInteractive:$NonInteractive
    }
}

$script:WizardTaskMetadata = @{}
$metadataPath = Join-Path $PSScriptRoot 'tasks.metadata.json'
if (Test-Path $metadataPath) {
    try {
        $script:WizardTaskMetadata = Get-Content -Path $metadataPath -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Write-Warning "Failed to load task metadata from ${metadataPath}: $($_.Exception.Message)"
        $script:WizardTaskMetadata = @{}
    }
}

function Clear-WizardProgress {
    Write-Host ''
}

function Get-ProcessTreeActivityPulse {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [timespan]$LastCpuTime,
        [int]$LastChildCount = -1
    )

    $cpuPulse = $false
    $childPulse = $false
    $cpuNow = $LastCpuTime
    $childCount = $LastChildCount

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($process) {
            $cpuNow = $process.TotalProcessorTime
            if ($LastCpuTime -ne [timespan]::Zero -and ($cpuNow - $LastCpuTime).TotalMilliseconds -gt 5) {
                $cpuPulse = $true
            }
        }
    }
    catch {}

    try {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" -ErrorAction SilentlyContinue)
        $childCount = $children.Count
        if ($LastChildCount -ge 0 -and $childCount -ne $LastChildCount) {
            $childPulse = $true
        }
    }
    catch {}

    $pulseStrength = 0
    if ($cpuPulse) { $pulseStrength += 2 }
    if ($childPulse) { $pulseStrength += 1 }

    return [PSCustomObject]@{
        PulseStrength = $pulseStrength
        CpuNow = $cpuNow
        ChildCount = $childCount
    }
}

function Is-Interactive {
    if ($NonInteractive -or $env:CRISPYBILLS_NONINTERACTIVE) {
        return $false
    }

    if (Get-Command -Name Test-IsInteractive -ErrorAction SilentlyContinue) {
        return (Test-IsInteractive)
    }

    try {
        $inRedirected = [Console]::IsInputRedirected
        $outRedirected = [Console]::IsOutputRedirected
        return -not ($inRedirected -or $outRedirected)
    } catch {
        return $true
    }
}

function Get-WizardResponse {
    param(
        [string]$Key,
        $Default = $null
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        return $Default
    }

    if (Get-Command -Name Get-Response -ErrorAction SilentlyContinue) {
        return Get-Response -ScriptName 'wizard' -Key $Key -Default $Default
    }

    return $Default
}

function Get-DetectedVersionFromScript {
    param(
        [switch]$AllowNoCommits
    )

    $tempOut = [IO.Path]::GetTempFileName()
    try {
        $versionScriptPath = Join-Path $PSScriptRoot 'version.ps1'
        $versionArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $versionScriptPath, '-OutFile', $tempOut)
        if ($AllowNoCommits) {
            $versionArgs += '-AllowNoCommits'
        }

        & pwsh @versionArgs | Out-Null

        if (-not (Test-Path $tempOut)) {
            return $null
        }

        $json = Get-Content -Path $tempOut -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ($json -and $json.Version) {
            return [string]$json.Version
        }

        return $null
    }
    catch {
        Write-Host "Warning: Unable to detect version via version.ps1: $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
    finally {
        if (Test-Path $tempOut) {
            Remove-Item $tempOut -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-VersionInfoFromScript {
    param(
        [switch]$AllowNoCommits
    )

    $tempOut = [IO.Path]::GetTempFileName()
    try {
        $versionScriptPath = Join-Path $PSScriptRoot 'version.ps1'
        $versionArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $versionScriptPath, '-OutFile', $tempOut)
        if ($AllowNoCommits) {
            $versionArgs += '-AllowNoCommits'
        }

        & pwsh @versionArgs | Out-Null

        if (-not (Test-Path $tempOut)) {
            return $null
        }

        return (Get-Content -Path $tempOut -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        Write-Host "Warning: Unable to read version info via version.ps1: $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
    finally {
        if (Test-Path $tempOut) {
            Remove-Item $tempOut -Force -ErrorAction SilentlyContinue
        }
    }
}

function Confirm-MajorVersionApproval {
    param(
        $VersionInfo,
        [bool]$DryRun,
        [bool]$ExplicitApproval
    )

    if ($null -eq $VersionInfo -or -not $VersionInfo.HasChanges -or $VersionInfo.Bump -ne 'major') {
        return $false
    }

    if ($DryRun) {
        Write-Host 'Dry-run: major version proposal detected; approval will be required for a real publish.' -ForegroundColor Yellow
        return $false
    }

    if ($ExplicitApproval) {
        Write-Host 'Major version approved via explicit flag.' -ForegroundColor Yellow
        return $true
    }

    $currentTag = if ([string]::IsNullOrWhiteSpace($VersionInfo.CurrentTag)) { 'none' } else { $VersionInfo.CurrentTag }
    $nextTag = if ([string]::IsNullOrWhiteSpace($VersionInfo.NextTag)) { 'unknown' } else { $VersionInfo.NextTag }
    $approvalMessage = "Major release detected ($currentTag -> $nextTag). Approve this major version publish?"

    if (-not (Is-Interactive)) {
        $approved = [bool](Get-WizardResponse -Key 'ApproveMajorVersion' -Default $false)
        if (-not $approved) {
            throw 'Major version bump detected but not explicitly approved. Re-run with -ApproveMajorVersion or provide wizard.ApproveMajorVersion=true.'
        }
        return $true
    }

    $approved = Prompt-YesNo -Message $approvalMessage -Key 'ApproveMajorVersion' -Default $false
    if (-not $approved) {
        throw 'Major version bump was not approved. Aborting before publish.'
    }

    return $true
}

function Show-WarningBlock {
    param(
        [string]$Title,
        [string[]]$Lines
    )

    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor Red
    Write-Host $Title -ForegroundColor Red
    foreach ($line in @($Lines)) {
        Write-Host $line -ForegroundColor Yellow
    }
    Write-Host ('=' * 72) -ForegroundColor Red
}

function Get-TaskMetadata {
    param([string]$ScriptName)

    if (-not $ScriptName) { return $null }
    if ($script:WizardTaskMetadata -is [System.Collections.IDictionary]) {
        if (-not $script:WizardTaskMetadata.Contains($ScriptName)) { return $null }
        return $script:WizardTaskMetadata[$ScriptName]
    }

    $property = $script:WizardTaskMetadata.PSObject.Properties[$ScriptName]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-TaskMetadataValue {
    param(
        $Metadata,
        [string]$Name,
        $Default = $null
    )

    if ($null -eq $Metadata -or [string]::IsNullOrWhiteSpace($Name)) {
        return $Default
    }

    if ($Metadata -is [System.Collections.IDictionary]) {
        if ($Metadata.Contains($Name)) { return $Metadata[$Name] }
        return $Default
    }

    $property = $Metadata.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Resolve-TaskSelectionValue {
    param(
        $Selection,
        [string[]]$Options
    )

    if ($null -eq $Selection) { return @() }

    $tokens = @()
    if ($Selection -is [string]) {
        $tokens = @($Selection -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    elseif ($Selection -is [System.Collections.IEnumerable]) {
        foreach ($item in $Selection) {
            if ($null -ne $item -and -not [string]::IsNullOrWhiteSpace([string]$item)) {
                $tokens += ([string]$item).Trim()
            }
        }
    }
    else {
        $tokens = @(([string]$Selection).Trim())
    }

    if ($tokens.Count -eq 0) { return @() }

    if ((@($tokens | Where-Object { $_.ToLowerInvariant() -in @('all', 'a') })).Count -gt 0) {
        return 0..($Options.Length - 1)
    }

    $indices = New-Object System.Collections.Generic.List[int]
    $lowerAvailable = $Options | ForEach-Object { $_.ToLowerInvariant() }

    foreach ($token in $tokens) {
        $normalized = $token.ToLowerInvariant()
        if ($normalized -match '^(\d+)-(\d+)$') {
            $start = [int]$Matches[1]
            $end = [int]$Matches[2]
            if ($start -le 0 -or $end -le 0 -or $start -gt $end) { continue }
            for ($n = $start; $n -le $end; $n++) {
                if ($n -ge 1 -and $n -le $Options.Length) { $indices.Add($n - 1) }
            }
            continue
        }

        $indexValue = -1
        if ([int]::TryParse($normalized, [ref]$indexValue)) {
            if ($indexValue -ge 1 -and $indexValue -le $Options.Length) {
                $indices.Add($indexValue - 1)
            }
            continue
        }

        $matchIndex = $lowerAvailable.IndexOf($normalized)
        if ($matchIndex -ge 0) {
            $indices.Add($matchIndex)
        }
    }

    return @($indices | Sort-Object -Unique)
}

function Test-WizardNonInteractiveReadiness {
    param(
        [string[]]$ProvidedTasks,
        [bool]$HasExplicitDryRun
    )

    if (-not $RequireNonInteractiveReady) { return }
    if (Is-Interactive) { return }

    $hasTaskSelection = $All -or (($ProvidedTasks | Measure-Object).Count -gt 0) -or ((Resolve-TaskSelectionValue -Selection (Get-WizardResponse -Key 'Tasks' -Default $null) -Options $available) | Measure-Object).Count -gt 0
    if (-not $hasTaskSelection) {
        throw 'Non-interactive readiness check failed: specify -Tasks/-All or provide wizard.Tasks in the responses file.'
    }

    if (-not $HasExplicitDryRun -and $null -eq (Get-WizardResponse -Key 'DryRun' -Default $null) -and -not $AutoConfirm) {
        throw 'Non-interactive readiness check failed: specify -DryRun or provide wizard.DryRun in the responses file.'
    }
}

function Prompt-YesNo {
    param(
        [string]$Message,
        [bool]$Default = $false,
        [string]$Key = ''
    )
    Clear-WizardProgress
    if (-not (Is-Interactive)) {
        $response = Get-WizardResponse -Key $Key -Default $null
        if ($null -ne $response) {
            if ($response -is [bool]) { return [bool]$response }
            $normalized = $response.ToString().Trim().ToLowerInvariant()
            if ($normalized -in @('y','yes','true','1')) { return $true }
            if ($normalized -in @('n','no','false','0')) { return $false }
        }

        Write-Host "Non-interactive session detected; defaulting answer to '$Default'." -ForegroundColor Yellow
        return $Default
    }
    $defaultChar = if ($Default) { 'Y/n' } else { 'y/N' }
    while ($true) {
        $r = Read-Host "$Message ($defaultChar)"
        if ([string]::IsNullOrWhiteSpace($r)) { return $Default }
        switch ($r.ToLowerInvariant()) {
            'y' { return $true }
            'yes' { return $true }
            'n' { return $false }
            'no' { return $false }
            default { Write-Host "Please answer 'y' or 'n'." }
        }
    }
}

function ConvertFrom-GitStatusLines {
    param([string[]]$Lines)

    $Lines = @($Lines)
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($line in @($Lines)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $trimmedLine = $line.TrimEnd()
        if ($trimmedLine -notmatch '^(?<status>[ MADRCU?!]{1,2})\s+(?<path>.+)$') { continue }

        $pathPart = $Matches['path'].Trim()
        if ([string]::IsNullOrWhiteSpace($pathPart)) { continue }

        if ($pathPart -match ' -> ') {
            $pathPart = ($pathPart -split ' -> ')[-1].Trim()
        }

        if (-not [string]::IsNullOrWhiteSpace($pathPart)) {
            $files.Add($pathPart)
        }
    }

    $sortedFiles = @($files | Sort-Object -Unique)
    return $sortedFiles
}

function Get-ChangedFiles {
    $root = Get-WorkspaceRoot
    $status = Get-GitOutput -Args @('status', '--porcelain') -WorkingDirectory $root
    if (-not $status) { return @() }
    $lines = $status -split "`n" | ForEach-Object { $_.TrimEnd() } | Where-Object { $_.Trim().Length -gt 0 }
    return @(ConvertFrom-GitStatusLines -Lines $lines)
}

function Get-RecommendedCommitType {
    param([string[]]$files)

    $files = @($files | Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($files.Count -eq 0) { return 'chore' }

    $lower = @($files | ForEach-Object { $_.ToLowerInvariant() })
    if ($lower -match '\bdocs?\b' -or $lower -match '\bmd$' -or $lower -match 'readme') {
        return 'docs'
    }

    if ($lower -match '\btest\b' -or $lower -match 'tests?') {
        return 'test'
    }

    if ($lower -match '\.csproj$' -or $lower -match '\.sln$' -or $lower -match 'tools\\release') {
        return 'build'
    }

    if ($lower -match '\.xaml$' -or $lower -match '\.cs$' -or $lower -match '\.java$') {
        # Flatten paths into one string[]; nesting @('diff','--', $files) breaks [string[]] binding when $files is empty.
        $gitArgs = @('diff', '--') + @($files)
        $diff = Get-GitOutput -Args $gitArgs -WorkingDirectory (Get-WorkspaceRoot)
        if ($diff -match '(?im)\bfix\b|\bbug\b|\bissue\b') {
            return 'fix'
        }
        if ($diff -match '(?im)\bnew feature\b|\badd(ed)?\b|\bimplements\b') {
            return 'feat'
        }
        return 'feat'
    }

    return 'chore'
}

function Get-RecommendedCommitMessage {
    param([string]$type, [string[]]$files)

    $root = Get-WorkspaceRoot
    $files = @($files | Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_) })
    # With a clean tree, $files is empty; @('diff','--stat','--', $files) nests an empty array and breaks Get-GitOutput -Args [string[]] binding.
    $diffSummary = ''
    if ($files.Count -gt 0) {
        $gitArgs = @('diff', '--stat', '--') + @($files)
        $diffSummary = Get-GitOutput -Args $gitArgs -WorkingDirectory $root
    }

    # Description only — conventional-commit.ps1 prepends type(scope): itself.
    if ($type -eq 'docs') {
        return "update documentation based on recent changes"
    }
    if ($type -eq 'test') {
        return "add/update tests for changed behavior"
    }
    if ($type -eq 'build') {
        return "update build process / dependencies"
    }
    if ($type -eq 'fix') {
        return "correct behavior based on current change set"
    }

    return "implement new behavior and improvements"
}

function Prompt-CommitMetadata {
    param(
        [string]$RecommendedType,
        [string]$RecommendedDescription,
        [string]$InitialScope,
        [string]$InitialDescription,
        [switch]$AllowSkip,
        [string]$ResponsePrefix = 'Commit'
    )

    if (-not (Is-Interactive)) {
        $skipDefault = $false
        if ($AllowSkip -and -not $AutoCommit -and -not $AutoConfirm) { $skipDefault = $true }
        $skip = [bool](Get-WizardResponse -Key ($ResponsePrefix + 'Skip') -Default $skipDefault)
        $defaultDescription = if ([string]::IsNullOrWhiteSpace($InitialDescription)) { $RecommendedDescription } else { $InitialDescription }
        $resolvedType = Get-WizardResponse -Key ($ResponsePrefix + 'Type') -Default $RecommendedType
        $resolvedScope = Get-WizardResponse -Key ($ResponsePrefix + 'Scope') -Default $InitialScope
        $resolvedDescription = Get-WizardResponse -Key ($ResponsePrefix + 'Description') -Default $defaultDescription
        return [PSCustomObject]@{
            Type = $resolvedType
            Scope = $resolvedScope
            Description = $resolvedDescription
            Skip = $skip
        }
    }

    $typeInput = Read-Host "Commit type [feat|fix|perf|refactor|docs|test|build|ci|chore] (default: $RecommendedType)"
    if ([string]::IsNullOrWhiteSpace($typeInput)) { $typeInput = $RecommendedType }

    $scopeDefaultText = if ([string]::IsNullOrWhiteSpace($InitialScope)) { 'none' } else { $InitialScope }
    $scopeInput = Read-Host "Optional scope (example: billing, mobile, release) [default: $scopeDefaultText]"
    if ([string]::IsNullOrWhiteSpace($scopeInput)) { $scopeInput = $InitialScope }

    $descriptionDefault = if ([string]::IsNullOrWhiteSpace($InitialDescription)) { $RecommendedDescription } else { $InitialDescription }
    $descriptionInput = Read-Host "Commit description (required) [default: $descriptionDefault]"
    if ([string]::IsNullOrWhiteSpace($descriptionInput)) { $descriptionInput = $descriptionDefault }

    if ($AllowSkip) {
        $skipCommit = -not (Prompt-YesNo -Message 'Use these commit details if a publish pre-commit is needed?' -Default $true)
    }
    else {
        $skipCommit = $false
    }

    return [PSCustomObject]@{
        Type = $typeInput
        Scope = $scopeInput
        Description = $descriptionInput
        Skip = $skipCommit
    }
}

function Check-VersionAgreements {
    $root = Get-WorkspaceRoot
    $localData = Get-VersionInfoFromScript -AllowNoCommits
    $tags = Get-GitOutput -Args @('ls-remote', '--tags', 'origin') -WorkingDirectory $root
    $remoteTag = $tags -split "`n" | Where-Object { $_ -match 'refs/tags/v\d+\.\d+\.\d+$' } | ForEach-Object { ($_ -split '\s+')[1] -replace 'refs/tags/', '' } | Sort-Object {[Version]($_.TrimStart('v'))} | Select-Object -Last 1

    if ($localData -and $localData.NextTag) {
        $localNext = $localData.NextTag
        if ($remoteTag) {
            try {
                $localV = [Version]($localNext.TrimStart('v'))
                $remoteV = [Version]($remoteTag.TrimStart('v'))
                if ($remoteV -ge $localV) {
                    Write-Host "Warning: remote tag $remoteTag is ahead of or equal to local next version $localNext" -ForegroundColor Yellow
                }
            } catch {
                Write-Host "Unable to compare local ($localNext) and remote ($remoteTag) versions." -ForegroundColor Yellow
            }
        }
    }
}

function Prompt-MultiSelect {
    param(
        [string[]]$Options,
        [string]$Key = 'Tasks'
    )
    Clear-WizardProgress

    if (-not (Is-Interactive)) {
        $response = Get-WizardResponse -Key $Key -Default $null
        $resolved = @(Resolve-TaskSelectionValue -Selection $response -Options $Options)
        if ($resolved.Count -gt 0) {
            Write-Host 'Non-interactive session: using task selection from responses file.' -ForegroundColor Yellow
            return $resolved
        }

        if ($AutoConfirm) {
            Write-Host 'Non-interactive session: AutoConfirm selecting all tasks.' -ForegroundColor Yellow
            return 0..($Options.Length-1)
        }
        Write-Host 'Non-interactive session detected and no AutoConfirm: defaulting to all tasks.' -ForegroundColor Yellow
        return 0..($Options.Length-1)
    }

    for ($i = 0; $i -lt $Options.Length; $i++) {
        Write-Host "[$($i+1)] $($Options[$i])"
    }

    $attempt = 0
    while ($true) {
        $attempt++
        $s = Read-Host "Select tasks to run (comma-separated numbers, ranges e.g. 1-3, 'all', or 'none')"

        if (-not [string]::IsNullOrWhiteSpace($s)) { break }

        if ($AutoConfirm) {
            Write-Host 'AutoConfirm: selecting all tasks.' -ForegroundColor Yellow
            return 0..($Options.Length-1)
        }

        if ($attempt -lt 3) {
            Write-Host "No input detected. Click the terminal, type a selection, then press Enter. (Try $attempt of 3)" -ForegroundColor Yellow
            Start-Sleep -Milliseconds 150
            continue
        }

        Write-Host 'No task input after multiple attempts; defaulting to all tasks.' -ForegroundColor Yellow
        return 0..($Options.Length-1)
    }

    $s = $s.Trim().ToLowerInvariant()
    if ($s -eq 'all' -or $s -eq 'a') { return 0..($Options.Length-1) }
    if ($s -eq 'none' -or $s -eq 'n') { return @() }

    $indices = New-Object System.Collections.Generic.List[int]
    foreach ($part in $s -split ',') {
        $part = $part.Trim()
        if (-not $part) { continue }

        if ($part -match '^(\d+)-(\d+)$') {
            $start = [int]$Matches[1]
            $end = [int]$Matches[2]
            if ($start -le 0 -or $end -le 0 -or $start -gt $end) {
                Write-Host "Ignoring invalid range '$part'" -ForegroundColor Yellow
                continue
            }
            for ($n = $start; $n -le $end; $n++) {
                if ($n -ge 1 -and $n -le $Options.Length) { $indices.Add($n - 1) }
            }
            continue
        }

        $n = 0
        if ([int]::TryParse($part, [ref]$n)) {
            if ($n -ge 1 -and $n -le $Options.Length) {
                $indices.Add($n - 1)
            } else {
                Write-Host "Ignoring out-of-range selection '$part'" -ForegroundColor Yellow
            }
        } else {
            Write-Host "Ignoring invalid selection '$part'" -ForegroundColor Yellow
        }
    }

    return $indices | Sort-Object -Unique
}

function Get-ShellCommand {
    if (Get-Command pwsh -ErrorAction SilentlyContinue) { return 'pwsh' }
    if (Get-Command powershell -ErrorAction SilentlyContinue) { return 'powershell' }
    throw 'No PowerShell executable found (pwsh or powershell).'
}

function Get-NormalizedProcessExitCode {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return 1
    }

    try {
        $Process.Refresh()
    }
    catch {}

    # Windows can leave ExitCode unset after WaitForExit; $null -ne 0 is true and would falsely fail the step.
    $code = $Process.ExitCode
    if ($null -eq $code) {
        return 0
    }

    return [int]$code
}

function Stop-ProcessTree {
    param([int]$ProcessId)

    try {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" -ErrorAction SilentlyContinue)
        foreach ($child in $children) {
            Stop-ProcessTree -ProcessId ([int]$child.ProcessId)
        }
    }
    catch {}

    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
    catch {}
}

function Show-DirtySummary {
    $root = Get-WorkspaceRoot
    $dirty = Get-GitOutput -Args @('status', '--porcelain') -WorkingDirectory $root
    if (-not [string]::IsNullOrWhiteSpace($dirty)) {
        $files = @(ConvertFrom-GitStatusLines -Lines @($dirty -split "`n"))
        Write-Host "Release summary: working tree dirty ($($files.Count) files changed)" -ForegroundColor Yellow
        @($files | Select-Object -First 10) | ForEach-Object { Write-Host " - $_" }
        if ($files.Count -gt 10) { Write-Host " - ... and $($files.Count - 10) more" -ForegroundColor Yellow }
    }
    else {
        Write-Host 'Release summary: working tree clean.' -ForegroundColor Green
    }
}

function Compose-CommandForScript {
    param([string]$ScriptName)
    $shell = Get-ShellCommand
    return @($shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $ScriptName))
}

function Get-WizardTaskArguments {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptName,
        [Parameter(Mandatory = $true)][bool]$AllowDirty,
        [Parameter(Mandatory = $true)][bool]$DisablePublishAutoCommit,
        [string]$DetectedVersion,
        [string]$PublishCommitType,
        [string]$PublishCommitScope,
        [string]$PublishCommitDescription,
        [bool]$ApproveMajorVersion = $false
    )

    $extraArgs = @()
    $metadata = Get-TaskMetadata -ScriptName $ScriptName

    if ($AllowDirty -and ($ScriptName -eq 'preflight.ps1' -or $ScriptName -like 'publish-*' -or $ScriptName -like 'release-*')) {
        $extraArgs += '-AllowDirty'
    }

    if ($NonInteractive -and [bool](Get-TaskMetadataValue -Metadata $metadata -Name 'supportsNonInteractive' -Default $false)) {
        $extraArgs += '-NonInteractive'
    }

    if (-not [string]::IsNullOrWhiteSpace($ResponsesFile) -and [bool](Get-TaskMetadataValue -Metadata $metadata -Name 'supportsResponsesFile' -Default $false)) {
        $extraArgs += @('-ResponsesFile', $ResponsesFile)
    }

    if ($ScriptName -like 'publish-*') {
        if ($ApproveMajorVersion) {
            $extraArgs += '-ApproveMajorVersion'
        }
        if ($DisablePublishAutoCommit) {
            $extraArgs += '-AutoCommitChanges:$false'
        }
        if (-not [string]::IsNullOrWhiteSpace($PublishCommitType)) {
            $extraArgs += @('-AutoCommitType', $PublishCommitType)
        }
        if (-not [string]::IsNullOrWhiteSpace($PublishCommitScope)) {
            $extraArgs += @('-AutoCommitScope', $PublishCommitScope)
        }
        if (-not [string]::IsNullOrWhiteSpace($PublishCommitDescription)) {
            $extraArgs += @('-AutoCommitDescription', $PublishCommitDescription)
        }
    }

    if ($ScriptName -eq 'changelog.ps1' -and -not [string]::IsNullOrWhiteSpace($DetectedVersion)) {
        $extraArgs += @('-Version', $DetectedVersion)
    }

    return $extraArgs
}

function Run-ScriptByName {
    param(
        [string]$ScriptName,
        [bool]$DryRun = $false,
        [int]$CurrentStep = 0,
        [int]$TotalSteps = 0,
        [string[]]$ExtraArgs,
        $RunState = $null
    )

    $scriptPath = Join-Path $PSScriptRoot $ScriptName
    if (-not (Test-Path $scriptPath)) {
        throw "Script not found: $scriptPath"
    }

    $cmd = Compose-CommandForScript -ScriptName $ScriptName
    $command = $cmd[0]
    $args = $cmd[1..($cmd.Length-1)]
    if ($ExtraArgs) { $args += $ExtraArgs }

    if ($DryRun) {
        if ($RunState -and $CurrentStep -gt 0) {
            Update-WizardStepState -RunState $RunState -StepIndex $CurrentStep -State 'running' -Message 'Dry-run command preview'
            Update-WizardStepState -RunState $RunState -StepIndex $CurrentStep -State 'skipped' -ExitCode 0 -Message 'Dry-run mode'
        }
        Write-Host "DRY-RUN: $command $($args -join ' ')"
        return $true
    }

    $activity = "[$CurrentStep/$TotalSteps] Running $ScriptName"
    if ($RunState -and $CurrentStep -gt 0) {
        Update-WizardStepState -RunState $RunState -StepIndex $CurrentStep -State 'running' -Message $activity
    }
    Write-Host "$activity ..."

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stepExitCode = 0
    $metadata = Get-TaskMetadata -ScriptName $ScriptName
    $metadataTimeout = [int](Get-TaskMetadataValue -Metadata $metadata -Name 'timeoutSeconds' -Default 0)
    $effectiveTimeoutSeconds = if ($TaskTimeoutSeconds -gt 0) { $TaskTimeoutSeconds } else { $metadataTimeout }
    $timeoutLabel = if ($effectiveTimeoutSeconds -gt 0) { "$effectiveTimeoutSeconds s idle" } else { 'none' }

    try {
        $scriptTextIsInteractive = $false
        $metadataMayPrompt = Get-TaskMetadataValue -Metadata $metadata -Name 'mayPrompt' -Default $null
        if ($null -ne $metadataMayPrompt) {
            $scriptTextIsInteractive = [bool]$metadataMayPrompt
        } else {
            try {
                if (Test-Path $scriptPath) {
                    $scriptTextIsInteractive = Select-String -Path $scriptPath -Pattern 'Read-Host' -SimpleMatch -Quiet
                }
            } catch {}
        }

        $requiresAttachedConsole = (Is-Interactive) -and $scriptTextIsInteractive

        # Interactive targets must stay attached: Invoke-LoggedCommand redirects streams and breaks Read-Host.
        $quotedArguments = @($args | ForEach-Object { ConvertTo-CommandLineArgument -Value $_ }) -join ' '
        Write-Host ''

        if ($requiresAttachedConsole) {
            $process = Start-Process -FilePath $command -ArgumentList $quotedArguments -WorkingDirectory (Get-WorkspaceRoot) -NoNewWindow -PassThru
            $lastActivityAt = [DateTime]::UtcNow
            $lastCpuTime = [timespan]::Zero
            try { $lastCpuTime = $process.TotalProcessorTime } catch {}
            $lastChildCount = -1
            while (-not $process.HasExited) {
                $pulse = Get-ProcessTreeActivityPulse -ProcessId $process.Id -LastCpuTime $lastCpuTime -LastChildCount $lastChildCount
                $lastCpuTime = $pulse.CpuNow
                $lastChildCount = $pulse.ChildCount

                $nowUtc = [DateTime]::UtcNow
                if ($pulse.PulseStrength -gt 0) {
                    $lastActivityAt = $nowUtc
                }

                $idleSeconds = ($nowUtc - $lastActivityAt).TotalSeconds
                if ($effectiveTimeoutSeconds -gt 0 -and $idleSeconds -ge $effectiveTimeoutSeconds) {
                    Stop-ProcessTree -ProcessId $process.Id
                    throw "Script $ScriptName timed out after $timeoutLabel (no activity for $([int][math]::Round($idleSeconds))s)."
                }
                Start-Sleep -Milliseconds 250
            }
            $process.WaitForExit()
            $stepExitCode = Get-NormalizedProcessExitCode -Process $process
            if ($stepExitCode -ne 0) {
                throw "Script $ScriptName failed with exit code $stepExitCode."
            }
        }
        else {
            Invoke-LoggedCommand -Command $command -Arguments $args -WorkingDirectory (Get-WorkspaceRoot)
            $stepExitCode = [int]$LASTEXITCODE
        }

        $stopwatch.Stop()
        if ($RunState -and $CurrentStep -gt 0) {
            Update-WizardStepState -RunState $RunState -StepIndex $CurrentStep -State 'succeeded' -ExitCode $stepExitCode -Message "Completed in $($stopwatch.Elapsed.TotalSeconds.ToString('F1'))s"
        }
        Write-Host "`r$activity completed in $($stopwatch.Elapsed.TotalSeconds.ToString('F1'))s.          " -ForegroundColor Green
        return $true
    }
    catch {
        $stopwatch.Stop()
        $msg = $_.Exception.Message
        if ($RunState -and $CurrentStep -gt 0) {
            Update-WizardStepState -RunState $RunState -StepIndex $CurrentStep -State 'failed' -ExitCode 1 -Message $msg
        }
        Write-Host "`n$activity failed in $($stopwatch.Elapsed.TotalSeconds.ToString('F1'))s: $msg" -ForegroundColor Red
        Write-Host "Failure details: step=$CurrentStep script=$ScriptName elapsed=$($stopwatch.Elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Red
        if ($_.Exception.InnerException) {
            Write-Host "Cause: $($_.Exception.InnerException.Message)" -ForegroundColor Red
        }
        return $false
    }
}

function Show-Header { Write-Host '=== Crispy_Bills Release Wizard ===' -ForegroundColor Cyan }

if ($RequireCleanTree -and $AllowDirty) {
    throw 'Specify either -AllowDirty or -RequireCleanTree, not both.'
}

if ($MyInvocation.InvocationName -eq '.') {
    return
}

Show-Header
Show-DirtySummary

# Honor the top-level switch parameter `-DryRun` by initializing the local `$dryRun` variable.
# Some code paths set `$dryRun` interactively later; prefer the explicit parameter when provided.
$dryRun = $DryRun
$allowDirtyForFlow = if ($RequireCleanTree) { $false } elseif ($PSBoundParameters.ContainsKey('AllowDirty')) { [bool]$AllowDirty } else { $true }

$available = @(
    'preflight.ps1',
    'build-both.ps1',
    'build-windows.ps1',
    'build-mobile.ps1',
    'version.ps1',
    'changelog.ps1',
    'release-both.ps1',
    'release-windows.ps1',
    'release-mobile.ps1',
    'publish-both.ps1',
    'publish-windows.ps1',
    'publish-mobile.ps1',
    'recover-missing-release.ps1'
)

Test-WizardNonInteractiveReadiness -ProvidedTasks $Tasks -HasExplicitDryRun:$PSBoundParameters.ContainsKey('DryRun')

Write-Host "Discovered release scripts (from: $PSScriptRoot):" -ForegroundColor Yellow
$chosen = @()

$taskList = @()
foreach ($taskToken in @($Tasks)) {
    if ($null -eq $taskToken) { continue }
    foreach ($splitToken in @(([string]$taskToken -split ','))) {
        $trimmedToken = $splitToken.Trim()
        if ($trimmedToken.Length -gt 0) {
            $taskList += $trimmedToken
        }
    }
}
if ($taskList.Count -gt 0) {
    $resolvedSelection = @(Resolve-TaskSelectionValue -Selection $taskList -Options $available)
    if ($resolvedSelection.Count -gt 0) {
        $chosen = @($resolvedSelection | ForEach-Object { $available[$_] })
    }
    else {
        Write-Host 'No valid tasks provided via -Tasks; falling back to interactive selection.' -ForegroundColor Yellow
        $taskList = @()
    }
}

if ($taskList.Count -eq 0 -and @($chosen).Count -eq 0) {
    $sel = @((Prompt-MultiSelect -Options $available -Key 'Tasks'))
    if ($sel.Count -eq 0) {
        Write-Host 'No tasks selected; exiting.' -ForegroundColor Yellow
        exit 0
    }
    $chosen = @($sel | ForEach-Object { $available[$_] })
}

$chosen = @($chosen)

Write-Host "Selected tasks:" -ForegroundColor Green
$chosen | ForEach-Object { Write-Host " - $_" }

$containsPublishTask = (@($chosen | Where-Object { $_ -like 'publish-*' })).Count -gt 0
$wizardChangedFiles = @(Get-ChangedFiles)
$publishAutoCommitDisabled = [bool]$NoCommit
$publishCommitType = $CommitType
$publishCommitScope = $CommitScope
$publishCommitDescription = $CommitMessage
$plannedVersionInfo = $null
$majorApprovalGranted = [bool]$ApproveMajorVersion

$total = @($chosen).Count
$wizardRunState = $null
if (Get-Command -Name New-WizardRunState -ErrorAction SilentlyContinue) {
    $wizardRunState = New-WizardRunState -Steps $chosen -DryRun:$dryRun -NonInteractive:$NonInteractive
}

if ($NoCommit) {
    Write-Host 'NoCommit requested; skipping commit step.' -ForegroundColor Yellow
    $doCommit = $false
}
elseif ($AutoConfirm) {
    Write-Host 'AutoConfirm requested; non-interactive defaults selected.' -ForegroundColor Yellow
    $doCommit = -not $containsPublishTask
    if (-not $PSBoundParameters.ContainsKey('DryRun')) {
        $dryRun = $false
    }
}
elseif ($containsPublishTask) {
    Write-Host 'Publish tasks selected; the wizard will gather pre-publish commit choices and pass them to publish scripts.' -ForegroundColor Yellow
    $doCommit = $false
    if ($PSBoundParameters.ContainsKey('DryRun')) {
        Write-Host "Using provided -DryRun parameter: $dryRun" -ForegroundColor Yellow
    }
    else {
        $dryRun = Prompt-YesNo -Message 'Run in dry-run mode (show commands but do not execute)?' -Default $false -Key 'DryRun'
    }

    if (@($wizardChangedFiles).Count -gt 0) {
        $allowPublishAutoCommit = Prompt-YesNo -Message 'If publish needs a pre-publish commit for current changes, let it create one?' -Default $true -Key 'AllowPublishAutoCommit'
        $publishAutoCommitDisabled = -not $allowPublishAutoCommit

        if (-not $publishAutoCommitDisabled) {
            $recommendedType = Get-RecommendedCommitType -files $wizardChangedFiles
            $recommendedMessage = Get-RecommendedCommitMessage -type $recommendedType -files $wizardChangedFiles
            $publishCommit = Prompt-CommitMetadata -RecommendedType $recommendedType -RecommendedDescription $recommendedMessage -InitialScope $CommitScope -InitialDescription $CommitMessage -AllowSkip -ResponsePrefix 'PublishCommit'
            if ($publishCommit.Skip) {
                $publishAutoCommitDisabled = $true
            }
            else {
                $publishCommitType = $publishCommit.Type
                $publishCommitScope = $publishCommit.Scope
                $publishCommitDescription = $publishCommit.Description
            }
        }
    }
}
else {
    $doCommit = Prompt-YesNo -Message 'Create a commit as part of the flow (will run conventional-commit.ps1 if available)?' -Default $true -Key 'DoCommit'

    if ($PSBoundParameters.ContainsKey('DryRun')) {
        Write-Host "Using provided -DryRun parameter: $dryRun" -ForegroundColor Yellow
    } else {
        $dryRun = Prompt-YesNo -Message 'Run in dry-run mode (show commands but do not execute)?' -Default $false -Key 'DryRun'
    }

}

if ($allowDirtyForFlow) {
    Write-Host 'Dirty working tree is allowed for this wizard flow. Use -RequireCleanTree to enforce a clean tree.' -ForegroundColor Yellow
}
else {
    Write-Host 'Clean working tree is required for this wizard flow.' -ForegroundColor Yellow
}

if ($containsPublishTask) {
    $plannedVersionInfo = Get-VersionInfoFromScript -AllowNoCommits
    if ($null -ne $plannedVersionInfo -and $plannedVersionInfo.HasChanges) {
        if ($plannedVersionInfo.Bump -eq 'major') {
            Show-WarningBlock -Title 'MAJOR VERSION PROPOSAL' -Lines @(
                "Current tag: $(if ([string]::IsNullOrWhiteSpace($plannedVersionInfo.CurrentTag)) { 'none' } else { $plannedVersionInfo.CurrentTag })",
                "Next tag: $($plannedVersionInfo.NextTag)",
                'Major releases require explicit approval before a real publish.'
            )
        }

        $majorApprovalGranted = Confirm-MajorVersionApproval -VersionInfo $plannedVersionInfo -DryRun:$dryRun -ExplicitApproval:$ApproveMajorVersion
    }
}

if ($doCommit) {
    if (Test-Path (Join-Path $PSScriptRoot 'conventional-commit.ps1')) {
        Write-Host 'Will run conventional-commit.ps1 during the flow.'
        $commitScript = 'conventional-commit.ps1'
    }
    else {
        Write-Host 'No conventional-commit.ps1 found; will prompt for a manual commit message.'
        $commitScript = $null
    }
    if ($NonInteractive) {
        Write-Host 'Non-interactive mode: enabling AutoCommit to avoid interactive commit prompts.' -ForegroundColor Yellow
        $AutoCommit = $true
    }
}

# Run release recovery in advisory mode before publish tasks. This check must stay non-destructive.
if ($containsPublishTask -and (Test-Path (Join-Path $PSScriptRoot 'recover-missing-release.ps1'))) {
    Write-Host 'Running advisory recovery pre-check (recover-missing-release.ps1 -DryRun)...' -ForegroundColor Cyan
    $recoveryPreCheckSucceeded = Run-ScriptByName -ScriptName 'recover-missing-release.ps1' -DryRun:$dryRun -CurrentStep 0 -TotalSteps $total -ExtraArgs @('-DryRun') -RunState $null
    if (-not $recoveryPreCheckSucceeded) {
        Write-Host 'Recovery pre-check reported a problem. Continuing because this check is advisory; run recover-missing-release.ps1 directly for details if needed.' -ForegroundColor Yellow
    }
}

if ($containsPublishTask -and -not $dryRun) {
    Write-Host 'Warning: dry-run is off. Publish tasks may create commits, build release artifacts, push tags, and create GitHub releases.' -ForegroundColor Red
}

if (-not $AutoConfirm) {
    if (-not (Prompt-YesNo -Message 'Proceed with the selected operations?' -Default $true -Key 'Proceed')) {
        Write-Host 'Aborted by user.' -ForegroundColor Yellow
        exit 1
    }
} else {
    Write-Host 'AutoConfirm: proceeding without interactive confirmation.' -ForegroundColor Yellow
}

if ($containsPublishTask) {
    Write-Host "`n=== GitHub CLI (publish) ===" -ForegroundColor Cyan
    Ensure-GitHubCliAuthenticated -TokenFile $GitHubTokenFile
}

try {
    $total = @($chosen).Count
    $detectedVersion = $null

    for ($index = 0; $index -lt $total; $index++) {
        $s = $chosen[$index]
        Write-Host "`n=== Running: $s ===" -ForegroundColor Cyan

        $extraArgs = Get-WizardTaskArguments -ScriptName $s -AllowDirty:$allowDirtyForFlow -DisablePublishAutoCommit:$publishAutoCommitDisabled -DetectedVersion $detectedVersion -PublishCommitType $publishCommitType -PublishCommitScope $publishCommitScope -PublishCommitDescription $publishCommitDescription -ApproveMajorVersion:$majorApprovalGranted

        if ($s -eq 'changelog.ps1' -and [string]::IsNullOrWhiteSpace($detectedVersion)) {
                Write-Host 'changelog requires a Version. Computing suggested version using version.ps1...'

                $detectedVersion = Get-DetectedVersionFromScript -AllowNoCommits

                if (-not [string]::IsNullOrWhiteSpace($detectedVersion)) {
                    if (-not $AutoConfirm) {
                        if (-not (Prompt-YesNo -Message "Use detected version $detectedVersion for changelog?" -Default $true -Key 'UseDetectedChangelogVersion')) {
                            $detectedVersion = Get-WizardResponse -Key 'ChangelogVersion' -Default $null
                            if ([string]::IsNullOrWhiteSpace($detectedVersion) -and (Is-Interactive)) {
                                $detectedVersion = Read-Host 'Enter version to use for changelog (e.g. 1.4.0)'
                            }
                        }
                    }
                }
                else {
                    if ($dryRun -and $AutoConfirm) {
                        Write-Host 'DRY-RUN: no detected version; changelog will be run without a version in dry-run (no-op).'
                    } else {
                        $detectedVersion = Get-WizardResponse -Key 'ChangelogVersion' -Default $null
                        if ([string]::IsNullOrWhiteSpace($detectedVersion)) {
                            if (-not (Is-Interactive) -and $RequireNonInteractiveReady) {
                                throw 'Non-interactive readiness check failed: changelog requires wizard.ChangelogVersion when no version can be detected.'
                            }
                            if (Is-Interactive) {
                                $detectedVersion = Read-Host 'Enter version to use for changelog (e.g. 1.4.0)'
                            }
                        }
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($detectedVersion)) {
                    $extraArgs = Get-WizardTaskArguments -ScriptName $s -AllowDirty:$allowDirtyForFlow -DisablePublishAutoCommit:$publishAutoCommitDisabled -DetectedVersion $detectedVersion -PublishCommitType $publishCommitType -PublishCommitScope $publishCommitScope -PublishCommitDescription $publishCommitDescription -ApproveMajorVersion:$majorApprovalGranted
                }
        }

        if ($s -eq 'version.ps1') {
            $stepSucceeded = Run-ScriptByName -ScriptName $s -DryRun:$dryRun -CurrentStep ($index + 1) -TotalSteps $total -ExtraArgs $extraArgs -RunState $wizardRunState
            if (-not $stepSucceeded) {
                throw "Step failed: $s. Aborting release flow."
            }

            if (-not $dryRun) {
                $versionOutput = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $s) -AllowNoCommits | Out-String
                try {
                    $json = $versionOutput | ConvertFrom-Json -ErrorAction Stop
                    if ($json -and $json.Version) { $detectedVersion = $json.Version }
                } catch {
                    Write-Host 'Warning: Unable to parse version output from version.ps1' -ForegroundColor Yellow
                }
            }

            continue
        }

        $stepSucceeded = Run-ScriptByName -ScriptName $s -DryRun:$dryRun -CurrentStep ($index + 1) -TotalSteps $total -ExtraArgs $extraArgs -RunState $wizardRunState
        if (-not $stepSucceeded) {
            throw "Step failed: $s. Aborting release flow."
        }
    }

    if ($doCommit -and -not $containsPublishTask) {
        Write-Host "`n=== Commit Step ===" -ForegroundColor Cyan

        Check-VersionAgreements

        $changedFiles = @(Get-ChangedFiles)
        $recommendedType = Get-RecommendedCommitType -files $changedFiles
        $recommendedMessage = Get-RecommendedCommitMessage -type $recommendedType -files $changedFiles

        if ($commitScript) {
            if (-not $AutoCommit) {
                $commitSelection = Prompt-CommitMetadata -RecommendedType $recommendedType -RecommendedDescription $recommendedMessage -InitialScope $CommitScope -InitialDescription $CommitMessage -ResponsePrefix 'Commit'

                $typeInput = $commitSelection.Type
                $scopeInput = $commitSelection.Scope
                $descriptionInput = $commitSelection.Description
                if (-not (Is-Interactive)) {
                    Write-Host 'Non-interactive session: skipping optional commit body and breaking-change prompt.' -ForegroundColor Yellow
                    $bodyInput = Get-WizardResponse -Key 'CommitBody' -Default $null
                    $breakingInput = [bool](Get-WizardResponse -Key 'CommitBreaking' -Default $false)
                }
                else {
                    $bodyInput = Read-Host 'Optional commit body (single line, press Enter to skip)'
                    Write-Host 'Breaking changes trigger a major release and require explicit approval before publish.' -ForegroundColor Yellow
                    $breakingInput = Prompt-YesNo -Message 'Breaking change? (y/N)' -Default $false -Key 'CommitBreaking'
                }

                $CommitType = $typeInput
                $CommitScope = $scopeInput
                $CommitMessage = $descriptionInput
                $CommitBody = $bodyInput
                $BreakingChange = $breakingInput
            }

            $commitArgs = @()
            if ($AutoCommit) { $commitArgs += '-Auto' }
            if ($NonInteractive) { $commitArgs += '-NonInteractive' }
            if ($CommitType) { $commitArgs += @('-Type', $CommitType) }
            if ($CommitScope) { $commitArgs += @('-Scope', $CommitScope) }
            if ($CommitMessage) { $commitArgs += @('-Description', $CommitMessage) }
            if ($CommitBody) { $commitArgs += @('-Body', $CommitBody) }
            if ($BreakingChange) { $commitArgs += '-Breaking' }
            if ($ResponsesFile) { $commitArgs += @('-ResponsesFile', $ResponsesFile) }
            if ($dryRun) { $commitArgs += '-DryRun' }
            # Wizard already collected metadata (Prompt-CommitMetadata or CLI); avoid duplicate prompts in conventional-commit.ps1.
            if (-not $AutoCommit) { $commitArgs += '-SkipPrompt' }

            $commitOk = Run-ScriptByName -ScriptName $commitScript -DryRun:$dryRun -ExtraArgs $commitArgs
            if (-not $commitOk) {
                throw 'Commit step failed (conventional-commit.ps1 reported failure).'
            }
        }
        else {
            if ($dryRun) {
                Write-Host "DRY-RUN: git add -A && git commit -m '<message>'"
            }
            else {
                if ($AutoCommit -or $AutoConfirm) {
                    $autoMsg = $CommitMessage
                    if (-not $autoMsg) {
                        $autoMsg = "${recommendedType}: ${recommendedMessage}"
                    }
                    Push-Location (Get-WorkspaceRoot)
                    try { & git add -A; & git commit -m $autoMsg }
                    finally { Pop-Location }
                }
                else {
                    if (-not (Is-Interactive)) {
                        Write-Host 'Non-interactive session detected: skipping manual commit prompt.' -ForegroundColor Yellow
                        $msg = $null
                    }
                    else {
                        do {
                            $msg = Read-Host "Enter commit message (conventional style recommended, or type SKIP to skip commit) [default: $recommendedMessage]"
                            if ([string]::IsNullOrWhiteSpace($msg)) { $msg = $recommendedMessage }
                            if ($msg -eq 'SKIP') {
                                Write-Host 'Skipping commit step.' -ForegroundColor Yellow
                                break
                            }

                            if ([string]::IsNullOrWhiteSpace($msg)) {
                                Write-Host 'Commit message cannot be empty unless SKIP is entered.' -ForegroundColor Yellow
                            }
                        } while ([string]::IsNullOrWhiteSpace($msg))
                    }

                    if ($msg -and $msg -ne 'SKIP') {
                        Push-Location (Get-WorkspaceRoot)
                        try { & git add -A; & git commit -m $msg }
                        finally { Pop-Location }
                    }
                }
            }
        }
    }

    if (Get-Command Write-TaskDiagnostics -ErrorAction SilentlyContinue) {
        Write-TaskDiagnostics -Prefix 'Wizard'
    } else {
        Write-Host 'Warning: Write-TaskDiagnostics not available.' -ForegroundColor Yellow
    }
    Write-Host "`nWizard completed." -ForegroundColor Green

    if ($wizardRunState) {
        Complete-WizardRunState -RunState $wizardRunState -Status 'succeeded'
        Write-WizardSummary -RunState $wizardRunState
        if (-not $NoProgressJson) {
            try {
                $progressPath = Write-WizardProgressJson -RunState $wizardRunState
                if (-not [string]::IsNullOrWhiteSpace($progressPath)) {
                    Write-Host "Wizard progress JSON: $progressPath" -ForegroundColor Cyan
                }
            }
            catch {
                Write-Host "Warning: failed to write wizard progress JSON: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }
}
catch {
    if ($wizardRunState) {
        Complete-WizardRunState -RunState $wizardRunState -Status 'failed'
        Write-WizardSummary -RunState $wizardRunState
        if (-not $NoProgressJson) {
            try {
                $progressPath = Write-WizardProgressJson -RunState $wizardRunState
                if (-not [string]::IsNullOrWhiteSpace($progressPath)) {
                    Write-Host "Wizard progress JSON: $progressPath" -ForegroundColor Cyan
                }
            }
            catch {
                Write-Host "Warning: failed to write wizard progress JSON: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }
    Write-Host "Wizard failed: $($_.Exception.Message)" -ForegroundColor Red
    if (Get-Command Write-TaskDiagnostics -ErrorAction SilentlyContinue) {
        Write-TaskDiagnostics -Prefix 'Wizard (partial)'
    } else {
        Write-Host 'Warning: Write-TaskDiagnostics not available.' -ForegroundColor Yellow
    }
    exit 1
}

<#
Interactive release wizard for local-first release orchestration.

This script is an orchestrator that delegates to the existing scripts in this
folder (build-*.ps1, publish-*.ps1, release-*.ps1, preflight.ps1, etc.). It
keeps the original scripts as the execution engines and only provides an
interactive selection, dry-run, commit synthesis, and confirmation checkpoints.
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
    [switch]$SkipVersion,
    [string]$ResponsesFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#. Load shared helpers (Invoke-LoggedCommand, Get-WorkspaceRoot, etc.)
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
    try { Write-Progress -Activity 'Crispy_Bills Release Wizard' -Status 'Clearing' -Completed } catch {}
    Write-Host ''
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
    # Clear any lingering progress UI so Read-Host prompt is visible/clickable
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

function Get-ChangedFiles {
    $root = Get-WorkspaceRoot
    $status = Get-GitOutput -Args @('status', '--porcelain') -WorkingDirectory $root
    if (-not $status) { return @() }
    $lines = $status -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $files = $lines | ForEach-Object { $_ -replace '^[ MADRCU?!]+', '' }
    return $files | Sort-Object -Unique
}

function Get-RecommendedCommitType {
    param([string[]]$files)

    if (-not $files -or $files.Count -eq 0) { return 'chore' }

    $lower = $files | ForEach-Object { $_.ToLowerInvariant() }
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
        # inspect diff for explicit fix indicators
        $diff = Get-GitOutput -Args @('diff', '--', $files) -WorkingDirectory (Get-WorkspaceRoot)
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
    $diffSummary = Get-GitOutput -Args @('diff', '--stat', '--', $files) -WorkingDirectory $root

    if ($type -eq 'docs') {
        return "docs: update documentation based on recent changes"
    }
    if ($type -eq 'test') {
        return "test: add/update tests for changed behavior"
    }
    if ($type -eq 'build') {
        return "build: update build process / dependencies"
    }
    if ($type -eq 'fix') {
        return "fix: correct behavior based on current change set"
    }

    return "feat: implement new behavior and improvements"
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
        # Non-interactive: return recommended values or indicate skip depending on flags
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
    $local = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'version.ps1') -OutFile ([IO.Path]::GetTempFileName()) | Out-String
    $tags = Get-GitOutput -Args @('ls-remote', '--tags', 'origin') -WorkingDirectory $root
    $remoteTag = $tags -split "`n" | Where-Object { $_ -match 'refs/tags/v\d+\.\d+\.\d+$' } | ForEach-Object { ($_ -split '\s+')[1] -replace 'refs/tags/', '' } | Sort-Object {[Version]($_.TrimStart('v'))} | Select-Object -Last 1

    $localData = $local | ConvertFrom-Json -ErrorAction SilentlyContinue
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
    # Clear any lingering progress UI so terminal input receives focus
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

function Show-DirtySummary {
    $root = Get-WorkspaceRoot
    $dirty = Get-GitOutput -Args @('status', '--porcelain') -WorkingDirectory $root
    if (-not [string]::IsNullOrWhiteSpace($dirty)) {
        $files = $dirty -split "`n" | ForEach-Object { ($_.Trim() -replace '^[ MADRCU?!]+', '') } | Where-Object { $_ }
        Write-Host "Release summary: working tree dirty ($($files.Count) files changed)" -ForegroundColor Yellow
        $files | Select-Object -First 10 | ForEach-Object { Write-Host " - $_" }
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
        [string]$PublishCommitDescription
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
        [string[]]$ExtraArgs
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
        Write-Host "DRY-RUN: $command $($args -join ' ')"
        return $true
    }

    $activity = "[$CurrentStep/$TotalSteps] Running $ScriptName"
    $progressPercent = if ($TotalSteps -gt 0) { [math]::Floor(($CurrentStep / $TotalSteps) * 100) } else { 0 }
    Write-Host "$activity ..."
    Write-Progress -Activity 'Crispy_Bills Release Wizard' -Status $activity -PercentComplete $progressPercent

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $scriptTextIsInteractive = $false
        $metadata = Get-TaskMetadata -ScriptName $ScriptName
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
        $useSpinner = -not $NoSpinner -and -not $requiresAttachedConsole

        # Build a properly quoted commandline so we can reuse for both interactive and non-interactive starts.
        $quotedArguments = @($args | ForEach-Object { ConvertTo-CommandLineArgument -Value $_ }) -join ' '

        if (-not $useSpinner) {
            # If the target script appears interactive (contains Read-Host or similar),
            # we must run it attached to the current console WITHOUT redirecting streams
            # so that Read-Host can receive stdin. Invoke-LoggedCommand redirects output
            # which breaks interactive prompts, so use Start-Process -Wait for interactive cases.
            try {
                Write-Progress -Activity 'Crispy_Bills Release Wizard' -Status 'Completed' -Completed -ErrorAction SilentlyContinue
            } catch {}
            Write-Host ''

            if ($requiresAttachedConsole) {
                $process = Start-Process -FilePath $command -ArgumentList $quotedArguments -WorkingDirectory (Get-WorkspaceRoot) -NoNewWindow -Wait -PassThru
                if ($process.ExitCode -ne 0) {
                    throw "Script $ScriptName failed with exit code $($process.ExitCode)."
                }
            }
            else {
                Invoke-LoggedCommand -Command $command -Arguments $args -WorkingDirectory (Get-WorkspaceRoot)
            }
        } else {
            $process = Start-Process -FilePath $command -ArgumentList $quotedArguments -WorkingDirectory (Get-WorkspaceRoot) -NoNewWindow -PassThru
            while (-not $process.HasExited) {
                for ($i = 0; $i -lt 4 -and -not $process.HasExited; $i++) {
                    $spinner = @('|','/','-','\')[$i]
                    Write-Host -NoNewline "`r$activity $spinner"
                    Write-Progress -Activity 'Crispy_Bills Release Wizard' -Status "$activity $spinner" -PercentComplete $progressPercent
                    Start-Sleep -Milliseconds 150
                }
            }
            $process.WaitForExit()
            $exitCode = [int]$process.ExitCode
            if ($exitCode -ne 0) {
                throw "Script $ScriptName failed with exit code $exitCode."
            }
        }

        $stopwatch.Stop()
        Write-Host "`r$activity completed in $($stopwatch.Elapsed.TotalSeconds.ToString('F1'))s.          " -ForegroundColor Green
        return $true
    }
    catch {
        $stopwatch.Stop()
        $msg = $_.Exception.Message
        Write-Host "`n$activity failed in $($stopwatch.Elapsed.TotalSeconds.ToString('F1'))s: $msg" -ForegroundColor Red
        if ($_.Exception.InnerException) {
            Write-Host "Cause: $($_.Exception.InnerException.Message)" -ForegroundColor Red
        }
        return $false
    }
    finally {
        Write-Progress -Activity 'Crispy_Bills Release Wizard' -Status 'Completed' -PercentComplete 100 -Completed
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

$taskList = @(@($Tasks) | Where-Object { $_ -and ($_.ToString()).Trim().Length -gt 0 })
if ($taskList.Count -gt 0) {
    $lowerAvailable = $available | ForEach-Object { $_.ToLowerInvariant() }
    $hasAllToken = (@($taskList | Where-Object { $_.ToString().Trim().ToLowerInvariant() -eq 'all' })).Count -gt 0
    if ($All -or $hasAllToken) {
        $chosen = $available
    } else {
        foreach ($t in $taskList) {
            if (-not $t) { continue }
            $ttrim = $t.ToString().Trim()
            if (-not $ttrim) { continue }
            if ($lowerAvailable -contains $ttrim.ToLowerInvariant()) {
                $chosen += $available[$lowerAvailable.IndexOf($ttrim.ToLowerInvariant())]
            } else {
                Write-Host "Unknown task specified: $ttrim" -ForegroundColor Yellow
            }
        }

        if ($chosen.Count -eq 0) {
            Write-Host 'No valid tasks provided via -Tasks; falling back to interactive selection.' -ForegroundColor Yellow
            $taskList = @()
        }
    }
}

if ($taskList.Count -eq 0 -and $chosen.Count -eq 0) {
    $sel = @((Prompt-MultiSelect -Options $available -Key 'Tasks'))
    if ($sel.Count -eq 0) {
        Write-Host 'No tasks selected; exiting.' -ForegroundColor Yellow
        exit 0
    }
    $chosen = $sel | ForEach-Object { $available[$_] }
}

$chosen = @($chosen)

Write-Host "Selected tasks:" -ForegroundColor Green
$chosen | ForEach-Object { Write-Host " - $_" }

$containsPublishTask = (@($chosen | Where-Object { $_ -like 'publish-*' })).Count -gt 0
$wizardChangedFiles = Get-ChangedFiles
$publishAutoCommitDisabled = [bool]$NoCommit
$publishCommitType = $CommitType
$publishCommitScope = $CommitScope
$publishCommitDescription = $CommitMessage

# Provide total count early so pre-checks can report progress
$total = $chosen.Count

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

    if ($wizardChangedFiles.Count -gt 0) {
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
    $recoveryPreCheckSucceeded = Run-ScriptByName -ScriptName 'recover-missing-release.ps1' -DryRun:$dryRun -CurrentStep 0 -TotalSteps $total -ExtraArgs @('-DryRun')
    if (-not $recoveryPreCheckSucceeded) {
        Write-Host 'Recovery pre-check reported a problem. Continuing because this check is advisory; run recover-missing-release.ps1 directly for details if needed.' -ForegroundColor Yellow
    }
}

if (-not $AutoConfirm) {
    if (-not (Prompt-YesNo -Message 'Proceed with the selected operations?' -Default $true -Key 'Proceed')) {
        Write-Host 'Aborted by user.' -ForegroundColor Yellow
        exit 1
    }
} else {
    Write-Host 'AutoConfirm: proceeding without interactive confirmation.' -ForegroundColor Yellow
}

try {
    $total = $chosen.Count
    $detectedVersion = $null

    for ($index = 0; $index -lt $total; $index++) {
        $s = $chosen[$index]
        Write-Host "`n=== Running: $s ===" -ForegroundColor Cyan

        $extraArgs = Get-WizardTaskArguments -ScriptName $s -AllowDirty:$allowDirtyForFlow -DisablePublishAutoCommit:$publishAutoCommitDisabled -DetectedVersion $detectedVersion -PublishCommitType $publishCommitType -PublishCommitScope $publishCommitScope -PublishCommitDescription $publishCommitDescription

        if ($s -eq 'changelog.ps1' -and [string]::IsNullOrWhiteSpace($detectedVersion)) {
                Write-Host 'changelog requires a Version. Computing suggested version using version.ps1...'

                # Ensure version.ps1 is executed (run it now) and write clean JSON to a temp file
                $tempOut = [IO.Path]::GetTempFileName()
                try {
                    $verArgs = @('-OutFile', $tempOut, '-AllowNoCommits')
                    $verRunSucceeded = Run-ScriptByName -ScriptName 'version.ps1' -DryRun:$dryRun -CurrentStep ($index + 1) -TotalSteps $total -ExtraArgs $verArgs
                    if ($verRunSucceeded -and -not $dryRun) {
                        try {
                            if (Test-Path $tempOut) {
                                $json = Get-Content -Raw $tempOut | ConvertFrom-Json -ErrorAction Stop
                                if ($json -and $json.Version) { $detectedVersion = $json.Version }
                            } else {
                                Write-Host 'Warning: version output file not found.' -ForegroundColor Yellow
                            }
                        } catch {
                            Write-Host 'Warning: Unable to parse version output from file.' -ForegroundColor Yellow
                        }
                    }
                } finally {
                    if (Test-Path $tempOut) { Remove-Item $tempOut -Force -ErrorAction SilentlyContinue }
                }

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
                    $extraArgs = Get-WizardTaskArguments -ScriptName $s -AllowDirty:$allowDirtyForFlow -DisablePublishAutoCommit:$publishAutoCommitDisabled -DetectedVersion $detectedVersion -PublishCommitType $publishCommitType -PublishCommitScope $publishCommitScope -PublishCommitDescription $publishCommitDescription
                }
        }

        if ($s -eq 'version.ps1') {
            $stepSucceeded = Run-ScriptByName -ScriptName $s -DryRun:$dryRun -CurrentStep ($index + 1) -TotalSteps $total -ExtraArgs $extraArgs
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

        $stepSucceeded = Run-ScriptByName -ScriptName $s -DryRun:$dryRun -CurrentStep ($index + 1) -TotalSteps $total -ExtraArgs $extraArgs
        if (-not $stepSucceeded) {
            throw "Step failed: $s. Aborting release flow."
        }
    }

    if ($doCommit -and -not $containsPublishTask) {
        Write-Host "`n=== Commit Step ===" -ForegroundColor Cyan

        # Version checks before commit
        Check-VersionAgreements

        $changedFiles = Get-ChangedFiles
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
            if ($AutoConfirm) { $commitArgs += '-SkipPrompt' }

            Run-ScriptByName -ScriptName $commitScript -DryRun:$dryRun -ExtraArgs $commitArgs
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
}
catch {
    Write-Host "Wizard failed: $($_.Exception.Message)" -ForegroundColor Red
    if (Get-Command Write-TaskDiagnostics -ErrorAction SilentlyContinue) {
        Write-TaskDiagnostics -Prefix 'Wizard (partial)'
    } else {
        Write-Host 'Warning: Write-TaskDiagnostics not available.' -ForegroundColor Yellow
    }
    exit 1
}

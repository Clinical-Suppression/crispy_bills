param(
    [Parameter(Mandatory = $true)][ValidateSet('windows', 'mobile', 'both')][string]$Target,
    [string]$Branch = 'main',
    [switch]$DryRun,
    [switch]$NoPush,
    [switch]$AllowDirty,
    [switch]$AllowNonMain,
    [bool]$AutoCommitChanges = $true,
    [ValidateSet('feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'chore')][string]$AutoCommitType = 'chore',
    [string]$AutoCommitDescription = 'prepare release changes'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

Reset-TaskDiagnostics

trap {
    if ($_.Exception) {
        Write-Host 'Publish fatal error details:' -ForegroundColor Red
        foreach ($line in (($_.Exception.Message | Out-String).TrimEnd() -split "`r?`n")) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                Write-Host ("  " + $line) -ForegroundColor Red
            }
        }
    }
    if ($_.ScriptStackTrace) {
        Write-Host 'Publish stack:' -ForegroundColor Red
        foreach ($line in (($_.ScriptStackTrace | Out-String).TrimEnd() -split "`r?`n")) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                Write-Host ("  " + $line) -ForegroundColor DarkRed
            }
        }
    }
    Write-TaskDiagnostics -Prefix 'Publish task'
    throw
}

function Test-TagExistsLocal {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $output = & git rev-parse -q --verify "refs/tags/$Tag" 2>$null
    return ($LASTEXITCODE -eq 0 -and $output)
}

function Test-TagExistsRemote {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $output = & git ls-remote --tags origin "refs/tags/$Tag" 2>$null
    return ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($output | Out-String)))
}

function New-ArtifactManifest {
    param(
        [Parameter(Mandatory = $true)][string[]]$Artifacts,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][bool]$DryRun
    )

    $entries = @()
    foreach ($artifact in $Artifacts) {
        if (-not (Test-Path $artifact)) {
            continue
        }

        $item = Get-Item $artifact
        $hash = (Get-FileHash -Path $artifact -Algorithm SHA256).Hash
        $entries += [PSCustomObject]@{
            FileName = $item.Name
            FullPath = $item.FullName
            SizeBytes = $item.Length
            Sha256 = $hash
        }
    }

    $manifest = [PSCustomObject]@{
        Tag = $Tag
        Target = $Target
        DryRun = $DryRun
        GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        Artifacts = $entries
    }

    Write-JsonFile -Object $manifest -Path $Path
    return $manifest
}

function Test-GhReleaseExists {
    param(
        [Parameter(Mandatory = $true)][string]$GhCommand,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $GhCommand 'release' 'view' $Tag *> $null
        return ($LASTEXITCODE -eq 0)
    }
    finally {
        Pop-Location
    }
}

function Invoke-GhReleaseCreateAndUpload {
    param(
        [Parameter(Mandatory = $true)][string]$GhCommand,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$NotesPath,
        [Parameter(Mandatory = $true)][string[]]$Artifacts,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $maxAttempts = 3
    $releaseReady = $false

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Invoke-LoggedCommand -Command $GhCommand -Arguments @('release', 'create', $Tag, '--title', $Tag, '--notes-file', $NotesPath) -WorkingDirectory $WorkingDirectory
            $releaseReady = $true
            break
        }
        catch {
            if (Test-GhReleaseExists -GhCommand $GhCommand -Tag $Tag -WorkingDirectory $WorkingDirectory) {
                Write-Warning "GitHub release $Tag already exists after create attempt; continuing with asset uploads."
                $releaseReady = $true
                break
            }

            if ($attempt -ge $maxAttempts) {
                throw
            }

            $sleepSeconds = 2 * $attempt
            Write-Warning "GitHub release create attempt $attempt/$maxAttempts failed. Retrying in $sleepSeconds seconds..."
            Start-Sleep -Seconds $sleepSeconds
        }
    }

    if (-not $releaseReady) {
        throw "Unable to create GitHub release $Tag after $maxAttempts attempts."
    }

    foreach ($artifact in $Artifacts) {
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            try {
                Invoke-LoggedCommand -Command $GhCommand -Arguments @('release', 'upload', $Tag, $artifact, '--clobber') -WorkingDirectory $WorkingDirectory
                break
            }
            catch {
                if ($attempt -ge $maxAttempts) {
                    throw
                }

                $sleepSeconds = 2 * $attempt
                Write-Warning "Asset upload failed for $(Split-Path -Leaf $artifact) attempt $attempt/$maxAttempts. Retrying in $sleepSeconds seconds..."
                Start-Sleep -Seconds $sleepSeconds
            }
        }
    }
}

function Get-WorkingTreeStatus {
    $status = (& git status --porcelain | Out-String).Trim()
    return $status
}

function Get-ChangedFiles {
    $files = New-Object System.Collections.Generic.List[string]

    $unstaged = (& git diff --name-only | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($unstaged)) {
        foreach ($f in ($unstaged -split "`r?`n")) {
            if (-not [string]::IsNullOrWhiteSpace($f)) { $files.Add($f.Trim()) }
        }
    }

    $staged = (& git diff --cached --name-only | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($staged)) {
        foreach ($f in ($staged -split "`r?`n")) {
            if (-not [string]::IsNullOrWhiteSpace($f)) { $files.Add($f.Trim()) }
        }
    }

    $untracked = (& git ls-files --others --exclude-standard | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($untracked)) {
        foreach ($f in ($untracked -split "`r?`n")) {
            if (-not [string]::IsNullOrWhiteSpace($f)) { $files.Add($f.Trim()) }
        }
    }

    return @($files | Sort-Object -Unique)
}

function Resolve-AutoCommitType {
    param(
        [string[]]$Files,
        [string]$Fallback
    )

    if (-not $Files -or $Files.Count -eq 0) {
        return $Fallback
    }

    $normalized = @($Files | ForEach-Object { $_.ToLowerInvariant() })
    $allDocs = $true
    foreach ($file in $normalized) {
        if ($file -notmatch '(^docs/|\.md$|\.txt$)') {
            $allDocs = $false
            break
        }
    }

    if ($allDocs) { return 'docs' }
    if ($normalized | Where-Object { $_ -match '^\.github/workflows/.*\.(yml|yaml)$' }) { return 'ci' }
    if ($normalized | Where-Object { $_ -match '(\.csproj$|\.sln$|nuget\.config$|packages\.lock\.json$)' }) { return 'build' }

    $allTests = $true
    foreach ($file in $normalized) {
        if ($file -notmatch '(test|tests)') {
            $allTests = $false
            break
        }
    }

    if ($allTests -and $normalized.Count -gt 0) { return 'test' }
    return $Fallback
}

function Resolve-AutoCommitDescription {
    param(
        [string[]]$Files,
        [string]$Target,
        [string]$CommitType,
        [string]$Fallback
    )

    $defaultDescription = $Fallback
    if ([string]::IsNullOrWhiteSpace($defaultDescription)) {
        $defaultDescription = "prepare $Target release changes"
    }

    if (-not $Files -or $Files.Count -eq 0) {
        return $defaultDescription
    }

    $normalized = @($Files | ForEach-Object { $_.ToLowerInvariant() })

    $allDocs = $true
    foreach ($file in $normalized) {
        if ($file -notmatch '(^docs/|\.md$|\.txt$)') {
            $allDocs = $false
            break
        }
    }

    $allTests = $true
    foreach ($file in $normalized) {
        if ($file -notmatch '(test|tests)') {
            $allTests = $false
            break
        }
    }

    if ($CommitType -eq 'docs' -or $allDocs) { return 'update documentation' }
    if ($CommitType -eq 'ci' -or ($normalized | Where-Object { $_ -match '^\.github/workflows/.*\.(yml|yaml)$' })) { return 'update ci workflows' }
    if ($CommitType -eq 'build' -or ($normalized | Where-Object { $_ -match '(^tools/release/|\.csproj$|\.sln$|nuget\.config$|packages\.lock\.json$)' })) { return 'update build and release automation' }
    if ($CommitType -eq 'test' -or ($allTests -and $normalized.Count -gt 0)) { return 'update tests' }

    if ($normalized.Count -eq 1) {
        $single = Split-Path -Leaf $Files[0]
        if (-not [string]::IsNullOrWhiteSpace($single)) {
            return "update $single"
        }
    }

    switch ($CommitType) {
        'feat' { return 'add release-related updates' }
        'fix' { return 'fix release-related issues' }
        'perf' { return 'improve release performance' }
        'refactor' { return 'refactor release-related code' }
        'chore' { return $defaultDescription }
        default { return $defaultDescription }
    }
}

function Select-CommitTypeAndDescription {
    param(
        [string[]]$ChangedFiles,
        [string]$DefaultType,
        [string]$DefaultDescription,
        [string]$Target
    )

    $typeOptions = @('feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'chore')
    $detectedType = Resolve-AutoCommitType -Files $ChangedFiles -Fallback $DefaultType

    Write-Host ''
    Write-Host 'Pre-publish commit selection:' -ForegroundColor Cyan
    Write-Host "[0] auto-detect (recommended now: $detectedType)"
    for ($i = 0; $i -lt $typeOptions.Count; $i++) {
        Write-Host ("[{0}] {1}" -f ($i + 1), $typeOptions[$i])
    }

    $selectedType = $null
    while (-not $selectedType) {
        $choice = (Read-Host "Select commit type number (default 0)").Trim()
        if ([string]::IsNullOrWhiteSpace($choice) -or $choice -eq '0') {
            $selectedType = $detectedType
            break
        }

        $index = 0
        if ([int]::TryParse($choice, [ref]$index) -and $index -ge 1 -and $index -le $typeOptions.Count) {
            $selectedType = $typeOptions[$index - 1]
            break
        }

        Write-Host 'Invalid selection. Enter 0-9.' -ForegroundColor Yellow
    }

    $defaultDesc = Resolve-AutoCommitDescription -Files $ChangedFiles -Target $Target -CommitType $selectedType -Fallback $DefaultDescription

    $description = (Read-Host "Commit description (Enter for auto: $defaultDesc)").Trim()
    if ([string]::IsNullOrWhiteSpace($description)) {
        $description = $defaultDesc
    }

    return @{
        Type = $selectedType
        Description = $description
    }
}

$root = Get-WorkspaceRoot
$logsRoot = Join-Path $root 'publish\logs'
Ensure-Directory -Path $logsRoot

# Ensure local tag view matches origin before semantic version calculation.
Invoke-LoggedCommand -Command 'git' -Arguments @('fetch', '--tags', 'origin') -WorkingDirectory $root

$prePublishCommit = $null
$initialStatus = Get-WorkingTreeStatus
$hasWorkingChanges = -not [string]::IsNullOrWhiteSpace($initialStatus)

if ($hasWorkingChanges -and -not $DryRun) {
    if (-not $AutoCommitChanges) {
        throw 'Working tree has uncommitted changes. Enable AutoCommitChanges or commit manually before publish.'
    }

    $changedFiles = Get-ChangedFiles
    $selection = Select-CommitTypeAndDescription -ChangedFiles $changedFiles -DefaultType $AutoCommitType -DefaultDescription $AutoCommitDescription -Target $Target
    $preCommitMessage = "$($selection.Type): $($selection.Description)"
    Write-Host "Auto-commit enabled. Creating pre-publish commit: $preCommitMessage"

    Invoke-LoggedCommand -Command 'git' -Arguments @('add', '-A') -WorkingDirectory $root

    $stagedForPrePublish = (& git diff --cached --name-only | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($stagedForPrePublish)) {
        Invoke-LoggedCommand -Command 'git' -Arguments @('commit', '-m', $preCommitMessage) -WorkingDirectory $root
        $prePublishCommit = Get-GitOutput -Args @('rev-parse', '--short', 'HEAD') -WorkingDirectory $root
        Write-Host "Pre-publish commit created: $prePublishCommit"
    }
}

$preflightArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'preflight.ps1'), '-Branch', $Branch)
$effectiveAllowDirty = $AllowDirty -or ($DryRun -and $hasWorkingChanges)
if ($effectiveAllowDirty) { $preflightArgs += '-AllowDirty' }
if ($AllowNonMain) { $preflightArgs += '-AllowNonMain' }
Invoke-LoggedCommand -Command 'powershell' -Arguments $preflightArgs -WorkingDirectory $root

$versionInfoPath = Join-Path $logsRoot 'next-version.json'
$usedSyntheticVersion = $false

Invoke-LoggedCommand -Command 'powershell' -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'version.ps1'), '-OutFile', $versionInfoPath, '-AllowNoCommits') -WorkingDirectory $root
$versionInfo = Get-Content -Path $versionInfoPath -Raw | ConvertFrom-Json

if (-not $versionInfo.HasChanges) {
    if (-not $DryRun) {
        $summaryPath = Join-Path $logsRoot 'publish-summary-noop.json'
        $summary = [PSCustomObject]@{
            Target = $Target
            NoOp = $true
            Reason = 'No new commits since latest release tag.'
            PreviousTag = $versionInfo.CurrentTag
            DryRun = $false
        }
        Write-JsonFile -Object $summary -Path $summaryPath
        Write-Host 'No new commits since latest release tag. Skipping publish as a safe no-op.'
        Write-Host "Summary: $summaryPath"
        Write-TaskDiagnostics -Prefix 'Publish task'
        return
    }

    $headShort = Get-GitOutput -Args @('rev-parse', '--short', 'HEAD') -WorkingDirectory $root
    $version = "0.0.0-dryrun-$headShort"
    $tag = "v$version"
    $previousTag = $versionInfo.CurrentTag
    $usedSyntheticVersion = $true
    Write-Host "Dry-run fallback: no new commits found for semantic bump. Using synthetic version $version"
}
else {
    $version = $versionInfo.Version
    $tag = $versionInfo.NextTag
    $previousTag = $versionInfo.CurrentTag
}

if (-not $DryRun) {
    if ((Test-TagExistsLocal -Tag $tag) -or (Test-TagExistsRemote -Tag $tag)) {
        throw "Refusing publish because tag already exists ($tag). Reconcile missing releases for existing tags first."
    }
}

$notesPath = Join-Path $logsRoot ("release-notes-" + $tag + '.md')
if ($DryRun) {
    $dryNotes = @(
        "## $tag (Dry Run)",
        '',
        'Dry-run mode: no commit, tag, push, or GitHub release was created.'
    ) -join "`r`n"
    Set-Content -Path $notesPath -Value $dryNotes -Encoding UTF8
}
else {
    $changelogArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'changelog.ps1'), '-Version', $version, '-OutNotesFile', $notesPath)
    if ($previousTag) {
        $changelogArgs += @('-PreviousTag', $previousTag)
    }
    Invoke-LoggedCommand -Command 'powershell' -Arguments $changelogArgs -WorkingDirectory $root
}

$releaseInfoPath = Join-Path $logsRoot ("release-artifacts-" + $tag + '.json')
Invoke-LoggedCommand -Command 'powershell' -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'release.ps1'), '-Target', $Target, '-Version', $version, '-OutFile', $releaseInfoPath) -WorkingDirectory $root
$releaseInfo = Get-Content -Path $releaseInfoPath -Raw | ConvertFrom-Json
$artifacts = @($releaseInfo.Artifacts)

$artifactManifestPath = Join-Path $logsRoot ("artifact-manifest-" + $tag + '.json')
$artifactManifest = New-ArtifactManifest -Artifacts $artifacts -Path $artifactManifestPath -Tag $tag -Target $Target -DryRun ([bool]$DryRun)

if ($DryRun) {
    Write-Host "Dry run completed for $tag."
    if ($usedSyntheticVersion) {
        Write-Host 'Semantic version was unavailable (no new commits). Used synthetic preview version.'
    }
    Write-Host "Artifact manifest: $artifactManifestPath"
    Write-Host "Artifacts:"
    $artifacts | ForEach-Object { Write-Host " - $_" }
    Write-TaskDiagnostics -Prefix 'Publish task'
    return
}

if (Test-TagExistsLocal -Tag $tag) {
    throw "Refusing publish because tag already exists locally: $tag"
}

if (Test-TagExistsRemote -Tag $tag) {
    throw "Refusing publish because tag already exists on origin: $tag"
}

Set-Content -Path (Join-Path $root 'VERSION') -Value $version -Encoding UTF8

Invoke-LoggedCommand -Command 'git' -Arguments @('add', 'CHANGELOG.md', 'VERSION') -WorkingDirectory $root

$staged = Get-GitOutput -Args @('diff', '--cached', '--name-only') -WorkingDirectory $root
if (-not $staged) {
    throw 'Nothing staged for release commit after changelog/version update.'
}

$baseCommit = Get-GitOutput -Args @('rev-parse', 'HEAD') -WorkingDirectory $root
$releaseCommitCreated = $false
$releaseCommitSha = $null
$releaseTagCreated = $false
$branchPushed = $false
$tagPushed = $false
$githubReleaseCreated = $false

try {
    Invoke-LoggedCommand -Command 'git' -Arguments @('commit', '-m', "release: $tag") -WorkingDirectory $root
    $releaseCommitCreated = $true
    $releaseCommitSha = Get-GitOutput -Args @('rev-parse', 'HEAD') -WorkingDirectory $root

    Invoke-LoggedCommand -Command 'git' -Arguments @('tag', '-a', $tag, '-m', "Release $tag") -WorkingDirectory $root
    $releaseTagCreated = $true

    if (-not $NoPush) {
        # Push branch and tag together to prevent partial remote state.
        Invoke-LoggedCommand -Command 'git' -Arguments @('push', '--atomic', 'origin', 'HEAD', $tag) -WorkingDirectory $root
        $branchPushed = $true
        $tagPushed = $true
    }

    if ($NoPush) {
        Write-Host 'Skipping GitHub release creation because -NoPush was specified.'
    }
    else {
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if (-not $gh) {
            throw 'GitHub CLI not found. Cannot create GitHub release.'
        }

        Invoke-GhReleaseCreateAndUpload -GhCommand $gh.Source -Tag $tag -NotesPath $notesPath -Artifacts $artifacts -WorkingDirectory $root
        $githubReleaseCreated = $true
    }
}
catch {
    $recoveredAfterPartialPush = $false

    Write-Warning "Publish failed for $tag. Attempting rollback of local release state."
    if ($_.Exception) {
        Write-Host 'Publish root failure details:' -ForegroundColor Red
        foreach ($line in (($_.Exception.Message | Out-String).TrimEnd() -split "`r?`n")) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                Write-Host ("  " + $line) -ForegroundColor Red
            }
        }
    }

    if ($githubReleaseCreated) {
        Write-Warning 'GitHub release was already created; rollback cannot fully undo remote state.'
    }

    if ($tagPushed -or $branchPushed) {
        Write-Warning 'Branch/tag were pushed to origin; automatic rollback will be local only.'
    }

    $remoteContainsReleaseCommit = $false
    if ($releaseCommitCreated -and -not [string]::IsNullOrWhiteSpace($releaseCommitSha)) {
        & git fetch origin $Branch *> $null
        & git merge-base --is-ancestor $releaseCommitSha ("origin/" + $Branch) *> $null
        if ($LASTEXITCODE -eq 0) {
            $remoteContainsReleaseCommit = $true
            Write-Warning 'Detected release commit already present on origin; treating remote state as changed.'
        }
    }

    $remoteStateChanged = $branchPushed -or $tagPushed -or $remoteContainsReleaseCommit

    if ($remoteStateChanged -and -not $NoPush -and -not $githubReleaseCreated) {
        try {
            $ghRecovery = Get-Command gh -ErrorAction SilentlyContinue
            if ($ghRecovery) {
                Write-Warning 'Remote push succeeded but release step failed. Attempting automatic release recovery.'
                Invoke-GhReleaseCreateAndUpload -GhCommand $ghRecovery.Source -Tag $tag -NotesPath $notesPath -Artifacts $artifacts -WorkingDirectory $root
                $githubReleaseCreated = $true
                $recoveredAfterPartialPush = $true
                Write-Warning 'Automatic release recovery succeeded. Continuing publish flow.'
            }
        }
        catch {
            Write-Warning 'Automatic release recovery attempt failed. Proceeding with normal failure handling.'
        }
    }

    if (-not $remoteStateChanged) {
        if ($releaseTagCreated) {
            & git tag -d $tag *> $null
        }

        if ($releaseCommitCreated) {
            & git reset --hard $baseCommit *> $null
        }
    }
    else {
        Write-Warning 'Skipping local reset/tag-delete because remote already changed. Keep local state and reconcile manually.'
    }

    if (-not $recoveredAfterPartialPush) {
        throw
    }
}

$summary = [PSCustomObject]@{
    Target = $Target
    Version = $version
    Tag = $tag
    PreviousTag = $previousTag
    PrePublishCommit = $prePublishCommit
    Artifacts = $artifacts
    ArtifactManifest = $artifactManifestPath
    Notes = $notesPath
}

$summaryPath = Join-Path $logsRoot ("publish-summary-" + $tag + '.json')
Write-JsonFile -Object $summary -Path $summaryPath
Write-Host "Publish completed for $tag"
Write-Host "Summary: $summaryPath"
Write-TaskDiagnostics -Prefix 'Publish task'

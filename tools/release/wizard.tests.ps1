if (-not (Get-Module -ListAvailable -Name Pester)) { throw 'Pester is required to run tests. Please install it via Install-Module Pester.' }
Import-Module Pester -ErrorAction Stop

Describe 'Crispy_Bills Release Wizard' {
    BeforeAll {
        $script = Join-Path $PSScriptRoot 'wizard.ps1'
        . $script
    }

    It 'Prompt-MultiSelect accepts all (case-insensitive)' {
        Mock -CommandName Is-Interactive -MockWith { $true }
        Mock -CommandName Read-Host -MockWith { 'All' }
        $options = @('a','b','c')
        $result = Prompt-MultiSelect -Options $options
        ($result -join ',') | Should -Be '0,1,2'
    }

    It 'Prompt-MultiSelect parses numeric ranges and ignores invalid entries' {
        Mock -CommandName Is-Interactive -MockWith { $true }
        Mock -CommandName Read-Host -MockWith { '1-2,99,abc,4' }
        $options = @('x','y','z','w')
        $result = Prompt-MultiSelect -Options $options
        ($result -join ',') | Should -Be '0,1,3'
    }

    It 'Get-ShellCommand returns a shell command' {
        Get-ShellCommand | Should -Match '^(pwsh|powershell)$'
    }

    It 'Run-ScriptByName throws when script not found' {
        { Run-ScriptByName -ScriptName 'does-not-exist.ps1' -DryRun:$true } | Should -Throw
    }

    It 'Prompt-YesNo returns default when response blank' {
        Mock -CommandName Read-Host -MockWith { '' }
        (Prompt-YesNo -Message 'Test?' -Default $false) | Should -Be $false
        (Prompt-YesNo -Message 'Test?' -Default $true) | Should -Be $true
    }

    It 'ConvertFrom-GitStatusLines preserves full filenames' {
        $lines = @(
            ' M CHANGELOG.md',
            ' M README.md',
            ' M docs/RELEASE_AUTOMATION_PLAN.md',
            'R  old-name.txt -> new-name.txt'
        )
        $result = ConvertFrom-GitStatusLines -Lines $lines
        ($result -join ',') | Should -Be 'CHANGELOG.md,docs/RELEASE_AUTOMATION_PLAN.md,new-name.txt,README.md'
    }

    It 'Resolve-TaskSelectionValue accepts script names from responses' {
        $options = @('preflight.ps1','publish-both.ps1','recover-missing-release.ps1')
        $result = Resolve-TaskSelectionValue -Selection @('publish-both.ps1','recover-missing-release.ps1') -Options $options
        ($result -join ',') | Should -Be '1,2'
    }

    It 'Prompt-YesNo uses wizard response in non-interactive mode' {
        $NonInteractive = $true
        $global:RELEASE_RESPONSES = @{ wizard = @{ Proceed = $false } }
        (Prompt-YesNo -Message 'Proceed?' -Default $true -Key 'Proceed') | Should -Be $false
        $NonInteractive = $false
    }

    It 'Get-WizardTaskArguments adds changelog version when available' {
        $result = Get-WizardTaskArguments -ScriptName 'changelog.ps1' -AllowDirty:$true -DisablePublishAutoCommit:$false -DetectedVersion '1.4.0' -PublishCommitType 'fix' -PublishCommitDescription 'test description'
        ($result -join ',') | Should -Be '-Version,1.4.0'
    }

    It 'Get-WizardTaskArguments forwards publish automation flags' {
        $result = Get-WizardTaskArguments -ScriptName 'publish-both.ps1' -AllowDirty:$true -DisablePublishAutoCommit:$true -DetectedVersion $null -PublishCommitType 'fix' -PublishCommitScope 'release' -PublishCommitDescription 'test description'
        ($result -join ',') | Should -Be '-AllowDirty,-AutoCommitChanges:$false,-AutoCommitType,fix,-AutoCommitScope,release,-AutoCommitDescription,test description'
    }

    It 'Get-WizardTaskArguments forwards non-interactive responses contract to publish wrappers' {
        $NonInteractive = $true
        $ResponsesFile = 'tools/release/responses/minimal.json'
        $result = Get-WizardTaskArguments -ScriptName 'publish-both.ps1' -AllowDirty:$false -DisablePublishAutoCommit:$false -DetectedVersion $null -PublishCommitType 'fix' -PublishCommitScope 'release' -PublishCommitDescription 'test description'
        ($result -join ',') | Should -Be '-NonInteractive,-ResponsesFile,tools/release/responses/minimal.json,-AutoCommitType,fix,-AutoCommitScope,release,-AutoCommitDescription,test description'
        $NonInteractive = $false
        $ResponsesFile = $null
    }

    It 'Get-WizardTaskArguments forwards major approval to publish wrappers' {
        $result = Get-WizardTaskArguments -ScriptName 'publish-both.ps1' -AllowDirty:$false -DisablePublishAutoCommit:$false -DetectedVersion $null -PublishCommitType 'fix' -PublishCommitScope 'release' -PublishCommitDescription 'test description' -ApproveMajorVersion:$true
        ($result -join ',') | Should -Be '-ApproveMajorVersion,-AutoCommitType,fix,-AutoCommitScope,release,-AutoCommitDescription,test description'
    }

    It 'Confirm-MajorVersionApproval throws in non-interactive mode when major release is not approved' {
        Mock -CommandName Is-Interactive -MockWith { $false }
        Mock -CommandName Get-WizardResponse -MockWith { $false }
        $versionInfo = [PSCustomObject]@{ HasChanges = $true; Bump = 'major'; CurrentTag = 'v1.1.0'; NextTag = 'v2.0.0' }
        { Confirm-MajorVersionApproval -VersionInfo $versionInfo -DryRun:$false -ExplicitApproval:$false } | Should -Throw
    }

    It 'Confirm-MajorVersionApproval accepts explicit approval for major releases' {
        $versionInfo = [PSCustomObject]@{ HasChanges = $true; Bump = 'major'; CurrentTag = 'v1.1.0'; NextTag = 'v2.0.0' }
        (Confirm-MajorVersionApproval -VersionInfo $versionInfo -DryRun:$false -ExplicitApproval:$true) | Should -Be $true
    }

    It 'Get-WizardTaskArguments does not send AllowDirty to recovery script' {
        $result = Get-WizardTaskArguments -ScriptName 'recover-missing-release.ps1' -AllowDirty:$true -DisablePublishAutoCommit:$false -DetectedVersion $null -PublishCommitType 'fix' -PublishCommitDescription 'test description'
        ($result | Measure-Object).Count | Should -Be 0
    }

    It 'ConvertFrom-GitStatusLines handles trimmed-first-line without clipping first character' {
        $lines = @(
            'M CHANGELOG.md',
            ' M README.md',
            ' M docs/RELEASE_AUTOMATION_PLAN.md',
            'R  old-name.txt -> new-name.txt'
        )
        $result = ConvertFrom-GitStatusLines -Lines $lines
        ($result -join ',') | Should -Be 'CHANGELOG.md,docs/RELEASE_AUTOMATION_PLAN.md,new-name.txt,README.md'
    }

    It 'Show-DirtySummary with a single changed file does not throw' {
        Mock -CommandName Get-WorkspaceRoot -MockWith { 'C:\repo' }
        Mock -CommandName Get-GitOutput -MockWith { ' M README.md' }
        Mock -CommandName Write-Host -MockWith { }

        { Show-DirtySummary } | Should -Not -Throw
    }

    It 'Get-RecommendedCommitType handles a single documentation file' {
        (Get-RecommendedCommitType -files 'README.md') | Should -Be 'docs'
    }

    It 'Check-VersionAgreements warns when remote tag is ahead of local next version' {
        $script:hostMessages = @()

        Mock -CommandName Get-WorkspaceRoot -MockWith { 'C:\repo' }
        Mock -CommandName Get-VersionInfoFromScript -MockWith {
            [PSCustomObject]@{
                NextTag = 'v1.2.0'
            }
        }
        Mock -CommandName Get-GitOutput -MockWith {
            @(
                "111111`trefs/tags/v1.1.0"
                "222222`trefs/tags/v1.3.0"
            ) -join "`n"
        }
        Mock -CommandName Write-Host -MockWith {
            param($Object, $ForegroundColor)
            $script:hostMessages += [string]$Object
        }

        Check-VersionAgreements

        ($script:hostMessages -join "`n") | Should -Match 'Warning: remote tag v1\.3\.0 is ahead of or equal to local next version v1\.2\.0'
    }

    It 'Check-VersionAgreements tolerates null local version data' {
        Mock -CommandName Get-WorkspaceRoot -MockWith { 'C:\repo' }
        Mock -CommandName Get-VersionInfoFromScript -MockWith { $null }
        Mock -CommandName Get-GitOutput -MockWith { "111111`trefs/tags/v1.3.0" }
        Mock -CommandName Write-Host -MockWith { }

        { Check-VersionAgreements } | Should -Not -Throw
    }

    It 'single task selections remain countable after name resolution' {
        $available = @('version.ps1', 'changelog.ps1')
        $resolvedSelection = @(Resolve-TaskSelectionValue -Selection 'version.ps1' -Options $available)
        $chosen = @($resolvedSelection | ForEach-Object { $available[$_] })

        $chosen.Count | Should -Be 1
        $chosen[0] | Should -Be 'version.ps1'
    }
}


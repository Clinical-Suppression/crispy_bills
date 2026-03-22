if (-not (Get-Module -ListAvailable -Name Pester)) { throw 'Pester is required to run tests. Please install it via Install-Module Pester.' }
Import-Module Pester -ErrorAction Stop

Describe 'Crispy_Bills Release Wizard' {
    BeforeAll {
        $script = Join-Path $PSScriptRoot 'wizard.ps1'
        . $script
    }

    It 'Prompt-MultiSelect accepts all (case-insensitive)' {
        Mock -CommandName Read-Host -MockWith { 'All' }
        $options = @('a','b','c')
        $result = Prompt-MultiSelect -Options $options
        ($result -join ',') | Should Be '0,1,2'
    }

    It 'Prompt-MultiSelect parses numeric ranges and ignores invalid entries' {
        Mock -CommandName Read-Host -MockWith { '1-2,99,abc,4' }
        $options = @('x','y','z','w')
        $result = Prompt-MultiSelect -Options $options
        ($result -join ',') | Should Be '0,1,3'
    }

    It 'Get-ShellCommand returns a shell command' {
        Get-ShellCommand | Should Match '^(pwsh|powershell)$'
    }

    It 'Run-ScriptByName throws when script not found' {
        { Run-ScriptByName -ScriptName 'does-not-exist.ps1' -DryRun:$true } | Should Throw
    }

    It 'Prompt-YesNo returns default when response blank' {
        Mock -CommandName Read-Host -MockWith { '' }
        (Prompt-YesNo -Message 'Test?' -Default $false) | Should Be $false
        (Prompt-YesNo -Message 'Test?' -Default $true) | Should Be $true
    }

    It 'Resolve-TaskSelectionValue accepts script names from responses' {
        $options = @('preflight.ps1','publish-both.ps1','recover-missing-release.ps1')
        $result = Resolve-TaskSelectionValue -Selection @('publish-both.ps1','recover-missing-release.ps1') -Options $options
        ($result -join ',') | Should Be '1,2'
    }

    It 'Prompt-YesNo uses wizard response in non-interactive mode' {
        $script:NonInteractive = $true
        $global:RELEASE_RESPONSES = @{ wizard = @{ Proceed = $false } }
        (Prompt-YesNo -Message 'Proceed?' -Default $true -Key 'Proceed') | Should Be $false
        $script:NonInteractive = $false
    }

    It 'Get-WizardTaskArguments adds changelog version when available' {
        $result = Get-WizardTaskArguments -ScriptName 'changelog.ps1' -AllowDirty:$true -DisablePublishAutoCommit:$false -DetectedVersion '1.4.0' -PublishCommitType 'fix' -PublishCommitDescription 'test description'
        ($result -join ',') | Should Be '-Version,1.4.0'
    }

    It 'Get-WizardTaskArguments forwards publish automation flags' {
        $result = Get-WizardTaskArguments -ScriptName 'publish-both.ps1' -AllowDirty:$true -DisablePublishAutoCommit:$true -DetectedVersion $null -PublishCommitType 'fix' -PublishCommitScope 'release' -PublishCommitDescription 'test description'
        ($result -join ',') | Should Be '-AllowDirty,-AutoCommitChanges:$false,-AutoCommitType,fix,-AutoCommitScope,release,-AutoCommitDescription,test description'
    }

    It 'Get-WizardTaskArguments forwards non-interactive responses contract to publish wrappers' {
        $script:NonInteractive = $true
        $script:ResponsesFile = 'tools/release/responses/minimal.json'
        $result = Get-WizardTaskArguments -ScriptName 'publish-both.ps1' -AllowDirty:$false -DisablePublishAutoCommit:$false -DetectedVersion $null -PublishCommitType 'fix' -PublishCommitScope 'release' -PublishCommitDescription 'test description'
        ($result -join ',') | Should Be '-NonInteractive,-ResponsesFile,tools/release/responses/minimal.json,-AutoCommitType,fix,-AutoCommitScope,release,-AutoCommitDescription,test description'
        $script:NonInteractive = $false
        $script:ResponsesFile = $null
    }

    It 'Get-WizardTaskArguments does not send AllowDirty to recovery script' {
        $result = Get-WizardTaskArguments -ScriptName 'recover-missing-release.ps1' -AllowDirty:$true -DisablePublishAutoCommit:$false -DetectedVersion $null -PublishCommitType 'fix' -PublishCommitDescription 'test description'
        ($result | Measure-Object).Count | Should Be 0
    }
}


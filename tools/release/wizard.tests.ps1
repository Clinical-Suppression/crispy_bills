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
        $result | Should -Be @([int]0,[int]1,[int]2)
    }

    It 'Prompt-MultiSelect parses numeric ranges and ignores invalid entries' {
        Mock -CommandName Read-Host -MockWith { '1-2,99,abc,4' }
        $options = @('x','y','z','w')
        $result = Prompt-MultiSelect -Options $options
        $result | Should -Be @([int]0,[int]1,[int]3)
    }

    It 'Get-ShellCommand returns a shell command' {
        Get-ShellCommand | Should -BeIn @('pwsh','powershell')
    }

    It 'Run-ScriptByName throws when script not found' {
        { Run-ScriptByName -ScriptName 'does-not-exist.ps1' -DryRun:$true } | Should -Throw
    }

    It 'Prompt-YesNo returns default when response blank' {
        Mock -CommandName Read-Host -MockWith { '' }
        Prompt-YesNo -Message 'Test?' -Default $false | Should -BeFalse
        Prompt-YesNo -Message 'Test?' -Default $true | Should -BeTrue
    }
}

